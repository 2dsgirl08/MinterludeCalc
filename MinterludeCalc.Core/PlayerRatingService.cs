using System.Text.Json.Serialization;
using MinterludeCalc.Scoring;

namespace MinterludeCalc
{
    public class PlayScoreResult
    {
        public string ChartId { get; set; } = "";
        public long ScoreId { get; set; }
        public long Timestamp { get; set; }
        public float Rate { get; set; }
        public double Accuracy { get; set; }
        public Dictionary<string, double> Ssr { get; set; } = new();

        // Derived, so kept out of the cache file - they'd just be dead weight in
        // it, and JSON has no setter to read them back into anyway.

        /// <summary>This play's overall SSR - the single number that represents it in a list.</summary>
        [JsonIgnore]
        public double Overall => Ssr.TryGetValue(PlayerRating.OverallName, out var value) ? value : 0.0;

        [JsonIgnore]
        public DateTime PlayedAtLocal => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).LocalDateTime;

        public double SsrFor(string skillset) => Ssr.TryGetValue(skillset, out var value) ? value : 0.0;
    }

    public class PlayerRatingResult
    {
        public double Overall { get; set; }
        public Dictionary<string, double> Skillsets { get; set; } = new();
    }

    /// <summary>One point on the "rating over time" curve: the player rating as of that play.</summary>
    public class RatingHistoryPoint
    {
        public int PlayNumber { get; set; }
        public long Timestamp { get; set; }
        public double Overall { get; set; }
    }

    /// <summary>
    /// Everything derived from one profile's plays, computed in a single pass so
    /// the rating, the score lists, the top plays and the graph can't disagree
    /// with each other.
    /// </summary>
    public class ProfileSnapshot
    {
        public string ProfileId { get; set; } = ProfileStore.MainProfileId;
        public string ProfileName { get; set; } = ProfileStore.MainProfileName;
        public PlayerRatingResult Rating { get; set; } = new();

        /// <summary>Every scored play in the profile, oldest first.</summary>
        public List<PlayScoreResult> Chronological { get; set; } = new();

        /// <summary>One entry per chart - the play the rating is actually built from.</summary>
        public List<PlayScoreResult> BestPerChart { get; set; } = new();

        public List<RatingHistoryPoint> History { get; set; } = new();
    }

    /// <summary>
    /// Ties everything together: decodes a score's replay, runs it through the
    /// SC J4 scoring engine to get a real accuracy %, feeds that % into MinaCalc
    /// as the goal to get that play's SSR, then aggregates every chart's best
    /// play across the whole library using Etterna's exact rating formula.
    ///
    /// Note: unlike Etterna's nocc/cc PB distinction (StepMania's "chord
    /// cohesion" doesn't have a direct Interlude equivalent), this uses a
    /// single PB per chart - whichever play has the highest accuracy.
    /// </summary>
    public class PlayerRatingService
    {
        /// <summary>
        /// How many points the rating graph is sampled down to. Each point costs
        /// a full re-aggregation, so this bounds the graph's cost by itself
        /// rather than by how much the player has played.
        /// </summary>
        public const int HistoryResolution = 250;

        private readonly InterludeNativeChartReader _charts;
        private readonly ScoresDatabaseReader _scores;
        private readonly IDifficultyCalculator _minaCalc;
        private readonly int _judge;
        private readonly ScoreResultCache? _cache;

        public PlayerRatingService(
            InterludeNativeChartReader charts,
            ScoresDatabaseReader scores,
            IDifficultyCalculator minaCalc,
            int judge = 4,
            ScoreResultCache? cache = null)
        {
            _charts = charts;
            _scores = scores;
            _minaCalc = minaCalc;
            _judge = judge;
            _cache = cache;
        }

        /// <summary>Scores a single play: decode replay + chart, run SC J4, get SSR at that play's own accuracy.</summary>
        public PlayScoreResult ComputeScoreResult(ScoreRecord score)
        {
            if (_cache != null && _cache.TryGet(score.Id, score.Timestamp, out var cached))
                return cached;

            var chart = _charts.GetChartNoteData(score.ChartId);

            // Mirror swaps column i with Keys-1-i before the player ever sees
            // the chart (see Prelude's Mirror.fs), so a mirrored play's
            // replay only lines up against a mirrored copy of the chart - on
            // the raw chart, every column comparison is wrong and both the
            // accuracy and the MSD end up wrong too.
            if (ScoreMods.HasMirror(score.Mods))
                chart = chart.Mirror();

            var replay = Replay.Decode(score.ReplayBlob);
            var scored = ScoringEngine.Score(chart, replay, score.Rate, _judge);

            var msdNotes = InterludeNativeChartReader.ToMsdNotes(chart, score.Rate);
            var ssr = _minaCalc.Calculate(msdNotes, goal: scored.Accuracy, keys: chart.Keys);

            var result = new PlayScoreResult
            {
                ChartId = score.ChartId,
                ScoreId = score.Id,
                Timestamp = score.Timestamp,
                Rate = score.Rate,
                Accuracy = scored.Accuracy,
                Ssr = ssr
            };

            _cache?.Put(result);
            return result;
        }

        /// <summary>
        /// Scores every play (optionally only those a profile claims), oldest
        /// first. Individually expensive but cached per play, so the first run
        /// is the slow one and everything after it is a file read.
        /// </summary>
        public List<PlayScoreResult> ComputeAllScoreResults(
            IProgress<(int done, int total)>? progress = null,
            Func<ScoreRecord, bool>? filter = null)
        {
            // Index first, replays second: a play that's already in the cache
            // never needs its replay read at all, and the blobs dwarf everything
            // else in that table.
            IEnumerable<ScoreRecord> scores = _scores.GetScoreIndex().Where(s => !s.IsFailed);

            if (filter != null)
                scores = scores.Where(filter);

            var ordered = scores.OrderBy(s => s.Timestamp).ThenBy(s => s.Id).ToList();
            var results = new List<PlayScoreResult>(ordered.Count);

            int done = 0;
            foreach (var entry in ordered)
            {
                done++;
                progress?.Report((done, ordered.Count));

                if (_cache != null && _cache.TryGet(entry.Id, entry.Timestamp, out var cached))
                {
                    results.Add(cached);
                    continue;
                }

                try
                {
                    var score = _scores.GetScoreById(entry.Id);
                    if (score == null)
                        continue;

                    results.Add(ComputeScoreResult(score));
                }
                catch
                {
                    // Chart no longer in the library, corrupt replay, etc. - skip it.
                }
            }

            _cache?.Save();
            return results;
        }

        /// <summary>
        /// Everything one profile's views need, from a single pass over its plays.
        /// </summary>
        public ProfileSnapshot ComputeProfileSnapshot(
            string profileId,
            string profileName,
            Func<ScoreRecord, bool>? filter,
            IProgress<(int done, int total)>? progress = null)
        {
            var chronological = ComputeAllScoreResults(progress, filter);
            var bestPerChart = BestPerChart(chronological);

            return new ProfileSnapshot
            {
                ProfileId = profileId,
                ProfileName = profileName,
                Chronological = chronological,
                BestPerChart = bestPerChart,
                Rating = AggregateFromBestPlays(bestPerChart),
                History = BuildRatingHistory(chronological)
            };
        }

        /// <summary>
        /// Recomputes player rating from every score in scores.db. This is
        /// expensive the first time (decodes + rescores every replay in the
        /// library) - call it in the background, not on every UI tick.
        /// </summary>
        public PlayerRatingResult ComputePlayerRating(
            IProgress<(int done, int total)>? progress = null,
            Func<ScoreRecord, bool>? filter = null)
        {
            return AggregateFromBestPlays(BestPerChart(ComputeAllScoreResults(progress, filter)));
        }

        /// <summary>
        /// Best (highest-accuracy) play per chart - all 7 skillsets travel
        /// together from that one play, matching Etterna's PB semantics.
        /// </summary>
        public static List<PlayScoreResult> BestPerChart(IEnumerable<PlayScoreResult> results)
        {
            var best = new Dictionary<string, PlayScoreResult>();

            foreach (var result in results)
            {
                if (!best.TryGetValue(result.ChartId, out var existing) || result.Accuracy > existing.Accuracy)
                    best[result.ChartId] = result;
            }

            return best.Values.ToList();
        }

        /// <summary>Aggregation step split out separately so a UI can cache best-per-chart and only re-aggregate on new plays.</summary>
        public static PlayerRatingResult AggregateFromBestPlays(IEnumerable<PlayScoreResult> bestPerChart)
        {
            var plays = bestPerChart.ToList();
            var result = new PlayerRatingResult();
            var skillsetRatings = new List<double>();

            foreach (var skillset in PlayerRating.SkillsetNames)
            {
                var values = plays
                    .Where(p => p.Ssr.ContainsKey(skillset))
                    .Select(p => p.Ssr[skillset])
                    .ToList();

                double rating = values.Count == 0 ? 0.0 : PlayerRating.AggregateSkillsetRating(values);
                result.Skillsets[skillset] = rating;
                skillsetRatings.Add(rating);
            }

            result.Overall = skillsetRatings.Count == 0 ? 0.0 : PlayerRating.AggregateOverallRating(skillsetRatings);
            return result;
        }

        /// <summary>
        /// Replays the profile's history in order, re-aggregating as each play
        /// lands, to get "what was my rating after play N". Sampled down to
        /// <see cref="HistoryResolution"/> points - the curve is smooth at any
        /// useful zoom, and the cost stays flat as the library grows.
        /// </summary>
        public static List<RatingHistoryPoint> BuildRatingHistory(
            IReadOnlyList<PlayScoreResult> chronological,
            int resolution = HistoryResolution)
        {
            var points = new List<RatingHistoryPoint>();

            if (chronological.Count == 0)
                return points;

            var best = new Dictionary<string, PlayScoreResult>();
            int step = Math.Max(1, chronological.Count / Math.Max(1, resolution));

            for (int i = 0; i < chronological.Count; i++)
            {
                var play = chronological[i];

                if (!best.TryGetValue(play.ChartId, out var existing) || play.Accuracy > existing.Accuracy)
                    best[play.ChartId] = play;

                bool isLast = i == chronological.Count - 1;

                // Always keep the first and last points so the curve spans the
                // player's actual history rather than the sampling grid.
                if (i % step != 0 && !isLast)
                    continue;

                points.Add(new RatingHistoryPoint
                {
                    PlayNumber = i + 1,
                    Timestamp = play.Timestamp,
                    Overall = AggregateFromBestPlays(best.Values).Overall
                });
            }

            return points;
        }

        /// <summary>
        /// The plays contributing most to one skillset (or Overall), best first.
        /// Ranked over best-per-chart rather than raw plays, so a chart you've
        /// run twenty times takes one slot instead of twenty.
        /// </summary>
        public static List<PlayScoreResult> TopPlays(IEnumerable<PlayScoreResult> bestPerChart, string skillset, int count = 25)
        {
            return bestPerChart
                .Where(p => p.SsrFor(skillset) > 0)
                .OrderByDescending(p => p.SsrFor(skillset))
                .Take(count)
                .ToList();
        }
    }
}
