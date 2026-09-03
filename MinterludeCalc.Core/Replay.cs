using System.IO.Compression;

namespace MinterludeCalc
{
    /// <summary>
    /// One input frame: the full set of currently-pressed columns at a given
    /// time. Time is relative to the chart's first note (not wall-clock zero),
    /// per Prelude.Gameplay.Replays' own convention - see Replay.fs.
    /// </summary>
    public readonly struct ReplayFrame
    {
        public readonly float Time;
        public readonly ushort PressedKeys;

        public ReplayFrame(float time, ushort pressedKeys)
        {
            Time = time;
            PressedKeys = pressedKeys;
        }
    }

    /// <summary>
    /// Decodes the gzip-compressed replay BLOB stored per-row in Interlude's
    /// scores.db (Prelude.Gameplay.Replays.Replay.WriteToStream format):
    ///   gzip( Int32 count; repeat count times { Single time_ms; UInt16 keys; } )
    /// </summary>
    public static class Replay
    {
        public static ReplayFrame[] Decode(byte[] blob)
        {
            using var compressed = new MemoryStream(blob);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip);

            int count = reader.ReadInt32();
            var frames = new ReplayFrame[count];

            for (int i = 0; i < count; i++)
            {
                float time = reader.ReadSingle();
                ushort keys = reader.ReadUInt16();
                frames[i] = new ReplayFrame(time, keys);
            }

            return frames;
        }
    }
}
