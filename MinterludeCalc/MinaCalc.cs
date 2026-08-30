using System.Diagnostics;
using System.Text.Json;

namespace MinterludeCalc
{
    internal class MinaCalc
    {
        private readonly string _msdPath;

        public MinaCalc(string msdPath)
        {
            _msdPath = msdPath;
        }

        public Dictionary<string, double> Calculate(List<MsdNote> notes)
        {
            if (!File.Exists(_msdPath))
                throw new FileNotFoundException(
                    $"MSD binary not found at '{_msdPath}'."
                );

            string json = JsonSerializer.Serialize(notes);

            //Console.WriteLine($"MSD INPUT: {json}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _msdPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();

            process.StandardInput.Write(json);
            process.StandardInput.Close();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }

                throw new TimeoutException("MSD calculation timed out.");
            }

            string output = outputTask.Result;
            string error = errorTask.Result;

            if (!string.IsNullOrWhiteSpace(error))
                Console.WriteLine($"MSD ERROR: {error}");

            Console.WriteLine($"MSD OUTPUT: {output}");

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