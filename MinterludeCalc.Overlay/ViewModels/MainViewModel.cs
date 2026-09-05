using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using MinterludeCalc;

namespace MinterludeCalc.Overlay.ViewModels;

public class MainViewModel : ObservableObject
{
    public const string TabNow = "Now";
    public const string TabScores = "Scores";
    public const string TabTop = "Top";
    public const string TabGraph = "Graph";

    /// <summary>Consecutive failed selection reads before we assume our cached addresses went stale.</summary>
    const int FailuresBeforeResync = 10;      // ~5s at a 500ms poll

    /// <summary>...and before we give up on resyncing and rebuild the whole reader.</summary>
    const int FailuresBeforeReattach = 60;    // ~30s

    /// <summary>How long a single poll may run before we tell the user it's wedged.</summary>
    static readonly TimeSpan StuckPollThreshold = TimeSpan.FromSeconds(10);

    /// <summary>Plot area, in pixels. The graph is drawn straight into this box.</summary>
    public const double GraphWidth = 268;
    public const double GraphHeight = 140;

    /// <summary>Radius of the dot that follows the pointer along the curve.</summary>
    const double HoverDotRadius = 3.5;

    /// <summary>Horizontal gridlines, which is also how many value labels the axis gets.</summary>
    const int GraphBands = 4;

    const int TopPlayCount = 25;

    private readonly OverlayService _overlay = new(MinaCalc.DefaultToolPath());
    private readonly DispatcherTimer _pollTimer;
    private bool _polling;
    private DateTime _pollStartedAt;
    private bool _snapshotComputeInProgress;
    private bool _snapshotRecomputeQueued;
    private bool _suppressProfileChange;

    private int _consecutiveReadFailures;
    private int _lastResyncAtFailureCount;
    private bool _hasReadSinceAttach;

    // Everything below is touched only from the UI thread (the poll runs on the
    // dispatcher timer and its awaits resume there); background work posts back.
    private string _lastChartKey = "";
    private string _pendingDifficultyKey = "";
    private string _currentChartHash = "";

    // ---- Tabs ----
    private string _selectedTab = TabNow;
    public string SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab == value)
                return;

            SetField(ref _selectedTab, value);
            Raise(nameof(ShowNow));
            Raise(nameof(ShowScores));
            Raise(nameof(ShowTop));
            Raise(nameof(ShowGraph));
        }
    }

    public bool ShowNow => _selectedTab == TabNow;
    public bool ShowScores => _selectedTab == TabScores;
    public bool ShowTop => _selectedTab == TabTop;
    public bool ShowGraph => _selectedTab == TabGraph;

    public void SelectTab(string tab) => SelectedTab = tab;

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

    // ---- Profiles ----
    public ObservableCollection<ProfileItem> ProfileOptions { get; } = new();

    private ProfileItem? _selectedProfile;
    public ProfileItem? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile?.Id == value?.Id)
                return;

            SetField(ref _selectedProfile, value);
            Raise(nameof(CanDeleteProfile));

            if (_suppressProfileChange || value == null)
                return;

            _overlay.Profiles.SetActiveProfile(value.Id);

            // Switching profile changes every number on screen. The plays are
            // already scored and cached, so this is a re-aggregate, not a rescore.
            RecomputeSnapshotInBackground();
        }
    }

    /// <summary>Main isn't a real profile - it's "no filter" - so it can't be deleted.</summary>
    public bool CanDeleteProfile => _selectedProfile != null && _selectedProfile.Id != ProfileStore.MainProfileId;

    private string _newProfileName = "";
    public string NewProfileName { get => _newProfileName; set => SetField(ref _newProfileName, value); }

    private string _profileStatusText = "";
    public string ProfileStatusText
    {
        get => _profileStatusText;
        set
        {
            SetField(ref _profileStatusText, value);
            Raise(nameof(HasProfileStatus));
        }
    }

    public bool HasProfileStatus => !string.IsNullOrEmpty(ProfileStatusText);

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

    /// <summary>
    /// Dims the previous chart's numbers while the new one is calculating. They
    /// are stale, but blanking them to zero flickers on every song change - and
    /// a song select gets flicked through a lot.
    /// </summary>
    private double _difficultyOpacity = 1.0;
    public double DifficultyOpacity { get => _difficultyOpacity; set => SetField(ref _difficultyOpacity, value); }

    // ---- Player rating ----
    private double _playerRatingOverall;
    public double PlayerRatingOverall { get => _playerRatingOverall; set => SetField(ref _playerRatingOverall, value); }

    public ObservableCollection<SkillsetRow> PlayerRatingSkillsets { get; } = new();

    private bool _isComputingRating;
    public bool IsComputingRating { get => _isComputingRating; set => SetField(ref _isComputingRating, value); }

    private string _ratingProgressText = "";
    public string RatingProgressText { get => _ratingProgressText; set => SetField(ref _ratingProgressText, value); }

    // ---- Scores for the selected chart ----
    public ObservableCollection<ScoreRow> ChartScores { get; } = new();

    private string _chartScoresEmptyText = "";
    public string ChartScoresEmptyText
    {
        get => _chartScoresEmptyText;
        set
        {
            SetField(ref _chartScoresEmptyText, value);
            Raise(nameof(HasChartScoresEmptyText));
        }
    }

    public bool HasChartScoresEmptyText => !string.IsNullOrEmpty(ChartScoresEmptyText);

    // ---- Top plays ----
    public ObservableCollection<string> SkillsetOptions { get; } = new(PlayerRating.AllRatingNames());

    private string _selectedSkillset = PlayerRating.OverallName;
    public string SelectedSkillset
    {
        get => _selectedSkillset;
        set
        {
            if (_selectedSkillset == value || string.IsNullOrEmpty(value))
                return;

            SetField(ref _selectedSkillset, value);
            RefreshTopPlays();
        }
    }

    public ObservableCollection<ScoreRow> TopPlays { get; } = new();

    private string _topPlaysEmptyText = "";
    public string TopPlaysEmptyText
    {
        get => _topPlaysEmptyText;
        set
        {
            SetField(ref _topPlaysEmptyText, value);
            Raise(nameof(HasTopPlaysEmptyText));
        }
    }

    public bool HasTopPlaysEmptyText => !string.IsNullOrEmpty(TopPlaysEmptyText);

    // ---- Rating graph ----

    /// <summary>The sampled history behind the curve - kept so hover can map a pixel back to a play.</summary>
    private List<RatingHistoryPoint> _graphSamples = new();

    private IList<Point> _graphPoints = new List<Point>();
    public IList<Point> GraphPoints { get => _graphPoints; private set => SetField(ref _graphPoints, value); }

    /// <summary>The same curve closed down to the baseline, so it can be filled.</summary>
    private IList<Point> _graphAreaPoints = new List<Point>();
    public IList<Point> GraphAreaPoints { get => _graphAreaPoints; private set => SetField(ref _graphAreaPoints, value); }

    private bool _hasGraph;
    public bool HasGraph
    {
        get => _hasGraph;
        set
        {
            SetField(ref _hasGraph, value);
            Raise(nameof(NoGraph));
        }
    }

    public bool NoGraph => !HasGraph;

    // Value axis, top to bottom. Fixed count, so the gridlines can be laid out
    // by the layout system instead of positioned by hand.
    private string _graphLabel0 = "";
    public string GraphLabel0 { get => _graphLabel0; set => SetField(ref _graphLabel0, value); }

    private string _graphLabel1 = "";
    public string GraphLabel1 { get => _graphLabel1; set => SetField(ref _graphLabel1, value); }

    private string _graphLabel2 = "";
    public string GraphLabel2 { get => _graphLabel2; set => SetField(ref _graphLabel2, value); }

    private string _graphLabel3 = "";
    public string GraphLabel3 { get => _graphLabel3; set => SetField(ref _graphLabel3, value); }

    private string _graphLabel4 = "";
    public string GraphLabel4 { get => _graphLabel4; set => SetField(ref _graphLabel4, value); }

    private string _graphStartLabel = "";
    public string GraphStartLabel { get => _graphStartLabel; set => SetField(ref _graphStartLabel, value); }

    private string _graphEndLabel = "";
    public string GraphEndLabel { get => _graphEndLabel; set => SetField(ref _graphEndLabel, value); }

    private string _graphSummary = "";
    public string GraphSummary { get => _graphSummary; set => SetField(ref _graphSummary, value); }

    /// <summary>
    /// The readout under the plot. Shows the hovered play while the pointer is
    /// over the graph and the latest one otherwise - always present, so reading
    /// a value doesn't shift the layout underneath the pointer.
    /// </summary>
    private string _graphReadoutText = "";
    public string GraphReadoutText { get => _graphReadoutText; set => SetField(ref _graphReadoutText, value); }

    private bool _hasHover;
    public bool HasHover { get => _hasHover; set => SetField(ref _hasHover, value); }

    private double _hoverLineLeft;
    public double HoverLineLeft { get => _hoverLineLeft; set => SetField(ref _hoverLineLeft, value); }

    private double _hoverDotLeft;
    public double HoverDotLeft { get => _hoverDotLeft; set => SetField(ref _hoverDotLeft, value); }

    private double _hoverDotTop;
    public double HoverDotTop { get => _hoverDotTop; set => SetField(ref _hoverDotTop, value); }

    public double HoverDotSize => HoverDotRadius * 2;

    // ---- New play popup ----
    private bool _hasNewPlay;
    public bool HasNewPlay { get => _hasNewPlay; set => SetField(ref _hasNewPlay, value); }

    private string _newPlayAccuracyText = "";
    public string NewPlayAccuracyText { get => _newPlayAccuracyText; set => SetField(ref _newPlayAccuracyText, value); }

    private string _newPlayRatingText = "";
    public string NewPlayRatingText { get => _newPlayRatingText; set => SetField(ref _newPlayRatingText, value); }

    public ObservableCollection<SkillsetRow> NewPlaySsr { get; } = new();

    public MainViewModel()
    {
        foreach (var name in PlayerRating.AllRatingNames())
            Difficulty.Add(new SkillsetRow(name));

        foreach (var name in PlayerRating.SkillsetNames)
            PlayerRatingSkillsets.Add(new SkillsetRow(name));

        ReloadProfiles();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
    }

    public void Start()
    {
        _pollTimer.Start();
    }

    // =========================================================================
    // PROFILES
    // =========================================================================

    private void ReloadProfiles()
    {
        string activeId = _overlay.Profiles.ActiveProfileId;

        _suppressProfileChange = true;
        try
        {
            ProfileOptions.Clear();

            foreach (var profile in _overlay.Profiles.AllProfiles())
                ProfileOptions.Add(new ProfileItem(profile.Id, profile.Name));

            SelectedProfile = ProfileOptions.FirstOrDefault(p => p.Id == activeId) ?? ProfileOptions.FirstOrDefault();
        }
        finally
        {
            _suppressProfileChange = false;
        }
    }

    public void CreateProfile()
    {
        try
        {
            var profile = _overlay.Profiles.CreateProfile(NewProfileName);

            NewProfileName = "";
            ProfileStatusText = $"Now on '{profile.Name}'. Plays from here on go to it.";

            ReloadProfiles();
            RecomputeSnapshotInBackground();
        }
        catch (Exception ex)
        {
            ProfileStatusText = ex.Message;
        }
    }

    public void DeleteActiveProfile()
    {
        var profile = SelectedProfile;

        if (profile == null || profile.Id == ProfileStore.MainProfileId)
            return;

        try
        {
            _overlay.Profiles.DeleteProfile(profile.Id);
            ProfileStatusText = $"Deleted '{profile.Name}'. Its plays are still in {ProfileStore.MainProfileName}.";

            ReloadProfiles();
            RecomputeSnapshotInBackground();
        }
        catch (Exception ex)
        {
            ProfileStatusText = ex.Message;
        }
    }

    // =========================================================================
    // POLL
    // =========================================================================

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
                ShowNewPlay(newScore);
                RecomputeSnapshotInBackground();
            }
        }
        finally
        {
            _polling = false;
        }
    }

    private void ShowNewPlay(PlayScoreResult play)
    {
        NewPlayAccuracyText = $"{play.Accuracy * 100:F2}% (SC J{OverlayService.Judge})";
        NewPlayRatingText = $"Rating {play.Overall:F2} - counted towards {_overlay.Profiles.ActiveProfileName}";

        NewPlaySsr.Clear();
        foreach (var (skillset, value) in play.Ssr)
            NewPlaySsr.Add(new SkillsetRow(skillset, value));

        HasNewPlay = true;
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

            if (current.Chart.Hash != _currentChartHash)
            {
                _currentChartHash = current.Chart.Hash;
                RefreshChartScores();
            }
        }

        if (current.DifficultyReady)
        {
            _pendingDifficultyKey = "";
            DifficultyStatusText = "";
            DifficultyOpacity = 1.0;
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

        // The values on screen still belong to the previous song until this
        // returns. Dimming them says so without the flicker of blanking them.
        DifficultyOpacity = 0.35;

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
                    DifficultyOpacity = 1.0;
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

            RecomputeSnapshotInBackground();
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

    // =========================================================================
    // PROFILE SNAPSHOT + THE VIEWS BUILT FROM IT
    // =========================================================================

    private void RecomputeSnapshotInBackground()
    {
        if (_snapshotComputeInProgress)
        {
            // Switching profile (or setting a play) mid-computation has to be
            // honoured, not dropped - otherwise the numbers on screen belong to
            // whichever profile happened to win the race.
            _snapshotRecomputeQueued = true;
            return;
        }

        _snapshotComputeInProgress = true;
        _snapshotRecomputeQueued = false;
        IsComputingRating = true;

        Task.Run(() =>
        {
            try
            {
                var progress = new Progress<(int done, int total)>(p =>
                    Dispatcher.UIThread.Post(() => RatingProgressText = $"Rating scores {p.done}/{p.total}..."));

                var snapshot = _overlay.ComputeProfileSnapshot(progress);

                Dispatcher.UIThread.Post(() =>
                {
                    ApplySnapshot(snapshot);
                    RatingProgressText = "";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => RatingProgressText = $"Rating failed: {ex.Message}");
            }
            finally
            {
                _snapshotComputeInProgress = false;

                Dispatcher.UIThread.Post(() =>
                {
                    if (_snapshotRecomputeQueued)
                    {
                        RecomputeSnapshotInBackground();
                        return;
                    }

                    IsComputingRating = false;
                });
            }
        });
    }

    private void ApplySnapshot(ProfileSnapshot snapshot)
    {
        PlayerRatingOverall = snapshot.Rating.Overall;

        foreach (var row in PlayerRatingSkillsets)
        {
            if (snapshot.Rating.Skillsets.TryGetValue(row.Name, out var value))
                row.Value = value;
        }

        RefreshChartScores();
        RefreshTopPlays();
        RefreshGraph();
    }

    private void RefreshChartScores()
    {
        ChartScores.Clear();

        var plays = _overlay.GetScoresForChart(_currentChartHash);
        var chart = _overlay.LookupChart(_currentChartHash);

        foreach (var play in plays)
            ChartScores.Add(new ScoreRow(play, chart, play.Overall));

        ChartScoresEmptyText = plays.Count > 0
            ? ""
            : _overlay.Snapshot == null
                ? "Scores appear once your plays have been rated."
                : $"No rated plays on this chart in {_overlay.Profiles.ActiveProfileName}.";
    }

    private void RefreshTopPlays()
    {
        TopPlays.Clear();

        var plays = _overlay.GetTopPlays(_selectedSkillset, TopPlayCount);

        int rank = 1;
        foreach (var play in plays)
        {
            var chart = _overlay.LookupChart(play.ChartId);
            TopPlays.Add(new ScoreRow(play, chart, play.SsrFor(_selectedSkillset), rank++));
        }

        TopPlaysEmptyText = plays.Count > 0
            ? ""
            : $"No rated plays in {_overlay.Profiles.ActiveProfileName} yet.";
    }

    private void RefreshGraph()
    {
        var history = _overlay.Snapshot?.History ?? new List<RatingHistoryPoint>();

        ClearGraphHover();

        if (history.Count < 2)
        {
            _graphSamples = new List<RatingHistoryPoint>();
            GraphPoints = new List<Point>();
            GraphAreaPoints = new List<Point>();
            HasGraph = false;

            GraphSummary = history.Count == 1
                ? "One rated play so far - the graph needs at least two."
                : $"No rated plays in {_overlay.Profiles.ActiveProfileName} yet.";
            GraphReadoutText = "";
            return;
        }

        double min = history.Min(p => p.Overall);
        double max = history.Max(p => p.Overall);
        double span = max - min;

        // A rating that barely moves would otherwise draw a line pinned to one
        // edge; pad the range so the curve always has room around it.
        double padding = Math.Max(span * 0.1, 0.05);
        min -= padding;
        max += padding;
        span = max - min;

        int lastPlay = history[^1].PlayNumber;
        double xSpan = Math.Max(1, lastPlay - 1);

        var points = history
            .Select(p => new Point(
                GraphWidth * (p.PlayNumber - 1) / xSpan,
                GraphHeight - GraphHeight * (p.Overall - min) / span))
            .ToList();

        _graphSamples = history;
        GraphPoints = points;

        // Close the curve down to the baseline so it can be filled - a filled
        // area reads as a trend far more quickly than a bare 1px line.
        var area = new List<Point>(points) { new(points[^1].X, GraphHeight), new(points[0].X, GraphHeight) };
        GraphAreaPoints = area;

        HasGraph = true;

        GraphLabel0 = FormatAxisValue(max);
        GraphLabel1 = FormatAxisValue(max - span / GraphBands);
        GraphLabel2 = FormatAxisValue(max - span * 2 / GraphBands);
        GraphLabel3 = FormatAxisValue(max - span * 3 / GraphBands);
        GraphLabel4 = FormatAxisValue(min);

        GraphStartLabel = "Play 1";
        GraphEndLabel = $"Play {lastPlay:N0}";

        GraphSummary = $"{history.Count:N0} points across {lastPlay:N0} plays ({_overlay.Profiles.ActiveProfileName})";
        GraphReadoutText = DescribePoint(history[^1], "Now");
    }

    private static string FormatAxisValue(double value) => value.ToString("F1");

    private static string DescribePoint(RatingHistoryPoint point, string? prefix = null)
    {
        var when = DateTimeOffset.FromUnixTimeMilliseconds(point.Timestamp).LocalDateTime;
        string label = prefix ?? $"Play {point.PlayNumber:N0}";

        return $"{label}  -  {point.Overall:F2}  -  {when:d MMM yyyy}";
    }

    /// <summary>
    /// Snaps the pointer to the nearest plotted sample and reports it. Called on
    /// every pointer move over the plot; a linear scan over at most
    /// <see cref="PlayerRatingService.HistoryResolution"/> points is nothing.
    /// </summary>
    public void UpdateGraphHover(double x)
    {
        if (!HasGraph || _graphSamples.Count == 0 || GraphPoints.Count != _graphSamples.Count)
            return;

        int nearest = 0;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < GraphPoints.Count; i++)
        {
            double distance = Math.Abs(GraphPoints[i].X - x);

            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            nearest = i;
        }

        var point = GraphPoints[nearest];

        HoverLineLeft = point.X;
        HoverDotLeft = point.X - HoverDotRadius;
        HoverDotTop = point.Y - HoverDotRadius;
        GraphReadoutText = DescribePoint(_graphSamples[nearest]);
        HasHover = true;
    }

    public void ClearGraphHover()
    {
        HasHover = false;

        if (_graphSamples.Count > 0)
            GraphReadoutText = DescribePoint(_graphSamples[^1], "Now");
    }

    public void DismissNewPlay() => HasNewPlay = false;
}
