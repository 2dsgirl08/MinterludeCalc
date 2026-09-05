using System.Text.Json;

namespace MinterludeCalc
{
    /// <summary>
    /// Reads the subset of Interlude's Mods column that actually matters for
    /// scoring/difficulty: mods that reorder or otherwise change which chart
    /// data a replay's column presses correspond to. Mods stores a
    /// `ModState` (Prelude's `Map&lt;string, int64&gt;`) as JSON, e.g.
    /// `{"mirror":0}` when mirror is on, `{}` when no mods are active -
    /// see prelude/src/Mods/ModState.fs and Mods.fs upstream.
    /// </summary>
    public static class ScoreMods
    {
        /// <summary>
        /// True if this score was played with the "mirror" mod (column i
        /// swapped with column Keys-1-i - see Prelude's Mirror.fs). Mirror is
        /// mutually exclusive with "shuffle"/"random"/"column_swap" upstream,
        /// so this alone is enough to know the chart needs reversing before
        /// scoring or feeding to MinaCalc; it says nothing about those other,
        /// currently-unhandled column-reordering mods.
        /// </summary>
        public static bool HasMirror(string? modsJson)
        {
            if (string.IsNullOrWhiteSpace(modsJson))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(modsJson);
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                       doc.RootElement.TryGetProperty("mirror", out _);
            }
            catch (JsonException)
            {
                // A malformed/unexpected Mods value shouldn't take the whole
                // score down - just fall back to "not mirrored" and let
                // scoring proceed on the raw chart.
                return false;
            }
        }
    }
}
