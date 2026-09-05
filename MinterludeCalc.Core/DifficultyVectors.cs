using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinterludeCalc
{
    /// <summary>
    /// One known-good difficulty calculation: an exact input, and what the
    /// reference implementation returned for it.
    /// </summary>
    public class DifficultyVector
    {
        public string ChartId { get; set; } = "";
        public int Keys { get; set; }
        public float Rate { get; set; }
        public double Goal { get; set; }

        /// <summary>
        /// The exact note stream that was fed in. Optional: omitted by default
        /// because it dwarfs everything else in the file, in which case the
        /// verifier re-derives it from charts.db by ChartId + Rate.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<MsdNote>? Notes { get; set; }

        /// <summary>Skillset name to MSD value, as the reference produced it.</summary>
        public Dictionary<string, double> Expected { get; set; } = new();
    }

    public class DifficultyVectorFile
    {
        public string Reference { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public List<DifficultyVector> Vectors { get; set; } = new();
    }

    /// <summary>Per-skillset error across a whole verification run.</summary>
    public class SkillsetError
    {
        public string Skillset { get; set; } = "";
        public int Samples { get; set; }
        public double MaxAbsolute { get; set; }
        public double MaxRelative { get; set; }
        public double MeanAbsolute { get; set; }
        public string WorstCase { get; set; } = "";
    }

    public class VectorVerificationReport
    {
        public int Total { get; set; }
        public int Compared { get; set; }
        public int Errored { get; set; }
        public int WithinTolerance { get; set; }
        public double Tolerance { get; set; }
        public List<SkillsetError> BySkillset { get; set; } = new();
        public List<string> Failures { get; set; } = new();

        public bool Passed => Errored == 0 && Compared > 0 && WithinTolerance == Compared;
    }

    /// <summary>
    /// Generates and checks reference vectors for a difficulty calculator.
    ///
    /// This exists because MinaCalc cannot be ported by inspection: it's a large
    /// pile of empirically-tuned constants where a single transposed digit
    /// produces numbers that look entirely plausible and are wrong - and wrong
    /// here means every player rating, every top-play list and every cached
    /// score in <see cref="ScoreResultCache"/> is quietly off. So the reference
    /// implementation (the msd tool, i.e. Etterna's own C++) is dumped against
    /// real charts first, and any replacement is diffed against that.
    /// </summary>
    public static class DifficultyVectors
    {
        public static readonly float[] DefaultRates = { 1.0f, 1.15f, 1.4f };
        public static readonly double[] DefaultGoals = { 0.93, 0.95, 0.99 };

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>
        /// Runs <paramref name="reference"/> over a spread of charts and records
        /// what it returned. Charts are sampled deterministically so the same
        /// library always produces the same set.
        /// </summary>
        public static DifficultyVectorFile Generate(
            InterludeNativeChartReader charts,
            IDifficultyCalculator reference,
            int chartCount = 60,
            bool embedNotes = false,
            IReadOnlyList<float>? rates = null,
            IReadOnlyList<double>? goals = null,
            IProgress<(int done, int total, string message)>? progress = null)
        {
            rates ??= DefaultRates;
            goals ??= DefaultGoals;

            var sampled = SampleCharts(charts.GetAllChartIds(), chartCount);
            var file = new DifficultyVectorFile
            {
                Reference = "msd (Etterna MinaCalc, standalone build)",
                GeneratedUtc = DateTime.UtcNow.ToString("o")
            };

            int total = sampled.Count * rates.Count * goals.Count;
            int done = 0;

            foreach (var (chartId, keys) in sampled)
            {
                ChartNoteData chart;

                try
                {
                    chart = charts.GetChartNoteData(chartId);
                }
                catch
                {
                    // Chart row is unreadable - nothing to generate from.
                    done += rates.Count * goals.Count;
                    continue;
                }

                foreach (var rate in rates)
                {
                    var notes = InterludeNativeChartReader.ToMsdNotes(chart, rate);

                    // A chart that produces no notes at this rate would only add
                    // a vector that tests nothing.
                    if (notes.Count == 0)
                    {
                        done += goals.Count;
                        continue;
                    }

                    foreach (var goal in goals)
                    {
                        done++;
                        progress?.Report((done, total, $"{chartId[..Math.Min(8, chartId.Length)]} @ {rate:0.00}x goal {goal:0.00}"));

                        try
                        {
                            var expected = reference.Calculate(notes, goal, keys);

                            file.Vectors.Add(new DifficultyVector
                            {
                                ChartId = chartId,
                                Keys = keys,
                                Rate = rate,
                                Goal = goal,
                                Notes = embedNotes ? notes : null,
                                Expected = expected
                            });
                        }
                        catch
                        {
                            // The reference failing on a chart tells us nothing
                            // about a candidate implementation - skip it rather
                            // than record a failure as if it were expected.
                        }
                    }
                }
            }

            return file;
        }

        /// <summary>
        /// Runs <paramref name="candidate"/> over recorded vectors and reports how
        /// far off it is. Charts are re-derived from charts.db when the vectors
        /// don't embed their notes.
        /// </summary>
        public static VectorVerificationReport Verify(
            DifficultyVectorFile file,
            IDifficultyCalculator candidate,
            InterludeNativeChartReader? charts = null,
            double tolerance = 1e-4,
            IProgress<(int done, int total, string message)>? progress = null)
        {
            var report = new VectorVerificationReport
            {
                Total = file.Vectors.Count,
                Tolerance = tolerance
            };

            var errors = new Dictionary<string, SkillsetError>();
            var noteCache = new Dictionary<string, ChartNoteData>();

            int done = 0;

            foreach (var vector in file.Vectors)
            {
                done++;
                progress?.Report((done, file.Vectors.Count, vector.ChartId));

                List<MsdNote>? notes = vector.Notes;

                if (notes == null)
                {
                    if (charts == null)
                    {
                        report.Errored++;
                        report.Failures.Add($"{Describe(vector)}: vector has no embedded notes and no chart database was supplied.");
                        continue;
                    }

                    try
                    {
                        if (!noteCache.TryGetValue(vector.ChartId, out var chart))
                        {
                            chart = charts.GetChartNoteData(vector.ChartId);
                            noteCache[vector.ChartId] = chart;
                        }

                        notes = InterludeNativeChartReader.ToMsdNotes(chart, vector.Rate);
                    }
                    catch (Exception ex)
                    {
                        report.Errored++;
                        report.Failures.Add($"{Describe(vector)}: could not rebuild notes ({ex.Message}).");
                        continue;
                    }
                }

                Dictionary<string, double> actual;

                try
                {
                    actual = candidate.Calculate(notes, vector.Goal, vector.Keys);
                }
                catch (Exception ex)
                {
                    report.Errored++;
                    report.Failures.Add($"{Describe(vector)}: threw {ex.GetType().Name} - {ex.Message}");
                    continue;
                }

                report.Compared++;
                bool within = true;

                foreach (var (skillset, expected) in vector.Expected)
                {
                    if (!actual.TryGetValue(skillset, out double got))
                    {
                        within = false;
                        report.Failures.Add($"{Describe(vector)}: missing skillset '{skillset}'.");
                        continue;
                    }

                    double absolute = Math.Abs(got - expected);
                    double relative = Math.Abs(expected) < 1e-9 ? absolute : absolute / Math.Abs(expected);

                    if (!errors.TryGetValue(skillset, out var error))
                        errors[skillset] = error = new SkillsetError { Skillset = skillset };

                    error.Samples++;
                    error.MeanAbsolute += absolute;

                    if (absolute > error.MaxAbsolute)
                    {
                        error.MaxAbsolute = absolute;
                        error.MaxRelative = relative;
                        error.WorstCase = $"{Describe(vector)} expected {expected:F6} got {got:F6}";
                    }

                    if (relative > tolerance)
                        within = false;
                }

                if (within)
                    report.WithinTolerance++;
                else if (report.Failures.Count < 40)
                    report.Failures.Add($"{Describe(vector)}: outside tolerance.");
            }

            foreach (var error in errors.Values)
            {
                if (error.Samples > 0)
                    error.MeanAbsolute /= error.Samples;
            }

            report.BySkillset = errors.Values.OrderBy(e => e.Skillset).ToList();
            return report;
        }

        private static string Describe(DifficultyVector vector) =>
            $"{vector.ChartId[..Math.Min(8, vector.ChartId.Length)]} {vector.Rate:0.00}x goal {vector.Goal:0.00}";

        /// <summary>
        /// Spreads the sample across the library rather than taking the first N,
        /// so the vectors cover easy and hard, short and long - and does it by a
        /// fixed stride so the same library always yields the same sample.
        /// </summary>
        private static List<(string ChartId, int Keys)> SampleCharts(List<(string ChartId, int Keys)> all, int count)
        {
            var ordered = all.OrderBy(c => c.ChartId, StringComparer.Ordinal).ToList();

            if (count <= 0 || ordered.Count <= count)
                return ordered;

            int stride = ordered.Count / count;
            var sampled = new List<(string, int)>(count);

            for (int i = 0; i < ordered.Count && sampled.Count < count; i += stride)
                sampled.Add(ordered[i]);

            return sampled;
        }

        public static void Save(DifficultyVectorFile file, string path)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(directory))
                System.IO.Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
        }

        public static DifficultyVectorFile Load(string path)
        {
            return JsonSerializer.Deserialize<DifficultyVectorFile>(File.ReadAllText(path))
                ?? throw new InvalidDataException($"'{path}' is not a difficulty vector file.");
        }
    }
}
