using System.Diagnostics;
using System.Text.Json;

namespace MinterludeCalc
{
    /// <summary>
    /// Difficulty via the standalone <c>msd</c> tool, one process per chart.
    /// Accurate (it is Etterna's own C++), but it needs a native binary built
    /// for each platform you want to run the overlay on.
    /// </summary>
    public class MinaCalc : IDifficultyCalculator
    {
        /// <summary>
        /// Generous, because this now bounds the whole exchange rather than just
        /// the tail of it: process start (which antivirus can make slow on the
        /// first run) plus feeding the chart in plus reading the result back.
        /// </summary>
        public const int DefaultTimeoutMs = 15000;

        private readonly string _msdPath;
        private readonly int _timeoutMs;

        public MinaCalc(string msdPath, int timeoutMs = DefaultTimeoutMs)
        {
            _msdPath = msdPath;
            _timeoutMs = timeoutMs;
        }

        /// <summary>
        /// The bundled tool's path for the OS this is currently running on,
        /// relative to the app's own directory: <c>Tools/win-x64/msd.exe</c> on
        /// Windows, <c>Tools/linux-x64/msd</c> on Linux. Only those two
        /// platforms ship a bundled build.
        /// </summary>
        public static string DefaultToolPath()
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine("Tools", "win-x64", "msd.exe");

            if (OperatingSystem.IsLinux())
                return Path.Combine("Tools", "linux-x64", "msd");

            throw new PlatformNotSupportedException(
                "No bundled msd build for this OS. MinterludeCalc ships standalone " +
                "MinaCalc builds for Windows (Tools/win-x64/msd.exe) and Linux " +
                "(Tools/linux-x64/msd) only; pass an explicit path to a build you " +
                "compiled yourself for anything else.");
        }

        /// <summary>
        /// Resolves a relative tool path against the working directory first (so
        /// a deliberate override still wins), then against the directory the app
        /// was deployed to. The build drops the tool at Tools/&lt;rid&gt;/msd(.exe)
        /// next to the built app, which is not the working directory when it's
        /// launched with `dotnet run`. Falls back to the path as given so a
        /// genuinely missing tool still reports the path that was asked for.
        /// </summary>
        private static string ResolveToolPath(string msdPath)
        {
            string resolved;

            if (Path.IsPathRooted(msdPath) || File.Exists(msdPath))
            {
                resolved = msdPath;
            }
            else
            {
                string besideApp = Path.Combine(AppContext.BaseDirectory, msdPath);
                resolved = File.Exists(besideApp) ? besideApp : msdPath;
            }

            // A zip extracted on Linux/macOS does not necessarily preserve the
            // executable bit, and .NET's Process.Start won't set it for you -
            // it'll just fail with "Permission denied". Fix that up once,
            // best-effort, rather than making every user chmod it by hand.
            if (!OperatingSystem.IsWindows() && File.Exists(resolved))
            {
                TryMarkExecutable(resolved);
            }

            return resolved;
        }

        private static void TryMarkExecutable(string path)
        {
            try
            {
                const UnixFileMode executableBits =
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

                UnixFileMode mode = File.GetUnixFileMode(path);
                if ((mode & executableBits) != executableBits)
                    File.SetUnixFileMode(path, mode | executableBits);
            }
            catch
            {
                // Best-effort: if this fails, Process.Start below will surface
                // a clear "Permission denied" instead, which is diagnosable.
            }
        }

        /// <summary>
        /// Runs the standalone msd tool against the given notes.
        /// </summary>
        /// <param name="goal">
        /// The score fraction (0-1) MinaCalc solves difficulty for - e.g. 0.95 for
        /// a fixed song-select difficulty reference, or a specific play's actual
        /// achieved accuracy % when computing that play's SSR.
        /// </param>
        public Dictionary<string, double> Calculate(List<MsdNote> notes, double goal = 0.93, int keys = 4)
        {
            string msdPath = ResolveToolPath(_msdPath);

            if (!File.Exists(msdPath))
                throw new FileNotFoundException($"MSD binary not found at '{msdPath}' (requested: '{_msdPath}').");

            string json = JsonSerializer.Serialize(notes);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = msdPath,
                    Arguments = $"--goal {goal.ToString(System.Globalization.CultureInfo.InvariantCulture)} --keys {keys}",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // Start draining both output pipes *before* writing anything. A
            // chart's JSON is much larger than the pipe buffer, so if the child
            // starts writing while we're still writing to its stdin and nobody
            // is reading, both sides block forever - and that deadlock happens
            // before the timeout below can do anything about it.
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            Task writeTask = Task.Run(async () =>
            {
                try
                {
                    await process.StandardInput.WriteAsync(json);
                    await process.StandardInput.FlushAsync();
                    process.StandardInput.Close();
                }
                catch
                {
                    // The child can legitimately exit before it has read all of
                    // stdin; a broken pipe here isn't the interesting failure,
                    // whatever it printed (or didn't) is.
                }
            });

            if (!process.WaitForExit(_timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
                catch
                {
                }

                throw new TimeoutException($"MSD calculation timed out after {_timeoutMs}ms.");
            }

            // The timed overload returns as soon as the process exits, without
            // waiting for the redirected streams to finish draining; the
            // parameterless one does.
            process.WaitForExit();

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();

            writeTask.GetAwaiter().GetResult();

            if (!string.IsNullOrWhiteSpace(error))
                Console.WriteLine($"MSD ERROR: {error}");

            int jsonStart = output.IndexOf('{');
            int jsonEnd = output.LastIndexOf('}');

            if (jsonStart == -1 || jsonEnd == -1 || jsonEnd < jsonStart)
                throw new InvalidOperationException("MSD returned no valid JSON.");

            string jsonOutput = output[jsonStart..(jsonEnd + 1)];

            return JsonSerializer.Deserialize<Dictionary<string, double>>(jsonOutput)
                ?? throw new InvalidOperationException("Could not parse MSD output.");
        }
    }
}
