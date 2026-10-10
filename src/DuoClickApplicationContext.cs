using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace DuoClick
{
    internal sealed class DuoClickApplicationContext : ApplicationContext
    {
        private readonly Control _dispatcher;
        private readonly GestureState _gesture;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _contextMenu;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly WinFormsTimer _selectionTimer;
        private readonly ClipboardCopyQueue _clipboardQueue;
        private readonly HashSet<long> _invalidSessions;
        private readonly Queue<SelectionStartRequest> _pendingStarts;
        private LowLevelInputHooks _hooks;
        private SelectionOverlay _overlay;
        private Bitmap _frozenDesktop;
        private Rectangle _desktopBounds;
        private Point _selectionStart;
        private long _selectionSessionId;
        private long _nextTestSessionId = 1000000;
        private bool _hasPendingFinish;
        private Point _pendingFinishPoint;
        private bool _paused;
        private bool _exitPending;
        private bool _closing;
        private long _latestClipboardSessionId;

        internal DuoClickApplicationContext()
            : this(true, true)
        {
        }

        // Internal switches keep the integration test seam independent of global
        // hooks and the user's notification area.
        internal DuoClickApplicationContext(bool installHooks, bool showTrayIcon)
        {
            _gesture = new GestureState();
            _invalidSessions = new HashSet<long>();
            _pendingStarts = new Queue<SelectionStartRequest>();

            _dispatcher = new Control();
            IntPtr unusedHandle = _dispatcher.Handle;

            _selectionTimer = new WinFormsTimer();
            _selectionTimer.Interval = 16;
            _selectionTimer.Tick += OnSelectionTimerTick;

            _clipboardQueue = new ClipboardCopyQueue(OnClipboardCopied, OnClipboardFailed);

            _pauseItem = new ToolStripMenuItem("暂停截屏");
            _pauseItem.Click += OnPauseClick;
            ToolStripMenuItem helpItem = new ToolStripMenuItem("使用说明");
            helpItem.Click += OnHelpClick;
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += OnExitClick;

            _contextMenu = new ContextMenuStrip();
            _contextMenu.Items.Add(_pauseItem);
            _contextMenu.Items.Add(helpItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(exitItem);

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = SystemIcons.Application;
            _trayIcon.Text = "Duo Click - Ready";
            _trayIcon.ContextMenuStrip = _contextMenu;
            _trayIcon.Visible = showTrayIcon;

            if (installHooks)
            {
                try
                {
                    _hooks = new LowLevelInputHooks(_dispatcher, _gesture,
                        delegate { return !_paused && !_exitPending && !_closing; },
                        BeginSelection, CompleteSelection, CancelSelection, OnButtonsDrained);
                }
                catch (Exception ex)
                {
                    ShowNotice("全局快捷键启动失败：" + ex.Message, ToolTipIcon.Error);
                }
            }
        }

        internal bool HasSelectionOverlayForTesting
        {
            get { return _overlay != null && !_overlay.IsDisposed; }
        }

        internal bool HasPendingClipboardCopyForTesting
        {
            get { return _clipboardQueue.HasPendingItems; }
        }

        private void BeginSelection(long sessionId, Point start)
        {
            if (_closing || _exitPending || _paused || _invalidSessions.Remove(sessionId))
                return;

            if (_selectionSessionId != 0)
            {
                _pendingStarts.Enqueue(new SelectionStartRequest(sessionId, start));
                return;
            }

            _selectionSessionId = sessionId;
            _selectionStart = start;
            _hasPendingFinish = false;

            Rectangle bounds = SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                FailSelection(sessionId, "无法读取桌面尺寸。");
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                Bitmap bitmap = null;
                Exception error = null;
                try { bitmap = CaptureDesktop(bounds); }
                catch (Exception ex) { error = ex; }

                Bitmap captured = bitmap;
                Exception captureError = error;
                bool posted = PostToUi(delegate
                {
                    OnCaptureReady(sessionId, bounds, captured, captureError);
                });
                if (!posted && captured != null)
                    captured.Dispose();
            });
        }

        private void OnCaptureReady(long sessionId, Rectangle bounds, Bitmap image, Exception error)
        {
            if (_closing || _exitPending || _selectionSessionId != sessionId ||
                _invalidSessions.Remove(sessionId))
            {
                if (image != null)
                    image.Dispose();
                return;
            }

            if (_paused)
            {
                if (image != null)
                    image.Dispose();
                CancelSelection(sessionId);
                return;
            }

            if (error != null || image == null)
            {
                if (image != null)
                    image.Dispose();
                FailSelection(sessionId, "截取桌面失败。 " + (error == null ? String.Empty : error.Message));
                return;
            }

            _desktopBounds = bounds;
            _frozenDesktop = image;
            try
            {
                _overlay = new SelectionOverlay(_frozenDesktop, _desktopBounds, _selectionStart);
                _overlay.Show();
                _selectionTimer.Start();

                if (_hasPendingFinish)
                {
                    Point end = _pendingFinishPoint;
                    _hasPendingFinish = false;
                    FinishSelection(sessionId, end);
                }
            }
            catch (Exception ex)
            {
                FailSelection(sessionId, "显示选区失败。 " + ex.Message);
            }
        }

        private void CompleteSelection(long sessionId, Point end)
        {
            if (_closing || _invalidSessions.Remove(sessionId))
                return;

            if (_selectionSessionId != sessionId)
            {
                SelectionStartRequest queued = FindQueuedStart(sessionId);
                if (queued != null)
                    queued.SetCompletion(end);
                return;
            }

            if (_frozenDesktop == null)
            {
                _hasPendingFinish = true;
                _pendingFinishPoint = end;
                return;
            }

            FinishSelection(sessionId, end);
        }

        private void FinishSelection(long sessionId, Point end)
        {
            if (_selectionSessionId != sessionId || _frozenDesktop == null)
                return;

            if (_overlay != null)
                _overlay.SetSelectionEnd(end);

            Rectangle cropRectangle = SelectionGeometry.GetRectangle(
                _desktopBounds, _selectionStart, end);
            Rectangle capturedBounds = _desktopBounds;
            Bitmap source = _frozenDesktop;
            _frozenDesktop = null;
            ClearSelectionResources();

            if (!SelectionGeometry.IsLargeEnough(cropRectangle))
            {
                source.Dispose();
                ProcessQueuedStarts();
                return;
            }

            ProcessQueuedStarts();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Bitmap crop = null;
                Exception error = null;
                try { crop = CropDesktop(source, capturedBounds, cropRectangle); }
                catch (Exception ex) { error = ex; }
                finally { source.Dispose(); }

                Bitmap result = crop;
                Exception cropError = error;
                long completedSessionId = sessionId;
                bool posted = PostToUi(delegate
                {
                    if (_closing)
                    {
                        if (result != null)
                            result.Dispose();
                        return;
                    }

                    if (cropError != null || result == null)
                    {
                        if (result != null)
                            result.Dispose();
                        ShowNotice("生成截图失败。", ToolTipIcon.Error);
                        return;
                    }

                    PublishCrop(completedSessionId, result);
                });
                if (!posted && result != null)
                    result.Dispose();
            });
        }

        private void CancelSelection(long sessionId)
        {
            if (_selectionSessionId == sessionId)
            {
                ClearSelectionResources();
                _invalidSessions.Remove(sessionId);
                ProcessQueuedStarts();
            }
            else if (!_closing)
            {
                // A cancel may be posted before its queued start callback runs.
                RemoveQueuedStart(sessionId);
                _invalidSessions.Add(sessionId);
                ProcessQueuedStarts();
            }
        }

        private void FailSelection(long sessionId, string message)
        {
            _invalidSessions.Add(sessionId);
            if (_selectionSessionId == sessionId)
            {
                ClearSelectionResources();
                ProcessQueuedStarts();
            }
            ShowNotice(message, ToolTipIcon.Error);
        }

        private void ClearSelectionResources()
        {
            _selectionTimer.Stop();
            if (_overlay != null)
            {
                _overlay.Hide();
                _overlay.Dispose();
                _overlay = null;
            }
            if (_frozenDesktop != null)
            {
                _frozenDesktop.Dispose();
                _frozenDesktop = null;
            }
            _selectionSessionId = 0;
            _hasPendingFinish = false;
        }

        private static Bitmap CaptureDesktop(Rectangle bounds)
        {
            Bitmap image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(image))
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size,
                        CopyPixelOperation.SourceCopy);
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        private static Bitmap CropDesktop(Bitmap source, Rectangle desktopBounds, Rectangle crop)
        {
            Bitmap result = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
            try
            {
                Rectangle sourceRectangle = new Rectangle(crop.Left - desktopBounds.Left,
                    crop.Top - desktopBounds.Top, crop.Width, crop.Height);
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.DrawImage(source, new Rectangle(0, 0, crop.Width, crop.Height),
                        sourceRectangle, GraphicsUnit.Pixel);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private bool PostToUi(MethodInvoker action)
        {
            if (_closing || _dispatcher.IsDisposed || !_dispatcher.IsHandleCreated)
                return false;

            try
            {
                _dispatcher.BeginInvoke(action);
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private void ProcessQueuedStarts()
        {
            if (_closing || _exitPending || _paused || _selectionSessionId != 0)
                return;

            while (_pendingStarts.Count > 0)
            {
                SelectionStartRequest request = _pendingStarts.Dequeue();
                if (_invalidSessions.Remove(request.SessionId))
                    continue;
                if (request.WaitForTestCapture)
                {
                    _selectionSessionId = request.SessionId;
                    _selectionStart = request.Start;
                    _desktopBounds = request.DesktopBounds;
                    _hasPendingFinish = false;
                }
                else
                {
                    BeginSelection(request.SessionId, request.Start);
                }
                if (request.HasCompletion)
                    CompleteSelection(request.SessionId, request.End);
                return;
            }
        }

        private void RemoveQueuedStart(long sessionId)
        {
            if (_pendingStarts.Count == 0)
                return;

            Queue<SelectionStartRequest> retained = new Queue<SelectionStartRequest>();
            while (_pendingStarts.Count > 0)
            {
                SelectionStartRequest request = _pendingStarts.Dequeue();
                if (request.SessionId != sessionId)
                    retained.Enqueue(request);
            }
            while (retained.Count > 0)
                _pendingStarts.Enqueue(retained.Dequeue());
        }

        private SelectionStartRequest FindQueuedStart(long sessionId)
        {
            foreach (SelectionStartRequest request in _pendingStarts)
            {
                if (request.SessionId == sessionId)
                    return request;
            }
            return null;
        }

        private void OnSelectionTimerTick(object sender, EventArgs e)
        {
            if (_overlay != null)
                _overlay.SetSelectionEnd(Cursor.Position);
        }

        private void OnButtonsDrained()
        {
            if (_exitPending)
                ShutDown();
        }

        private void OnPauseClick(object sender, EventArgs e)
        {
            if (_exitPending || _closing)
                return;

            _paused = !_paused;
            _pauseItem.Text = _paused ? "恢复截屏" : "暂停截屏";
            if (_paused)
            {
                long session = _gesture.CancelCurrent();
                if (session != 0)
                    CancelSelection(session);
                else if (_selectionSessionId != 0)
                    CancelSelection(_selectionSessionId);
                ShowNotice("截屏已暂停。", ToolTipIcon.Info);
            }
            else
            {
                ShowNotice("截屏已恢复。", ToolTipIcon.Info);
                ProcessQueuedStarts();
            }
        }

        private void OnHelpClick(object sender, EventArgs e)
        {
            MessageBox.Show(
                "同时按住鼠标左键和右键。按下第二个键时会冻结桌面并开始选区。\r\n" +
                "拖动鼠标后松开两个键，选区会复制到剪贴板。按 Esc 可取消。\r\n\r\n" +
                "组合键中的第二个按键会被截屏功能拦截；第一个按键会照常传递。",
                "Duo Click 使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnExitClick(object sender, EventArgs e)
        {
            RequestExit();
        }

        private void RequestExit()
        {
            if (_exitPending || _closing)
                return;

            _exitPending = true;
            _pauseItem.Enabled = false;
            long session = _gesture.CancelCurrent();
            if (session != 0)
                CancelSelection(session);

            if (_gesture.AllButtonsReleased)
                ShutDown();
        }

        private void ShutDown()
        {
            if (_closing)
                return;

            CleanupResources();
            ExitThread();
        }

        private void CleanupResources()
        {
            if (_closing)
                return;

            _closing = true;
            ClearSelectionResources();
            _pendingStarts.Clear();
            if (_hooks != null)
            {
                _hooks.Dispose();
                _hooks = null;
            }
            _clipboardQueue.Dispose();
            _selectionTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _contextMenu.Dispose();
            _dispatcher.Dispose();
        }

        private void OnClipboardCopied()
        {
            if (_closing)
                return;
            _trayIcon.Text = "Duo Click - Copied";
            ShowNotice("截图已复制到剪贴板。", ToolTipIcon.Info);
        }

        private void OnClipboardFailed()
        {
            if (_closing)
                return;
            ShowNotice("剪贴板正忙，截图未复制。", ToolTipIcon.Warning);
        }

        private void ShowNotice(string text, ToolTipIcon icon)
        {
            if (_closing || !_trayIcon.Visible)
                return;
            try { _trayIcon.ShowBalloonTip(1400, "Duo Click", text, icon); }
            catch (InvalidOperationException) { }
        }

        // Focused integration seam: tests can exercise selection completion with
        // a synthetic frozen image without installing process-wide input hooks.
        internal long BeginSelectionWithImageForTesting(Bitmap image, Rectangle desktopBounds,
            Point start)
        {
            if (image == null)
                throw new ArgumentNullException("image");
            if (image.Width != desktopBounds.Width || image.Height != desktopBounds.Height)
                throw new ArgumentException("Image dimensions must match desktop bounds.", "image");

            ClearSelectionResources();
            long session = ++_nextTestSessionId;
            _selectionSessionId = session;
            _desktopBounds = desktopBounds;
            _selectionStart = start;
            _frozenDesktop = (Bitmap)image.Clone();
            _overlay = new SelectionOverlay(_frozenDesktop, _desktopBounds, start);
            _overlay.Show();
            return session;
        }

        internal long BeginPendingSelectionForTesting(Rectangle desktopBounds, Point start)
        {
            if (_selectionSessionId != 0)
                throw new InvalidOperationException("A selection is already active.");

            long session = ++_nextTestSessionId;
            _selectionSessionId = session;
            _desktopBounds = desktopBounds;
            _selectionStart = start;
            _hasPendingFinish = false;
            return session;
        }

        internal long QueuePendingSelectionForTesting(Rectangle desktopBounds, Point start)
        {
            if (_selectionSessionId == 0)
                throw new InvalidOperationException("There is no selection to queue behind.");

            long session = ++_nextTestSessionId;
            _pendingStarts.Enqueue(new SelectionStartRequest(session, start,
                desktopBounds, true));
            return session;
        }

        internal void CompleteCaptureForTesting(long sessionId, Bitmap image)
        {
            if (image == null)
                throw new ArgumentNullException("image");
            CompleteCaptureForTesting(sessionId, _desktopBounds, image);
        }

        internal void CompleteCaptureForTesting(long sessionId, Rectangle desktopBounds,
            Bitmap image)
        {
            if (image == null)
                throw new ArgumentNullException("image");
            if (image.Width != desktopBounds.Width || image.Height != desktopBounds.Height)
                throw new ArgumentException("Image dimensions must match desktop bounds.", "image");
            Bitmap copy = (Bitmap)image.Clone();
            OnCaptureReady(sessionId, desktopBounds, copy, null);
        }

        internal void CompleteSelectionForTesting(long sessionId, Point end)
        {
            CompleteSelection(sessionId, end);
        }

        internal void PublishCropForTesting(long sessionId, Bitmap image)
        {
            if (image == null)
                throw new ArgumentNullException("image");
            PublishCrop(sessionId, image);
        }

        private void PublishCrop(long sessionId, Bitmap image)
        {
            if (_closing || sessionId < _latestClipboardSessionId)
            {
                image.Dispose();
                return;
            }

            _latestClipboardSessionId = sessionId;
            _clipboardQueue.Enqueue(image);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                CleanupResources();
            base.Dispose(disposing);
        }

        protected override void ExitThreadCore()
        {
            CleanupResources();
            base.ExitThreadCore();
        }

        private sealed class SelectionStartRequest
        {
            internal readonly long SessionId;
            internal readonly Point Start;
            internal readonly Rectangle DesktopBounds;
            internal readonly bool WaitForTestCapture;
            internal bool HasCompletion;
            internal Point End;

            internal SelectionStartRequest(long sessionId, Point start)
                : this(sessionId, start, Rectangle.Empty, false)
            {
            }

            internal SelectionStartRequest(long sessionId, Point start,
                Rectangle desktopBounds, bool waitForTestCapture)
            {
                SessionId = sessionId;
                Start = start;
                DesktopBounds = desktopBounds;
                WaitForTestCapture = waitForTestCapture;
            }

            internal void SetCompletion(Point end)
            {
                HasCompletion = true;
                End = end;
            }
        }

        private sealed class ClipboardCopyQueue : IDisposable
        {
            private const int MaximumAttempts = 10;
            private readonly Queue<Bitmap> _items = new Queue<Bitmap>();
            private readonly WinFormsTimer _timer;
            private readonly Action _onCopied;
            private readonly Action _onFailed;
            private int _attempts;
            private bool _disposed;

            internal ClipboardCopyQueue(Action onCopied, Action onFailed)
            {
                _onCopied = onCopied;
                _onFailed = onFailed;
                _timer = new WinFormsTimer();
                _timer.Interval = 100;
                _timer.Tick += OnTimerTick;
            }

            internal bool HasPendingItems
            {
                get { return _items.Count > 0; }
            }

            internal void Enqueue(Bitmap image)
            {
                if (_disposed)
                {
                    image.Dispose();
                    return;
                }

                _items.Enqueue(image);
                if (_items.Count == 1)
                    AttemptCurrent();
            }

            private void OnTimerTick(object sender, EventArgs e)
            {
                _timer.Stop();
                AttemptCurrent();
            }

            private void AttemptCurrent()
            {
                if (_disposed || _items.Count == 0)
                    return;

                _attempts++;
                Bitmap image = _items.Peek();
                bool copied = false;
                try
                {
                    DataObject data = new DataObject();
                    data.SetData(DataFormats.Bitmap, image);
                    // Zero internal retries keeps OLE clipboard contention from
                    // blocking the hook-owning UI thread; the WinForms timer retries.
                    Clipboard.SetDataObject(data, true, 0, 0);
                    copied = true;
                }
                catch (Exception)
                {
                    copied = false;
                }

                if (copied)
                {
                    image.Dispose();
                    _items.Dequeue();
                    _attempts = 0;
                    _onCopied();
                    if (_items.Count > 0)
                        _timer.Start();
                    return;
                }

                if (_attempts >= MaximumAttempts)
                {
                    Bitmap failed = _items.Dequeue();
                    failed.Dispose();
                    _attempts = 0;
                    _onFailed();
                    if (_items.Count > 0)
                        _timer.Start();
                }
                else
                {
                    _timer.Start();
                }
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                _timer.Stop();
                _timer.Dispose();
                while (_items.Count > 0)
                    _items.Dequeue().Dispose();
            }
        }
    }
}
