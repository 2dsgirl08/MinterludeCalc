namespace MinterludeCalc
{
    /// <summary>
    /// Etterna's player-rating aggregation, ported directly from
    /// src/Etterna/Singletons/ScoreManager.cpp (AggregateSSRs / AggregateSkillsets
    /// / CalcPlayerRating). This is an iterative equilibrium search, NOT a simple
    /// top-N weighted sum - see the recursive halving-step search below.
    /// </summary>
    public static class PlayerRating
    {
        /// <summary>
        /// Skillset order matches Etterna/MinaCalc's Skillset enum and the
        /// standalone msd tool's JSON output keys (Overall is excluded from
        /// the per-skillset aggregation, then re-derived from the other 7).
        /// </summary>
        public static readonly string[] SkillsetNames =
        {
            "Stream", "Jumpstream", "Handstream", "Stamina", "JackSpeed", "Chordjack", "Technical"
        };

        /// <summary>
        /// Aggregates a set of SSR values for one skillset (i.e. every chart PB's
        /// SSR for that skillset) into a single rating, then applies Etterna's
        /// 1.05x scale factor and clamps to [0, 100].
        /// </summary>
        public static double AggregateSkillsetRating(IReadOnlyList<double> ssrValues)
        {
            double rating = AggregateEquilibrium(ssrValues) * 1.05;
            return Math.Clamp(rating, 0.0, 100.0);
        }

        /// <summary>
        /// Aggregates the 7 already-computed skillset ratings into the final
        /// overall Player Rating, applying Etterna's 1.125x scale factor.
        /// </summary>
        public static double AggregateOverallRating(IReadOnlyList<double> skillsetRatings)
        {
            return AggregateEquilibrium(skillsetRatings) * 1.125;
        }

        /// <summary>
        /// The actual recursive equilibrium search shared by both AggregateSSRs
        /// and AggregateSkillsets in the C++ source - identical algorithm, just
        /// applied to a different list of input values.
        /// </summary>
        private static double AggregateEquilibrium(IReadOnlyList<double> values)
        {
            return Recurse(values, rating: 0.0, resolution: 10.24, iteration: 1);
        }

        private static double Recurse(IReadOnlyList<double> values, double rating, double resolution, int iteration)
        {
            double sum;

            do
            {
                rating += resolution;
                sum = 0.0;

                foreach (var v in values)
                {
                    double contribution = 2.0 / Erfc(0.1 * (v - rating)) - 2.0;
                    if (contribution > 0.0)
                        sum += contribution;
                }
            }
            while (Math.Pow(2.0, rating * 0.1) < sum);

            if (iteration == 11)
                return rating;

            return Recurse(values, rating - resolution, resolution / 2.0, iteration + 1);
        }

        /// <summary>
        /// Complementary error function - .NET has no built-in erf/erfc, so this
        /// uses the standard Abramowitz &amp; Stegun 7.1.26 approximation
        /// (max error ~1.5e-7), which is more than accurate enough here.
        /// </summary>
        public static double Erfc(double x) => 1.0 - Erf(x);

        public static double Erf(double x)
        {
            double sign = x < 0 ? -1.0 : 1.0;
            x = Math.Abs(x);

            const double a1 = 0.254829592;
            const double a2 = -0.284496736;
            const double a3 = 1.421413741;
            const double a4 = -1.453152027;
            const double a5 = 1.061405429;
            const double p = 0.3275911;

            double t = 1.0 / (1.0 + p * x);
            double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

            return sign * y;
        }
    }
}
