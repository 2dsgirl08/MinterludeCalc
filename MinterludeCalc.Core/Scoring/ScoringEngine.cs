namespace MinterludeCalc.Scoring
{
    public class ScoreResult
    {
        public double Accuracy { get; set; }          // 0.0 - 1.0
        public int[] JudgementCounts { get; set; } = new int[6]; // Marvellous, Perfect, Great, Good, Bad, Miss
    }

    internal enum HitFlag : byte
    {
        Nothing,
        HitRequired,
        HitHoldRequired,
        HitAccepted,
        ReleaseRequired,
        ReleaseAccepted
    }

    internal enum HoldStateKind : byte
    {
        Nothing,
        Holding,
        Dropped,
        Regrabbed,
        MissedHeadDropped,
        MissedHeadRegrabbed
    }

    internal static class HoldStateKindExtensions
    {
        public static bool IsDropped(this HoldStateKind s) =>
            s is HoldStateKind.Dropped or HoldStateKind.Regrabbed or HoldStateKind.MissedHeadDropped or HoldStateKind.MissedHeadRegrabbed;

        public static bool IsMissedHead(this HoldStateKind s) =>
            s is HoldStateKind.MissedHeadDropped or HoldStateKind.MissedHeadRegrabbed;
    }

    internal class HitRow
    {
        public readonly float Time;
        public readonly float[] Deltas;   // per column, GameplayTime (ms, rate-independent once finalized)
        public readonly HitFlag[] Status; // per column

        public HitRow(float time, float[] deltas, HitFlag[] status)
        {
            Time = time;
            Deltas = deltas;
            Status = status;
        }
    }

    /// <summary>
    /// Replays a stored input (Prelude.Gameplay.Replays.Replay) against a chart's
    /// note data under the SC ruleset, reproducing Interlude's own
    /// GameplayEventProcessor + ScoreProcessor pipeline closely enough to compute
    /// an accurate final Accuracy percentage and judgement counts.
    ///
    /// NOT reproduced (out of scope - these don't affect Accuracy, only
    /// lamps/grades/combo display, which this engine doesn't need):
    ///  - combo / max combo / combo breaks tracking
    ///  - ghost tap judgement (SC's ruleset sets this to None anyway - zero effect)
    /// </summary>
    public class ScoringEngine
    {
        public static ScoreResult Score(ChartNoteData chart, ReplayFrame[] replay, float rate, int judge = 4)
        {
            if (chart.Notes.Length == 0)
                return new ScoreResult { Accuracy = 1.0 };

            var ruleset = new ScJ4Ruleset(judge);
            return new ScoringEngine(chart, ruleset, rate).Run(replay);
        }

        private readonly ChartNoteData _chart;
        private readonly ScJ4Ruleset _ruleset;
        private readonly float _rate;
        private readonly int _keys;
        private readonly HitRow[] _hitData;
        private readonly float _firstNote;

        private readonly float _noteEarlyRaw, _noteLateRaw;
        private readonly float _relEarlyRaw, _relLateRaw;
        private readonly float _noteEarlyScaled, _noteLateScaled;
        private readonly float _relEarlyScaled, _relLateScaled;
        private readonly float _earlyWindowScaled, _lateWindowScaled;

        private readonly HoldStateKind[] _holdState;
        private readonly int[] _holdHeadIndex;
        private readonly bool[] _keyDown;
        private int _expiredIndex;

        private double _pointsScored;
        private double _maxPossiblePoints;
        private readonly int[] _judgementCounts = new int[6];

        private ScoringEngine(ChartNoteData chart, ScJ4Ruleset ruleset, float rate)
        {
            _chart = chart;
            _ruleset = ruleset;
            _rate = rate;
            _keys = chart.Keys;

            (_noteEarlyRaw, _noteLateRaw) = ruleset.NoteWindows;
            (_relEarlyRaw, _relLateRaw) = ruleset.ReleaseWindows;

            _noteEarlyScaled = _noteEarlyRaw * rate;
            _noteLateScaled = _noteLateRaw * rate;
            _relEarlyScaled = _relEarlyRaw * rate;
            _relLateScaled = _relLateRaw * rate;

            _earlyWindowScaled = Math.Min(_relEarlyScaled, _noteEarlyScaled);
            _lateWindowScaled = Math.Max(_relLateScaled, _noteLateScaled);

            _hitData = BuildHitData(chart, _noteLateRaw, _relLateRaw);
            _firstNote = _hitData[0].Time;

            _holdState = new HoldStateKind[_keys];
            _holdHeadIndex = new int[_keys];
            _keyDown = new bool[_keys];
        }

        private static HitRow[] BuildHitData(ChartNoteData chart, float noteWindow, float releaseWindow)
        {
            var rows = new HitRow[chart.Notes.Length];

            for (int i = 0; i < chart.Notes.Length; i++)
            {
                var source = chart.Notes[i];
                var deltas = new float[chart.Keys];
                var status = new HitFlag[chart.Keys];

                for (int k = 0; k < chart.Keys; k++)
                    deltas[k] = noteWindow;

                for (int k = 0; k < chart.Keys; k++)
                {
                    byte noteType = source.Columns[k];
                    if (noteType == NoteType.Normal)
                        status[k] = HitFlag.HitRequired;
                    else if (noteType == NoteType.HoldHead)
                        status[k] = HitFlag.HitHoldRequired;
                    else if (noteType == NoteType.HoldTail)
                    {
                        status[k] = HitFlag.ReleaseRequired;
                        deltas[k] = releaseWindow;
                    }
                }

                rows[i] = new HitRow(source.TimeMs, deltas, status);
            }

            return rows;
        }

        private ScoreResult Run(ReplayFrame[] replay)
        {
            ushort current = 0;

            foreach (var frame in replay)
            {
                for (int k = 0; k < _keys; k++)
                {
                    bool wasDown = (current & (1 << k)) != 0;
                    bool nowDown = (frame.PressedKeys & (1 << k)) != 0;

                    if (wasDown && !nowDown)
                    {
                        _keyDown[k] = false;
                        HandleKeyUp(frame.Time, k);
                    }
                    else if (nowDown && !wasDown)
                    {
                        _keyDown[k] = true;
                        HandleKeyDown(frame.Time, k);
                    }
                }

                current = frame.PressedKeys;
            }

            // Flush: mark anything still unresolved at the end as missed.
            MissUnhitExpiredNotes(float.PositiveInfinity);

            double accuracy = _maxPossiblePoints == 0.0 ? 1.0 : _pointsScored / _maxPossiblePoints;

            return new ScoreResult
            {
                Accuracy = accuracy,
                JudgementCounts = _judgementCounts
            };
        }

        private void ScorePoints(double points, int judgement)
        {
            _maxPossiblePoints += 1.0;
            _pointsScored += points;
            _judgementCounts[judgement]++;
        }

        /// <summary>Ports ProcessRelease's HeadJudgementOr branch - the only hold rule SC J4 uses.</summary>
        private void ProcessReleaseEvent(bool missed, bool overhold, bool dropped, float headDelta, bool missedHead)
        {
            int headJudgement = _ruleset.MsToJudgement(headDelta);
            int judgement;

            if (missedHead && missed)
                judgement = _ruleset.DefaultJudgement;
            else if (overhold && !dropped)
                judgement = Math.Max(headJudgement, ScJ4Ruleset.JudgementIfOverheld);
            else if (dropped)
                judgement = Math.Max(headJudgement, ScJ4Ruleset.JudgementIfDropped);
            else
                judgement = headJudgement;

            ScorePoints(_ruleset.AccuracyPoints[judgement], judgement);
        }

        private void MissUnhitExpiredNotes(float chartTime)
        {
            float now = _firstNote + chartTime;
            float endOfSearch = now - _lateWindowScaled;

            while (_expiredIndex < _hitData.Length && _hitData[_expiredIndex].Time < endOfSearch)
            {
                var row = _hitData[_expiredIndex];

                for (int k = 0; k < _keys; k++)
                {
                    if (row.Status[k] == HitFlag.HitRequired)
                    {
                        ScorePoints(_ruleset.AccuracyPoints[_ruleset.DefaultJudgement], _ruleset.DefaultJudgement);
                        row.Status[k] = HitFlag.HitAccepted;
                    }
                    else if (row.Status[k] == HitFlag.HitHoldRequired)
                    {
                        // Head miss under CombineHeadAndTail scores nothing directly -
                        // its judgement gets folded into the tail's release judgement.
                        _holdState[k] = HoldStateKind.MissedHeadDropped;
                        _holdHeadIndex[k] = _expiredIndex;
                        row.Status[k] = HitFlag.HitAccepted;
                    }
                    else if (row.Status[k] == HitFlag.ReleaseRequired)
                    {
                        bool overhold =
                            (_holdState[k] == HoldStateKind.Regrabbed
                             || _holdState[k] == HoldStateKind.Holding
                             || _holdState[k] == HoldStateKind.MissedHeadRegrabbed)
                            && _keyDown[k];

                        bool dropped = _holdState[k].IsDropped();
                        bool missedHead = _holdState[k].IsMissedHead();
                        var headRow = _hitData[_holdHeadIndex[k]];

                        ProcessReleaseEvent(missed: true, overhold, dropped, headRow.Deltas[k], missedHead);
                        row.Status[k] = HitFlag.ReleaseAccepted;

                        if (_holdHeadIndex[k] < _expiredIndex)
                        {
                            _holdState[k] = HoldStateKind.Nothing;
                            _holdHeadIndex[k] = _expiredIndex;
                        }
                    }
                }

                _expiredIndex++;
            }
        }

        private void KillExistingHold(int k)
        {
            if (_holdState[k] == HoldStateKind.Nothing)
                return;

            int headIndex = _holdHeadIndex[k];
            var priorState = _holdState[k];
            int tailSearch = headIndex;

            while (tailSearch < _hitData.Length)
            {
                var row = _hitData[tailSearch];

                if (row.Status[k] == HitFlag.ReleaseAccepted)
                    break;

                if (row.Status[k] == HitFlag.ReleaseRequired)
                {
                    row.Status[k] = HitFlag.ReleaseAccepted;
                    _holdState[k] = HoldStateKind.Nothing;
                    _holdHeadIndex[k] = headIndex;

                    var headRow = _hitData[headIndex];
                    ProcessReleaseEvent(missed: true, overhold: false, dropped: true, headRow.Deltas[k], priorState.IsMissedHead());
                    break;
                }

                tailSearch++;
            }
        }

        private void HandleKeyDown(float chartTime, int k)
        {
            MissUnhitExpiredNotes(chartTime);
            float now = _firstNote + chartTime;

            int startIndex = _expiredIndex;
            while (startIndex < _hitData.Length && _hitData[startIndex].Time < now - _noteLateScaled)
                startIndex++;

            var (found, blocked, index, delta) = InterludeHitSearch(k, startIndex, now);

            if (blocked)
                return;

            if (found)
            {
                KillExistingHold(k);
                var row = _hitData[index];
                bool isHoldHead = row.Status[k] != HitFlag.HitRequired; // must be HitHoldRequired
                row.Status[k] = HitFlag.HitAccepted;
                row.Deltas[k] = delta / _rate;

                if (isHoldHead)
                {
                    _holdState[k] = HoldStateKind.Holding;
                    _holdHeadIndex[k] = index;
                    // Head hit under CombineHeadAndTail scores nothing directly.
                }
                else
                {
                    int judgement = _ruleset.MsToJudgement(row.Deltas[k]);
                    ScorePoints(_ruleset.AccuracyPoints[judgement], judgement);
                }

                return;
            }

            // Not found - either a ghost tap (no scoring effect for SC) or a regrab.
            switch (_holdState[k])
            {
                case HoldStateKind.MissedHeadDropped:
                    _holdState[k] = HoldStateKind.MissedHeadRegrabbed;
                    break;
                case HoldStateKind.Dropped:
                    _holdState[k] = HoldStateKind.Regrabbed;
                    break;
            }
        }

        private void HandleKeyUp(float chartTime, int k)
        {
            MissUnhitExpiredNotes(chartTime);
            float now = _firstNote + chartTime;

            if (_holdState[k] != HoldStateKind.Holding
                && _holdState[k] != HoldStateKind.Regrabbed
                && _holdState[k] != HoldStateKind.MissedHeadRegrabbed)
                return;

            int headIndex = _holdHeadIndex[k];
            var priorState = _holdState[k];

            int tailSearch = headIndex;
            float delta = 0f;
            int found = -1;

            while (tailSearch < _hitData.Length && _hitData[tailSearch].Time <= now - _earlyWindowScaled)
            {
                var row = _hitData[tailSearch];
                float d = now - row.Time;

                if (row.Status[k] == HitFlag.ReleaseAccepted)
                    break;

                if (row.Status[k] == HitFlag.ReleaseRequired)
                {
                    found = tailSearch;
                    delta = d;
                    break;
                }

                tailSearch++;
            }

            if (found >= 0 && delta >= _relEarlyScaled)
            {
                var tailRow = _hitData[found];
                tailRow.Status[k] = HitFlag.ReleaseAccepted;

                bool overhold;
                if (delta > _relLateScaled)
                {
                    overhold = true;
                }
                else
                {
                    tailRow.Deltas[k] = delta / _rate;
                    overhold = false;
                }

                var headRow = _hitData[headIndex];
                bool dropped = priorState.IsDropped();
                bool missedHead = priorState.IsMissedHead();

                ProcessReleaseEvent(missed: overhold, overhold, dropped, headRow.Deltas[k], missedHead);

                _holdState[k] = HoldStateKind.Nothing;
                _holdHeadIndex[k] = headIndex;
            }
            else
            {
                // Early release - hold dropped, nothing scored yet (scored when the
                // tail is eventually resolved, hit or missed).
                switch (priorState)
                {
                    case HoldStateKind.Holding:
                    case HoldStateKind.Regrabbed:
                        _holdState[k] = HoldStateKind.Dropped;
                        break;
                    case HoldStateKind.MissedHeadRegrabbed:
                        _holdState[k] = HoldStateKind.MissedHeadDropped;
                        break;
                }
            }
        }

        /// <summary>Ports HitMechanics.interlude - the NotePriority SC J4 uses.</summary>
        private (bool found, bool blocked, int index, float delta) InterludeHitSearch(int k, int startIndex, float now)
        {
            int i = startIndex;
            float closestBadDelta = _noteLateScaled;
            int closestIndex = -1;
            float closestDelta = _noteLateScaled;
            float cbrushWindow = ScJ4Ruleset.CbrushWindow * _rate;
            float endOfWindow = now - _noteEarlyScaled;

            while (i < _hitData.Length && _hitData[i].Time <= endOfWindow)
            {
                float delta = now - _hitData[i].Time;
                var row = _hitData[i];
                bool stop = false;

                if (row.Status[k] == HitFlag.HitRequired || row.Status[k] == HitFlag.HitHoldRequired)
                {
                    if (closestIndex < 0 || Math.Abs(closestDelta) > Math.Abs(delta))
                    {
                        closestIndex = i;
                        closestDelta = delta;
                    }

                    if (Math.Abs(closestDelta) < cbrushWindow)
                        stop = true;
                }
                else if (row.Status[k] == HitFlag.HitAccepted && row.Deltas[k] <= -ScJ4Ruleset.CbrushWindow)
                {
                    if (Math.Abs(closestBadDelta) > Math.Abs(delta))
                        closestBadDelta = delta;
                }

                i++;
                if (stop)
                    break;
            }

            if (closestIndex >= 0)
            {
                if (Math.Abs(closestBadDelta) < Math.Abs(closestDelta))
                    return (false, true, -1, 0f);

                return (true, false, closestIndex, closestDelta);
            }

            return (false, false, -1, 0f);
        }
    }
}
