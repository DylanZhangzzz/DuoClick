using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DuoClick.Tests
{
    internal static class TestRunner
    {
        private static int _passed;
        private static int _failed;
        private const int CompositeMinimumWidth = 96;
        private const int CompositeHeaderHeight = 32;
        private const int CompositeImageGap = 8;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 1 || (args.Length == 1 && args[0] != "--integration" &&
                args[0] != "--previews"))
            {
                Console.WriteLine("Usage: DuoClick.Tests.exe [--integration | --previews]");
                return 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 1 && args[0] == "--previews")
            {
                WritePreviewArtifacts();
                return 0;
            }

            Run("left then right, left then right release", LeftThenRightLeftThenRightRelease);
            Run("left then right, right then left release", LeftThenRightRightThenLeftRelease);
            Run("right then left, left then right release", RightThenLeftLeftThenRightRelease);
            Run("right then left, right then left release", RightThenLeftRightThenLeftRelease);
            Run("cancel drains suppressed release without completion", CancellationDrainsSuppressedRelease);
            Run("rapid chord after completed chord gets a new session", RapidRechordGetsNewSession);
            Run("repress during a chord remains suppressed until drained", RepressDuringChordRemainsSuppressed);
            Run("single clicks are forwarded", SingleClicksAreForwarded);
            Run("disabled input does not start a chord", DisabledInputDoesNotStartChord);
            Run("pause on second press prevents chord start", PauseOnSecondPressPreventsChordStart);
            Run("unmatched initial release is forwarded", UnmatchedInitialReleaseIsForwarded);
            Run("paste shortcut state recognizes supported key edges", PasteShortcutStateRecognizesSupportedEdges);
            Run("selection geometry supports negative origins and reverse direction", GeometrySupportsNegativeOriginsAndReverseDirection);
            Run("selection geometry clips to desktop bounds", GeometryClipsToDesktopBounds);
            Run("selection minimum size is two pixels per side", GeometryEnforcesMinimumSize);
            Run("composite chrome keeps its minimum width and numbered headers", CompositeChromeMetricsAreStable);
            CaptureBatchTests.Run(Run);
            Run("localization catalog is complete and tray text fits Windows limits", LocalizationCatalogIsComplete);
            Run("language settings choose system default and persist selections", LanguageSettingsPersistSelections);
            Run("capture limit settings handle missing, invalid, and persisted values", CaptureLimitSettingsPersistValues);
            Run("tray feedback restores ready or paused status", TrayFeedbackRestoresBaseStatus);
            Run("language switch persists and restores localized paused status", LanguageSwitchPersistsAndRestoresTooltip);
            Run("embedded tray icon resource is a valid ICO", EmbeddedTrayIconIsValid);

            if (args.Length == 1)
            {
                Run("application capacity persists and evicts oldest captures first", ApplicationCapacityPersistsAndEvictsOldest);
                Run("lowering capacity keeps foreign clipboard content", LoweringCapacityPreservesForeignClipboard);
                Run("live desktop capture returns a disposable bitmap", LiveDesktopCaptureSmoke);
                Run("synthetic selection copies the expected crop and disposes overlay", SyntheticClipboardIntegrationSmoke);
                Run("three synthetic crops stitch in selection order", ThreeSyntheticCropsStitchInSelectionOrder);
                Run("matching paste shortcut resets group and preserves clipboard", MatchingPasteShortcutResetsGroupAndPreservesClipboard);
                Run("unrelated clipboard change cannot reset the group", UnrelatedClipboardChangeDoesNotResetGroup);
                Run("external clipboard update during capture is preserved", ExternalClipboardUpdateDuringCaptureIsPreserved);
                Run("own publication does not stale a newer selection", OwnPublicationDoesNotStaleNewerSelection);
                Run("canceled and too-small selections do not enter the batch", CanceledAndTooSmallSelectionsAreExcluded);
                Run("completion before capture readiness uses the queued end point", CompletionBeforeCaptureReadySmoke);
                Run("older queued crop cannot replace a newer session", OlderCropCannotReplaceNewerSession);
                Run("queued next selection completes after the active capture", QueuedSelectionCompletionSurvivesActiveCapture);
            }

            Console.WriteLine("Tests: " + _passed + " passed, " + _failed + " failed.");
            return _failed == 0 ? 0 : 1;
        }

        private static void LocalizationCatalogIsComplete()
        {
            Assert(AppLocalization.HasCompleteCatalog,
                "each supported language should define every user-facing string");

            AppLanguage[] languages = new AppLanguage[]
            {
                AppLanguage.English,
                AppLanguage.SimplifiedChinese
            };
            for (int i = 0; i < languages.Length; i++)
            {
                Assert(FormatTooltip(languages[i], AppText.TooltipReady, "999+").Length <= 63,
                    "ready tooltip should fit NotifyIcon's text limit");
                Assert(FormatTooltip(languages[i], AppText.TooltipPaused, "999+").Length <= 63,
                    "paused tooltip should fit NotifyIcon's text limit");
                Assert(FormatTooltip(languages[i], AppText.TooltipCopied, "999+").Length <= 63,
                    "copied tooltip should fit NotifyIcon's text limit");
                Assert(FormatTooltip(languages[i], AppText.TooltipFailed, "999+").Length <= 63,
                    "failed tooltip should fit NotifyIcon's text limit");
            }
        }

        private static void CompositeChromeMetricsAreStable()
        {
            AssertEqual(CompositeMinimumWidth, CaptureBatch.MinimumCompositeWidth,
                "composite minimum width");
            AssertEqual(CompositeHeaderHeight, CaptureBatch.CompositeHeaderHeight,
                "numbered header height");
            AssertEqual(CompositeImageGap, CaptureBatch.CompositeImageGap,
                "inter-image gap");
        }

        private static void LanguageSettingsPersistSelections()
        {
            string directory = Path.Combine(Path.GetTempPath(), "DuoClickTests",
                Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "language.txt");
            try
            {
                LanguageLoadResult missing = AppLanguageSettings.Load(path,
                    new CultureInfo("zh-CN"));
                AssertEqual(AppLanguage.SimplifiedChinese, missing.Language,
                    "first run with Chinese UI culture should select Simplified Chinese");
                Assert(missing.Error == null, "a missing language file should be expected");
                AssertEqual(AppLanguage.English,
                    AppLanguageSettings.GetSystemDefault(new CultureInfo("en-GB")),
                    "non-Chinese UI culture should select English");

                Exception error;
                Assert(AppLanguageSettings.TrySave(path, AppLanguage.SimplifiedChinese, out error),
                    "language preference should save: " + (error == null ? String.Empty : error.Message));
                AssertEqual("zh-CN", File.ReadAllText(path), "saved language code");
                LanguageLoadResult saved = AppLanguageSettings.Load(path,
                    new CultureInfo("en-US"));
                AssertEqual(AppLanguage.SimplifiedChinese, saved.Language,
                    "saved language should take precedence over system culture");
                Assert(saved.Error == null, "valid saved language should not report an error");
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        private static void CaptureLimitSettingsPersistValues()
        {
            string directory = Path.Combine(Path.GetTempPath(), "DuoClickTests",
                Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "capture-limit.txt");
            try
            {
                CaptureLimitLoadResult missing = CaptureLimitSettings.Load(path);
                AssertEqual(CaptureLimitSettings.DefaultCapacity, missing.Capacity,
                    "missing capture-limit file should use the default");
                Assert(missing.Error == null, "missing capture-limit file is expected on first run");

                Exception error;
                Assert(!CaptureLimitSettings.TrySave(path,
                    CaptureLimitSettings.MaximumCapacity + 1, out error),
                    "a value above the supported maximum should not be saved");
                Assert(error != null, "invalid capacity save should report its error");

                Assert(CaptureLimitSettings.TrySave(path,
                    CaptureLimitSettings.MaximumCapacity, out error),
                    "maximum capture limit should save: " +
                        (error == null ? String.Empty : error.Message));
                CaptureLimitLoadResult saved = CaptureLimitSettings.Load(path);
                AssertEqual(CaptureLimitSettings.MaximumCapacity, saved.Capacity,
                    "saved maximum capture limit should load");
                Assert(saved.Error == null, "valid capture-limit file should not report an error");

                File.WriteAllText(path, "101");
                CaptureLimitLoadResult invalid = CaptureLimitSettings.Load(path);
                AssertEqual(CaptureLimitSettings.DefaultCapacity, invalid.Capacity,
                    "out-of-range saved value should fall back to default");
                Assert(invalid.Error != null,
                    "out-of-range saved value should be reported as invalid");

                string languagePath = Path.Combine(directory, "language.txt");
                string applicationPath =
                    CaptureLimitSettings.GetPathForLanguageSettingsPath(languagePath);
                File.WriteAllText(applicationPath, "101");
                using (DuoClickApplicationContext context =
                    new DuoClickApplicationContext(false, false, languagePath))
                {
                    AssertEqual(CaptureLimitSettings.DefaultCapacity,
                        context.CaptureLimitForTesting,
                        "application should use default capacity for an invalid saved value");
                }
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        private static void ApplicationCapacityPersistsAndEvictsOldest()
        {
            string directory = Path.Combine(Path.GetTempPath(), "DuoClickTests",
                Guid.NewGuid().ToString("N"));
            string languagePath = Path.Combine(directory, "language.txt");
            DuoClickApplicationContext context = null;
            DuoClickApplicationContext reloadedContext = null;
            List<IDisposable> clipboardSnapshotValues = null;
            IDataObject originalClipboard = null;
            Bitmap copied = null;
            try
            {
                originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
                DataObject sentinel = new DataObject();
                sentinel.SetData("DuoClick.TestSentinel", false, Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, languagePath);
                AssertEqual(CaptureLimitSettings.DefaultCapacity,
                    context.CaptureLimitForTesting, "first-run application capacity");
                Assert(CaptureLimitSettings.MaximumCapacity == 100,
                    "the supported maximum should remain the documented 100 captures");
                Assert(context.ApplyCaptureLimitForTesting(CaptureLimitSettings.MaximumCapacity),
                    "application should apply and persist its maximum capture limit");
                context.ExitThread();
                context.Dispose();
                context = null;

                context = new DuoClickApplicationContext(false, false, languagePath);
                AssertEqual(CaptureLimitSettings.MaximumCapacity, context.CaptureLimitForTesting,
                    "maximum capture limit should persist across application contexts");
                Assert(context.ApplyCaptureLimitForTesting(3),
                    "application should apply and persist a three-capture limit");

                context.PublishCropForTesting(1, CreateSolidBitmap(1, 1, Color.Red));
                context.PublishCropForTesting(2, CreateSolidBitmap(1, 1, Color.Green));
                context.PublishCropForTesting(3, CreateSolidBitmap(1, 1, Color.Blue));
                context.PublishCropForTesting(4, CreateSolidBitmap(1, 1, Color.Yellow));
                WaitForClipboardQueueToDrain(context, TimeSpan.FromSeconds(10));

                copied = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(1, 1, 1), TimeSpan.FromSeconds(10));
                Assert(copied != null,
                    "three retained captures should publish a numbered composite");
                AssertEqual(3, context.BatchCountForTesting,
                    "configured capacity should bound the application batch");
                AssertSolidImageRegion(copied, CompositeContentTop(), 1, 1, Color.Green,
                    "oldest retained capture after FIFO eviction");
                AssertSolidImageRegion(copied, CompositeContentTop(1), 1, 1, Color.Blue,
                    "middle retained capture after FIFO eviction");
                AssertSolidImageRegion(copied, CompositeContentTop(1, 1), 1, 1, Color.Yellow,
                    "newest retained capture after FIFO eviction");

                context.ExitThread();
                context.Dispose();
                context = null;
                reloadedContext = new DuoClickApplicationContext(false, false, languagePath);
                AssertEqual(3, reloadedContext.CaptureLimitForTesting,
                    "application changes to the capture limit should persist");
            }
            finally
            {
                if (copied != null)
                    copied.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }
                if (reloadedContext != null)
                {
                    reloadedContext.ExitThread();
                    reloadedContext.Dispose();
                }

                if (originalClipboard != null)
                {
                    try { RestoreClipboard(originalClipboard); }
                    finally { DisposeAll(clipboardSnapshotValues); }
                }
                else if (clipboardSnapshotValues != null)
                    DisposeAll(clipboardSnapshotValues);

                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        private static void LoweringCapacityPreservesForeignClipboard()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            string directory = Path.Combine(Path.GetTempPath(), "DuoClickTests",
                Guid.NewGuid().ToString("N"));
            string languagePath = Path.Combine(directory, "language.txt");
            DuoClickApplicationContext context = null;
            Bitmap foreignImage = null;
            Bitmap observedImage = null;
            try
            {
                context = new DuoClickApplicationContext(false, false, languagePath);
                context.PublishCropForTesting(1, CreateSolidBitmap(2, 1, Color.Red));
                context.PublishCropForTesting(2, CreateSolidBitmap(2, 1, Color.Green));
                context.PublishCropForTesting(3, CreateSolidBitmap(2, 1, Color.Blue));
                WaitForClipboardQueueToDrain(context, TimeSpan.FromSeconds(10));

                const string markerFormat = "DuoClick.ForeignClipboardMarker";
                const string markerValue = "keep this external content";
                foreignImage = CreateSolidBitmap(3, 2, Color.Orange);
                DataObject unrelated = new DataObject();
                unrelated.SetData(markerFormat, false, markerValue);
                unrelated.SetData(DataFormats.Bitmap, foreignImage);
                SetClipboardForTesting(unrelated);

                Assert(context.ApplyCaptureLimitForTesting(2),
                    "lowering the capacity should succeed");
                WaitForClipboardQueueToDrain(context, TimeSpan.FromSeconds(10));
                AssertEqual(2, context.BatchCountForTesting,
                    "lowering capacity should retain only the newest two captures");

                IDataObject current = Clipboard.GetDataObject();
                Assert(current != null && current.GetDataPresent(markerFormat),
                    "capacity change should preserve a foreign clipboard marker");
                AssertEqual(markerValue, (string)current.GetData(markerFormat, false),
                    "foreign clipboard marker value should remain intact");
                string[] formats = current.GetFormats(false);
                observedImage = current.GetData(DataFormats.Bitmap, false) as Bitmap;
                Assert(observedImage != null,
                    "capacity change should preserve the foreign clipboard bitmap; formats: " +
                    String.Join(", ", formats));
                AssertEqual(Color.Orange.ToArgb(), observedImage.GetPixel(2, 1).ToArgb(),
                    "foreign bitmap pixels should remain intact");
            }
            finally
            {
                if (observedImage != null)
                    observedImage.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally
                {
                    if (foreignImage != null)
                        foreignImage.Dispose();
                    DisposeAll(clipboardSnapshotValues);
                }

                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        private static void TrayFeedbackRestoresBaseStatus()
        {
            TrayStatusModel status = new TrayStatusModel(AppLanguage.English);
            AssertEqual(FormatTooltip(AppLanguage.English, AppText.TooltipReady, "0"),
                status.Text, "initial tray tooltip should explain the gesture");
            status.ShowCopied();
            AssertEqual(FormatTooltip(AppLanguage.English, AppText.TooltipCopied, "0"),
                status.Text, "copy feedback should be visible temporarily");
            status.ClearFeedback();
            AssertEqual(FormatTooltip(AppLanguage.English, AppText.TooltipReady, "0"),
                status.Text, "copy feedback should restore ready status");

            status.SetPaused(true);
            status.ShowCopied();
            AssertEqual(FormatTooltip(AppLanguage.English, AppText.TooltipCopied, "0"),
                status.Text, "copy feedback should appear while paused");
            status.ClearFeedback();
            AssertEqual(FormatTooltip(AppLanguage.English, AppText.TooltipPaused, "0"),
                status.Text, "copy feedback should restore paused status");
            status.SetLanguage(AppLanguage.SimplifiedChinese);
            AssertEqual(FormatTooltip(AppLanguage.SimplifiedChinese,
                AppText.TooltipPaused, "0"), status.Text,
                "language switching should immediately localize the current status");
        }

        private static void EmbeddedTrayIconIsValid()
        {
            using (Icon icon = AppTrayIcon.TryLoadEmbedded(Assembly.GetExecutingAssembly()))
            {
                Assert(icon != null,
                    "test and app builds should embed DuoClick.AppIcon.ico");
                Assert(icon.Width > 0 && icon.Height > 0,
                    "embedded ICO should expose valid dimensions");
            }
        }

        private static void LanguageSwitchPersistsAndRestoresTooltip()
        {
            string directory = Path.Combine(Path.GetTempPath(), "DuoClickTests",
                Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "language.txt");
            DuoClickApplicationContext context = null;
            try
            {
                context = new DuoClickApplicationContext(false, false, path);
                Assert(!File.Exists(path),
                    "constructing an invisible test context should not write user settings");

                context.SetPausedForTesting(true);
                context.ShowCopiedFeedbackForTesting();
                AssertEqual(FormatTooltip(context.LanguageForTesting,
                    AppText.TooltipCopied, "0"), context.TrayTooltipForTesting,
                    "copy feedback should replace paused tooltip temporarily");
                context.ClearTooltipFeedbackForTesting();
                AssertEqual(FormatTooltip(context.LanguageForTesting,
                    AppText.TooltipPaused, "0"), context.TrayTooltipForTesting,
                    "clearing copy feedback should restore paused tooltip");

                context.ChangeLanguageForTesting(AppLanguage.SimplifiedChinese);
                AssertEqual(AppLanguage.SimplifiedChinese, context.LanguageForTesting,
                    "language should change immediately");
                AssertEqual("zh-CN", File.ReadAllText(path),
                    "language switch should persist in the selected settings path");
                AssertEqual(FormatTooltip(AppLanguage.SimplifiedChinese,
                    AppText.TooltipPaused, "0"), context.TrayTooltipForTesting,
                    "language change should immediately localize current paused status");
            }
            finally
            {
                if (context != null)
                    context.Dispose();
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                _failed++;
                Console.WriteLine("FAIL " + name + ": " + exception.Message);
            }
        }

        private static void LeftThenRightLeftThenRightRelease()
        {
            GestureState state = new GestureState();
            GestureTransition firstDown = state.Process(MouseButtonKind.Left, true, 10, 11, true);
            GestureTransition chordDown = state.Process(MouseButtonKind.Right, true, 20, 21, true);
            GestureTransition firstUp = state.Process(MouseButtonKind.Left, false, 30, 31, true);
            GestureTransition lastUp = state.Process(MouseButtonKind.Right, false, 40, 41, true);

            AssertTransition(firstDown, false, false, false, false, 0, 10, 11, "first down");
            AssertTransition(chordDown, true, true, false, false, 1, 20, 21, "second down");
            AssertTransition(firstUp, false, false, false, false, 0, 30, 31, "first button up");
            AssertTransition(lastUp, true, false, true, true, 1, 40, 41, "suppressed button up");
            Assert(state.AllButtonsReleased, "both buttons should be released at the end");
        }

        private static void LeftThenRightRightThenLeftRelease()
        {
            GestureState state = new GestureState();
            state.Process(MouseButtonKind.Left, true, 1, 2, true);
            GestureTransition chordDown = state.Process(MouseButtonKind.Right, true, 3, 4, true);
            GestureTransition firstUp = state.Process(MouseButtonKind.Right, false, 5, 6, true);
            GestureTransition lastUp = state.Process(MouseButtonKind.Left, false, 7, 8, true);

            Assert(chordDown.Started && chordDown.Suppress, "second down should start and be suppressed");
            AssertTransition(firstUp, true, false, false, false, 0, 5, 6, "suppressed button up");
            AssertTransition(lastUp, false, false, true, true, 1, 7, 8, "first button up");
        }

        private static void RightThenLeftLeftThenRightRelease()
        {
            GestureState state = new GestureState();
            GestureTransition firstDown = state.Process(MouseButtonKind.Right, true, 12, 13, true);
            GestureTransition chordDown = state.Process(MouseButtonKind.Left, true, 14, 15, true);
            GestureTransition firstUp = state.Process(MouseButtonKind.Right, false, 16, 17, true);
            GestureTransition lastUp = state.Process(MouseButtonKind.Left, false, 18, 19, true);

            AssertTransition(firstDown, false, false, false, false, 0, 12, 13, "first down");
            AssertTransition(chordDown, true, true, false, false, 1, 14, 15, "second down");
            AssertTransition(firstUp, false, false, false, false, 0, 16, 17, "first button up");
            AssertTransition(lastUp, true, false, true, true, 1, 18, 19, "suppressed button up");
        }

        private static void RightThenLeftRightThenLeftRelease()
        {
            GestureState state = new GestureState();
            state.Process(MouseButtonKind.Right, true, 1, 2, true);
            GestureTransition chordDown = state.Process(MouseButtonKind.Left, true, 3, 4, true);
            GestureTransition firstUp = state.Process(MouseButtonKind.Left, false, 5, 6, true);
            GestureTransition lastUp = state.Process(MouseButtonKind.Right, false, 7, 8, true);

            Assert(chordDown.Started && chordDown.Suppress, "second down should start and be suppressed");
            AssertTransition(firstUp, true, false, false, false, 0, 5, 6, "suppressed button up");
            AssertTransition(lastUp, false, false, true, true, 1, 7, 8, "first button up");
        }

        private static void CancellationDrainsSuppressedRelease()
        {
            GestureState state = new GestureState();
            GestureTransition firstDown = state.Process(MouseButtonKind.Left, true, 1, 1, true);
            GestureTransition chordDown = state.Process(MouseButtonKind.Right, true, 2, 2, true);
            long cancelledSession = state.CancelCurrent();
            GestureTransition forwardedUp = state.Process(MouseButtonKind.Left, false, 3, 3, true);
            GestureTransition suppressedUp = state.Process(MouseButtonKind.Right, false, 4, 4, true);

            Assert(!firstDown.Suppress, "first button down should be forwarded");
            Assert(chordDown.Started, "chord should be active before cancellation");
            AssertEqual(1L, cancelledSession, "cancelled session id");
            AssertTransition(forwardedUp, false, false, false, false, 0, 3, 3, "forwarded release");
            AssertTransition(suppressedUp, true, false, false, true, 0, 4, 4, "drained suppressed release");
            Assert(state.AllButtonsReleased, "cancelled gesture should drain all physical buttons");
        }

        private static void RapidRechordGetsNewSession()
        {
            GestureState state = new GestureState();
            state.Process(MouseButtonKind.Left, true, 1, 1, true);
            GestureTransition firstStart = state.Process(MouseButtonKind.Right, true, 2, 2, true);
            state.Process(MouseButtonKind.Right, false, 3, 3, true);
            state.Process(MouseButtonKind.Left, false, 4, 4, true);

            GestureTransition nextFirstDown = state.Process(MouseButtonKind.Right, true, 5, 5, true);
            GestureTransition secondStart = state.Process(MouseButtonKind.Left, true, 6, 6, true);

            AssertEqual(1L, firstStart.SessionId, "first chord session");
            Assert(!nextFirstDown.Suppress && !nextFirstDown.Started,
                "next gesture's first press should be forwarded immediately after release");
            Assert(secondStart.Suppress && secondStart.Started, "rapid second chord should start");
            AssertEqual(2L, secondStart.SessionId, "second chord should have a new session id");
        }

        private static void RepressDuringChordRemainsSuppressed()
        {
            GestureState state = new GestureState();
            GestureTransition firstDown = state.Process(MouseButtonKind.Left, true, 1, 1, true);
            GestureTransition chordDown = state.Process(MouseButtonKind.Right, true, 2, 2, true);
            GestureTransition firstUp = state.Process(MouseButtonKind.Left, false, 3, 3, true);
            GestureTransition repress = state.Process(MouseButtonKind.Left, true, 4, 4, true);
            GestureTransition repressUp = state.Process(MouseButtonKind.Left, false, 5, 5, true);
            GestureTransition finalUp = state.Process(MouseButtonKind.Right, false, 6, 6, true);

            Assert(!firstDown.Suppress && chordDown.Started, "initial chord should start normally");
            Assert(!firstUp.Suppress && !firstUp.AllReleased,
                "the first button release should be forwarded while the other remains held");
            Assert(repress.Suppress && !repress.Started && !repress.AllReleased,
                "a repress during the active chord should be suppressed");
            Assert(repressUp.Suppress && !repressUp.Completed && !repressUp.AllReleased,
                "the repress release should stay suppressed without ending the chord");
            Assert(finalUp.Suppress && finalUp.Completed && finalUp.AllReleased,
                "completion should occur only when the remaining button is released");
            AssertEqual(chordDown.SessionId, finalUp.SessionId, "completion session id");
        }

        private static void SingleClicksAreForwarded()
        {
            GestureState state = new GestureState();
            GestureTransition leftDown = state.Process(MouseButtonKind.Left, true, 10, 20, true);
            GestureTransition leftUp = state.Process(MouseButtonKind.Left, false, 11, 21, true);
            GestureTransition rightDown = state.Process(MouseButtonKind.Right, true, 12, 22, true);
            GestureTransition rightUp = state.Process(MouseButtonKind.Right, false, 13, 23, true);

            AssertTransition(leftDown, false, false, false, false, 0, 10, 20, "left click down");
            AssertTransition(leftUp, false, false, false, true, 0, 11, 21, "left click up");
            AssertTransition(rightDown, false, false, false, false, 0, 12, 22, "right click down");
            AssertTransition(rightUp, false, false, false, true, 0, 13, 23, "right click up");
        }

        private static void DisabledInputDoesNotStartChord()
        {
            GestureState state = new GestureState();
            GestureTransition firstDown = state.Process(MouseButtonKind.Left, true, 1, 2, false);
            GestureTransition secondDown = state.Process(MouseButtonKind.Right, true, 3, 4, false);
            GestureTransition firstUp = state.Process(MouseButtonKind.Left, false, 5, 6, false);
            GestureTransition secondUp = state.Process(MouseButtonKind.Right, false, 7, 8, false);

            AssertTransition(firstDown, false, false, false, false, 0, 1, 2, "disabled first down");
            AssertTransition(secondDown, false, false, false, false, 0, 3, 4, "disabled second down");
            AssertTransition(firstUp, false, false, false, false, 0, 5, 6, "disabled first up");
            AssertTransition(secondUp, false, false, false, true, 0, 7, 8, "disabled second up");
            Assert(state.AllButtonsReleased, "disabled click sequence should leave no button down");
        }

        private static void PauseOnSecondPressPreventsChordStart()
        {
            GestureState state = new GestureState();
            GestureTransition firstDown = state.Process(MouseButtonKind.Left, true, 1, 2, true);
            GestureTransition pausedSecondDown = state.Process(MouseButtonKind.Right, true, 3, 4, false);
            GestureTransition firstUp = state.Process(MouseButtonKind.Left, false, 5, 6, false);
            GestureTransition secondUp = state.Process(MouseButtonKind.Right, false, 7, 8, true);

            Assert(!firstDown.Suppress && !firstDown.Started, "first press should be forwarded");
            Assert(!pausedSecondDown.Suppress && !pausedSecondDown.Started,
                "a second press received while paused should not start or suppress a chord");
            Assert(!firstUp.Suppress && !firstUp.Completed,
                "first release should remain forwarded with no active chord");
            Assert(!secondUp.Suppress && !secondUp.Completed && secondUp.AllReleased,
                "resuming while the buttons drain should not start a chord retroactively");
        }

        private static void UnmatchedInitialReleaseIsForwarded()
        {
            GestureState state = new GestureState();
            GestureTransition release = state.Process(MouseButtonKind.Right, false, 31, 32, true);

            AssertTransition(release, false, false, false, true, 0, 31, 32,
                "release without an observed press");
            Assert(state.AllButtonsReleased, "unmatched release should leave state released");
        }

        private static void PasteShortcutStateRecognizesSupportedEdges()
        {
            const int vKey = 0x56;
            const int insertKey = 0x2D;
            PasteShortcutState state = new PasteShortcutState();

            Assert(state.Observe(vKey, true, false, true, false, false, false),
                "Ctrl+V keydown should identify a paste shortcut");
            Assert(!state.Observe(vKey, true, false, true, false, false, false),
                "repeated Ctrl+V keydown should be ignored until keyup");
            Assert(!state.Observe(vKey, false, true, false, false, false, false),
                "Ctrl+V keyup should only clear the pressed state");
            Assert(state.Observe(vKey, true, false, true, false, false, false),
                "a new Ctrl+V edge after keyup should be recognized");

            PasteShortcutState insertState = new PasteShortcutState();
            Assert(insertState.Observe(insertKey, true, false, false, true, false, false),
                "Shift+Insert keydown should identify a paste shortcut");

            PasteShortcutState rejected = new PasteShortcutState();
            Assert(!rejected.Observe(vKey, true, false, false, false, false, false),
                "V without Ctrl should not be recognized");
            Assert(!rejected.Observe(vKey, false, true, false, false, false, false),
                "V release should reset key edge state");
            Assert(!rejected.Observe(vKey, true, false, true, true, false, false),
                "Ctrl+Shift+V should not be recognized as paste");
            Assert(!rejected.Observe(vKey, false, true, false, false, false, false),
                "V release should reset key edge state after modified shortcut");
            Assert(!rejected.Observe(insertKey, true, false, true, true, false, false),
                "Ctrl+Shift+Insert should not be recognized as paste");
            Assert(!rejected.Observe(insertKey, false, true, false, false, false, false),
                "Insert release should reset key edge state");
            Assert(!rejected.Observe(vKey, true, false, true, false, true, false),
                "Alt+Ctrl+V should not be recognized as paste");
            Assert(!rejected.Observe(insertKey, true, false, false, true, false, true),
                "Win+Shift+Insert should not be recognized as paste");
        }

        private static void GeometrySupportsNegativeOriginsAndReverseDirection()
        {
            Rectangle bounds = new Rectangle(-1920, -100, 3840, 1180);
            Rectangle expected = new Rectangle(-100, 100, 400, 700);
            Rectangle actual = SelectionGeometry.GetRectangle(bounds,
                new Point(300, 800), new Point(-100, 100));

            AssertEqual(expected, actual, "reverse-drag selection rectangle");
            Assert(SelectionGeometry.IsLargeEnough(actual), "large selection should be accepted");
        }

        private static void GeometryClipsToDesktopBounds()
        {
            Rectangle bounds = new Rectangle(-1920, -1080, 3840, 2160);
            Rectangle expected = new Rectangle(-1920, -1080, 20, 80);
            Rectangle actual = SelectionGeometry.GetRectangle(bounds,
                new Point(-2000, -1100), new Point(-1900, -1000));

            AssertEqual(expected, actual, "clipped selection rectangle");
        }

        private static void GeometryEnforcesMinimumSize()
        {
            Assert(!SelectionGeometry.IsLargeEnough(new Rectangle(0, 0, 1, 2)),
                "one-pixel width should be rejected");
            Assert(!SelectionGeometry.IsLargeEnough(new Rectangle(0, 0, 2, 1)),
                "one-pixel height should be rejected");
            Assert(SelectionGeometry.IsLargeEnough(new Rectangle(0, 0, 2, 2)),
                "two-by-two selection should be accepted");
            Assert(!SelectionGeometry.IsLargeEnough(Rectangle.Empty),
                "empty selection should be rejected");
        }

        private static void LiveDesktopCaptureSmoke()
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
            Assert(bounds.Width > 0 && bounds.Height > 0,
                "interactive desktop must report positive virtual-screen dimensions");

            MethodInfo capture = typeof(DuoClickApplicationContext).GetMethod(
                "CaptureDesktop", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(capture != null, "private desktop capture method should exist");

            Bitmap image = null;
            try
            {
                image = (Bitmap)capture.Invoke(null, new object[] { bounds });
                Assert(image != null, "desktop capture should return an image");
                AssertEqual(bounds.Width, image.Width, "captured image width");
                AssertEqual(bounds.Height, image.Height, "captured image height");
            }
            catch (TargetInvocationException exception)
            {
                throw new Exception("desktop capture failed: " + exception.InnerException);
            }
            finally
            {
                if (image != null)
                    image.Dispose();
            }
        }

        private static void SyntheticClipboardIntegrationSmoke()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            DuoClickApplicationContext context = null;
            Bitmap copied = null;
            try
            {
                DataObject sentinel = new DataObject();
                sentinel.SetData("DuoClick.TestSentinel", false, Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, CreateTemporaryLanguageSettingsPath());

                Rectangle bounds = new Rectangle(-100, -50, 12, 10);
                using (Bitmap source = CreatePatternBitmap(bounds.Size))
                {
                    long sessionId = context.BeginSelectionWithImageForTesting(source, bounds,
                        new Point(-96, -47));
                    Assert(context.HasSelectionOverlayForTesting,
                        "selection overlay should be visible after selection starts");

                    context.CompleteSelectionForTesting(sessionId, new Point(-90, -41));
                    Assert(!context.HasSelectionOverlayForTesting,
                        "selection overlay should be disposed after completion");
                }

                copied = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(6), TimeSpan.FromSeconds(10));
                Assert(copied != null,
                    "one crop should reach the clipboard with numbered composite chrome");
                AssertPatternImageRegion(copied, CompositeHeaderHeight, 4, 3, 6, 6,
                    "cropped image pixel");
            }
            finally
            {
                if (copied != null)
                    copied.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally { DisposeAll(clipboardSnapshotValues); }
            }
        }

        private static void ThreeSyntheticCropsStitchInSelectionOrder()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            DuoClickApplicationContext context = null;
            Bitmap copied = null;
            try
            {
                DataObject sentinel = new DataObject();
                sentinel.SetData("DuoClick.TestSentinel", false, Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, CreateTemporaryLanguageSettingsPath());
                context.PublishCropForTesting(1, CreateSolidBitmap(4, 2, Color.Red));
                context.PublishCropForTesting(2, CreateSolidBitmap(2, 3, Color.Green));
                context.PublishCropForTesting(3, CreateSolidBitmap(3, 1, Color.Blue));

                copied = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(2, 3, 1), TimeSpan.FromSeconds(10));
                Assert(copied != null,
                    "three accepted crops should create a numbered composite clipboard bitmap");
                AssertEqual(3, context.BatchCountForTesting, "batch should retain all three captures");
                int secondHeaderTop = CompositeContentTop(2) - CompositeHeaderHeight;
                int thirdHeaderTop = CompositeContentTop(2, 3) - CompositeHeaderHeight;
                AssertNumberedHeader(copied, 0, "first composite header");
                AssertNumberedHeader(copied, secondHeaderTop, "second composite header");
                AssertNumberedHeader(copied, thirdHeaderTop, "third composite header");
                AssertHeaderRegionsDiffer(copied, 0, secondHeaderTop,
                    "the capture number should change between headers");
                AssertSolidImageRegion(copied, CompositeContentTop(), 4, 2, Color.Red,
                    "first stitched crop");
                AssertSolidImageRegion(copied, CompositeContentTop(2), 2, 3, Color.Green,
                    "second stitched crop");
                AssertSolidImageRegion(copied, CompositeContentTop(2, 3), 3, 1, Color.Blue,
                    "third stitched crop");
            }
            finally
            {
                if (copied != null)
                    copied.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally { DisposeAll(clipboardSnapshotValues); }
            }
        }

        private static void MatchingPasteShortcutResetsGroupAndPreservesClipboard()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            DuoClickApplicationContext context = null;
            Bitmap published = null;
            Bitmap preserved = null;
            Bitmap nextBatch = null;
            try
            {
                DataObject sentinel = new DataObject();
                sentinel.SetData("DuoClick.TestSentinel", false, Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, CreateTemporaryLanguageSettingsPath());
                context.PublishCropForTesting(1, CreateSolidBitmap(4, 2, Color.Red));
                published = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(2), TimeSpan.FromSeconds(10));
                Assert(published != null, "first capture should be published before reset");
                AssertEqual(1, context.BatchCountForTesting, "first capture should be in the group");

                uint publishedSequence = context.ClipboardSequenceForTesting;
                context.ObservePasteShortcutForTesting(publishedSequence);
                AssertEqual(0, context.BatchCountForTesting,
                    "paste shortcut against the current published bitmap should reset the group");

                preserved = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(2), TimeSpan.FromSeconds(1));
                Assert(preserved != null, "reset should leave the published clipboard bitmap intact");
                AssertBitmapsEqual(published, preserved, "clipboard pixels should survive reset");

                context.PublishCropForTesting(2, CreateSolidBitmap(5, 1, Color.Blue));
                nextBatch = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(1), TimeSpan.FromSeconds(10));
                Assert(nextBatch != null, "next capture should publish as a one-capture group");
                AssertEqual(1, context.BatchCountForTesting,
                    "next group should contain only the capture made after reset");
                AssertSolidImageRegion(nextBatch, CompositeHeaderHeight, 5, 1, Color.Blue,
                    "next-group crop");
            }
            finally
            {
                if (published != null)
                    published.Dispose();
                if (preserved != null)
                    preserved.Dispose();
                if (nextBatch != null)
                    nextBatch.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally { DisposeAll(clipboardSnapshotValues); }
            }
        }

        private static void UnrelatedClipboardChangeDoesNotResetGroup()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            DuoClickApplicationContext context = null;
            Bitmap published = null;
            try
            {
                DataObject sentinel = new DataObject();
                sentinel.SetData("DuoClick.TestSentinel", false, Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, CreateTemporaryLanguageSettingsPath());
                context.PublishCropForTesting(1, CreateSolidBitmap(4, 2, Color.Red));
                published = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(2), TimeSpan.FromSeconds(10));
                Assert(published != null, "capture should be published before foreign clipboard update");
                uint publishedSequence = context.ClipboardSequenceForTesting;

                DataObject unrelated = new DataObject();
                unrelated.SetData("DuoClick.UnrelatedClipboardData", false,
                    Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(unrelated);
                uint unrelatedSequence = context.ClipboardSequenceForTesting;
                Assert(unrelatedSequence != publishedSequence,
                    "unrelated clipboard write should advance the sequence number");
                AssertEqual(1, context.BatchCountForTesting,
                    "clipboard update without a paste shortcut should leave captures grouped");

                context.ObservePasteShortcutForTesting(publishedSequence);
                AssertEqual(1, context.BatchCountForTesting,
                    "paste callback captured before a clipboard change should be ignored");
                context.ObservePasteShortcutForTesting(unrelatedSequence);
                AssertEqual(1, context.BatchCountForTesting,
                    "paste callback for unrelated clipboard data should be ignored");
            }
            finally
            {
                if (published != null)
                    published.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally { DisposeAll(clipboardSnapshotValues); }
            }
        }

        private static void ExternalClipboardUpdateDuringCaptureIsPreserved()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            DuoClickApplicationContext context = null;
            Bitmap externalImage = null;
            Bitmap observedImage = null;
            string stage = "initial clipboard setup";
            try
            {
                DataObject sentinel = new DataObject();
                sentinel.SetData("DuoClick.TestSentinel", false, Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, CreateTemporaryLanguageSettingsPath());
                stage = "capture reservation";
                Assert(context.ReserveCaptureForTesting(1, new Size(4, 2)),
                    "capture should reserve its place before crop completion");

                const string markerFormat = "DuoClick.UnrelatedClipboardData";
                const string markerValue = "external clipboard update";
                externalImage = CreateSolidBitmap(2, 2, Color.Orange);
                DataObject unrelated = new DataObject();
                unrelated.SetData(DataFormats.Bitmap, externalImage);
                unrelated.SetData(markerFormat, false, markerValue);
                stage = "external clipboard write";
                SetClipboardForTesting(unrelated);

                stage = "crop completion and asynchronous composition";
                context.CompleteReservedCropForTesting(1,
                    CreateSolidBitmap(4, 2, Color.Red));

                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                stage = "composition completion";
                while (context.HasPendingClipboardCopyForTesting && DateTime.UtcNow < deadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }
                Application.DoEvents();
                Assert(!context.HasPendingClipboardCopyForTesting,
                    "capture composition should finish or be discarded within the timeout");
                AssertEqual(1, context.BatchCountForTesting,
                    "external clipboard update should not discard the completed capture");

                stage = "external clipboard verification";
                IDataObject current = Clipboard.GetDataObject();
                Assert(current != null && current.GetDataPresent(markerFormat),
                    "capture completion should not replace unrelated clipboard data");
                AssertEqual(markerValue, (string)current.GetData(markerFormat, false),
                    "external clipboard marker should remain available");
                observedImage = current.GetData(DataFormats.Bitmap, false) as Bitmap;
                Assert(observedImage != null, "external bitmap should remain on the clipboard");
                AssertEqual(Color.Orange.ToArgb(), observedImage.GetPixel(1, 1).ToArgb(),
                    "external bitmap pixel should remain intact");
            }
            catch (Exception exception)
            {
                throw new Exception(stage + ": " + exception.Message);
            }
            finally
            {
                if (observedImage != null)
                    observedImage.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally
                {
                    if (externalImage != null)
                        externalImage.Dispose();
                    DisposeAll(clipboardSnapshotValues);
                }
            }
        }

        private static void OwnPublicationDoesNotStaleNewerSelection()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            DuoClickApplicationContext context = null;
            Bitmap firstPublished = null;
            Bitmap combined = null;
            try
            {
                DataObject sentinel = new DataObject();
                sentinel.SetData("DuoClick.TestSentinel", false, Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, CreateTemporaryLanguageSettingsPath());
                context.PublishCropForTesting(1, CreateSolidBitmap(4, 2, Color.Red));

                Rectangle bounds = new Rectangle(-100, -50, 12, 10);
                Point start = new Point(-98, -48);
                Point end = new Point(-95, -45);
                long nextSession = context.BeginPendingSelectionForTesting(bounds, start);

                // The next selection records the current clipboard sequence before
                // the first composition is allowed to publish.
                firstPublished = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(2), TimeSpan.FromSeconds(10));
                Assert(firstPublished != null,
                    "first composition should publish while next capture is active");

                context.CompleteSelectionForTesting(nextSession, end);
                using (Bitmap source = CreateSolidBitmap(bounds.Width, bounds.Height, Color.Green))
                    context.CompleteCaptureForTesting(nextSession, bounds, source);

                combined = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(2, 3), TimeSpan.FromSeconds(10));
                Assert(combined != null,
                    "the active selection should publish after the app's own earlier clipboard write");
                AssertEqual(2, context.BatchCountForTesting,
                    "both the first capture and active selection should remain grouped");
                AssertSolidImageRegion(combined, CompositeContentTop(), 4, 2, Color.Red,
                    "first rebased crop");
                AssertSolidImageRegion(combined, CompositeContentTop(2), 3, 3, Color.Green,
                    "second rebased crop");
            }
            finally
            {
                if (firstPublished != null)
                    firstPublished.Dispose();
                if (combined != null)
                    combined.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally { DisposeAll(clipboardSnapshotValues); }
            }
        }

        private static void CanceledAndTooSmallSelectionsAreExcluded()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            DuoClickApplicationContext context = null;
            try
            {
                const string markerFormat = "DuoClick.TestSentinel";
                const string markerValue = "keep clipboard";
                DataObject sentinel = new DataObject();
                sentinel.SetData(markerFormat, false, markerValue);
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, CreateTemporaryLanguageSettingsPath());
                Rectangle bounds = new Rectangle(-100, -50, 12, 10);
                Point start = new Point(-96, -47);
                using (Bitmap source = CreatePatternBitmap(bounds.Size))
                {
                    long cancelledSession = context.BeginSelectionWithImageForTesting(
                        source, bounds, start);
                    context.CancelSelectionForTesting(cancelledSession);
                }
                AssertEqual(0, context.BatchCountForTesting,
                    "canceling before completion should leave the batch empty");
                Assert(!context.HasSelectionOverlayForTesting,
                    "canceling should dispose the selection overlay");

                using (Bitmap source = CreatePatternBitmap(bounds.Size))
                {
                    long tinySession = context.BeginSelectionWithImageForTesting(
                        source, bounds, start);
                    context.CompleteSelectionForTesting(tinySession,
                        new Point(start.X + 1, start.Y + 1));
                }
                AssertEqual(0, context.BatchCountForTesting,
                    "a one by one selection should not be added to the batch");
                Assert(!context.HasSelectionOverlayForTesting,
                    "rejecting a too-small selection should dispose the overlay");

                IDataObject current = Clipboard.GetDataObject();
                Assert(current != null && current.GetDataPresent(markerFormat),
                    "invalid selections should leave the clipboard untouched");
                AssertEqual(markerValue, (string)current.GetData(markerFormat, false),
                    "clipboard marker should survive canceled and too-small selections");
                Assert(!current.GetDataPresent(DataFormats.Bitmap),
                    "invalid selections should not publish a screenshot");
            }
            finally
            {
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally { DisposeAll(clipboardSnapshotValues); }
            }
        }

        private static void CompletionBeforeCaptureReadySmoke()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            DuoClickApplicationContext context = null;
            Bitmap copied = null;
            try
            {
                DataObject sentinel = new DataObject();
                sentinel.SetData("DuoClick.TestSentinel", false, Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, CreateTemporaryLanguageSettingsPath());
                Rectangle bounds = new Rectangle(-100, -50, 12, 10);
                Point start = new Point(-96, -47);
                Point end = new Point(-90, -41);
                long sessionId = context.BeginPendingSelectionForTesting(bounds, start);

                // Store completion before delivering the frozen image, which
                // deterministically exercises the pending-finish path.
                context.CompleteSelectionForTesting(sessionId, end);
                using (Bitmap source = CreatePatternBitmap(bounds.Size))
                    context.CompleteCaptureForTesting(sessionId, source);

                copied = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(6), TimeSpan.FromSeconds(10));
                Assert(copied != null,
                    "queued finish point should produce a numbered 6 by 6 clipboard crop");
                Assert(!context.HasSelectionOverlayForTesting,
                    "overlay should be disposed after early completion is drained");

                AssertPatternImageRegion(copied, CompositeHeaderHeight, 4, 3, 6, 6,
                    "early-finish cropped image pixel");
            }
            finally
            {
                if (copied != null)
                    copied.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally { DisposeAll(clipboardSnapshotValues); }
            }
        }

        private static void OlderCropCannotReplaceNewerSession()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            DuoClickApplicationContext context = null;
            Bitmap copied = null;
            try
            {
                DataObject sentinel = new DataObject();
                sentinel.SetData("DuoClick.TestSentinel", false, Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, CreateTemporaryLanguageSettingsPath());

                context.PublishCropForTesting(200, CreateSolidBitmap(7, 5, Color.Blue));
                context.PublishCropForTesting(199, CreateSolidBitmap(2, 2, Color.Red));

                copied = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(5), TimeSpan.FromSeconds(10));
                Assert(copied != null, "newer session crop should reach the clipboard");
                AssertEqual(Color.Blue.ToArgb(), copied.GetPixel(0, CompositeHeaderHeight).ToArgb(),
                    "clipboard should retain the newer session image");
                AssertEqual(Color.Blue.ToArgb(), copied.GetPixel(6, CompositeHeaderHeight + 4).ToArgb(),
                    "older crop should not replace the newer image");
            }
            finally
            {
                if (copied != null)
                    copied.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally { DisposeAll(clipboardSnapshotValues); }
            }
        }

        private static void QueuedSelectionCompletionSurvivesActiveCapture()
        {
            List<IDisposable> clipboardSnapshotValues;
            IDataObject originalClipboard = CaptureClipboardSnapshot(out clipboardSnapshotValues);
            DuoClickApplicationContext context = null;
            Bitmap copied = null;
            try
            {
                DataObject sentinel = new DataObject();
                sentinel.SetData("DuoClick.TestSentinel", false, Guid.NewGuid().ToString("N"));
                SetClipboardForTesting(sentinel);

                context = new DuoClickApplicationContext(false, false, CreateTemporaryLanguageSettingsPath());
                Rectangle firstBounds = new Rectangle(-100, -50, 12, 10);
                Rectangle nextBounds = new Rectangle(-200, -80, 10, 8);
                Point firstStart = new Point(-96, -47);
                Point firstEnd = new Point(-90, -41);
                Point nextStart = new Point(-198, -78);
                Point nextEnd = new Point(-194, -75);

                long firstSession = context.BeginPendingSelectionForTesting(firstBounds, firstStart);
                long nextSession = context.QueuePendingSelectionForTesting(nextBounds, nextStart);

                // B completes while A is still waiting for its frozen image.
                context.CompleteSelectionForTesting(nextSession, nextEnd);
                context.CompleteSelectionForTesting(firstSession, firstEnd);

                using (Bitmap firstImage = CreatePatternBitmap(firstBounds.Size))
                    context.CompleteCaptureForTesting(firstSession, firstBounds, firstImage);

                // Completing A starts queued B and replays B's saved finish.
                using (Bitmap nextImage = CreateSolidBitmap(nextBounds.Width, nextBounds.Height, Color.Green))
                    context.CompleteCaptureForTesting(nextSession, nextBounds, nextImage);

                Assert(!context.HasSelectionOverlayForTesting,
                    "both completed selections should release their overlays");
                copied = WaitForExpectedClipboardBitmap(CompositeMinimumWidth,
                    CompositeHeight(6, 3), TimeSpan.FromSeconds(10));
                Assert(copied != null,
                    "queued captures should produce a numbered vertically stitched bitmap");
                AssertEqual(2, context.BatchCountForTesting,
                    "both queued selections should remain in the batch");
                AssertPatternImageRegion(copied, CompositeContentTop(), 4, 3, 6, 6,
                    "first queued crop pixel");
                AssertSolidImageRegion(copied, CompositeContentTop(6), 4, 3, Color.Green,
                    "second queued crop");
            }
            finally
            {
                if (copied != null)
                    copied.Dispose();
                if (context != null)
                {
                    context.ExitThread();
                    context.Dispose();
                }

                try { RestoreClipboard(originalClipboard); }
                finally { DisposeAll(clipboardSnapshotValues); }
            }
        }

        private static Bitmap WaitForExpectedClipboardBitmap(int width, int height,
            TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                try
                {
                    IDataObject data = Clipboard.GetDataObject();
                    if (data != null && data.GetDataPresent(DataFormats.Bitmap))
                    {
                        Bitmap image = data.GetData(DataFormats.Bitmap) as Bitmap;
                        if (image != null)
                        {
                            try
                            {
                                if (image.Width == width && image.Height == height)
                                    return (Bitmap)image.Clone();
                            }
                            finally
                            {
                                image.Dispose();
                            }
                        }
                    }
                }
                catch (ExternalException)
                {
                    // The app's clipboard queue retries while this STA pumps messages.
                }
                Thread.Sleep(10);
            }
            return null;
        }

        private static string CreateTemporaryLanguageSettingsPath()
        {
            return Path.Combine(Path.GetTempPath(), "DuoClickTests",
                Guid.NewGuid().ToString("N"), "language.txt");
        }

        private static void WaitForClipboardQueueToDrain(DuoClickApplicationContext context,
            TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (context.HasPendingClipboardCopyForTesting && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Application.DoEvents();
            Assert(!context.HasPendingClipboardCopyForTesting,
                "composite and clipboard work should finish within the timeout");
        }

        private static int CompositeHeight(params int[] imageHeights)
        {
            if (imageHeights == null || imageHeights.Length == 0)
                throw new ArgumentException("At least one capture height is required.", "imageHeights");

            int height = imageHeights.Length * CompositeHeaderHeight +
                (imageHeights.Length - 1) * CompositeImageGap;
            for (int i = 0; i < imageHeights.Length; i++)
                height += imageHeights[i];
            return height;
        }

        private static int CompositeContentTop(params int[] precedingImageHeights)
        {
            int top = CompositeHeaderHeight;
            if (precedingImageHeights == null)
                return top;

            for (int i = 0; i < precedingImageHeights.Length; i++)
                top += precedingImageHeights[i] + CompositeImageGap + CompositeHeaderHeight;
            return top;
        }

        private static void AssertPatternImageRegion(Bitmap composite, int contentTop,
            int sourceX, int sourceY, int width, int height, string message)
        {
            AssertImageRegion(composite, contentTop, width, height,
                delegate(int x, int y) { return PatternColor(sourceX + x, sourceY + y); },
                message);
        }

        private static void AssertSolidImageRegion(Bitmap composite, int contentTop,
            int width, int height, Color color, string message)
        {
            AssertImageRegion(composite, contentTop, width, height,
                delegate { return color; }, message);
        }

        private static void AssertImageRegion(Bitmap composite, int contentTop,
            int width, int height, Func<int, int, Color> expectedPixel, string message)
        {
            Assert(composite != null, message + " composite should exist");
            Assert(contentTop >= 0 && contentTop + height <= composite.Height,
                message + " should fit within composite height");
            Assert(width <= composite.Width,
                message + " should fit within composite width");
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    AssertEqual(expectedPixel(x, y).ToArgb(),
                        composite.GetPixel(x, contentTop + y).ToArgb(),
                        message + " pixel " + x + "," + y);
            }
        }

        private static void AssertNumberedHeader(Bitmap composite, int headerTop, string message)
        {
            AssertEqual(Color.FromArgb(240, 240, 240).ToArgb(),
                composite.GetPixel(0, headerTop).ToArgb(), message + " background");
            int darkTextPixels = 0;
            for (int y = headerTop; y < headerTop + CompositeHeaderHeight - 1; y++)
            {
                for (int x = 8; x < composite.Width; x++)
                {
                    Color pixel = composite.GetPixel(x, y);
                    if (pixel.R < 160 && pixel.G < 160 && pixel.B < 160)
                        darkTextPixels++;
                }
            }
            Assert(darkTextPixels > 0, message + " should contain its capture label");
        }

        private static void AssertHeaderRegionsDiffer(Bitmap composite, int firstHeaderTop,
            int secondHeaderTop, string message)
        {
            bool differs = false;
            for (int y = 0; y < CompositeHeaderHeight - 1 && !differs; y++)
            {
                for (int x = 8; x < composite.Width; x++)
                {
                    if (composite.GetPixel(x, firstHeaderTop + y).ToArgb() !=
                        composite.GetPixel(x, secondHeaderTop + y).ToArgb())
                    {
                        differs = true;
                        break;
                    }
                }
            }
            Assert(differs, message);
        }

        private static Bitmap CreatePatternBitmap(Size size)
        {
            Bitmap image = new Bitmap(size.Width, size.Height);
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                    image.SetPixel(x, y, PatternColor(x, y));
            }
            return image;
        }

        private static Bitmap CreateSolidBitmap(int width, int height, Color color)
        {
            Bitmap image = new Bitmap(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    image.SetPixel(x, y, color);
            }
            return image;
        }

        private static void WritePreviewArtifacts()
        {
            DirectoryInfo projectRoot = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (projectRoot != null &&
                !File.Exists(Path.Combine(projectRoot.FullName, "build.ps1")))
                projectRoot = projectRoot.Parent;
            if (projectRoot == null)
                throw new DirectoryNotFoundException(
                    "Could not locate build.ps1 to find the project output directory.");

            string outputDirectory = Path.Combine(projectRoot.FullName,
                "dist", "v0.3.0");
            Directory.CreateDirectory(outputDirectory);

            using (CaptureBatch batch = new CaptureBatch(3))
            {
                Color[] colors = new Color[]
                {
                    Color.FromArgb(245, 139, 130),
                    Color.FromArgb(137, 201, 154),
                    Color.FromArgb(132, 175, 232)
                };
                string[] labels = new string[] { "Warm panel", "Growth panel", "Cool panel" };
                for (int i = 0; i < colors.Length; i++)
                {
                    long sessionId = i + 1;
                    using (Bitmap panel = CreateSolidBitmap(360, 88, colors[i]))
                    using (Graphics graphics = Graphics.FromImage(panel))
                    using (Font font = new Font(FontFamily.GenericSansSerif, 19.0f,
                        FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(40, 48, 58)))
                    {
                        graphics.DrawString(labels[i], font, brush, 22, 25);
                        Assert(batch.Reserve(sessionId, panel.Size),
                            "preview panel should reserve in the compositor");
                        AssertEqual(CaptureBatchCompletionResult.Accepted,
                            batch.Complete(sessionId, (Bitmap)panel.Clone()),
                            "preview panel should enter the compositor");
                    }
                }

                using (CaptureBatchSnapshot snapshot = batch.CreateSnapshot())
                using (Bitmap composite = CaptureBatch.Compose(snapshot))
                    composite.Save(Path.Combine(outputDirectory, "composite-demo.png"),
                        System.Drawing.Imaging.ImageFormat.Png);
            }

            WriteSettingsDialogPreview(AppLanguage.English,
                Path.Combine(outputDirectory, "settings-en.png"));
            WriteSettingsDialogPreview(AppLanguage.SimplifiedChinese,
                Path.Combine(outputDirectory, "settings-zh-CN.png"));
            Console.WriteLine("Wrote preview images to " + outputDirectory);
        }

        private static void WriteSettingsDialogPreview(AppLanguage language, string path)
        {
            using (CaptureLimitSettingsDialog dialog = new CaptureLimitSettingsDialog(language, 3))
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(-10000, -10000);
                dialog.Show();
                Application.DoEvents();
                dialog.PerformLayout();
                AssertDialogControlsFit(dialog);
                using (Bitmap preview = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(preview,
                        new Rectangle(Point.Empty, preview.Size));
                    preview.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                dialog.Close();
            }
        }

        private static void AssertDialogControlsFit(Control parent)
        {
            parent.PerformLayout();
            foreach (Control child in parent.Controls)
            {
                Console.WriteLine("Dialog preview control: " + child.GetType().Name +
                    " " + child.Bounds + " in " + parent.GetType().Name +
                    " client " + parent.ClientSize);
                Assert(child.Left >= 0 && child.Top >= 0 &&
                    child.Right <= parent.ClientSize.Width &&
                    child.Bottom <= parent.ClientSize.Height,
                    child.GetType().Name + " should fit in its parent control");
                AssertDialogControlsFit(child);
            }
        }

        private static void AssertBitmapsEqual(Bitmap expected, Bitmap actual, string message)
        {
            AssertEqual(expected.Width, actual.Width, message + " width");
            AssertEqual(expected.Height, actual.Height, message + " height");
            for (int y = 0; y < expected.Height; y++)
            {
                for (int x = 0; x < expected.Width; x++)
                    AssertEqual(expected.GetPixel(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb(),
                        message + " pixel " + x + "," + y);
            }
        }

        private static Color PatternColor(int x, int y)
        {
            return Color.FromArgb(255, x * 10, y * 10, x + y);
        }

        private static IDataObject CaptureClipboardSnapshot(out List<IDisposable> ownedValues)
        {
            IDataObject liveClipboard = Clipboard.GetDataObject();
            ownedValues = new List<IDisposable>();
            if (liveClipboard == null)
                return null;

            try
            {
                DataObject snapshot = new DataObject();
                string[] formats = liveClipboard.GetFormats(false);
                for (int i = 0; i < formats.Length; i++)
                {
                    object value = liveClipboard.GetData(formats[i], false);
                    object copy = CloneClipboardValue(value, ownedValues);
                    if (copy != null)
                        snapshot.SetData(formats[i], false, copy);
                }
                return snapshot;
            }
            catch
            {
                DisposeAll(ownedValues);
                throw;
            }
        }

        private static object CloneClipboardValue(object value, List<IDisposable> ownedValues)
        {
            if (value == null)
                return null;

            Image image = value as Image;
            if (image != null)
            {
                try
                {
                    Bitmap clone = new Bitmap(image);
                    ownedValues.Add(clone);
                    return clone;
                }
                finally
                {
                    Bitmap returnedBitmap = value as Bitmap;
                    if (returnedBitmap != null)
                        returnedBitmap.Dispose();
                }
            }

            MemoryStream memory = value as MemoryStream;
            if (memory != null)
            {
                MemoryStream clone = new MemoryStream(memory.ToArray());
                ownedValues.Add(clone);
                return clone;
            }

            Stream stream = value as Stream;
            if (stream != null)
            {
                MemoryStream clone = new MemoryStream();
                long originalPosition = stream.CanSeek ? stream.Position : -1;
                try
                {
                    if (stream.CanSeek)
                        stream.Position = 0;
                    stream.CopyTo(clone);
                    clone.Position = 0;
                }
                finally
                {
                    if (originalPosition >= 0)
                        stream.Position = originalPosition;
                }
                ownedValues.Add(clone);
                return clone;
            }

            byte[] bytes = value as byte[];
            if (bytes != null)
                return (byte[])bytes.Clone();

            string[] strings = value as string[];
            if (strings != null)
                return (string[])strings.Clone();

            StringCollection stringCollection = value as StringCollection;
            if (stringCollection != null)
            {
                StringCollection clone = new StringCollection();
                string[] values = new string[stringCollection.Count];
                stringCollection.CopyTo(values, 0);
                clone.AddRange(values);
                return clone;
            }

            ICloneable cloneable = value as ICloneable;
            if (cloneable != null)
            {
                object clone = cloneable.Clone();
                IDisposable disposable = clone as IDisposable;
                if (disposable != null)
                    ownedValues.Add(disposable);
                return clone;
            }

            Array array = value as Array;
            if (array != null)
                return array.Clone();

            return value;
        }

        private static void RestoreClipboard(IDataObject originalClipboard)
        {
            if (originalClipboard == null)
            {
                ExternalException lastError = null;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        Clipboard.Clear();
                        return;
                    }
                    catch (ExternalException exception)
                    {
                        lastError = exception;
                        Thread.Sleep(50);
                    }
                }
                throw lastError;
            }
            else
                SetClipboardForTesting(originalClipboard);
        }

        private static void SetClipboardForTesting(IDataObject data)
        {
            Clipboard.SetDataObject(data, true, 10, 50);
        }

        private static void DisposeAll(List<IDisposable> values)
        {
            for (int i = values.Count - 1; i >= 0; i--)
            {
                try { values[i].Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }

        private static void AssertTransition(GestureTransition transition, bool suppress,
            bool started, bool completed, bool allReleased, long sessionId, int x, int y, string name)
        {
            AssertEqual(suppress, transition.Suppress, name + " suppress");
            AssertEqual(started, transition.Started, name + " started");
            AssertEqual(completed, transition.Completed, name + " completed");
            AssertEqual(allReleased, transition.AllReleased, name + " all released");
            AssertEqual(sessionId, transition.SessionId, name + " session id");
            AssertEqual(x, transition.X, name + " X");
            AssertEqual(y, transition.Y, name + " Y");
        }

        private static string FormatTooltip(AppLanguage language, AppText text, string count)
        {
            return String.Format(CultureInfo.InvariantCulture,
                AppLocalization.Get(language, text), count);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new Exception(message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!Object.Equals(expected, actual))
                throw new Exception(message + ": expected " + expected + ", got " + actual);
        }
    }
}
