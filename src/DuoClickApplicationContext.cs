using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
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
        private readonly ToolStripMenuItem _headerItem;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly ToolStripMenuItem _languageItem;
        private readonly ToolStripMenuItem _englishItem;
        private readonly ToolStripMenuItem _simplifiedChineseItem;
        private readonly ToolStripMenuItem _helpItem;
        private readonly ToolStripMenuItem _exitItem;
        private readonly WinFormsTimer _selectionTimer;
        private readonly WinFormsTimer _trayFeedbackTimer;
        private readonly ClipboardCopyQueue _clipboardQueue;
        private readonly string _languageSettingsPath;
        private readonly TrayStatusModel _trayStatus;
        private readonly Icon _trayIconImage;
        private readonly bool _loadedEmbeddedIcon;
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
        private AppLanguage _language;

        internal DuoClickApplicationContext()
            : this(true, true)
        {
        }

        // Internal switches keep the integration test seam independent of global
        // hooks and the user's notification area.
        internal DuoClickApplicationContext(bool installHooks, bool showTrayIcon)
            : this(installHooks, showTrayIcon, AppLanguageSettings.DefaultPath)
        {
        }

        internal DuoClickApplicationContext(bool installHooks, bool showTrayIcon,
            string languageSettingsPath)
        {
            _languageSettingsPath = languageSettingsPath;
            LanguageLoadResult languageLoad = AppLanguageSettings.Load(languageSettingsPath,
                CultureInfo.CurrentUICulture);
            _language = languageLoad.Language;
            _trayStatus = new TrayStatusModel(_language);

            _gesture = new GestureState();
            _invalidSessions = new HashSet<long>();
            _pendingStarts = new Queue<SelectionStartRequest>();

            _dispatcher = new Control();
            IntPtr unusedHandle = _dispatcher.Handle;

            _selectionTimer = new WinFormsTimer();
            _selectionTimer.Interval = 16;
            _selectionTimer.Tick += OnSelectionTimerTick;

            _trayFeedbackTimer = new WinFormsTimer();
            _trayFeedbackTimer.Interval = 2000;
            _trayFeedbackTimer.Tick += OnTrayFeedbackTimerTick;

            _clipboardQueue = new ClipboardCopyQueue(OnClipboardCopied, OnClipboardFailed);

            _headerItem = new ToolStripMenuItem();
            _headerItem.Enabled = false;
            _pauseItem = new ToolStripMenuItem();
            _pauseItem.Click += OnPauseClick;
            _languageItem = new ToolStripMenuItem();
            _englishItem = new ToolStripMenuItem();
            _englishItem.Click += delegate { ChangeLanguage(AppLanguage.English); };
            _simplifiedChineseItem = new ToolStripMenuItem();
            _simplifiedChineseItem.Click += delegate
            {
                ChangeLanguage(AppLanguage.SimplifiedChinese);
            };
            _languageItem.DropDownItems.Add(_englishItem);
            _languageItem.DropDownItems.Add(_simplifiedChineseItem);
            _helpItem = new ToolStripMenuItem();
            _helpItem.Click += OnHelpClick;
            _exitItem = new ToolStripMenuItem();
            _exitItem.Click += OnExitClick;

            _contextMenu = new ContextMenuStrip();
            _contextMenu.Items.Add(_headerItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(_pauseItem);
            _contextMenu.Items.Add(_languageItem);
            _contextMenu.Items.Add(_helpItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(_exitItem);
            UpdateMenuLanguage();

            _trayIcon = new NotifyIcon();
            _trayIconImage = AppTrayIcon.Load(out _loadedEmbeddedIcon);
            _trayIcon.Icon = _trayIconImage;
            _trayIcon.Text = _trayStatus.Text;
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
                    ShowNotice(AppLocalization.Get(_language, AppText.HookStartFailurePrefix) +
                        ex.Message, ToolTipIcon.Error);
                }
            }

            if (languageLoad.Error != null)
                ShowNotice(AppLocalization.Get(_language, AppText.LanguageLoadFailure),
                    ToolTipIcon.Warning);
        }

        internal bool HasSelectionOverlayForTesting
        {
            get { return _overlay != null && !_overlay.IsDisposed; }
        }

        internal bool HasPendingClipboardCopyForTesting
        {
            get { return _clipboardQueue.HasPendingItems; }
        }

        internal AppLanguage LanguageForTesting
        {
            get { return _language; }
        }

        internal string TrayTooltipForTesting
        {
            get { return _trayStatus.Text; }
        }

        internal bool LoadedEmbeddedIconForTesting
        {
            get { return _loadedEmbeddedIcon; }
        }

        internal void ChangeLanguageForTesting(AppLanguage language)
        {
            ChangeLanguage(language);
        }

        internal void SetPausedForTesting(bool paused)
        {
            _paused = paused;
            _trayFeedbackTimer.Stop();
            _trayStatus.SetPaused(paused);
            _pauseItem.Text = AppLocalization.Get(_language,
                paused ? AppText.Resume : AppText.Pause);
            UpdateTrayTooltip();
        }

        internal void ShowCopiedFeedbackForTesting()
        {
            _trayStatus.ShowCopied();
            UpdateTrayTooltip();
            RestartTrayFeedbackTimer();
        }

        internal void ClearTooltipFeedbackForTesting()
        {
            _trayFeedbackTimer.Stop();
            _trayStatus.ClearFeedback();
            UpdateTrayTooltip();
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
                FailSelection(sessionId, AppLocalization.Get(_language,
                    AppText.DesktopSizeFailure));
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
                FailSelection(sessionId, AppLocalization.Get(_language,
                    AppText.CaptureFailurePrefix) + (error == null ? String.Empty : error.Message));
                return;
            }

            _desktopBounds = bounds;
            _frozenDesktop = image;
            try
            {
                _overlay = new SelectionOverlay(_frozenDesktop, _desktopBounds, _selectionStart,
                    _language);
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
                FailSelection(sessionId, AppLocalization.Get(_language,
                    AppText.OverlayFailurePrefix) + ex.Message);
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
                        ShowNotice(AppLocalization.Get(_language, AppText.CropFailure),
                            ToolTipIcon.Error);
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
            _trayFeedbackTimer.Stop();
            _trayStatus.SetPaused(_paused);
            UpdateMenuLanguage();
            UpdateTrayTooltip();
            if (_paused)
            {
                long session = _gesture.CancelCurrent();
                if (session != 0)
                    CancelSelection(session);
                else if (_selectionSessionId != 0)
                    CancelSelection(_selectionSessionId);
                ShowNotice(AppLocalization.Get(_language, AppText.NoticePaused),
                    ToolTipIcon.Info);
            }
            else
            {
                ShowNotice(AppLocalization.Get(_language, AppText.NoticeResumed),
                    ToolTipIcon.Info);
                ProcessQueuedStarts();
            }
        }

        private void OnHelpClick(object sender, EventArgs e)
        {
            MessageBox.Show(this.MainForm,
                AppLocalization.Get(_language, AppText.HelpBody),
                AppLocalization.Get(_language, AppText.HelpTitle),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ChangeLanguage(AppLanguage language)
        {
            if (_closing || _exitPending)
                return;

            _language = language;
            _trayStatus.SetLanguage(language);
            UpdateMenuLanguage();
            if (_overlay != null)
                _overlay.SetLanguage(language);
            UpdateTrayTooltip();

            Exception saveError;
            if (!AppLanguageSettings.TrySave(_languageSettingsPath, language, out saveError))
                ShowNotice(AppLocalization.Get(_language, AppText.LanguageSaveFailure),
                    ToolTipIcon.Warning);
        }

        private void UpdateMenuLanguage()
        {
            _headerItem.Text = AppLocalization.Get(_language, AppText.TrayHeader);
            _pauseItem.Text = AppLocalization.Get(_language,
                _paused ? AppText.Resume : AppText.Pause);
            _languageItem.Text = AppLocalization.Get(_language, AppText.LanguageMenu);
            _englishItem.Text = AppLocalization.Get(_language, AppText.English);
            _englishItem.Checked = _language == AppLanguage.English;
            _simplifiedChineseItem.Text = AppLocalization.Get(_language,
                AppText.SimplifiedChinese);
            _simplifiedChineseItem.Checked = _language == AppLanguage.SimplifiedChinese;
            _helpItem.Text = AppLocalization.Get(_language, AppText.Help);
            _exitItem.Text = AppLocalization.Get(_language, AppText.Exit);
        }

        private void UpdateTrayTooltip()
        {
            if (_trayIcon != null)
                _trayIcon.Text = _trayStatus.Text;
        }

        private void RestartTrayFeedbackTimer()
        {
            _trayFeedbackTimer.Stop();
            _trayFeedbackTimer.Start();
        }

        private void OnTrayFeedbackTimerTick(object sender, EventArgs e)
        {
            _trayFeedbackTimer.Stop();
            _trayStatus.ClearFeedback();
            UpdateTrayTooltip();
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
            _trayFeedbackTimer.Stop();
            _trayFeedbackTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIconImage.Dispose();
            _contextMenu.Dispose();
            _dispatcher.Dispose();
        }

        private void OnClipboardCopied()
        {
            if (_closing)
                return;
            _trayStatus.ShowCopied();
            UpdateTrayTooltip();
            RestartTrayFeedbackTimer();
            ShowNotice(AppLocalization.Get(_language, AppText.NoticeCopied), ToolTipIcon.Info);
        }

        private void OnClipboardFailed()
        {
            if (_closing)
                return;
            _trayStatus.ShowFailed();
            UpdateTrayTooltip();
            RestartTrayFeedbackTimer();
            ShowNotice(AppLocalization.Get(_language, AppText.NoticeClipboardBusy),
                ToolTipIcon.Warning);
        }

        private void ShowNotice(string text, ToolTipIcon icon)
        {
            if (_closing || !_trayIcon.Visible)
                return;
            try
            {
                _trayIcon.ShowBalloonTip(1400,
                    AppLocalization.Get(_language, AppText.AppName), text, icon);
            }
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
            _overlay = new SelectionOverlay(_frozenDesktop, _desktopBounds, start, _language);
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
