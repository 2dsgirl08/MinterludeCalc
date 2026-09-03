using System.Collections.ObjectModel;
using Avalonia.Threading;
using MinterludeCalc;

namespace MinterludeCalc.Overlay.ViewModels;

public class MainViewModel : ObservableObject
{
    /// <summary>Consecutive failed selection reads before we assume our cached addresses went stale.</summary>
    const int FailuresBeforeResync = 10;      // ~5s at a 500ms poll

    /// <summary>...and before we give up on resyncing and rebuild the whole reader.</summary>
    const int FailuresBeforeReattach = 60;    // ~30s

    /// <summary>How long a single poll may run before we tell the user it's wedged.</summary>
    static readonly TimeSpan StuckPollThreshold = TimeSpan.FromSeconds(10);

    private readonly OverlayService _overlay = new("Tools/msd.exe");
    private readonly DispatcherTimer _pollTimer;
    private bool _polling;
    private DateTime _pollStartedAt;
    private bool _ratingComputeInProgress;

    private int _consecutiveReadFailures;
    private int _lastResyncAtFailureCount;
    private bool _hasReadSinceAttach;

    // Everything below is touched only from the UI thread (the poll runs on the
    // dispatcher timer and its awaits resume there); background work posts back.
    private string _lastChartKey = "";
    private string _pendingDifficultyKey = "";

    // ---- Connection state ----
    private string _statusText = "Connecting to Interlude...";
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

    private bool _isAttached;
    public bool IsAttached { get => _isAttached; set => SetField(ref _isAttached, value); }

    /// <summary>
    /// Shown while attached but not reading cleanly - e.g. resyncing after the
    /// game moved things around in memory. Without this the overlay just silently
    /// keeps displaying the last song it managed to read, which looks frozen.
    /// </summary>
    private string _syncStatusText = "";
    public string SyncStatusText
    {
        get => _syncStatusText;
        set
        {
            SetField(ref _syncStatusText, value);
            Raise(nameof(HasSyncStatus));
        }
    }

    public bool HasSyncStatus => !string.IsNullOrEmpty(SyncStatusText);

    // ---- Current chart / song select ----
    private string _chartTitle = "";
    public string ChartTitle { get => _chartTitle; set => SetField(ref _chartTitle, value); }

    private string _chartSubtitle = "";
    public string ChartSubtitle { get => _chartSubtitle; set => SetField(ref _chartSubtitle, value); }

    public ObservableCollection<SkillsetRow> Difficulty { get; } = new();

    private string _difficultyStatusText = "";
    public string DifficultyStatusText
    {
        get => _difficultyStatusText;
        set
        {
            SetField(ref _difficultyStatusText, value);
            Raise(nameof(HasDifficultyStatus));
        }
    }

    public bool HasDifficultyStatus => !string.IsNullOrEmpty(DifficultyStatusText);

    // ---- Player rating ----
    private double _playerRatingOverall;
    public double PlayerRatingOverall { get => _playerRatingOverall; set => SetField(ref _playerRatingOverall, value); }

    public ObservableCollection<SkillsetRow> PlayerRatingSkillsets { get; } = new();

    private bool _isComputingRating;
    public bool IsComputingRating { get => _isComputingRating; set => SetField(ref _isComputingRating, value); }

    private string _ratingProgressText = "";
    public string RatingProgressText { get => _ratingProgressText; set => SetField(ref _ratingProgressText, value); }

    // ---- New play popup ----
    private bool _hasNewPlay;
    public bool HasNewPlay { get => _hasNewPlay; set => SetField(ref _hasNewPlay, value); }

    private string _newPlayAccuracyText = "";
    public string NewPlayAccuracyText { get => _newPlayAccuracyText; set => SetField(ref _newPlayAccuracyText, value); }

    public ObservableCollection<SkillsetRow> NewPlaySsr { get; } = new();

    public MainViewModel()
    {
        foreach (var name in new[] { "Overall" }.Concat(PlayerRating.SkillsetNames))
            Difficulty.Add(new SkillsetRow(name));

        foreach (var name in PlayerRating.SkillsetNames)
            PlayerRatingSkillsets.Add(new SkillsetRow(name));

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
    }

    public void Start()
    {
        _pollTimer.Start();
    }

    private async Task PollAsync()
    {
        if (_polling)
        {
            // A poll that never finishes would otherwise wedge this guard shut
            // for good and stop every future update without a word.
            if (DateTime.UtcNow - _pollStartedAt > StuckPollThreshold)
                SyncStatusText = "Reading Interlude is taking a while...";

            return;
        }

        _polling = true;
        _pollStartedAt = DateTime.UtcNow;

        try
        {
            if (!IsAttached)
            {
                await TryAttachAsync();
                return;
            }

            var current = await Task.Run(SafeGetCurrentChart);

            if (current != null)
            {
                _consecutiveReadFailures = 0;
                _lastResyncAtFailureCount = 0;
                _hasReadSinceAttach = true;
                SyncStatusText = "";

                ApplyCurrentChart(current);
            }
            else
            {
                HandleReadFailure();
            }

            var newScore = await Task.Run(SafeCheckForNewScore);

            if (newScore != null)
            {
                NewPlayAccuracyText = $"{newScore.Accuracy * 100:F2}% (SC J{OverlayService.Judge})";
                NewPlaySsr.Clear();
                foreach (var (skillset, value) in newScore.Ssr)
                    NewPlaySsr.Add(new SkillsetRow(skillset, value));
                HasNewPlay = true;

                RecomputePlayerRatingInBackground();
            }
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// Pushes the newly-read selection into the UI. The chart title and rate go
    /// up immediately; the difficulty numbers follow once MinaCalc has run,
    /// because that takes seconds and used to be done inline - a slow or failed
    /// calculation would throw the whole update away and leave the previous
    /// song on screen.
    /// </summary>
    private void ApplyCurrentChart(CurrentChartInfo current)
    {
        string chartKey = $"{current.Chart.Hash}:{current.Rate:0.###}";

        if (chartKey != _lastChartKey)
        {
            _lastChartKey = chartKey;

            ChartTitle = current.Chart.Title;
            ChartSubtitle = $"{current.Chart.Artist} - {current.Chart.Difficulty} @ {current.Rate:0.00}x";

            // Don't leave the previous song's numbers sitting under the new
            // song's title while its own calculation is still running.
            if (!current.DifficultyReady)
            {
                foreach (var row in Difficulty)
                    row.Value = 0;
            }
        }

        if (current.DifficultyReady)
        {
            _pendingDifficultyKey = "";
            DifficultyStatusText = "";
            ApplyDifficulty(current.Difficulty);
            return;
        }

        RequestDifficultyInBackground(current.Chart.Hash, current.Rate, chartKey);
    }

    private void ApplyDifficulty(IReadOnlyDictionary<string, double> difficulty)
    {
        foreach (var row in Difficulty)
        {
            if (difficulty.TryGetValue(row.Name, out var value))
                row.Value = value;
        }
    }

    private void RequestDifficultyInBackground(string hash, float rate, string chartKey)
    {
        if (_pendingDifficultyKey == chartKey)
            return;

        _pendingDifficultyKey = chartKey;
        DifficultyStatusText = "Calculating...";

        Task.Run(() =>
        {
            try
            {
                var difficulty = _overlay.GetDifficulty(hash, rate);

                Dispatcher.UIThread.Post(() =>
                {
                    // The player may well have moved on while msd.exe ran.
                    if (_pendingDifficultyKey != chartKey)
                        return;

                    _pendingDifficultyKey = "";
                    DifficultyStatusText = "";
                    ApplyDifficulty(difficulty);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_pendingDifficultyKey != chartKey)
                        return;

                    _pendingDifficultyKey = "";
                    DifficultyStatusText = $"Difficulty unavailable ({ex.Message})";
                });
            }
        });
    }

    /// <summary>
    /// Escalating recovery for "we can't read the selection any more". Interlude's
    /// GC relocates the objects we cached addresses for - most likely while the
    /// game idles in the background - so a run of failed reads means resyncing,
    /// not giving up. Left alone, this is what makes the overlay sit frozen on
    /// the last song it saw.
    /// </summary>
    private void HandleReadFailure()
    {
        _consecutiveReadFailures++;

        if (!_overlay.IsAttached)
        {
            // Interlude went away - everything we resolved belonged to a process
            // that no longer exists.
            ResetConnection("Interlude closed - waiting for it to come back...");
            return;
        }

        if (!_hasReadSinceAttach)
        {
            // We've attached but have never managed a read. The reader retries
            // resolving on its own, so there's nothing stale to recover from -
            // but say which step is failing, because "waiting" on its own is
            // indistinguishable from a reader that will never succeed.
            SyncStatusText = _overlay.LastReadIssue ?? "Waiting for a song selection...";
            return;
        }

        if (_consecutiveReadFailures >= FailuresBeforeReattach)
        {
            ResetConnection("Reconnecting to Interlude...");
            return;
        }

        if (_consecutiveReadFailures >= FailuresBeforeResync
            && _consecutiveReadFailures - _lastResyncAtFailureCount >= FailuresBeforeResync)
        {
            _lastResyncAtFailureCount = _consecutiveReadFailures;
            SyncStatusText = string.IsNullOrEmpty(_overlay.LastReadIssue)
                ? "Resyncing with Interlude..."
                : $"Resyncing with Interlude... ({_overlay.LastReadIssue})";

            // Just drops the addresses; the re-resolving heap walk happens on
            // the next poll, which already runs off the UI thread.
            _overlay.ChartReader.InvalidateAllCaches();
        }
    }

    private void ResetConnection(string status)
    {
        try { _overlay.Detach(); } catch { /* tearing down is best-effort */ }

        IsAttached = false;
        StatusText = status;
        SyncStatusText = "";
        DifficultyStatusText = "";
        _consecutiveReadFailures = 0;
        _lastResyncAtFailureCount = 0;
        _hasReadSinceAttach = false;
        _lastChartKey = "";
        _pendingDifficultyKey = "";
    }

    private async Task TryAttachAsync()
    {
        try
        {
            StatusText = "Connecting to Interlude...";
            await Task.Run(() => _overlay.Attach());

            StatusText = "Loading chart library...";
            await Task.Run(() => _overlay.RefreshChartLibrary());

            IsAttached = true;
            StatusText = "";
            SyncStatusText = "";

            RecomputePlayerRatingInBackground();
        }
        catch (Exception ex)
        {
            StatusText = $"Waiting for Interlude... ({ex.Message})";
        }
    }

    private CurrentChartInfo? SafeGetCurrentChart()
    {
        // computeDifficulty: false keeps the poll cheap - see RequestDifficultyInBackground.
        try { return _overlay.GetCurrentChart(computeDifficulty: false); }
        catch { return null; }
    }

    private PlayScoreResult? SafeCheckForNewScore()
    {
        try { return _overlay.CheckForNewScore(); }
        catch { return null; }
    }

    private void RecomputePlayerRatingInBackground()
    {
        if (_ratingComputeInProgress)
            return;

        _ratingComputeInProgress = true;
        IsComputingRating = true;

        Task.Run(() =>
        {
            try
            {
                var progress = new Progress<(int done, int total)>(p =>
                    Dispatcher.UIThread.Post(() => RatingProgressText = $"Rating scores {p.done}/{p.total}..."));

                var rating = _overlay.ComputePlayerRating(progress);

                Dispatcher.UIThread.Post(() =>
                {
                    PlayerRatingOverall = rating.Overall;
                    foreach (var row in PlayerRatingSkillsets)
                    {
                        if (rating.Skillsets.TryGetValue(row.Name, out var value))
                            row.Value = value;
                    }
                    RatingProgressText = "";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => RatingProgressText = $"Rating failed: {ex.Message}");
            }
            finally
            {
                _ratingComputeInProgress = false;
                Dispatcher.UIThread.Post(() => IsComputingRating = false);
            }
        });
    }

    public void DismissNewPlay() => HasNewPlay = false;
}
