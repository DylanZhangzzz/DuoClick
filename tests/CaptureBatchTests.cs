using System;
using System.Drawing;

namespace DuoClick.Tests
{
    internal static class CaptureBatchTests
    {
        internal static void Run(Action<string, Action> run)
        {
            run("capture batch composes in reservation order with white alignment",
                ComposesInReservationOrder);
            run("capture batch waits for failures and skips failed captures",
                WaitsForPendingAndSkipsFailures);
            run("capture batch reset preserves newer pending captures",
                ResetPreservesNewerPendingCapture);
            run("capture batch rejects stale results and invalidates old snapshots",
                RejectsStaleWork);
            run("capture batch releases image capacity after failed captures",
                FailedCapturesReleaseCapacity);
            run("capture batch enforces image and canvas limits",
                EnforcesLimits);
        }

        private static void ComposesInReservationOrder()
        {
            using (CaptureBatch batch = new CaptureBatch())
            {
                Assert(batch.Reserve(10, new Size(2, 2)), "first crop should reserve");
                Assert(batch.Reserve(11, new Size(1, 1)), "second crop should reserve");

                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(11, CreateSolidBitmap(1, 1, Color.Blue)),
                    "newer crop completion");
                Assert(batch.CreateSnapshot() == null,
                    "snapshot should wait for the earlier reserved crop");

                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(10, CreateSolidBitmap(2, 2, Color.Red)),
                    "older crop completion");

                using (CaptureBatchSnapshot snapshot = batch.CreateSnapshot())
                {
                    Assert(snapshot != null, "resolved batch should create a snapshot");
                    AssertEqual(2, snapshot.Count, "snapshot image count");
                    AssertEqual(2, snapshot.Width, "composite width");
                    AssertEqual(3, snapshot.Height, "composite height");
                    AssertEqual(11L, snapshot.ThroughSessionId, "snapshot boundary");

                    using (Bitmap composite = CaptureBatch.Compose(snapshot))
                    {
                        AssertPixel(Color.Red, composite.GetPixel(0, 0), "first image top");
                        AssertPixel(Color.Red, composite.GetPixel(1, 1), "first image bottom");
                        AssertPixel(Color.Blue, composite.GetPixel(0, 2), "second image");
                        AssertPixel(Color.White, composite.GetPixel(1, 2),
                            "white space beside the narrower image");
                    }
                }
            }
        }

        private static void WaitsForPendingAndSkipsFailures()
        {
            using (CaptureBatch batch = new CaptureBatch())
            {
                Assert(batch.Reserve(20, new Size(2, 2)), "failed crop should reserve");
                Assert(batch.Reserve(21, new Size(1, 1)), "successful crop should reserve");
                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(21, CreateSolidBitmap(1, 1, Color.Green)),
                    "later crop should be accepted while earlier work is pending");
                AssertEqual(1, batch.PendingCount, "pending count before failure");
                Assert(batch.CreateSnapshot() == null,
                    "batch should wait until every reservation is resolved");

                batch.Fail(20);
                AssertEqual(0, batch.PendingCount, "pending count after failure");
                using (CaptureBatchSnapshot snapshot = batch.CreateSnapshot())
                {
                    Assert(snapshot != null, "remaining successful crop should be snapshot-able");
                    AssertEqual(1, snapshot.Count, "failed crop should be skipped");
                    AssertEqual(21L, snapshot.ThroughSessionId, "boundary includes resolved failure");
                    using (Bitmap composite = CaptureBatch.Compose(snapshot))
                        AssertPixel(Color.Green, composite.GetPixel(0, 0), "successful crop pixel");
                }
            }
        }

        private static void ResetPreservesNewerPendingCapture()
        {
            using (CaptureBatch batch = new CaptureBatch())
            {
                Assert(batch.Reserve(30, new Size(1, 1)), "published crop should reserve");
                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(30, CreateSolidBitmap(1, 1, Color.Red)),
                    "published crop should complete");

                CaptureBatchSnapshot oldSnapshot = batch.CreateSnapshot();
                Assert(oldSnapshot != null, "first crop should be snapshotted");
                try
                {
                    Assert(batch.IsSnapshotCurrent(oldSnapshot),
                        "fresh snapshot should match the current batch");
                    Assert(batch.Reserve(31, new Size(2, 1)),
                        "newer crop should reserve while the old one is being published");
                    batch.ResetThrough(oldSnapshot.ThroughSessionId);

                    Assert(!batch.IsSnapshotCurrent(oldSnapshot),
                        "reset should invalidate an in-flight old composite");
                    AssertEqual(0, batch.Count, "old published image should be removed");
                    AssertEqual(1, batch.PendingCount,
                        "newer pending reservation should survive boundary reset");

                    Bitmap staleResult = CreateSolidBitmap(1, 1, Color.Purple);
                    AssertEqual(CaptureBatchCompletionResult.IgnoredStale,
                        batch.Complete(30, staleResult),
                        "late completion for a reset capture");
                    AssertDisposed(staleResult, "reset capture result should be disposed");

                    AssertEqual(CaptureBatchCompletionResult.Accepted,
                        batch.Complete(31, CreateSolidBitmap(2, 1, Color.Blue)),
                        "newer completion should still be accepted");
                    using (CaptureBatchSnapshot nextSnapshot = batch.CreateSnapshot())
                    {
                        Assert(nextSnapshot != null, "newer image should form its own snapshot");
                        AssertEqual(31L, nextSnapshot.ThroughSessionId, "new snapshot boundary");
                        using (Bitmap composite = CaptureBatch.Compose(nextSnapshot))
                            AssertPixel(Color.Blue, composite.GetPixel(1, 0), "newer image pixel");
                    }
                }
                finally
                {
                    oldSnapshot.Dispose();
                }
            }
        }

        private static void RejectsStaleWork()
        {
            using (CaptureBatch batch = new CaptureBatch())
            {
                Assert(batch.Reserve(40, new Size(1, 1)), "crop should reserve");
                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(40, CreateSolidBitmap(1, 1, Color.Orange)),
                    "crop should complete");
                CaptureBatchSnapshot snapshot = batch.CreateSnapshot();
                Assert(snapshot != null, "completed capture should create a snapshot");
                try
                {
                    batch.Clear();
                    Assert(!batch.IsSnapshotCurrent(snapshot),
                        "clear should prevent an old composite from being published");

                    Bitmap staleResult = CreateSolidBitmap(1, 1, Color.Purple);
                    AssertEqual(CaptureBatchCompletionResult.IgnoredStale,
                        batch.Complete(40, staleResult), "completion after clear");
                    AssertDisposed(staleResult, "stale crop should be disposed");
                    AssertEqual(0, batch.Count, "stale result must not repopulate the batch");
                }
                finally
                {
                    snapshot.Dispose();
                }
            }
        }

        private static void EnforcesLimits()
        {
            using (CaptureBatch batch = new CaptureBatch())
            {
                long initialVersion = batch.Version;
                Assert(!batch.Reserve(50, new Size(CaptureBatch.MaximumDimension + 1, 1)),
                    "a crop beyond the dimension limit should be rejected");
                AssertEqual(initialVersion, batch.Version,
                    "a rejected reservation should not mutate the batch");

                Assert(batch.Reserve(51, new Size(20000, 2000)),
                    "an exactly 40 megapixel canvas should fit");
                long reservedVersion = batch.Version;
                Assert(!batch.Reserve(52, new Size(1, 1)),
                    "an extra row beyond the pixel limit should be rejected");
                AssertEqual(reservedVersion, batch.Version,
                    "oversized aggregate reservation should not mutate the batch");
                batch.Clear();
            }

            using (CaptureBatch batch = new CaptureBatch())
            {
                for (int i = 0; i < CaptureBatch.MaximumImages; i++)
                    Assert(batch.Reserve(100 + i, new Size(1, 1)),
                        "reservation " + i + " should fit the image-count limit");
                long fullVersion = batch.Version;
                Assert(!batch.Reserve(100 + CaptureBatch.MaximumImages, new Size(1, 1)),
                    "the 101st reservation should be rejected");
                AssertEqual(fullVersion, batch.Version,
                    "image-count rejection should not mutate the batch");
            }

            using (CaptureBatch batch = new CaptureBatch())
            {
                Assert(batch.Reserve(300, new Size(20000, 1999)),
                    "existing pending canvas should reserve");
                Assert(batch.Reserve(301, new Size(1, 1)),
                    "expected canvas at the pixel limit should reserve");
                Bitmap oversized = CreateSolidBitmap(20000, 2, Color.Black);
                AssertEqual(CaptureBatchCompletionResult.Rejected,
                    batch.Complete(301, oversized),
                    "actual crop exceeding the batch pixel limit should be rejected");
                AssertDisposed(oversized, "rejected crop should be disposed");
                AssertEqual(0, batch.Count, "rejected crop should not enter the batch");
                batch.Fail(300);
                AssertEqual(0, batch.PendingCount, "rejected crop resolves its reservation");
                Assert(batch.Reserve(302, new Size(1, 1)),
                    "rejected crop should release its image capacity");
            }
        }

        private static void FailedCapturesReleaseCapacity()
        {
            using (CaptureBatch batch = new CaptureBatch())
            {
                for (int i = 0; i < CaptureBatch.MaximumImages; i++)
                {
                    long sessionId = 400 + i;
                    Assert(batch.Reserve(sessionId, new Size(1, 1)),
                        "failed capture reservation " + i + " should fit");
                    batch.Fail(sessionId);
                }

                AssertEqual(0, batch.Count, "failed captures should not count as images");
                AssertEqual(0, batch.PendingCount, "failed captures should not remain pending");
                Assert(batch.Reserve(500, new Size(1, 1)),
                    "failed captures should release the 100-image capacity");
            }
        }

        private static Bitmap CreateSolidBitmap(int width, int height, Color color)
        {
            Bitmap image = new Bitmap(width, height);
            using (Graphics graphics = Graphics.FromImage(image))
                graphics.Clear(color);
            return image;
        }

        private static void AssertDisposed(Bitmap image, string message)
        {
            try
            {
                image.GetPixel(0, 0);
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            throw new Exception(message);
        }

        private static void AssertPixel(Color expected, Color actual, string message)
        {
            AssertEqual(expected.ToArgb(), actual.ToArgb(), message);
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
