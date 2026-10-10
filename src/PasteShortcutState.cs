namespace DuoClick
{
    /// <summary>
    /// Recognizes physical Ctrl+V and Shift+Insert keydown edges without
    /// intercepting or replaying the shortcut.
    /// </summary>
    internal sealed class PasteShortcutState
    {
        private const int VirtualKeyInsert = 0x2D;
        private const int VirtualKeyV = 0x56;
        private bool _vDown;
        private bool _insertDown;

        internal bool Observe(int virtualKey, bool isDown, bool isUp,
            bool controlDown, bool shiftDown, bool altDown, bool windowsKeyDown)
        {
            if (!isDown && !isUp)
                return false;

            bool isV = virtualKey == VirtualKeyV;
            bool isInsert = virtualKey == VirtualKeyInsert;
            if (!isV && !isInsert)
                return false;

            bool wasDown = isV ? _vDown : _insertDown;
            if (isUp)
            {
                if (isV)
                    _vDown = false;
                else
                    _insertDown = false;
                return false;
            }

            if (isV)
                _vDown = true;
            else
                _insertDown = true;

            if (wasDown || altDown || windowsKeyDown)
                return false;

            if (isV)
                return controlDown && !shiftDown;
            return shiftDown && !controlDown;
        }
    }
}
