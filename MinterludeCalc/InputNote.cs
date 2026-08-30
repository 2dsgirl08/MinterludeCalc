using System.Text.Json.Serialization;

internal class InputNote
{
    [JsonPropertyName("notes")]
    public int Notes { get; set; }

    [JsonPropertyName("time")]
    public double Time { get; set; }
}