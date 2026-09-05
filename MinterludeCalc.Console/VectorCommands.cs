namespace MinterludeCalc
{
    /// <summary>
    /// The reference-vector workflow that a managed MinaCalc port is built
    /// against. Two commands:
    ///
    ///   --dump-vectors &lt;out.json&gt; --game-dir &lt;dir&gt; [--charts N] [--embed-notes]
    ///   --verify-vectors &lt;in.json&gt;  --game-dir &lt;dir&gt; [--tolerance 0.0001]
    ///
    /// Dump runs the real msd tool over a spread of your own charts and records
    /// what it returned. Verify runs a candidate implementation over the same
    /// inputs and reports how far off it is, per skillset. Neither needs
    /// Interlude to be running - they read charts.db directly.
    /// </summary>
    public static class VectorCommands
    {
        public static bool TryRun(string[] args)
        {
            if (args.Length == 0)
                return false;

            return args[0] switch
            {
                "--dump-vectors" => Dump(args),
                "--verify-vectors" => Verify(args),
                "--help" or "-h" => Help(),
                _ => false
            };
        }

        private static bool Help()
        {
            Console.WriteLine("MinterludeCalc console");
            Console.WriteLine();
            Console.WriteLine("  (no arguments)                      Live overlay/monitor mode (needs Interlude running)");
            Console.WriteLine("  --dump-vectors <out.json>           Record msd's output over a spread of your charts");
            Console.WriteLine("      --game-dir <dir>                Interlude working directory (contains Songs/charts.db)");
            Console.WriteLine("      --charts <n>                    How many charts to sample (default 60)");
            Console.WriteLine("      --embed-notes                   Make the file self-contained (much larger)");
            Console.WriteLine("  --verify-vectors <in.json>          Check a calculator against recorded vectors");
            Console.WriteLine("      --game-dir <dir>                Needed unless the vectors embed their notes");
            Console.WriteLine("      --tolerance <r>                 Max relative error to accept (default 0.0001)");
            return true;
        }

        private static bool Dump(string[] args)
        {
            string output = Positional(args, 0) ?? "vectors.json";
            string? gameDir = Option(args, "--game-dir");

            if (gameDir == null)
            {
                Console.WriteLine("--game-dir is required (the Interlude folder containing Songs/charts.db).");
                return true;
            }

            int chartCount = int.TryParse(Option(args, "--charts"), out int n) ? n : 60;
            bool embedNotes = args.Contains("--embed-notes");

            var charts = new InterludeNativeChartReader(gameDir);

            if (!charts.DatabaseExists)
            {
                Console.WriteLine($"No charts.db under '{gameDir}'.");
                return true;
            }

            var reference = new MinaCalc(MinaCalc.DefaultToolPath());

            Console.WriteLine($"Sampling {chartCount} charts x {DifficultyVectors.DefaultRates.Length} rates x {DifficultyVectors.DefaultGoals.Length} goals...");
            Console.WriteLine("This shells out to msd once per combination, so it takes a while.");

            var progress = new Progress<(int done, int total, string message)>(p =>
            {
                if (p.done % 10 == 0 || p.done == p.total)
                    Console.Write($"\r  {p.done}/{p.total}  {p.message}".PadRight(78));
            });

            var file = DifficultyVectors.Generate(charts, reference, chartCount, embedNotes, progress: progress);

            Console.WriteLine();

            if (file.Vectors.Count == 0)
            {
                Console.WriteLine($"No vectors were produced - is {MinaCalc.DefaultToolPath()} present and working?");
                return true;
            }

            DifficultyVectors.Save(file, output);

            Console.WriteLine($"Wrote {file.Vectors.Count} vectors to {Path.GetFullPath(output)}.");
            Console.WriteLine(embedNotes
                ? "Notes are embedded, so this file can be verified against without charts.db."
                : "Notes are not embedded - verifying needs --game-dir pointing at the same library.");

            return true;
        }

        private static bool Verify(string[] args)
        {
            string? input = Positional(args, 0);

            if (input == null || !File.Exists(input))
            {
                Console.WriteLine("Usage: --verify-vectors <in.json> [--game-dir <dir>] [--tolerance 0.0001]");
                return true;
            }

            string? gameDir = Option(args, "--game-dir");
            double tolerance = double.TryParse(Option(args, "--tolerance"), out double t) ? t : 1e-4;

            var file = DifficultyVectors.Load(input);
            var charts = gameDir == null ? null : new InterludeNativeChartReader(gameDir);

            // Until the managed port exists, the candidate is the reference
            // itself. That is not a real test of anything except the harness -
            // it should come back with zero error, and if it doesn't, the
            // problem is here rather than in any port.
            IDifficultyCalculator candidate = new MinaCalc(MinaCalc.DefaultToolPath());

            Console.WriteLine($"Verifying {file.Vectors.Count} vectors (reference: {file.Reference}).");

            var progress = new Progress<(int done, int total, string message)>(p =>
            {
                if (p.done % 10 == 0 || p.done == p.total)
                    Console.Write($"\r  {p.done}/{p.total}".PadRight(40));
            });

            var report = DifficultyVectors.Verify(file, candidate, charts, tolerance, progress);

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"Compared {report.Compared}/{report.Total}, {report.WithinTolerance} within {tolerance:G}, {report.Errored} errored.");
            Console.WriteLine();
            Console.WriteLine("  " + "Skillset".PadRight(12) + " " + "max abs".PadLeft(10)
                              + " " + "max rel".PadLeft(10) + " " + "mean abs".PadLeft(10));

            foreach (var error in report.BySkillset)
                Console.WriteLine($"  {error.Skillset,-12} {error.MaxAbsolute,10:F6} {error.MaxRelative,10:F6} {error.MeanAbsolute,10:F6}");

            if (report.Failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("First failures:");

                foreach (var failure in report.Failures.Take(15))
                    Console.WriteLine($"  {failure}");
            }

            Console.WriteLine();
            Console.WriteLine(report.Passed ? "PASS" : "FAIL");

            return true;
        }

        /// <summary>The nth argument that isn't a flag and isn't a flag's value.</summary>
        private static string? Positional(string[] args, int index)
        {
            int seen = 0;

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    // Skip this flag's value too, unless it's a bare switch.
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        i++;

                    continue;
                }

                if (seen++ == index)
                    return args[i];
            }

            return null;
        }

        private static string? Option(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);

            return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1]
                : null;
        }
    }
}
