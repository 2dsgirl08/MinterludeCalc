using Avalonia.Media;
using MinterludeCalc;

namespace MinterludeCalc.Overlay.ViewModels;

/// <summary>
/// One play as it appears in a list - the chart it was set on, what was scored,
/// and the rating that play is worth. Built fresh on every refresh, so it needs
/// no change notification of its own.
/// </summary>
public class ScoreRow
{
    public string Title { get; }
    public string Subtitle { get; }
    public string AccuracyText { get; }
    public double Rating { get; }
    public string RankText { get; }
    public bool HasRank => RankText.Length > 0;

    public string RatingText => Rating.ToString("F2");
    public IBrush RatingColor => MsdColor.ForValue(Rating);

    public ScoreRow(PlayScoreResult play, ChartInfo? chart, double rating, int rank = 0)
    {
        Title = chart?.Title ?? "<not in library>";

        string difficulty = chart?.Difficulty ?? play.ChartId[..Math.Min(8, play.ChartId.Length)];
        Subtitle = $"{difficulty} @ {play.Rate:0.00}x - {play.PlayedAtLocal:d MMM yyyy}";

        AccuracyText = $"{play.Accuracy * 100:F2}%";
        Rating = rating;
        RankText = rank > 0 ? $"{rank}." : "";
    }
}
