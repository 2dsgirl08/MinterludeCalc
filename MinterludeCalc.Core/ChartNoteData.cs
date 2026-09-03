namespace MinterludeCalc
{
    /// <summary>Interlude's raw per-note-type values, as stored in the Chart blob.</summary>
    public static class NoteType
    {
        public const byte Nothing = 0;
        public const byte Normal = 1;
        public const byte HoldHead = 2;
        public const byte HoldBody = 3;
        public const byte HoldTail = 4;
    }

    /// <summary>One row of notes at a given absolute chart time (raw, unscaled ms).</summary>
    public readonly struct ChartNoteRow
    {
        public readonly float TimeMs;
        public readonly byte[] Columns; // Columns[i] = NoteType for column i

        public ChartNoteRow(float timeMs, byte[] columns)
        {
            TimeMs = timeMs;
            Columns = columns;
        }
    }

    /// <summary>
    /// Full-fidelity decode of a chart's Notes section - every column's exact
    /// NoteType at every row, unlike InterludeNativeChartReader's MsdNote
    /// conversion (which collapses this down to a simple hit/no-hit bitmask
    /// for MinaCalc). The scoring engine needs this full fidelity to pair up
    /// hold heads with their tails.
    /// </summary>
    public class ChartNoteData
    {
        public int Keys { get; }
        public ChartNoteRow[] Notes { get; }

        public ChartNoteData(int keys, ChartNoteRow[] notes)
        {
            Keys = keys;
            Notes = notes;
        }

        // =====================================================================
        // Binary format (written by Prelude's Chart.WriteToStreamHeadless):
        //
        //   Notes: TimeArray<NoteRow>
        //   BPM:   TimeArray<BPM>      (not needed here)
        //   SV:    TimeArray<float>    (not needed here)
        //
        // TimeArray<T>:
        //   Int32  count
        //   repeat count times: { Single time_ms; T data; }
        //
        // NoteRow:
        //   UInt16 column_bitmask
        //   for each set bit, ascending column order: Byte note_type
        // =====================================================================

        public static ChartNoteData Decode(byte[] blob, int keys)
        {
            using var stream = new MemoryStream(blob);
            using var reader = new BinaryReader(stream);

            int noteRowCount = reader.ReadInt32();
            var rows = new ChartNoteRow[noteRowCount];

            for (int i = 0; i < noteRowCount; i++)
            {
                float timeMs = reader.ReadSingle();
                ushort columnMask = reader.ReadUInt16();

                var columns = new byte[keys];

                for (int column = 0; column < keys; column++)
                {
                    if ((columnMask & (1 << column)) != 0)
                        columns[column] = reader.ReadByte();
                }

                rows[i] = new ChartNoteRow(timeMs, columns);
            }

            // BPM/SV sections follow but aren't needed by anything that
            // consumes this type, so we stop reading here.
            return new ChartNoteData(keys, rows);
        }
    }
}
