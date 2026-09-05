// Requires the Microsoft.Data.Sqlite NuGet package:
//   dotnet add package Microsoft.Data.Sqlite
using Microsoft.Data.Sqlite;

namespace MinterludeCalc
{
    /// <summary>
    /// Reads charts that live entirely inside Interlude's own database, rather
    /// than being linked to an external osu!/StepMania file on disk. Interlude
    /// stores its whole library in Songs/charts.db (a SQLite file), with each
    /// chart's actual note/BPM/SV data packed into a BLOB column.
    /// </summary>
    public class InterludeNativeChartReader
    {
        private readonly string _databasePath;

        public InterludeNativeChartReader(string gameWorkingDirectory)
        {
            _databasePath = Path.Combine(gameWorkingDirectory, "Songs", "charts.db");
        }

        public bool DatabaseExists => File.Exists(_databasePath);

        /// <summary>
        /// Every chart in the database, id and key count only - no note blobs.
        /// Used to pick a spread of real charts to generate reference vectors
        /// from, without needing Interlude to be running.
        /// </summary>
        public List<(string ChartId, int Keys)> GetAllChartIds()
        {
            if (!DatabaseExists)
                throw new FileNotFoundException($"Interlude chart database not found at '{_databasePath}'.");

            using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Keys FROM charts;";

            var results = new List<(string, int)>();

            using var reader = command.ExecuteReader();
            while (reader.Read())
                results.Add((reader.GetString(0), Convert.ToInt32(reader.GetValue(1))));

            return results;
        }

        /// <summary>Fetches and decodes a chart's full note data (for the scoring engine, MinaCalc, etc).</summary>
        public ChartNoteData GetChartNoteData(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                throw new ArgumentException("Chart hash was empty.", nameof(hash));

            if (!DatabaseExists)
                throw new FileNotFoundException($"Interlude chart database not found at '{_databasePath}'.");

            using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Keys, Chart FROM charts WHERE Id = @hash;";
            command.Parameters.AddWithValue("@hash", hash);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
                throw new InvalidDataException($"No chart with hash '{hash}' found in charts.db.");

            int keys = Convert.ToInt32(reader["Keys"]);
            byte[] blob = (byte[])reader["Chart"];

            return ChartNoteData.Decode(blob, keys);
        }

        /// <summary>
        /// Loads a chart directly from charts.db by its hash and converts it
        /// into the simplified MsdNote shape MinaCalc's difficulty rating expects
        /// (a hit/no-hit bitmask per row, with hold bodies AND hold tails
        /// excluded, and times scaled to seconds at the given rate).
        /// </summary>
        public List<MsdNote> GetNotes(string hash, float rate)
        {
            return ToMsdNotes(GetChartNoteData(hash), rate);
        }

        /// <summary>Pure transform, reusable when the caller has already decoded a ChartNoteData (e.g. PlayerRatingService).</summary>
        public static List<MsdNote> ToMsdNotes(ChartNoteData chart, float rate)
        {
            var result = new List<MsdNote>();

            foreach (var row in chart.Notes)
            {
                int mask = 0;

                for (int column = 0; column < chart.Keys; column++)
                {
                    byte noteType = row.Columns[column];

                    // Only the two note types that require a fresh input go
                    // into the mask, matching Etterna's own TapNote::IsNote()
                    // (type == Tap || type == HoldHead) - a hold body is a
                    // continuous state between the head and tail rather than
                    // a hit, and the tail is a release, not a press, so
                    // (unlike the raw '!=0' check the .sm parser uses) we
                    // deliberately exclude both here. Counting the tail as a
                    // note manufactures a phantom extra hit at every hold's
                    // release and can noticeably inflate the rating on
                    // hold-heavy charts.
                    if (noteType == NoteType.Normal || noteType == NoteType.HoldHead)
                        mask |= 1 << column;
                }

                if (mask != 0)
                {
                    result.Add(new MsdNote
                    {
                        notes = mask,
                        time = Math.Round(row.TimeMs / 1000.0 / rate, 4)
                    });
                }
            }

            return result.OrderBy(x => x.time).ToList();
        }
    }
}
