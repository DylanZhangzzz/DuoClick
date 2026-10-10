using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DuoClick
{
    /// <summary>
    /// Installs low-level hooks for the two mouse buttons and Escape. The hook
    /// procedures only update the small gesture state and post UI work; screen
    /// capture and painting happen later on the WinForms thread.
    /// </summary>
    internal sealed class LowLevelInputHooks : IDisposable
    {
        private const int WhMouseLl = 14;
        private const int WhKeyboardLl = 13;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmRButtonDown = 0x0204;
        private const int WmRButtonUp = 0x0205;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int VkEscape = 0x1B;
        private const int VkInsert = 0x2D;
        private const int VkV = 0x56;
        private const int VkControl = 0x11;
        private const int VkShift = 0x10;
        private const int VkMenu = 0x12;
        private const int VkLeftWindows = 0x5B;
        private const int VkRightWindows = 0x5C;
        private const uint MouseInjectedFlag = 0x00000001;
        private const uint KeyboardInjectedFlag = 0x00000010;
        private const uint LowerIntegrityInjectedFlag = 0x00000002;

        private readonly Control _dispatcher;
        private readonly GestureState _gesture;
        private readonly Func<bool> _isEnabled;
        private readonly Action<long, Point, uint, long> _onStarted;
        private readonly Func<long> _getBatchEpoch;
        private readonly Action<long, Point> _onCompleted;
        private readonly Action<long> _onCancelled;
        private readonly Action _onAllReleased;
        private readonly Action<uint> _onPasteShortcut;
        private readonly HookProcedure _mouseProcedure;
        private readonly HookProcedure _keyboardProcedure;
        private IntPtr _mouseHook;
        private IntPtr _keyboardHook;
        private bool _escapeWasSwallowed;
        private readonly PasteShortcutState _pasteShortcutState = new PasteShortcutState();
        private bool _disposed;

        internal LowLevelInputHooks(Control dispatcher, GestureState gesture,
            Func<bool> isEnabled, Action<long, Point, uint, long> onStarted,
            Action<long, Point> onCompleted, Action<long> onCancelled,
            Action onAllReleased, Action<uint> onPasteShortcut,
            Func<long> getBatchEpoch)
        {
            _dispatcher = dispatcher;
            _gesture = gesture;
            _isEnabled = isEnabled;
            _onStarted = onStarted;
            _getBatchEpoch = getBatchEpoch;
            _onCompleted = onCompleted;
            _onCancelled = onCancelled;
            _onAllReleased = onAllReleased;
            _onPasteShortcut = onPasteShortcut;
            _mouseProcedure = MouseHookCallback;
            _keyboardProcedure = KeyboardHookCallback;

            IntPtr module = GetModuleHandle(null);
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProcedure, module, 0);
            if (_mouseHook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the mouse hook.");

            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProcedure, module, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error, "Could not install the keyboard hook.");
            }
        }

        private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && !_disposed)
            {
                int messageId = message.ToInt32();
                MouseButtonKind button;
                bool isDown;
                if (TryGetButtonTransition(messageId, out button, out isDown))
                {
                    LowLevelMouseData input = (LowLevelMouseData)Marshal.PtrToStructure(
                        data, typeof(LowLevelMouseData));
                    if (!IsMouseInjected(input.Flags))
                    {
                        GestureTransition transition = _gesture.Process(button, isDown,
                            input.Point.X, input.Point.Y, _isEnabled());

                        if (transition.Started)
                        {
                            Point point = new Point(transition.X, transition.Y);
                            uint clipboardSequence = GetClipboardSequenceNumber();
                            long batchEpoch = _getBatchEpoch == null
                                ? 0 : _getBatchEpoch();
                            Post(delegate
                            {
                                _onStarted(transition.SessionId, point,
                                    clipboardSequence, batchEpoch);
                            });
                        }
                        if (transition.Completed)
                        {
                            Point point = new Point(transition.X, transition.Y);
                            Post(delegate { _onCompleted(transition.SessionId, point); });
                        }
                        if (transition.AllReleased)
                            Post(delegate { _onAllReleased(); });

                        if (transition.Suppress)
                            return new IntPtr(1);
                    }
                }
            }

            return CallNextHookEx(_mouseHook, code, message, data);
        }

        private IntPtr KeyboardHookCallback(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && !_disposed)
            {
                int messageId = message.ToInt32();
                LowLevelKeyboardData input = (LowLevelKeyboardData)Marshal.PtrToStructure(
                    data, typeof(LowLevelKeyboardData));
                if (!IsKeyboardInjected(input.Flags))
                {
                    if (input.VirtualKey == VkEscape)
                    {
                        if (_escapeWasSwallowed &&
                            (messageId == WmKeyDown || messageId == WmSysKeyDown))
                            return new IntPtr(1);

                        if (_escapeWasSwallowed &&
                            (messageId == WmKeyUp || messageId == WmSysKeyUp))
                        {
                            _escapeWasSwallowed = false;
                            return new IntPtr(1);
                        }

                        if (messageId == WmKeyDown || messageId == WmSysKeyDown)
                        {
                            long session = _gesture.CancelCurrent();
                            if (session != 0)
                            {
                                _escapeWasSwallowed = true;
                                Post(delegate { _onCancelled(session); });
                                return new IntPtr(1);
                            }
                        }
                    }

                    ObservePasteShortcut(messageId, input);
                }
            }

            return CallNextHookEx(_keyboardHook, code, message, data);
        }

        private void ObservePasteShortcut(int messageId, LowLevelKeyboardData input)
        {
            bool isDown = messageId == WmKeyDown || messageId == WmSysKeyDown;
            bool isUp = messageId == WmKeyUp || messageId == WmSysKeyUp;
            if (!isDown && !isUp)
                return;

            bool paste = _pasteShortcutState.Observe(unchecked((int)input.VirtualKey),
                isDown, isUp, IsKeyDown(VkControl), IsKeyDown(VkShift),
                IsKeyDown(VkMenu), IsKeyDown(VkLeftWindows) || IsKeyDown(VkRightWindows));

            // A held key generates repeated keydown messages. Observe only its
            // first edge and always let Windows deliver the shortcut.
            if (paste && _onPasteShortcut != null)
            {
                uint clipboardSequence = GetClipboardSequenceNumber();
                Post(delegate { _onPasteShortcut(clipboardSequence); });
            }
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
        }

        private void Post(MethodInvoker action)
        {
            if (_disposed || _dispatcher.IsDisposed || !_dispatcher.IsHandleCreated)
                return;

            try { _dispatcher.BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static bool TryGetButtonTransition(int message, out MouseButtonKind button,
            out bool isDown)
        {
            switch (message)
            {
                case WmLButtonDown:
                    button = MouseButtonKind.Left;
                    isDown = true;
                    return true;
                case WmLButtonUp:
                    button = MouseButtonKind.Left;
                    isDown = false;
                    return true;
                case WmRButtonDown:
                    button = MouseButtonKind.Right;
                    isDown = true;
                    return true;
                case WmRButtonUp:
                    button = MouseButtonKind.Right;
                    isDown = false;
                    return true;
                default:
                    button = MouseButtonKind.Left;
                    isDown = false;
                    return false;
            }
        }

        private static bool IsMouseInjected(uint flags)
        {
            return (flags & (MouseInjectedFlag | LowerIntegrityInjectedFlag)) != 0;
        }

        private static bool IsKeyboardInjected(uint flags)
        {
            return (flags & (KeyboardInjectedFlag | LowerIntegrityInjectedFlag)) != 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LowLevelMouseData
        {
            internal NativePoint Point;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LowLevelKeyboardData
        {
            internal uint VirtualKey;
            internal uint ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookType, HookProcedure callback,
            IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code,
            IntPtr message, IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
    }
}
