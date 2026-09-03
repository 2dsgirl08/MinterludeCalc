namespace MinterludeCalc.Scoring
{
    /// <summary>
    /// Constants ported from prelude/src/Gameplay/Rulesets/SC.fs (SC.create).
    /// Judgement indices: 0 Marvellous, 1 Perfect, 2 Great, 3 Good, 4 Bad, 5 Miss.
    /// Deliberately hardcoded to SC J&lt;judge&gt; only - not a generic ruleset interpreter,
    /// since that's the one ruleset this tool needs to reproduce exactly.
    /// </summary>
    public class ScJ4Ruleset
    {
        public const int Marvellous = 0;
        public const int Perfect = 1;
        public const int Great = 2;
        public const int Good = 3;
        public const int Bad = 4;
        public const int Miss = 5;

        public readonly float PerfectWindow;     // ms, half-width of the "Perfect" window at this judge
        public readonly float MissPenaltyPoints;
        public readonly double[] AccuracyPoints; // indexed by judgement
        public readonly (float early, float late)?[] TimingWindows; // indexed by judgement; null = catches everything else (Miss)
        public readonly int DefaultJudgement = Miss;

        // HitMechanics.Interlude cbrush_threshold, from SC.fs's HitMechanics field.
        public const float CbrushWindow = 90.0f;

        // HoldMechanics.CombineHeadAndTail(HeadTailCombineRule.HeadJudgementOr(-180, 180, 3, 3))
        public const float ReleaseEarlyWindow = -180.0f;
        public const float ReleaseLateWindow = 180.0f;
        public const int JudgementIfDropped = Good;   // 3
        public const int JudgementIfOverheld = Good;  // 3

        public ScJ4Ruleset(int judge = 4)
        {
            if (judge < 2 || judge > 9)
                throw new ArgumentOutOfRangeException(nameof(judge), "Judge must be between 2 and 9.");

            PerfectWindow = judge == 9
                ? 9.0f
                : 45.0f * ((10.0f - judge) / 6.0f);

            MissPenaltyPoints = judge switch
            {
                2 => -0.4f,
                3 => -0.6f,
                4 => -1.0f,
                5 => -1.6f,
                6 => -2.6f,
                7 => -4.7f,
                8 => -10.0f,
                9 => -20.0f,
                _ => throw new ArgumentOutOfRangeException(nameof(judge))
            };

            AccuracyPoints = new double[] { 1.0, 0.9, 0.5, -0.5, MissPenaltyPoints, MissPenaltyPoints };

            float badHalfWidth = Math.Max(PerfectWindow * 4.0f, 180.0f);

            TimingWindows = new (float, float)?[]
            {
                (-PerfectWindow * 0.5f, PerfectWindow * 0.5f), // Marvellous
                (-PerfectWindow, PerfectWindow),               // Perfect
                (-PerfectWindow * 2.0f, PerfectWindow * 2.0f), // Great
                (-PerfectWindow * 3.0f, PerfectWindow * 3.0f), // Good
                (-badHalfWidth, badHalfWidth),                 // Bad
                null                                           // Miss - catches everything else
            };
        }

        /// <summary>Widest of all note judgement windows - the "you missed it" cutoff (Bad's window, since Miss has none).</summary>
        public (float early, float late) NoteWindows => TimingWindows[Bad]!.Value;

        public (float early, float late) ReleaseWindows => (ReleaseEarlyWindow, ReleaseLateWindow);

        /// <summary>Ported from Ruleset.member this.NoteWindows/ReleaseWindows -> LargestWindow, only the parts this engine needs.</summary>
        public bool BreaksCombo(int judgement) => judgement >= Good; // Good, Bad, Miss all break combo per SC.fs

        public int MsToJudgement(float delta)
        {
            int j = 0;
            while (j + 1 < TimingWindows.Length)
            {
                var window = TimingWindows[j];
                if (window.HasValue && delta >= window.Value.early && delta <= window.Value.late)
                    break;
                j++;
            }
            return j;
        }
    }
}
