using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
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

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 1 || (args.Length == 1 && args[0] != "--integration"))
            {
                Console.WriteLine("Usage: DuoClick.Tests.exe [--integration]");
                return 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

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
            Run("selection geometry supports negative origins and reverse direction", GeometrySupportsNegativeOriginsAndReverseDirection);
            Run("selection geometry clips to desktop bounds", GeometryClipsToDesktopBounds);
            Run("selection minimum size is two pixels per side", GeometryEnforcesMinimumSize);

            if (args.Length == 1)
            {
                Run("live desktop capture returns a disposable bitmap", LiveDesktopCaptureSmoke);
                Run("synthetic selection copies the expected crop and disposes overlay", SyntheticClipboardIntegrationSmoke);
                Run("completion before capture readiness uses the queued end point", CompletionBeforeCaptureReadySmoke);
                Run("older queued crop cannot replace a newer session", OlderCropCannotReplaceNewerSession);
                Run("queued next selection completes after the active capture", QueuedSelectionCompletionSurvivesActiveCapture);
            }

            Console.WriteLine("Tests: " + _passed + " passed, " + _failed + " failed.");
            return _failed == 0 ? 0 : 1;
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
                Clipboard.SetDataObject(sentinel, true, 0, 0);

                context = new DuoClickApplicationContext(false, false);

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

                copied = WaitForExpectedClipboardBitmap(6, 6, TimeSpan.FromSeconds(10));
                Assert(copied != null, "cropped bitmap should reach the clipboard");
                for (int y = 0; y < copied.Height; y++)
                {
                    for (int x = 0; x < copied.Width; x++)
                    {
                        Color expected = PatternColor(x + 4, y + 3);
                        AssertEqual(expected.ToArgb(), copied.GetPixel(x, y).ToArgb(),
                            "cropped pixel " + x + "," + y);
                    }
                }
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
                Clipboard.SetDataObject(sentinel, true, 0, 0);

                context = new DuoClickApplicationContext(false, false);
                Rectangle bounds = new Rectangle(-100, -50, 12, 10);
                Point start = new Point(-96, -47);
                Point end = new Point(-90, -41);
                long sessionId = context.BeginPendingSelectionForTesting(bounds, start);

                // Store completion before delivering the frozen image, which
                // deterministically exercises the pending-finish path.
                context.CompleteSelectionForTesting(sessionId, end);
                using (Bitmap source = CreatePatternBitmap(bounds.Size))
                    context.CompleteCaptureForTesting(sessionId, source);

                copied = WaitForExpectedClipboardBitmap(6, 6, TimeSpan.FromSeconds(10));
                Assert(copied != null, "queued finish point should produce a 6 by 6 clipboard crop");
                Assert(!context.HasSelectionOverlayForTesting,
                    "overlay should be disposed after early completion is drained");

                for (int y = 0; y < copied.Height; y++)
                {
                    for (int x = 0; x < copied.Width; x++)
                    {
                        Color expected = PatternColor(x + 4, y + 3);
                        AssertEqual(expected.ToArgb(), copied.GetPixel(x, y).ToArgb(),
                            "early-finish cropped pixel " + x + "," + y);
                    }
                }
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
                Clipboard.SetDataObject(sentinel, true, 0, 0);

                context = new DuoClickApplicationContext(false, false);

                context.PublishCropForTesting(200, CreateSolidBitmap(7, 5, Color.Blue));
                context.PublishCropForTesting(199, CreateSolidBitmap(2, 2, Color.Red));

                copied = WaitForExpectedClipboardBitmap(7, 5, TimeSpan.FromSeconds(10));
                Assert(copied != null, "newer session crop should reach the clipboard");
                AssertEqual(Color.Blue.ToArgb(), copied.GetPixel(0, 0).ToArgb(),
                    "clipboard should retain the newer session image");
                AssertEqual(Color.Blue.ToArgb(), copied.GetPixel(6, 4).ToArgb(),
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
                Clipboard.SetDataObject(sentinel, true, 0, 0);

                context = new DuoClickApplicationContext(false, false);
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
                copied = WaitForExpectedClipboardBitmap(4, 3, TimeSpan.FromSeconds(10));
                Assert(copied != null, "queued selection B crop should reach the clipboard");
                AssertEqual(Color.Green.ToArgb(), copied.GetPixel(0, 0).ToArgb(),
                    "queued B should publish its own crop");
                AssertEqual(Color.Green.ToArgb(), copied.GetPixel(3, 2).ToArgb(),
                    "queued B should remain the latest crop");
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
                Clipboard.Clear();
            else
                Clipboard.SetDataObject(originalClipboard, true, 10, 50);
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
