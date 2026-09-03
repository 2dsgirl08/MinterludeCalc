using MinterludeCalc.Scoring;

namespace MinterludeCalc
{
    public class PlayScoreResult
    {
        public string ChartId { get; set; } = "";
        public long ScoreId { get; set; }
        public double Accuracy { get; set; }
        public Dictionary<string, double> Ssr { get; set; } = new();
    }

    public class PlayerRatingResult
    {
        public double Overall { get; set; }
        public Dictionary<string, double> Skillsets { get; set; } = new();
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
        private readonly InterludeNativeChartReader _charts;
        private readonly ScoresDatabaseReader _scores;
        private readonly MinaCalc _minaCalc;
        private readonly int _judge;

        public PlayerRatingService(InterludeNativeChartReader charts, ScoresDatabaseReader scores, MinaCalc minaCalc, int judge = 4)
        {
            _charts = charts;
            _scores = scores;
            _minaCalc = minaCalc;
            _judge = judge;
        }

        /// <summary>Scores a single play: decode replay + chart, run SC J4, get SSR at that play's own accuracy.</summary>
        public PlayScoreResult ComputeScoreResult(ScoreRecord score)
        {
            var chart = _charts.GetChartNoteData(score.ChartId);
            var replay = Replay.Decode(score.ReplayBlob);
            var scored = ScoringEngine.Score(chart, replay, score.Rate, _judge);

            var msdNotes = InterludeNativeChartReader.ToMsdNotes(chart, score.Rate);
            var ssr = _minaCalc.Calculate(msdNotes, goal: scored.Accuracy, keys: chart.Keys);

            return new PlayScoreResult
            {
                ChartId = score.ChartId,
                ScoreId = score.Id,
                Accuracy = scored.Accuracy,
                Ssr = ssr
            };
        }

        /// <summary>
        /// Recomputes player rating from every score in scores.db. This is
        /// expensive (decodes + rescoring every replay in the library) - call
        /// it in the background, not on every UI tick. See OverlayViewModel for
        /// an incremental-friendly usage pattern.
        /// </summary>
        public PlayerRatingResult ComputePlayerRating(IProgress<(int done, int total)>? progress = null)
        {
            var allScores = _scores.GetAllScores().Where(s => !s.IsFailed).ToList();

            // Best (highest-accuracy) play per chart - all 7 skillsets travel
            // together from that one play, matching Etterna's PB semantics.
            var bestPerChart = new Dictionary<string, PlayScoreResult>();

            int done = 0;
            foreach (var score in allScores)
            {
                done++;
                progress?.Report((done, allScores.Count));

                PlayScoreResult result;
                try
                {
                    result = ComputeScoreResult(score);
                }
                catch
                {
                    // Chart no longer in the library, corrupt replay, etc. - skip it.
                    continue;
                }

                if (!bestPerChart.TryGetValue(score.ChartId, out var existing) || result.Accuracy > existing.Accuracy)
                    bestPerChart[score.ChartId] = result;
            }

            return AggregateFromBestPlays(bestPerChart.Values);
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
    }
}
