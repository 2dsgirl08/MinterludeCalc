using Spectre.Console;
using DrawingColor = System.Drawing.Color;

namespace MinterludeCalc
{
    public class Application
    {
        private readonly OverlayService _overlay = new(MinaCalc.DefaultToolPath());

        private PlayerRatingResult? _playerRating;
        private volatile bool _ratingComputeInProgress;

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
            _overlay.Attach();
            Console.WriteLine("Attached!");

            Console.WriteLine("Retrieving and caching charts... (this might take a bit!)");
            _overlay.RefreshChartLibrary();
            Console.WriteLine($"Retrieved {_overlay.ChartReader.Charts.Count} charts.");

            RecomputePlayerRatingInBackground();

            Monitor();
        }

        private void RecomputePlayerRatingInBackground()
        {
            if (_ratingComputeInProgress)
                return;

            _ratingComputeInProgress = true;

            Task.Run(() =>
            {
                try
                {
                    var progress = new Progress<(int done, int total)>(p =>
                    {
                        try { Console.Title = $"MinterludeCalc - rating scores {p.done}/{p.total}"; }
                        catch { /* Console.Title isn't supported on all platforms */ }
                    });

                    _playerRating = _overlay.ComputePlayerRating(progress);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Player rating computation failed: {ex.Message}");
                }
                finally
                {
                    _ratingComputeInProgress = false;
                }
            });
        }

        private void PrintPlayerRating()
        {
            if (_playerRating == null)
            {
                Console.WriteLine("Player rating: (computing...)");
                return;
            }

            AnsiConsole.MarkupLine($"[bold]Player Rating: {_playerRating.Overall:F2}[/]");
            foreach (var (skillset, value) in _playerRating.Skillsets)
            {
                var color = GetMsdColor(value);
                AnsiConsole.MarkupLine($"  {skillset}: [rgb({color.R},{color.G},{color.B})]{value:F2}[/]");
            }
        }

        public void Monitor()
        {
            string lastChartKey = "";
            string lastError = "";
            bool reportedDisconnect = false;

            while (true)
            {
                try
                {
                    if (!_overlay.IsAttached)
                    {
                        // Interlude was closed (or we lost the runtime): every
                        // address we resolved belongs to a dead process, so
                        // rebuild rather than keep reading garbage.
                        if (!reportedDisconnect)
                        {
                            Console.WriteLine("Lost Interlude - waiting for it to come back...");
                            reportedDisconnect = true;
                        }

                        _overlay.Detach();
                        _overlay.Attach();
                        _overlay.RefreshChartLibrary();

                        lastChartKey = "";
                        reportedDisconnect = false;
                        Console.WriteLine("Reattached!");
                    }

                    var current = _overlay.GetCurrentChart();
                    // DifficultyReady is part of the key so the display refreshes
                    // once a calculation that failed earlier finally lands.
                    string chartKey = current == null ? "" : $"{current.Chart.Hash}:{current.Rate:0.###}:{current.DifficultyReady}";

                    if (current != null && chartKey != lastChartKey)
                    {
                        Console.Clear();
                        AnsiConsole.MarkupLine($"[bold]{current.Chart.Title} - {current.Chart.Difficulty}[/] @ {current.Rate:0.00}x");
                        Console.WriteLine($"Difficulty (95% SC J{OverlayService.Judge} reference):");

                        if (!current.DifficultyReady)
                        {
                            Console.WriteLine("  (unavailable - MinaCalc failed for this chart, retrying shortly)");
                        }

                        foreach (var (skillset, value) in current.Difficulty)
                        {
                            var color = GetMsdColor(value);
                            AnsiConsole.MarkupLine($"  {skillset}: [rgb({color.R},{color.G},{color.B})]{value:F4}[/]");
                        }

                        Console.WriteLine();
                        PrintPlayerRating();

                        lastChartKey = chartKey;
                    }

                    lastError = "";

                    var newScore = _overlay.CheckForNewScore();
                    if (newScore != null)
                    {
                        Console.WriteLine();
                        AnsiConsole.MarkupLine($"[bold yellow]New play![/] Accuracy: {newScore.Accuracy * 100:F2}% (SC J{OverlayService.Judge})");
                        foreach (var (skillset, value) in newScore.Ssr)
                        {
                            var color = GetMsdColor(value);
                            AnsiConsole.MarkupLine($"  {skillset}: [rgb({color.R},{color.G},{color.B})]{value:F2}[/]");
                        }

                        // A new score can change your PB for that chart, so the
                        // overall player rating needs recomputing.
                        RecomputePlayerRatingInBackground();
                    }
                }
                catch (Exception ex)
                {
                    // While Interlude is closed this loop would otherwise print
                    // the same failure twice a second forever.
                    if (ex.Message != lastError)
                    {
                        lastError = ex.Message;
                        Console.WriteLine($"Error: {ex.Message}");
                    }
                }

                Thread.Sleep(500);
            }
        }
    }
}
