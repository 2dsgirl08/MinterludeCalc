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
    internal class InterludeNativeChartReader
    {
        private readonly string _databasePath;

        public InterludeNativeChartReader(string gameWorkingDirectory)
        {
            _databasePath = Path.Combine(gameWorkingDirectory, "Songs", "charts.db");
        }

        public bool DatabaseExists => File.Exists(_databasePath);

        /// <summary>
        /// Loads a chart directly from charts.db by its hash and converts it
        /// into the same MsdNote shape the .osu/.sm parsers produce.
        /// </summary>
        public List<MsdNote> GetNotes(string hash, float rate)
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

            return DecodeChartBlob(blob, keys, rate);
        }

        // =====================================================================
        // Binary format (written by Prelude's Chart.WriteToStreamHeadless):
        //
        //   Notes: TimeArray<NoteRow>
        //   BPM:   TimeArray<BPM>      (not needed here - note times are already absolute ms)
        //   SV:    TimeArray<float>    (not needed here)
        //
        // TimeArray<T>:
        //   Int32  count
        //   repeat count times: { Single time_ms; T data; }
        //
        // NoteRow:
        //   UInt16 column_bitmask
        //   for each set bit, ascending column order: Byte note_type
        //     (1 = Normal, 2 = HoldHead, 3 = HoldBody, 4 = HoldTail)
        //
        // We only need the Notes section, so we stop reading once it's consumed.
        // =====================================================================

        private const byte NoteTypeNormal = 1;
        private const byte NoteTypeHoldHead = 2;
        private const byte NoteTypeHoldBody = 3;
        private const byte NoteTypeHoldTail = 4;

        private static List<MsdNote> DecodeChartBlob(byte[] blob, int keys, float rate)
        {
            using var stream = new MemoryStream(blob);
            using var reader = new BinaryReader(stream);

            var result = new List<MsdNote>();

            int noteRowCount = reader.ReadInt32();

            for (int i = 0; i < noteRowCount; i++)
            {
                float timeMs = reader.ReadSingle();
                ushort columnMask = reader.ReadUInt16();

                int outMask = 0;

                for (int column = 0; column < keys; column++)
                {
                    if ((columnMask & (1 << column)) == 0)
                        continue;

                    byte noteType = reader.ReadByte();

                    // Only discrete hit events go into the mask - a hold body
                    // is a continuous state between the head and tail, not a
                    // hit, so (unlike the raw '!=0' check the .sm parser uses)
                    // we deliberately exclude it here.
                    if (noteType == NoteTypeNormal || noteType == NoteTypeHoldHead || noteType == NoteTypeHoldTail)
                        outMask |= 1 << column;
                }

                if (outMask != 0)
                {
                    result.Add(new MsdNote
                    {
                        notes = outMask,
                        time = Math.Round(timeMs / 1000.0 / rate, 4)
                    });
                }
            }

            // BPM/SV sections follow in the stream but aren't needed - note
            // times are already absolute milliseconds, so we stop here.
            return result.OrderBy(x => x.time).ToList();
        }
    }
}