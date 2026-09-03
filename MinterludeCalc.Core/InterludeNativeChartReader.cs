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
        /// (a hit/no-hit bitmask per row, with hold bodies excluded and times
        /// scaled to seconds at the given rate).
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

                    // Only discrete hit events go into the mask - a hold body
                    // is a continuous state between the head and tail, not a
                    // hit, so (unlike the raw '!=0' check the .sm parser uses)
                    // we deliberately exclude it here.
                    if (noteType == NoteType.Normal || noteType == NoteType.HoldHead || noteType == NoteType.HoldTail)
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
