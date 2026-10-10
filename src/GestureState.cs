using System;

namespace DuoClick
{
    internal enum MouseButtonKind
    {
        Left,
        Right
    }

    /// <summary>
    /// Result of one physical mouse-button transition. X/Y are desktop coordinates
    /// associated with the event that started or completed a chord.
    /// </summary>
    internal sealed class GestureTransition
    {
        internal bool Suppress { get; private set; }
        internal bool Started { get; private set; }
        internal bool Completed { get; private set; }
        internal bool AllReleased { get; private set; }
        internal long SessionId { get; private set; }
        internal int X { get; private set; }
        internal int Y { get; private set; }

        internal GestureTransition(bool suppress, bool started, bool completed,
            bool allReleased, long sessionId, int x, int y)
        {
            Suppress = suppress;
            Started = started;
            Completed = completed;
            AllReleased = allReleased;
            SessionId = sessionId;
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// Pure state machine for the left+right mouse chord. The first down remains
    /// forwarded and its matching up is also forwarded. The second down and its
    /// matching up are suppressed so the target application never sees an
    /// unmatched button transition.
    /// </summary>
    internal sealed class GestureState
    {
        private bool _leftDown;
        private bool _rightDown;
        private bool _leftSuppressed;
        private bool _rightSuppressed;
        private bool _active;
        private bool _cancelled;
        private bool _blockedUntilAllReleased;
        private long _sessionId;

        internal bool AllButtonsReleased
        {
            get { return !_leftDown && !_rightDown; }
        }

        internal long CancelCurrent()
        {
            if (!_active)
                return 0;

            _cancelled = true;
            return _sessionId;
        }

        internal GestureTransition Process(MouseButtonKind button, bool isDown,
            int x, int y, bool enabled)
        {
            bool isLeft = button == MouseButtonKind.Left;
            bool buttonDown = isLeft ? _leftDown : _rightDown;
            bool otherDown = isLeft ? _rightDown : _leftDown;

            if (isDown)
            {
                // Ignore duplicate down notifications. Swallowing them avoids
                // delivering a second down without a corresponding second up.
                if (buttonDown)
                    return Make(true, false, false, x, y);

                if (isLeft)
                    _leftDown = true;
                else
                    _rightDown = true;

                // Once a chord starts, any extra presses remain inside that
                // session until both original physical buttons have drained.
                if (_active)
                {
                    SetSuppressed(button, true);
                    return Make(true, false, false, x, y);
                }

                // While paused, preserve ordinary mouse behavior but continue
                // tracking physical state so a held button cannot start a chord
                // merely because the user resumes.
                if (!enabled)
                {
                    SetSuppressed(button, false);
                    _blockedUntilAllReleased = true;
                    return Make(false, false, false, x, y);
                }

                if (otherDown && !_blockedUntilAllReleased)
                {
                    _active = true;
                    _cancelled = false;
                    _sessionId++;
                    SetSuppressed(button, true);
                    return Make(true, true, false, x, y);
                }

                SetSuppressed(button, false);
                return Make(false, false, false, x, y);
            }

            // An up without a down observed by this state machine can happen if
            // the hook was installed while a button was already held. Forward it
            // so it can balance the down delivered before the hook was installed.
            if (!buttonDown)
                return Make(false, false, false, x, y);

            bool suppressUp = IsSuppressed(button);
            SetSuppressed(button, false);
            if (isLeft)
                _leftDown = false;
            else
                _rightDown = false;

            bool allReleased = AllButtonsReleased;
            bool completed = allReleased && _active && !_cancelled;
            long completedSession = completed ? _sessionId : 0;

            if (allReleased)
            {
                _active = false;
                _cancelled = false;
                _blockedUntilAllReleased = false;
            }

            return new GestureTransition(suppressUp, false, completed,
                allReleased, completedSession, x, y);
        }

        private void SetSuppressed(MouseButtonKind button, bool value)
        {
            if (button == MouseButtonKind.Left)
                _leftSuppressed = value;
            else
                _rightSuppressed = value;
        }

        private bool IsSuppressed(MouseButtonKind button)
        {
            return button == MouseButtonKind.Left ? _leftSuppressed : _rightSuppressed;
        }

        private GestureTransition Make(bool suppress, bool started, bool completed,
            int x, int y)
        {
            return new GestureTransition(suppress, started, completed,
                AllButtonsReleased, started ? _sessionId : 0, x, y);
        }
    }
}
