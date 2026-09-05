using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace MinterludeCalc
{
    /// <summary>
    /// Persistent cache of per-play results, keyed by scores.db row id.
    ///
    /// Scoring one play means decoding its replay, running it through the SC J4
    /// engine and then shelling out to MinaCalc - so recomputing the whole
    /// library on every start (or every profile switch, or to draw a rating
    /// graph) is the difference between "instant" and "minutes". A play's result
    /// never changes once recorded, so it only ever has to be computed once -
    /// unless how it's computed changes, which is what CurrentVersion is for.
    ///
    /// The judge is part of the file's identity: change it and every cached
    /// accuracy is wrong, so the cache is dropped rather than trusted.
    /// </summary>
    public class ScoreResultCache
    {
        // Bump this whenever anything that changes a play's computed result
        // changes - not just the cache file's own schema. Two fixes have
        // needed this so far: the note-mask fix in
        // InterludeNativeChartReader.ToMsdNotes() (hold tails no longer
        // counted as notes), and mirror-mod handling in
        // PlayerRatingService.ComputeScoreResult (mirrored plays are now
        // scored against a mirrored chart instead of the raw one). The cache
        // has no way to know the *inputs* to a cached result changed, only
        // whether its own format did, so a stale cache would keep serving
        // pre-fix results forever otherwise.
        const int CurrentVersion = 3;

        private class CacheFile
        {
            public int Version { get; set; } = CurrentVersion;
            public int Judge { get; set; }
            public List<PlayScoreResult> Results { get; set; } = new();
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        private readonly string _path;
        private readonly int _judge;
        private readonly object _lock = new();
        private readonly Dictionary<long, PlayScoreResult> _byScoreId = new();
        private bool _dirty;

        public ScoreResultCache(int judge, string? path = null)
        {
            _judge = judge;
            _path = path ?? AppData.PathTo("score-cache.json");
            Load();
        }

        public int Count
        {
            get { lock (_lock) return _byScoreId.Count; }
        }

        /// <summary>
        /// Looks up a play's cached result. The timestamp has to match too:
        /// scores.db row ids are reused if the database is ever rebuilt, and a
        /// stale hit there would silently attribute one play's rating to another.
        /// </summary>
        public bool TryGet(long scoreId, long timestamp, [MaybeNullWhen(false)] out PlayScoreResult result)
        {
            lock (_lock)
            {
                if (_byScoreId.TryGetValue(scoreId, out var cached) && cached.Timestamp == timestamp)
                {
                    result = cached;
                    return true;
                }
            }

            result = null;
            return false;
        }

        public void Put(PlayScoreResult result)
        {
            lock (_lock)
            {
                _byScoreId[result.ScoreId] = result;
                _dirty = true;
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return;

                var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_path));

                if (file == null || file.Version != CurrentVersion || file.Judge != _judge)
                    return;

                lock (_lock)
                {
                    foreach (var result in file.Results)
                        _byScoreId[result.ScoreId] = result;
                }
            }
            catch
            {
                // A corrupt or half-written cache is not worth failing over -
                // it just means everything gets computed again.
            }
        }

        /// <summary>Writes the cache out if anything changed. Cheap to call after a batch; a no-op otherwise.</summary>
        public void Save()
        {
            CacheFile file;

            lock (_lock)
            {
                if (!_dirty)
                    return;

                file = new CacheFile { Judge = _judge, Results = _byScoreId.Values.ToList() };
                _dirty = false;
            }

            try
            {
                AppData.EnsureDirectory();

                // Write-then-move so an interrupted save can't leave a truncated
                // file where a good one used to be.
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(file, JsonOptions));
                File.Move(temporary, _path, overwrite: true);
            }
            catch
            {
                lock (_lock)
                    _dirty = true;
            }
        }
    }
}
