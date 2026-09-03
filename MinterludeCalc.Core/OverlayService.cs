using System.Diagnostics.CodeAnalysis;

namespace MinterludeCalc
{
    public class CurrentChartInfo
    {
        public ChartInfo Chart { get; set; } = new();
        public float Rate { get; set; }
        public Dictionary<string, double> Difficulty { get; set; } = new(); // MinaCalc at DifficultyGoal

        /// <summary>
        /// False when the difficulty for this chart+rate hasn't been calculated
        /// yet (only possible when it was requested without computing). The
        /// chart title and rate are still valid and worth showing - see
        /// <see cref="OverlayService.GetCurrentChart"/>.
        /// </summary>
        public bool DifficultyReady { get; set; }
    }

    /// <summary>
    /// The single entry point the UI (console or Avalonia) talks to. Owns the
    /// ClrMD memory reader (for "what's currently selected"), charts.db,
    /// scores.db, and the SC J4 scoring engine, and caches the expensive parts
    /// (MinaCalc invocations, player rating) so the UI can poll cheaply.
    /// </summary>
    public class OverlayService
    {
        /// <summary>
        /// Fixed reference goal for song-select difficulty display. Not literally
        /// "95% under SC's formula" - MinaCalc has no notion of SC's judgement
        /// bands, this just picks a stricter threshold in its own Wife3-calibrated
        /// abstract model than Etterna's usual 93% convention. See PlayerRating
        /// for where real per-play percentages (computed via ScoringEngine) are
        /// used instead of this fixed constant.
        /// </summary>
        public const double DifficultyGoal = 0.95;
        public const int Judge = 4;

        /// <summary>How long to wait before retrying a chart+rate whose difficulty calculation failed.</summary>
        static readonly TimeSpan DifficultyRetryDelay = TimeSpan.FromSeconds(30);

        /// <summary>Floor on how often selecting an unknown chart may trigger a full library rescan.</summary>
        static readonly TimeSpan LibraryRescanCooldown = TimeSpan.FromSeconds(10);

        public readonly ChartReader ChartReader = new();
        public InterludeNativeChartReader NativeCharts { get; private set; } = null!;
        public ScoresDatabaseReader Scores { get; private set; } = null!;
        public MinaCalc MinaCalc { get; }
        public PlayerRatingService RatingService { get; private set; } = null!;

        // Difficulty results are read on the poll thread and written from
        // whichever background thread computed them, so this needs a lock.
        private readonly object _difficultyLock = new();
        private readonly Dictionary<string, Dictionary<string, double>> _difficultyCache = new();
        private readonly Dictionary<string, DateTime> _difficultyFailures = new();

        private long _lastSeenScoreId = -1;
        private DateTime _lastLibraryRescan = DateTime.MinValue;

        /// <summary>
        /// Why the last <see cref="GetCurrentChart"/> came back null, or null if
        /// it didn't. "Nothing is showing" is otherwise indistinguishable from
        /// "nothing is selected", which makes a stuck reader impossible to tell
        /// apart from an idle one.
        /// </summary>
        public string? LastReadIssue { get; private set; }

        public OverlayService(string msdToolPath)
        {
            MinaCalc = new MinaCalc(msdToolPath);
        }

        /// <summary>True while the memory reader still has a live handle on Interlude.</summary>
        public bool IsAttached => ChartReader.IsAttached;

        public void Attach()
        {
            ChartReader.Attach();
            NativeCharts = new InterludeNativeChartReader(ChartReader.WorkingDirectory);
            Scores = new ScoresDatabaseReader(ChartReader.WorkingDirectory);
            RatingService = new PlayerRatingService(NativeCharts, Scores, MinaCalc, Judge);

            // Don't alert on every future new score from before this session started.
            if (Scores.DatabaseExists)
                _lastSeenScoreId = Scores.GetMaxScoreId();
        }

        /// <summary>
        /// Releases the memory reader. The next <see cref="Attach"/> starts from
        /// scratch - needed when Interlude exits, since every address we resolved
        /// belongs to a process that no longer exists.
        /// </summary>
        public void Detach()
        {
            ChartReader.Detach();
            _lastLibraryRescan = DateTime.MinValue;
        }

        public void RefreshChartLibrary(bool rescan = false) => ChartReader.GetCharts(rescan);

        /// <summary>
        /// Currently selected chart + rate, or null if nothing is selected (or
        /// the selection couldn't be read this time).
        /// </summary>
        /// <param name="computeDifficulty">
        /// When true, blocks on MinaCalc for any chart+rate not already cached -
        /// that can take seconds, so a UI polling on a timer should pass false
        /// and follow up with <see cref="GetDifficulty"/> off the poll loop.
        /// Otherwise the chart is returned with DifficultyReady = false.
        /// </param>
        public CurrentChartInfo? GetCurrentChart(bool computeDifficulty = true)
        {
            var chart = ChartReader.GetSelectedChart();
            var rate = ChartReader.GetRate();

            if (rate == null)
            {
                LastReadIssue = "Can't read the rate setting from Interlude's memory.";
                return null;
            }

            if (chart == null)
            {
                // We read a hash but don't have it in the library snapshot -
                // almost always a chart imported since we cached it. Rescanning
                // (throttled) picks it up instead of leaving song select stuck
                // on whatever was showing before.
                if (ChartReader.LastSelectedHash != null && DateTime.UtcNow - _lastLibraryRescan > LibraryRescanCooldown)
                {
                    _lastLibraryRescan = DateTime.UtcNow;
                    RefreshChartLibrary(rescan: true);
                    chart = ChartReader.GetSelectedChart();
                }

                if (chart == null)
                {
                    LastReadIssue = ChartReader.LastSelectedHash == null
                        ? "Can't find the selected chart in Interlude's memory."
                        : "Selected chart isn't in the cached library.";
                    return null;
                }
            }

            LastReadIssue = null;

            var info = new CurrentChartInfo
            {
                Chart = chart,
                Rate = rate.Value
            };

            if (TryGetCachedDifficulty(chart.Hash, rate.Value, out var cached))
            {
                info.Difficulty = cached;
                info.DifficultyReady = true;
            }
            else if (computeDifficulty && !HasRecentDifficultyFailure(chart.Hash, rate.Value))
            {
                // A first failure propagates - the caller should see why. Once
                // it's on record, the chart still comes back (just without
                // numbers) rather than the selection being lost along with it.
                info.Difficulty = GetDifficulty(chart.Hash, rate.Value);
                info.DifficultyReady = true;
            }

            return info;
        }

        private static string DifficultyKey(string hash, float rate) => $"{hash}:{rate:0.###}";

        /// <summary>Did MinaCalc recently fail on this chart+rate? (Then it's not worth retrying yet.)</summary>
        private bool HasRecentDifficultyFailure(string hash, float rate)
        {
            lock (_difficultyLock)
            {
                return _difficultyFailures.TryGetValue(DifficultyKey(hash, rate), out var failedAt)
                    && DateTime.UtcNow - failedAt < DifficultyRetryDelay;
            }
        }

        /// <summary>Cache-only difficulty lookup - never runs MinaCalc, so it's safe on a poll loop.</summary>
        public bool TryGetCachedDifficulty(string hash, float rate, [MaybeNullWhen(false)] out Dictionary<string, double> difficulty)
        {
            lock (_difficultyLock)
                return _difficultyCache.TryGetValue(DifficultyKey(hash, rate), out difficulty);
        }

        /// <summary>
        /// Difficulty for a chart+rate, running MinaCalc if it isn't cached yet.
        /// Blocking and potentially slow - call it off the UI/poll thread.
        /// </summary>
        public Dictionary<string, double> GetDifficulty(string hash, float rate)
        {
            string key = DifficultyKey(hash, rate);

            lock (_difficultyLock)
            {
                if (_difficultyCache.TryGetValue(key, out var cached))
                    return cached;

                // A chart MinaCalc just failed or timed out on will keep failing
                // for now; retrying it on every poll would mean a multi-second
                // stall each time round the loop.
                if (_difficultyFailures.TryGetValue(key, out var failedAt)
                    && DateTime.UtcNow - failedAt < DifficultyRetryDelay)
                {
                    throw new InvalidOperationException($"Difficulty calculation for '{hash}' failed recently; not retrying yet.");
                }
            }

            Dictionary<string, double> difficulty;

            try
            {
                var notes = NativeCharts.GetNotes(hash, rate);
                difficulty = MinaCalc.Calculate(notes, goal: DifficultyGoal);
            }
            catch
            {
                lock (_difficultyLock)
                    _difficultyFailures[key] = DateTime.UtcNow;
                throw;
            }

            lock (_difficultyLock)
            {
                _difficultyCache[key] = difficulty;
                _difficultyFailures.Remove(key);
            }

            return difficulty;
        }

        /// <summary>
        /// Call this periodically (e.g. every second). Returns the newly-scored
        /// play if one or more new rows appeared in scores.db since the last
        /// call, otherwise null. Only reports the single most recent one even
        /// if several appeared at once (e.g. after an offline import).
        /// </summary>
        public PlayScoreResult? CheckForNewScore()
        {
            if (Scores == null || !Scores.DatabaseExists)
                return null;

            long maxId = Scores.GetMaxScoreId();
            if (maxId <= _lastSeenScoreId)
                return null;

            _lastSeenScoreId = maxId;

            var latest = Scores.GetMostRecentScore();
            if (latest == null)
                return null;

            return RatingService.ComputeScoreResult(latest);
        }

        /// <summary>
        /// Full player-rating recompute. Expensive (rescoring every replay in
        /// the library) - run this on a background thread, not the UI/poll loop.
        /// </summary>
        public PlayerRatingResult ComputePlayerRating(IProgress<(int done, int total)>? progress = null)
        {
            return RatingService.ComputePlayerRating(progress);
        }
    }
}
