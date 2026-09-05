namespace MinterludeCalc
{
    /// <summary>
    /// A MinaCalc implementation. The rest of the app only ever needs "these
    /// notes, at this goal, on this many keys - give me the 7 skillsets plus
    /// Overall", so swapping the out-of-process <see cref="MinaCalc"/> tool for
    /// an in-process one is a one-line change at construction.
    /// </summary>
    public interface IDifficultyCalculator
    {
        /// <summary>
        /// Difficulty for a chart, as a map of skillset name to MSD value. Keys
        /// are the ones <see cref="PlayerRating.AllRatingNames"/> lists.
        /// </summary>
        /// <param name="goal">
        /// The score fraction (0-1) difficulty is solved for - e.g. 0.95 for a
        /// fixed song-select reference, or a specific play's achieved accuracy
        /// when computing that play's SSR.
        /// </param>
        Dictionary<string, double> Calculate(List<MsdNote> notes, double goal = 0.93, int keys = 4);
    }
}
