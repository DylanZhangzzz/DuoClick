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
        private readonly ToolStripMenuItem _batchCountItem;
        private readonly ToolStripMenuItem _newBatchItem;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly ToolStripMenuItem _languageItem;
        private readonly ToolStripMenuItem _settingsItem;
        private readonly ToolStripMenuItem _englishItem;
        private readonly ToolStripMenuItem _simplifiedChineseItem;
        private readonly ToolStripMenuItem _helpItem;
        private readonly ToolStripMenuItem _exitItem;
        private readonly WinFormsTimer _selectionTimer;
        private readonly WinFormsTimer _trayFeedbackTimer;
        private readonly ClipboardCopyQueue _clipboardQueue;
        private readonly CaptureBatch _captureBatch;
        private readonly string _languageSettingsPath;
        private readonly string _captureLimitSettingsPath;
        private readonly TrayStatusModel _trayStatus;
        private readonly Icon _trayIconImage;
        private readonly bool _loadedEmbeddedIcon;
        private readonly HashSet<long> _invalidSessions;
        private readonly Queue<SelectionStartRequest> _pendingStarts;
        private readonly Dictionary<long, uint> _captureClipboardSequences =
            new Dictionary<long, uint>();
        private readonly Queue<uint> _clipboardSequenceRebaseOrder = new Queue<uint>();
        private readonly Dictionary<uint, uint> _clipboardSequenceRebases =
            new Dictionary<uint, uint>();
        private readonly object _compositeSync = new object();
        private LowLevelInputHooks _hooks;
        private SelectionOverlay _overlay;
        private Bitmap _frozenDesktop;
        private Rectangle _desktopBounds;
        private Point _selectionStart;
        private long _selectionSessionId;
        private long _selectionBatchEpoch;
        private long _batchEpoch;
        private long _nextTestSessionId = 1000000;
        private bool _hasPendingFinish;
        private Point _pendingFinishPoint;
        private bool _paused;
        private bool _exitPending;
        private bool _closing;
        private bool _compositeWorkerRunning;
        private bool _compositeRequested;
        private long _compositeRequestId;
        private long _latestPublishedBoundary;
        private uint _lastSuccessfulClipboardSequence;
        private bool _hasSuccessfulClipboardPublication;
        private AppLanguage _language;

        private const int MaxClipboardSequenceRebases = 128;

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
            _captureLimitSettingsPath =
                CaptureLimitSettings.GetPathForLanguageSettingsPath(languageSettingsPath);
            LanguageLoadResult languageLoad = AppLanguageSettings.Load(languageSettingsPath,
                CultureInfo.CurrentUICulture);
            CaptureLimitLoadResult captureLimitLoad =
                CaptureLimitSettings.Load(_captureLimitSettingsPath);
            _language = languageLoad.Language;
            _trayStatus = new TrayStatusModel(_language);

            _gesture = new GestureState();
            _captureBatch = new CaptureBatch(captureLimitLoad.Capacity);
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

            _clipboardQueue = new ClipboardCopyQueue(OnClipboardCopied, OnClipboardFailed,
                GetClipboardSequenceNumber);

            _headerItem = new ToolStripMenuItem();
            _headerItem.Enabled = false;
            _batchCountItem = new ToolStripMenuItem();
            _batchCountItem.Enabled = false;
            _newBatchItem = new ToolStripMenuItem();
            _newBatchItem.Click += OnNewBatchClick;
            _pauseItem = new ToolStripMenuItem();
            _pauseItem.Click += OnPauseClick;
            _languageItem = new ToolStripMenuItem();
            _settingsItem = new ToolStripMenuItem();
            _settingsItem.Click += OnSettingsClick;
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
            _contextMenu.Items.Add(_batchCountItem);
            _contextMenu.Items.Add(_newBatchItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(_pauseItem);
            _contextMenu.Items.Add(_languageItem);
            _contextMenu.Items.Add(_settingsItem);
            _contextMenu.Items.Add(_helpItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(_exitItem);
            UpdateMenuLanguage();
            UpdateBatchCountMenu();
            UpdateTrayTooltip();

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
                        BeginSelection, CompleteSelection, CancelSelection, OnButtonsDrained,
                        OnPasteShortcutObserved,
                        delegate { return Interlocked.Read(ref _batchEpoch); });
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
            if (captureLimitLoad.Error != null)
                ShowNotice(AppLocalization.Get(_language, AppText.CaptureLimitLoadFailure),
                    ToolTipIcon.Warning);
        }

        internal bool HasSelectionOverlayForTesting
        {
            get { return _overlay != null && !_overlay.IsDisposed; }
        }

        internal bool HasPendingClipboardCopyForTesting
        {
            get { return _clipboardQueue.HasPendingItems || HasPendingCompositeForTesting; }
        }

        internal int BatchCountForTesting
        {
            get { return _captureBatch.Count; }
        }

        internal int CaptureLimitForTesting
        {
            get { return _captureBatch.Capacity; }
        }

        internal string CaptureLimitSettingsPathForTesting
        {
            get { return _captureLimitSettingsPath; }
        }

        private bool HasPendingCompositeForTesting
        {
            get
            {
                lock (_compositeSync)
                    return _compositeWorkerRunning || _compositeRequested;
            }
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

        internal bool ApplyCaptureLimitForTesting(int capacity)
        {
            Exception error;
            return TrySaveAndApplyCaptureLimit(capacity, out error);
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

        internal void StartNewBatchForTesting()
        {
            StartNewBatch();
        }

        internal void ClearBatchForTesting()
        {
            StartNewBatch();
        }

        internal uint ClipboardSequenceForTesting
        {
            get { return GetClipboardSequenceNumber(); }
        }

        internal void ObservePasteShortcutForTesting(uint sequence)
        {
            OnPasteShortcutObserved(sequence);
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

        private void BeginSelection(long sessionId, Point start, uint clipboardSequence,
            long batchEpoch)
        {
            if (_closing || _exitPending || _paused || batchEpoch != _batchEpoch ||
                _invalidSessions.Remove(sessionId))
            {
                _captureClipboardSequences.Remove(sessionId);
                _invalidSessions.Remove(sessionId);
                return;
            }

            _captureClipboardSequences[sessionId] =
                NormalizeClipboardSequence(clipboardSequence);
            clipboardSequence = _captureClipboardSequences[sessionId];

            if (_selectionSessionId != 0)
            {
                _pendingStarts.Enqueue(new SelectionStartRequest(sessionId, start,
                    clipboardSequence, batchEpoch));
                return;
            }

            _captureClipboardSequences[sessionId] = clipboardSequence;
            _selectionSessionId = sessionId;
            _selectionBatchEpoch = batchEpoch;
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
            long captureBatchEpoch = _selectionBatchEpoch;
            Rectangle capturedBounds = _desktopBounds;
            Bitmap source = _frozenDesktop;
            _frozenDesktop = null;
            ClearSelectionResources();

            if (!SelectionGeometry.IsLargeEnough(cropRectangle))
            {
                _captureClipboardSequences.Remove(sessionId);
                source.Dispose();
                ProcessQueuedStarts();
                return;
            }

            if (captureBatchEpoch != _batchEpoch)
            {
                _captureClipboardSequences.Remove(sessionId);
                source.Dispose();
                ProcessQueuedStarts();
                return;
            }

            if (!_captureBatch.Reserve(sessionId, cropRectangle.Size))
            {
                _captureClipboardSequences.Remove(sessionId);
                source.Dispose();
                ShowNotice(AppLocalization.Get(_language, AppText.BatchTooLarge),
                    ToolTipIcon.Warning);
                ProcessQueuedStarts();
                return;
            }

            OnBatchChanged(false);

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
                        _captureBatch.Fail(completedSessionId);
                        PruneCaptureClipboardSequences();
                        OnBatchChanged(true);
                        ShowNotice(AppLocalization.Get(_language, AppText.CropFailure),
                            ToolTipIcon.Error);
                        return;
                    }

                    CompleteCrop(completedSessionId, result);
                });
                if (!posted && result != null)
                    result.Dispose();
            });
        }

        private void CancelSelection(long sessionId)
        {
            _captureClipboardSequences.Remove(sessionId);
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
            _captureClipboardSequences.Remove(sessionId);
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
            _selectionBatchEpoch = 0;
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
                {
                    _captureClipboardSequences.Remove(request.SessionId);
                    continue;
                }
                if (request.BatchEpoch != _batchEpoch)
                {
                    _captureClipboardSequences.Remove(request.SessionId);
                    continue;
                }
                if (request.WaitForTestCapture)
                {
                    if (!_captureClipboardSequences.ContainsKey(request.SessionId))
                        _captureClipboardSequences[request.SessionId] =
                            NormalizeClipboardSequence(request.ClipboardSequence);
                    else
                        _captureClipboardSequences[request.SessionId] =
                            NormalizeClipboardSequence(
                                _captureClipboardSequences[request.SessionId]);
                    _selectionSessionId = request.SessionId;
                    _selectionBatchEpoch = request.BatchEpoch;
                    _selectionStart = request.Start;
                    _desktopBounds = request.DesktopBounds;
                    _hasPendingFinish = false;
                }
                else
                {
                    BeginSelection(request.SessionId, request.Start,
                        GetCaptureClipboardSequence(request.SessionId,
                            request.ClipboardSequence), request.BatchEpoch);
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
                else
                    _captureClipboardSequences.Remove(request.SessionId);
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

        private void OnNewBatchClick(object sender, EventArgs e)
        {
            StartNewBatch();
        }

        private void OnSettingsClick(object sender, EventArgs e)
        {
            if (_closing || _exitPending)
                return;

            using (CaptureLimitSettingsDialog dialog = new CaptureLimitSettingsDialog(
                _language, _captureBatch.Capacity))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                Exception error;
                if (!TrySaveAndApplyCaptureLimit(dialog.Capacity, out error))
                    ShowNotice(AppLocalization.Get(_language,
                        AppText.CaptureLimitSaveFailure), ToolTipIcon.Warning);
            }
        }

        private bool TrySaveAndApplyCaptureLimit(int capacity, out Exception error)
        {
            try
            {
                CaptureLimitSettings.ValidateCapacity(capacity);
            }
            catch (Exception validationError)
            {
                error = validationError;
                return false;
            }

            if (!CaptureLimitSettings.TrySave(_captureLimitSettingsPath, capacity, out error))
                return false;

            if (capacity == _captureBatch.Capacity)
                return true;

            if (!_captureBatch.SetCapacity(capacity))
            {
                error = new InvalidOperationException(
                    "The capture limit could not be applied.");
                return false;
            }

            PruneCaptureClipboardSequences();
            // Rebuild through the ordinary sequence-checked publication path.
            // If another app has changed the clipboard, its sequence will no
            // longer match a retained capture baseline and nothing is written.
            OnBatchChanged(_captureBatch.Count > 0);
            error = null;
            return true;
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
            _settingsItem.Text = AppLocalization.Get(_language, AppText.CaptureLimitMenu);
            _newBatchItem.Text = AppLocalization.Get(_language, AppText.StartNewBatch);
            _englishItem.Text = AppLocalization.Get(_language, AppText.English);
            _englishItem.Checked = _language == AppLanguage.English;
            _simplifiedChineseItem.Text = AppLocalization.Get(_language,
                AppText.SimplifiedChinese);
            _simplifiedChineseItem.Checked = _language == AppLanguage.SimplifiedChinese;
            _helpItem.Text = AppLocalization.Get(_language, AppText.Help);
            _exitItem.Text = AppLocalization.Get(_language, AppText.Exit);
            UpdateBatchCountMenu();
        }

        private void UpdateBatchCountMenu()
        {
            if (_batchCountItem == null || _captureBatch == null)
                return;

            string count = _captureBatch.Count.ToString(CultureInfo.InvariantCulture);
            _batchCountItem.Text = String.Format(CultureInfo.InvariantCulture,
                AppLocalization.Get(_language, AppText.BatchCount), count);
        }

        private void UpdateTrayTooltip()
        {
            if (_trayStatus != null && _captureBatch != null)
                _trayStatus.SetCount(_captureBatch.Count);
            if (_trayIcon != null)
                _trayIcon.Text = _trayStatus.Text;
        }

        private void OnBatchChanged(bool requestComposite)
        {
            InvalidateCompositeJobs(true);
            _trayFeedbackTimer.Stop();
            _trayStatus.ClearFeedback();
            UpdateBatchCountMenu();
            UpdateTrayTooltip();
            if (requestComposite && !_closing)
                RequestComposite();
        }

        private void StartNewBatch()
        {
            if (_closing || _exitPending)
                return;

            // A start or queued start belongs to the previous group. Invalidate
            // its UI callbacks before clearing the model so a delayed desktop
            // capture cannot reserve itself in the fresh group.
            Interlocked.Increment(ref _batchEpoch);
            _gesture.CancelCurrent();
            _invalidSessions.Clear();
            _pendingStarts.Clear();
            _captureClipboardSequences.Clear();
            _clipboardSequenceRebaseOrder.Clear();
            _clipboardSequenceRebases.Clear();
            if (_selectionSessionId != 0)
                ClearSelectionResources();

            _captureBatch.Clear();
            _latestPublishedBoundary = 0;
            _hasSuccessfulClipboardPublication = false;
            _trayFeedbackTimer.Stop();
            _trayStatus.ClearFeedback();
            OnBatchChanged(false);
        }

        private void OnPasteShortcutObserved(uint clipboardSequence)
        {
            if (_closing || !_hasSuccessfulClipboardPublication ||
                clipboardSequence != _lastSuccessfulClipboardSequence ||
                GetClipboardSequenceNumber() != _lastSuccessfulClipboardSequence)
                return;

            long boundary = _latestPublishedBoundary;
            InvalidateCompositeJobs(true);
            _captureBatch.ResetThrough(boundary);
            RemoveCaptureClipboardSequencesThrough(boundary);
            _hasSuccessfulClipboardPublication = false;
            _trayFeedbackTimer.Stop();
            _trayStatus.ClearFeedback();
            UpdateBatchCountMenu();
            UpdateTrayTooltip();
            // Keep the published bitmap on the clipboard. Any captures after
            // this publication boundary remain in memory until the next capture.
        }

        private void RemoveCaptureClipboardSequencesThrough(long boundarySessionId)
        {
            List<long> removed = new List<long>();
            foreach (KeyValuePair<long, uint> entry in _captureClipboardSequences)
            {
                if (entry.Key <= boundarySessionId)
                    removed.Add(entry.Key);
            }
            for (int i = 0; i < removed.Count; i++)
                _captureClipboardSequences.Remove(removed[i]);
        }

        private void PruneCaptureClipboardSequences()
        {
            if (_captureClipboardSequences.Count == 0)
                return;

            List<long> sessionIds = new List<long>(_captureClipboardSequences.Keys);
            for (int i = 0; i < sessionIds.Count; i++)
            {
                long sessionId = sessionIds[i];
                if (_captureBatch.ContainsSession(sessionId) ||
                    sessionId == _selectionSessionId || IsQueuedStart(sessionId))
                    continue;
                _captureClipboardSequences.Remove(sessionId);
            }
        }

        private bool IsQueuedStart(long sessionId)
        {
            foreach (SelectionStartRequest request in _pendingStarts)
            {
                if (request.SessionId == sessionId)
                    return true;
            }
            return false;
        }

        private uint GetCaptureClipboardSequence(long sessionId, uint fallback)
        {
            uint sequence;
            sequence = _captureClipboardSequences.TryGetValue(sessionId, out sequence)
                ? sequence : fallback;
            sequence = NormalizeClipboardSequence(sequence);
            if (_captureClipboardSequences.ContainsKey(sessionId))
                _captureClipboardSequences[sessionId] = sequence;
            return sequence;
        }

        private uint NormalizeClipboardSequence(uint sequence)
        {
            uint original = sequence;
            HashSet<uint> visited = new HashSet<uint>();
            for (int i = 0; i < MaxClipboardSequenceRebases; i++)
            {
                if (!visited.Add(sequence))
                    return original;

                uint publishedSequence;
                if (!_clipboardSequenceRebases.TryGetValue(sequence,
                    out publishedSequence))
                    return sequence;
                sequence = publishedSequence;
            }
            return sequence;
        }

        private void RequestComposite()
        {
            bool startWorker = false;
            lock (_compositeSync)
            {
                _compositeRequestId++;
                _compositeRequested = true;
                if (!_compositeWorkerRunning)
                {
                    _compositeWorkerRunning = true;
                    startWorker = true;
                }
            }

            _clipboardQueue.CancelPending();
            if (startWorker && !ThreadPool.QueueUserWorkItem(CompositeWorker))
            {
                lock (_compositeSync)
                {
                    _compositeRequested = false;
                    _compositeWorkerRunning = false;
                }
                ShowNotice(AppLocalization.Get(_language, AppText.CompositeFailure),
                    ToolTipIcon.Error);
            }
        }

        private void InvalidateCompositeJobs(bool cancelPublication)
        {
            lock (_compositeSync)
            {
                _compositeRequestId++;
                _compositeRequested = false;
            }
            if (cancelPublication && _clipboardQueue != null)
                _clipboardQueue.CancelPending();
        }

        private void CompositeWorker(object state)
        {
            while (true)
            {
                long requestId;
                lock (_compositeSync)
                {
                    if (_closing || !_compositeRequested)
                    {
                        _compositeWorkerRunning = false;
                        return;
                    }
                    requestId = _compositeRequestId;
                    _compositeRequested = false;
                }

                CaptureBatchSnapshot snapshot = null;
                Bitmap composite = null;
                Exception composeError = null;
                try
                {
                    snapshot = _captureBatch.CreateSnapshot();
                    if (snapshot != null)
                        composite = CaptureBatch.Compose(snapshot);
                }
                catch (Exception error)
                {
                    composeError = error;
                }

                bool retryLatest = false;
                bool isCurrent = false;
                lock (_compositeSync)
                {
                    if (_compositeRequestId != requestId || _compositeRequested)
                    {
                        retryLatest = _compositeRequested;
                        if (!retryLatest)
                            _compositeWorkerRunning = false;
                    }
                    else
                    {
                        _compositeWorkerRunning = false;
                        isCurrent = true;
                    }
                }

                if (!isCurrent)
                {
                    if (composite != null)
                        composite.Dispose();
                    if (snapshot != null)
                        snapshot.Dispose();
                    if (retryLatest)
                        continue;
                    return;
                }

                if (snapshot == null && composeError == null)
                    return;

                CaptureBatchSnapshot completedSnapshot = snapshot;
                Bitmap completedComposite = composite;
                Exception completedError = composeError;
                long completedRequestId = requestId;
                bool posted = PostToUi(delegate
                {
                    OnCompositeReady(completedSnapshot, completedComposite,
                        completedRequestId, completedError);
                });
                if (!posted)
                {
                    if (completedComposite != null)
                        completedComposite.Dispose();
                    if (completedSnapshot != null)
                        completedSnapshot.Dispose();
                }
                return;
            }
        }

        private void OnCompositeReady(CaptureBatchSnapshot snapshot, Bitmap composite,
            long requestId, Exception composeError)
        {
            if (composeError != null)
            {
                if (snapshot != null)
                    snapshot.Dispose();
                if (!_closing && IsCompositeRequestCurrent(requestId))
                    ShowNotice(AppLocalization.Get(_language, AppText.CompositeFailure),
                        ToolTipIcon.Error);
                return;
            }

            if (_closing || snapshot == null || composite == null ||
                !IsCompositeRequestCurrent(requestId) ||
                !_captureBatch.IsSnapshotCurrent(snapshot))
            {
                if (composite != null)
                    composite.Dispose();
                if (snapshot != null)
                    snapshot.Dispose();
                return;
            }

            uint expectedClipboardSequence = GetCaptureClipboardSequence(
                snapshot.ThroughSessionId, GetClipboardSequenceNumber());
            if (GetClipboardSequenceNumber() != expectedClipboardSequence)
            {
                composite.Dispose();
                snapshot.Dispose();
                return;
            }

            _clipboardQueue.Enqueue(composite, expectedClipboardSequence,
                snapshot.ThroughSessionId, snapshot.Version, requestId);
            snapshot.Dispose();
        }

        private bool IsCompositeRequestCurrent(long requestId)
        {
            lock (_compositeSync)
                return requestId == _compositeRequestId;
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
            InvalidateCompositeJobs(false);
            _clipboardQueue.Dispose();
            _captureBatch.Dispose();
            _selectionTimer.Dispose();
            _trayFeedbackTimer.Stop();
            _trayFeedbackTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIconImage.Dispose();
            _contextMenu.Dispose();
            _dispatcher.Dispose();
        }

        private void OnClipboardCopied(long boundarySessionId, long batchVersion,
            long requestId, uint expectedClipboardSequence, uint clipboardSequence)
        {
            if (_closing || !IsCompositeRequestCurrent(requestId) ||
                _captureBatch.Version != batchVersion ||
                GetClipboardSequenceNumber() != clipboardSequence)
                return;
            RecordClipboardSequenceRebase(expectedClipboardSequence, clipboardSequence);
            RebaseCaptureClipboardSequences(expectedClipboardSequence, clipboardSequence);
            _latestPublishedBoundary = boundarySessionId;
            _lastSuccessfulClipboardSequence = clipboardSequence;
            _hasSuccessfulClipboardPublication = true;
            _trayStatus.ShowCopied();
            UpdateTrayTooltip();
            RestartTrayFeedbackTimer();
            ShowNotice(AppLocalization.Get(_language, AppText.NoticeCopied), ToolTipIcon.Info);
        }

        private void RebaseCaptureClipboardSequences(uint expectedSequence,
            uint publishedSequence)
        {
            List<long> sessionIds = new List<long>(_captureClipboardSequences.Keys);
            for (int i = 0; i < sessionIds.Count; i++)
            {
                long sessionId = sessionIds[i];
                if (_captureClipboardSequences[sessionId] == expectedSequence)
                    _captureClipboardSequences[sessionId] = publishedSequence;
            }
        }

        private void RecordClipboardSequenceRebase(uint expectedSequence,
            uint publishedSequence)
        {
            if (expectedSequence == publishedSequence)
                return;

            if (!_clipboardSequenceRebases.ContainsKey(expectedSequence))
                _clipboardSequenceRebaseOrder.Enqueue(expectedSequence);
            _clipboardSequenceRebases[expectedSequence] = publishedSequence;
            while (_clipboardSequenceRebaseOrder.Count > MaxClipboardSequenceRebases)
            {
                uint oldestSequence = _clipboardSequenceRebaseOrder.Dequeue();
                _clipboardSequenceRebases.Remove(oldestSequence);
            }
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

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

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
            _selectionBatchEpoch = _batchEpoch;
            _desktopBounds = desktopBounds;
            _selectionStart = start;
            _captureClipboardSequences[session] = GetClipboardSequenceNumber();
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
            _selectionBatchEpoch = _batchEpoch;
            _desktopBounds = desktopBounds;
            _selectionStart = start;
            _captureClipboardSequences[session] = GetClipboardSequenceNumber();
            _hasPendingFinish = false;
            return session;
        }

        internal long QueuePendingSelectionForTesting(Rectangle desktopBounds, Point start)
        {
            if (_selectionSessionId == 0)
                throw new InvalidOperationException("There is no selection to queue behind.");

            long session = ++_nextTestSessionId;
            uint clipboardSequence = GetClipboardSequenceNumber();
            _captureClipboardSequences[session] = clipboardSequence;
            _pendingStarts.Enqueue(new SelectionStartRequest(session, start,
                desktopBounds, true, clipboardSequence, _batchEpoch));
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

        internal void CancelSelectionForTesting(long sessionId)
        {
            CancelSelection(sessionId);
        }

        internal void PublishCropForTesting(long sessionId, Bitmap image)
        {
            if (image == null)
                throw new ArgumentNullException("image");
            if (!_captureBatch.Reserve(sessionId, image.Size))
            {
                _captureClipboardSequences.Remove(sessionId);
                image.Dispose();
                return;
            }
            if (!_captureClipboardSequences.ContainsKey(sessionId))
                _captureClipboardSequences[sessionId] = GetClipboardSequenceNumber();
            OnBatchChanged(false);
            CompleteCrop(sessionId, image);
        }

        internal bool ReserveCaptureForTesting(long sessionId, Size size)
        {
            bool reserved = _captureBatch.Reserve(sessionId, size);
            if (reserved)
            {
                _captureClipboardSequences[sessionId] = GetClipboardSequenceNumber();
                OnBatchChanged(false);
            }
            return reserved;
        }

        internal void CompleteReservedCropForTesting(long sessionId, Bitmap image)
        {
            if (image == null)
                throw new ArgumentNullException("image");
            CompleteCrop(sessionId, image);
        }

        private void CompleteCrop(long sessionId, Bitmap image)
        {
            if (_closing)
            {
                image.Dispose();
                return;
            }

            CaptureBatchCompletionResult result = _captureBatch.Complete(sessionId, image);
            if (result == CaptureBatchCompletionResult.IgnoredStale)
            {
                _captureClipboardSequences.Remove(sessionId);
                return;
            }

            PruneCaptureClipboardSequences();
            OnBatchChanged(true);
            if (result == CaptureBatchCompletionResult.Rejected)
                ShowNotice(AppLocalization.Get(_language, AppText.BatchTooLarge),
                    ToolTipIcon.Warning);
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
            internal readonly uint ClipboardSequence;
            internal readonly long BatchEpoch;
            internal bool HasCompletion;
            internal Point End;

            internal SelectionStartRequest(long sessionId, Point start,
                uint clipboardSequence, long batchEpoch)
                : this(sessionId, start, Rectangle.Empty, false,
                    clipboardSequence, batchEpoch)
            {
            }

            internal SelectionStartRequest(long sessionId, Point start,
                Rectangle desktopBounds, bool waitForTestCapture,
                uint clipboardSequence, long batchEpoch)
            {
                SessionId = sessionId;
                Start = start;
                DesktopBounds = desktopBounds;
                WaitForTestCapture = waitForTestCapture;
                ClipboardSequence = clipboardSequence;
                BatchEpoch = batchEpoch;
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
            private const string PublicationTokenFormat =
                "DuoClick.BatchPublicationToken.1";
            private readonly WinFormsTimer _timer;
            private readonly Action<long, long, long, uint, uint> _onCopied;
            private readonly Action _onFailed;
            private readonly Func<uint> _getClipboardSequence;
            private PendingPublication _pending;
            private int _attempts;
            private bool _disposed;

            internal ClipboardCopyQueue(Action<long, long, long, uint, uint> onCopied,
                Action onFailed, Func<uint> getClipboardSequence)
            {
                _onCopied = onCopied;
                _onFailed = onFailed;
                _getClipboardSequence = getClipboardSequence;
                _timer = new WinFormsTimer();
                _timer.Interval = 100;
                _timer.Tick += OnTimerTick;
            }

            internal bool HasPendingItems
            {
                get { return _pending != null; }
            }

            internal void Enqueue(Bitmap image, uint expectedClipboardSequence,
                long boundarySessionId, long batchVersion, long requestId)
            {
                if (_disposed)
                {
                    image.Dispose();
                    return;
                }

                CancelPending();
                _pending = new PendingPublication(image, expectedClipboardSequence,
                    boundarySessionId, batchVersion, requestId);
                AttemptCurrent();
            }

            internal void CancelPending()
            {
                _timer.Stop();
                if (_pending != null)
                {
                    _pending.Image.Dispose();
                    _pending = null;
                }
                _attempts = 0;
            }

            private void OnTimerTick(object sender, EventArgs e)
            {
                _timer.Stop();
                AttemptCurrent();
            }

            private void AttemptCurrent()
            {
                if (_disposed || _pending == null)
                    return;

                PendingPublication publication = _pending;
                if (_getClipboardSequence() != publication.ExpectedClipboardSequence)
                {
                    // A user's later clipboard copy invalidates this image and
                    // every retry based on the earlier clipboard contents.
                    CancelPending();
                    return;
                }

                _attempts++;
                bool copied = false;
                try
                {
                    DataObject data = new DataObject();
                    data.SetData(DataFormats.Bitmap, publication.Image);
                    data.SetData(PublicationTokenFormat, false, publication.Token);
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
                    uint writtenSequence = _getClipboardSequence();
                    object token = null;
                    try { token = Clipboard.GetData(PublicationTokenFormat); }
                    catch (Exception) { }
                    uint validatedSequence = _getClipboardSequence();
                    // Sequence numbers have no fixed increment contract. Verify
                    // our unique marker is still present and that no later owner
                    // replaced the clipboard while it was being checked.
                    if (!publication.Token.Equals(token as string) ||
                        validatedSequence != writtenSequence)
                    {
                        CancelPending();
                        return;
                    }

                    _pending = null;
                    publication.Image.Dispose();
                    _attempts = 0;
                    _onCopied(publication.BoundarySessionId, publication.BatchVersion,
                        publication.RequestId, publication.ExpectedClipboardSequence,
                        validatedSequence);
                    return;
                }

                if (_getClipboardSequence() != publication.ExpectedClipboardSequence)
                {
                    CancelPending();
                    return;
                }

                if (_attempts >= MaximumAttempts)
                {
                    _pending.Image.Dispose();
                    _pending = null;
                    _attempts = 0;
                    _onFailed();
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

                CancelPending();
                _disposed = true;
                _timer.Dispose();
            }

            private sealed class PendingPublication
            {
                internal readonly Bitmap Image;
                internal readonly uint ExpectedClipboardSequence;
                internal readonly long BoundarySessionId;
                internal readonly long BatchVersion;
                internal readonly long RequestId;
                internal readonly string Token;

                internal PendingPublication(Bitmap image, uint expectedClipboardSequence,
                    long boundarySessionId, long batchVersion, long requestId)
                {
                    Image = image;
                    ExpectedClipboardSequence = expectedClipboardSequence;
                    BoundarySessionId = boundarySessionId;
                    BatchVersion = batchVersion;
                    RequestId = requestId;
                    Token = Guid.NewGuid().ToString("N");
                }
            }
        }
    }
}
