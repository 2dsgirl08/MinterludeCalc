using System.Diagnostics;
using System.Text.Json;

namespace MinterludeCalc
{
    public class MinaCalc
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
        /// Runs the standalone msd tool against the given notes.
        /// </summary>
        /// <param name="goal">
        /// The score fraction (0-1) MinaCalc solves difficulty for - e.g. 0.95 for
        /// a fixed song-select difficulty reference, or a specific play's actual
        /// achieved accuracy % when computing that play's SSR.
        /// </param>
        public Dictionary<string, double> Calculate(List<MsdNote> notes, double goal = 0.93, int keys = 4)
        {
            if (!File.Exists(_msdPath))
                throw new FileNotFoundException($"MSD binary not found at '{_msdPath}'.");

            string json = JsonSerializer.Serialize(notes);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _msdPath,
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
