using System.Globalization;

namespace MinterludeCalc
{
    internal class ChartParser
    {
        public List<MsdNote> Parse(string filePath, float rate, string difficulty)
        {
            string extension = Path.GetExtension(filePath);

            if (extension.Equals(".osu", StringComparison.OrdinalIgnoreCase))
                return ParseOsu(filePath, rate);

            if (extension.Equals(".sm", StringComparison.OrdinalIgnoreCase))
                return ParseSm(filePath, rate, difficulty);

            throw new NotSupportedException(
                $"Unsupported chart format: '{extension}' {filePath}."
            );
        }

        private List<MsdNote> ParseOsu(string filePath, float rate)
        {
            var hitObjects = new List<(int X, int Time, int Type)>();

            bool inSection = false;

            int keyCount = 4;

            foreach (string rawLine in File.ReadLines(filePath))
            {
                string line = rawLine.Trim();

                if (line == "[HitObjects]")
                {
                    inSection = true;
                    continue;
                }

                if (!inSection || string.IsNullOrWhiteSpace(line))
                    continue;

                string[] parts = line.Split(',');

                int x = int.Parse(parts[0]);
                int time = int.Parse(parts[2]);
                int type = int.Parse(parts[3]);

                hitObjects.Add((x, time, type));
            }

            return OsuToEtternaRows(hitObjects, keyCount, rate);
        }

        private List<MsdNote> OsuToEtternaRows(
            List<(int X, int Time, int Type)> hitObjects,
            int keyCount,
            float rate)
        {
            var rows = new Dictionary<double, int>();

            double columnWidth = 512.0 / keyCount;

            foreach (var obj in hitObjects)
            {
                double time = Math.Round(
                    obj.Time / 1000.0 / rate,
                    4
                );

                int column = (int)(obj.X / columnWidth);

                rows[time] =
                    rows.GetValueOrDefault(time) |
                    (1 << column);
            }

            return rows
                .OrderBy(x => x.Key)
                .Select(x => new MsdNote
                {
                    notes = x.Value,
                    time = x.Key
                })
                .ToList();
        }

        private List<MsdNote> ParseSm(string filePath, float rate, string difficulty)
        {
            string text = File.ReadAllText(filePath);
            var bpms = ParseBpms(text);

            // difficulty comes in as something like "4K Challenge 26":
            // ... <difficulty name> <meter>. We only care about the last two tokens.
            string[] parts = difficulty.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
                throw new InvalidDataException($"Could not parse difficulty name/meter from '{difficulty}'.");

            string wantedDifficultyName = parts[^2];
            string wantedMeter = parts[^1];

            int searchFrom = 0;
            var availableDifficulties = new List<string>();

            while (true)
            {
                int notesIndex = text.IndexOf("#NOTES:", searchFrom, StringComparison.OrdinalIgnoreCase);

                if (notesIndex == -1)
                {
                    throw new InvalidDataException(
                        $"No dance-single #NOTES section found matching '{wantedDifficultyName} {wantedMeter}'. " +
                        $"Available difficulties: {string.Join(", ", availableDifficulties)}"
                    );
                }

                int sectionStart = notesIndex + "#NOTES:".Length;
                int endIndex = text.IndexOf(';', sectionStart);

                if (endIndex == -1)
                    throw new InvalidDataException("SM #NOTES section is missing ';'.");

                string notesSection = text[sectionStart..endIndex];
                searchFrom = endIndex + 1; // resume past this section on the next loop

                // dance-single:
                // beary605:
                // Challenge:
                // 26:
                // 0,0,0,0,0:
                // <note data>

                string[] fields = notesSection.Split(':');

                if (fields.Length < 6)
                    continue;

                string stepType = fields[0].Trim();

                if (!stepType.Equals("dance-single", StringComparison.OrdinalIgnoreCase))
                    continue;

                string chartDifficultyName = fields[2].Trim();
                string chartMeter = fields[3].Trim();

                availableDifficulties.Add($"{chartDifficultyName} {chartMeter}");

                bool nameMatches = chartDifficultyName.Equals(wantedDifficultyName, StringComparison.OrdinalIgnoreCase);
                bool meterMatches = chartMeter.Equals(wantedMeter, StringComparison.OrdinalIgnoreCase);

                if (!nameMatches || !meterMatches)
                    continue; // not the one we want - keep scanning for the next #NOTES:

                string noteData = string.Join(":", fields.Skip(5));

                var measures = noteData
                    .Split(',')
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                var result = new List<MsdNote>();
                double currentBeat = 0.0;

                foreach (string measure in measures)
                {
                    string[] rows = measure.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    if (rows.Length == 0)
                        continue;

                    for (int i = 0; i < rows.Length; i++)
                    {
                        string row = rows[i].Trim();

                        if (row.Length < 4)
                            continue;

                        int noteMask = 0;

                        for (int column = 0; column < 4; column++)
                        {
                            if (row[column] != '0')
                                noteMask |= 1 << column;
                        }

                        if (noteMask == 0)
                            continue;

                        double beat = currentBeat + (i / (double)rows.Length) * 4.0;
                        double time = BeatToSeconds(beat, bpms);

                        result.Add(new MsdNote
                        {
                            notes = noteMask,
                            time = Math.Round(time / rate, 4)
                        });
                    }

                    currentBeat += 4.0;
                }

                return result.OrderBy(x => x.time).ToList();
            }
        }

        private List<(double Beat, double Bpm)> ParseBpms(
            string text)
        {
            var result = new List<(double Beat, double Bpm)>();

            int index = 0;

            while (true)
            {
                index = text.IndexOf(
                    "#BPMS:",
                    index,
                    StringComparison.OrdinalIgnoreCase
                );

                if (index == -1)
                    break;

                int start = index + "#BPMS:".Length;

                int end = text.IndexOf(';', start);

                if (end == -1)
                    break;

                string bpmData = text[start..end];

                foreach (string entry in bpmData.Split(','))
                {
                    string[] parts = entry.Split('=');

                    if (parts.Length != 2)
                        continue;

                    if (!double.TryParse(
                            parts[0].Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double beat))
                        continue;

                    if (!double.TryParse(
                            parts[1].Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double bpm))
                        continue;

                    if (bpm <= 0)
                        continue;

                    result.Add((beat, bpm));
                }

                index = end + 1;
            }

            result.Sort(
                (a, b) => a.Beat.CompareTo(b.Beat)
            );

            if (result.Count == 0)
            {
                throw new InvalidDataException(
                    "SM file does not contain a valid #BPMS section."
                );
            }

            return result;
        }

        private double BeatToSeconds(
            double targetBeat,
            List<(double Beat, double Bpm)> bpms)
        {
            double seconds = 0.0;

            for (int i = 0; i < bpms.Count; i++)
            {
                double startBeat = bpms[i].Beat;
                double bpm = bpms[i].Bpm;

                double endBeat =
                    i + 1 < bpms.Count
                        ? bpms[i + 1].Beat
                        : targetBeat;

                if (targetBeat <= startBeat)
                    break;

                endBeat = Math.Min(
                    endBeat,
                    targetBeat
                );

                if (endBeat <= startBeat)
                    continue;

                double beatDuration = 60.0 / bpm;

                seconds +=
                    (endBeat - startBeat) *
                    beatDuration;

                if (endBeat >= targetBeat)
                    break;
            }

            return seconds;
        }
    }
}