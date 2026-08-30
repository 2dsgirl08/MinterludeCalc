using Spectre.Console;
using DrawingColor = System.Drawing.Color;

namespace MinterludeCalc
{
    public class Application
    {
        private readonly ChartReader _chartReader = new();
        private readonly ChartParser _chartParser = new();
        private readonly MinaCalc _minaCalc = new("Tools/msd.exe");

        // Only known once we've attached (needs Interlude's working directory),
        // so it's created in Run() rather than at field-initialization time.
        private InterludeNativeChartReader? _nativeChartReader;

        static DrawingColor GetMsdColor(double msd)
        {
            msd = Math.Clamp(msd, 0, 35);

            DrawingColor[] colors =
            {
        DrawingColor.FromArgb(0, 255, 0),
        DrawingColor.FromArgb(255, 255, 0),
        DrawingColor.FromArgb(255, 165, 0),
        DrawingColor.FromArgb(255, 0, 0),
        DrawingColor.FromArgb(255, 0, 255)
    };

            double position = msd / 35.0 * (colors.Length - 1);
            int index = Math.Min((int)position, colors.Length - 2);
            double t = position - index;

            DrawingColor a = colors[index];
            DrawingColor b = colors[index + 1];

            return DrawingColor.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t)
            );
        }

        public void Run()
        {
            Console.WriteLine("MinterludeCalc by ken. ^_^");
            _chartReader.Attach();
            Console.WriteLine("Attached!");

            _nativeChartReader = new InterludeNativeChartReader(_chartReader.WorkingDirectory);

            if (!_nativeChartReader.DatabaseExists)
                Console.WriteLine("Warning: could not find Songs/charts.db - natively-stored charts (not linked to an osu!/StepMania file) won't be calculable.");

            Console.WriteLine("Retrieving and caching charts... (this might take a bit!)");
            _chartReader.GetCharts();
            Console.WriteLine($"Retrieved {_chartReader.Charts.Count} charts.");

            Monitor();
        }

        public void Monitor()
        {
            ChartInfo? lastChart = null;
            float lastRate = 1;

            while (true)
            {
                var selectedChart = _chartReader.GetSelectedChart();
                var rate = _chartReader.GetRate();

                if (selectedChart != null && rate != null && (selectedChart != lastChart || rate != lastRate))
                {
                    Console.Clear();

                    try
                    {
                        List<MsdNote> notes;

                        if (string.IsNullOrEmpty(selectedChart.File))
                        {
                            // No linked external .osu/.sm file - this chart lives
                            // natively in Interlude's own charts.db (e.g. created
                            // in-game or downloaded directly, rather than mounted
                            // from an osu!/StepMania library folder).
                            if (_nativeChartReader == null)
                                throw new InvalidOperationException("Native chart reader was not initialized.");

                            notes = _nativeChartReader.GetNotes(selectedChart.Hash, (float)rate);
                        }
                        else
                        {
                            notes = _chartParser.Parse(selectedChart.File, (float)rate, selectedChart.Difficulty);
                        }

                        var result = _minaCalc.Calculate(notes);
                        foreach (var value in result)
                        {
                            DrawingColor color = GetMsdColor(value.Value);

                            AnsiConsole.MarkupLine(
                                $"{value.Key}: [rgb({color.R},{color.G},{color.B})]{value.Value:F4}[/]"
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Calculation failed: {ex.Message}");
                    }

                    lastChart = selectedChart;
                    lastRate = (float)rate;
                }

                Thread.Sleep(100);
            }
        }
    }
}