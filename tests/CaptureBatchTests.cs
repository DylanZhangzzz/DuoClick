using System;
using System.Drawing;

namespace DuoClick.Tests
{
    internal static class CaptureBatchTests
    {
        internal static void Run(Action<string, Action> run)
        {
            run("capture batch composes in reservation order with numbered headers and gaps",
                ComposesInReservationOrder);
            run("capture batch waits for failures and skips failed captures",
                WaitsForPendingAndSkipsFailures);
            run("capture batch reset preserves newer pending captures",
                ResetPreservesNewerPendingCapture);
            run("capture batch rejects stale results and invalidates old snapshots",
                RejectsStaleWork);
            run("capture batch retains the newest completed captures by capacity",
                RetainsNewestCompletedCaptures);
            run("capture batch capacity changes retain newest captures",
                ChangesCapacity);
            run("capture batch failures do not evict completed captures",
                FailuresDoNotEvictCompletedCaptures);
            run("capture batch completion projection evicts oversized oldest capture",
                ProjectsNewestSetForCompletion);
            run("capture batch keeps newest session IDs after out-of-order completions",
                RetainsNewestSessionIdsAfterOutOfOrderCompletions);
            run("capture batch includes composite chrome in output limits",
                EnforcesOutputLimitsWithChrome);
            run("capture batch bounds pending reservations independently",
                BoundsPendingReservations);
        }

        private static void ComposesInReservationOrder()
        {
            using (CaptureBatch batch = new CaptureBatch())
            {
                AssertEqual(3, batch.Capacity, "default capacity");
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
                    AssertEqual(CaptureBatch.MinimumCompositeWidth, snapshot.Width,
                        "minimum composite width");
                    AssertEqual(75, snapshot.Height, "composite height with headers and gap");
                    AssertEqual(32, snapshot.ContentOffsets[0], "first content offset");
                    AssertEqual(74, snapshot.ContentOffsets[1], "second content offset");
                    AssertEqual(11L, snapshot.ThroughSessionId, "snapshot boundary");

                    using (Bitmap composite = CaptureBatch.Compose(snapshot))
                    {
                        AssertPixel(Color.FromArgb(240, 240, 240),
                            composite.GetPixel(95, 0), "neutral first header");
                        Assert(ContainsDarkPixel(composite, 8, 8, 20, 23),
                            "first header should render its numeric label");
                        AssertPixel(Color.FromArgb(208, 208, 208),
                            composite.GetPixel(95, 31), "header divider");
                        AssertPixel(Color.Red, composite.GetPixel(0, 32),
                            "first image top");
                        AssertPixel(Color.Red, composite.GetPixel(1, 33),
                            "first image bottom");
                        AssertPixel(Color.White, composite.GetPixel(50, 34),
                            "white gap between image blocks");
                        AssertPixel(Color.FromArgb(240, 240, 240),
                            composite.GetPixel(95, 42), "second numbered header");
                        Assert(ContainsDarkPixel(composite, 8, 50, 20, 65),
                            "second header should render its numeric label");
                        AssertPixel(Color.Blue, composite.GetPixel(0, 74),
                            "second image remains unscaled");
                        AssertPixel(Color.White, composite.GetPixel(1, 74),
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
                    AssertEqual(32, snapshot.ContentOffsets[0], "single image content offset");
                    using (Bitmap composite = CaptureBatch.Compose(snapshot))
                        AssertPixel(Color.Green, composite.GetPixel(0, 32),
                            "successful crop pixel");
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
                            AssertPixel(Color.Blue, composite.GetPixel(1, 32),
                                "newer image pixel");
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

        private static void RetainsNewestCompletedCaptures()
        {
            using (CaptureBatch batch = new CaptureBatch(2))
            {
                Bitmap first = CreateSolidBitmap(1, 1, Color.Red);
                Bitmap second = CreateSolidBitmap(1, 1, Color.Blue);
                Bitmap third = CreateSolidBitmap(1, 1, Color.Green);

                Assert(batch.Reserve(100, new Size(1, 1)), "first capture should reserve");
                AssertEqual(CaptureBatchCompletionResult.Accepted, batch.Complete(100, first),
                    "first capture should complete");
                Assert(batch.Reserve(101, new Size(1, 1)), "second capture should reserve");
                AssertEqual(CaptureBatchCompletionResult.Accepted, batch.Complete(101, second),
                    "second capture should complete");
                Assert(batch.Reserve(102, new Size(1, 1)), "third capture should reserve");
                AssertEqual(CaptureBatchCompletionResult.Accepted, batch.Complete(102, third),
                    "third capture should complete");

                AssertEqual(2, batch.Count, "capacity should retain two images");
                Assert(!batch.ContainsSession(100), "oldest session should be evicted");
                Assert(batch.ContainsSession(101), "second session should remain");
                Assert(batch.ContainsSession(102), "newest session should remain");
                AssertDisposed(first, "evicted image should be disposed");

                using (CaptureBatchSnapshot snapshot = batch.CreateSnapshot())
                {
                    AssertEqual(2, snapshot.Count, "snapshot should contain retained images");
                    AssertEqual(74, snapshot.Height, "two one-pixel captures include chrome");
                    using (Bitmap composite = CaptureBatch.Compose(snapshot))
                    {
                        AssertPixel(Color.Blue, composite.GetPixel(0, 32),
                            "older retained capture stays first");
                        AssertPixel(Color.Green, composite.GetPixel(0, 73),
                            "newest retained capture stays last");
                    }
                }
            }
        }

        private static void ChangesCapacity()
        {
            using (CaptureBatch batch = new CaptureBatch())
            {
                Bitmap first = CompleteSolid(batch, 200, Color.Red);
                Bitmap second = CompleteSolid(batch, 201, Color.Blue);
                Bitmap third = CompleteSolid(batch, 202, Color.Green);

                Assert(batch.SetCapacity(1), "capacity should lower to one");
                AssertEqual(1, batch.Capacity, "new capacity");
                AssertEqual(1, batch.Count, "lowered capacity trims completed images");
                AssertDisposed(first, "oldest image should be disposed after lowering");
                AssertDisposed(second, "second-oldest image should be disposed after lowering");
                Assert(!batch.SetCapacity(0), "capacity below one should be rejected");
                AssertEqual(1, batch.Capacity, "invalid capacity should preserve old setting");
                AssertEqual(1, batch.Count, "invalid capacity should preserve captures");

                using (CaptureBatchSnapshot snapshot = batch.CreateSnapshot())
                {
                    AssertEqual(1, snapshot.Count, "only newest completed image remains");
                    using (Bitmap composite = CaptureBatch.Compose(snapshot))
                        AssertPixel(Color.Green, composite.GetPixel(0, 32),
                            "newest completed image is retained");
                }

                Assert(batch.SetCapacity(3), "capacity should grow again");
                AssertEqual(3, batch.Capacity, "grown capacity");
                AssertEqual(1, batch.Count, "growing capacity does not invent images");
                AssertNotDisposed(third, "retained image should remain owned by batch");
            }
        }

        private static void FailuresDoNotEvictCompletedCaptures()
        {
            using (CaptureBatch batch = new CaptureBatch(1))
            {
                Bitmap retained = CompleteSolid(batch, 300, Color.Orange);

                Assert(batch.Reserve(301, new Size(1, 1)), "failed capture should reserve");
                batch.Fail(301);
                Assert(!batch.ContainsSession(301), "failed session should be removed");
                Assert(batch.ContainsSession(300), "failure should preserve prior capture");
                AssertEqual(1, batch.Count, "failure should not evict prior image");

                Assert(batch.Reserve(302, new Size(1, 1)), "rejected capture should reserve");
                Bitmap invalid = CreateSolidBitmap(1, CaptureBatch.MaximumDimension -
                    CaptureBatch.CompositeHeaderHeight + 1, Color.Black);
                AssertEqual(CaptureBatchCompletionResult.Rejected,
                    batch.Complete(302, invalid), "actual composite height exceeds limit");
                AssertDisposed(invalid, "rejected image should be disposed");
                Assert(!batch.ContainsSession(302), "rejected session should be removed");
                Assert(batch.ContainsSession(300), "rejected capture should preserve prior image");
                AssertEqual(1, batch.Count, "rejected capture should not evict prior image");

                using (CaptureBatchSnapshot snapshot = batch.CreateSnapshot())
                using (Bitmap composite = CaptureBatch.Compose(snapshot))
                    AssertPixel(Color.Orange, composite.GetPixel(0, 32),
                        "prior image survives failed and rejected captures");
                AssertNotDisposed(retained, "preserved image should remain owned by batch");
            }
        }

        private static void ProjectsNewestSetForCompletion()
        {
            using (CaptureBatch batch = new CaptureBatch(1))
            {
                Bitmap oldest = CreateSolidBitmap(1,
                    CaptureBatch.MaximumDimension - CaptureBatch.CompositeHeaderHeight,
                    Color.Red);
                Assert(batch.Reserve(400, oldest.Size),
                    "large but valid oldest capture should reserve");
                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(400, oldest), "large capture should fit with its header");

                Assert(batch.Reserve(401, new Size(1, 1)),
                    "newest reservation should be checked against its projected retained set");
                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(401, CreateSolidBitmap(1, 1, Color.Blue)),
                    "small newest crop should fit after FIFO eviction");
                AssertEqual(1, batch.Count, "capacity retains only newest image");
                Assert(!batch.ContainsSession(400), "oversized oldest session should be evicted");
                AssertDisposed(oldest, "evicted oversized image should be disposed");

                using (CaptureBatchSnapshot snapshot = batch.CreateSnapshot())
                using (Bitmap composite = CaptureBatch.Compose(snapshot))
                    AssertPixel(Color.Blue, composite.GetPixel(0, 32),
                        "newest small image remains after eviction");
            }
        }

        private static void RetainsNewestSessionIdsAfterOutOfOrderCompletions()
        {
            using (CaptureBatch batch = new CaptureBatch(2))
            {
                Assert(batch.Reserve(500, new Size(1, 1)), "oldest crop should reserve");
                Assert(batch.Reserve(501, new Size(1, 1)), "middle crop should reserve");
                Assert(batch.Reserve(502, new Size(1, 1)), "newest crop should reserve");

                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(502, CreateSolidBitmap(1, 1, Color.Green)),
                    "newest crop completes first");
                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(500, CreateSolidBitmap(1, 1, Color.Red)),
                    "oldest crop completes late");
                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(501, CreateSolidBitmap(1, 1, Color.Blue)),
                    "middle crop completes last");

                AssertEqual(2, batch.Count, "capacity should retain two captures");
                Assert(!batch.ContainsSession(500), "oldest session should be dropped");
                using (CaptureBatchSnapshot snapshot = batch.CreateSnapshot())
                {
                    AssertEqual(2, snapshot.Count, "snapshot contains newest session IDs");
                    AssertEqual(502L, snapshot.ThroughSessionId,
                        "snapshot boundary follows retained reservation order");
                    using (Bitmap composite = CaptureBatch.Compose(snapshot))
                    {
                        AssertPixel(Color.Blue, composite.GetPixel(0, 32),
                            "session 501 precedes session 502");
                        AssertPixel(Color.Green, composite.GetPixel(0, 73),
                            "session 502 follows session 501");
                    }
                }
            }
        }

        private static void EnforcesOutputLimitsWithChrome()
        {
            using (CaptureBatch batch = new CaptureBatch())
            {
                long initialVersion = batch.Version;
                Assert(!batch.Reserve(600,
                    new Size(CaptureBatch.MaximumDimension + 1, 1)),
                    "a crop beyond the dimension limit should be rejected");
                AssertEqual(initialVersion, batch.Version,
                    "a rejected reservation should not mutate the batch");

                Assert(batch.Reserve(601, new Size(20000, 1968)),
                    "a 40 megapixel composite including its header should fit");
                batch.Fail(601);

                Assert(batch.Reserve(602, new Size(1,
                    CaptureBatch.MaximumDimension - CaptureBatch.CompositeHeaderHeight)),
                    "an image with a header exactly at the dimension limit should fit");
                Bitmap tall = CreateSolidBitmap(1,
                    CaptureBatch.MaximumDimension - CaptureBatch.CompositeHeaderHeight,
                    Color.DarkBlue);
                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(602, tall), "maximum-height composite should complete");
                long retainedVersion = batch.Version;
                Assert(!batch.Reserve(603, new Size(1, 1)),
                    "new image that makes the newest retained set too tall should be rejected");
                AssertEqual(retainedVersion, batch.Version,
                    "invalid projected reservation should preserve the batch");
                Assert(batch.ContainsSession(602), "invalid projection should preserve old capture");
                AssertEqual(1, batch.Count, "invalid projection should not evict old capture");
            }

            using (CaptureBatch batch = new CaptureBatch(1))
            {
                Assert(batch.Reserve(610, new Size(1, 1)), "old capture should reserve");
                AssertEqual(CaptureBatchCompletionResult.Accepted,
                    batch.Complete(610, CreateSolidBitmap(1, 1, Color.Orange)),
                    "old capture should complete");
                Assert(batch.Reserve(611, new Size(1, 1)), "new capture should reserve");
                Bitmap tooTall = CreateSolidBitmap(1,
                    CaptureBatch.MaximumDimension - CaptureBatch.CompositeHeaderHeight + 1,
                    Color.Black);
                AssertEqual(CaptureBatchCompletionResult.Rejected,
                    batch.Complete(611, tooTall),
                    "actual dimensions that exceed the composed canvas should be rejected");
                AssertDisposed(tooTall, "oversized actual image should be disposed");
                Assert(batch.ContainsSession(610), "rejected actual output should preserve old capture");
                AssertEqual(1, batch.Count, "rejected actual output should not evict old capture");
            }
        }

        private static void BoundsPendingReservations()
        {
            using (CaptureBatch batch = new CaptureBatch())
            {
                CompleteSolid(batch, 700, Color.Red);
                CompleteSolid(batch, 701, Color.Blue);
                CompleteSolid(batch, 702, Color.Green);

                for (int i = 0; i < CaptureBatch.MaximumImages; i++)
                    Assert(batch.Reserve(703 + i, new Size(1, 1)),
                        "pending reservation " + i + " should fit independently of capacity");
                long fullVersion = batch.Version;
                AssertEqual(3, batch.Count,
                    "completed history should remain while pending work is reserved");
                AssertEqual(CaptureBatch.MaximumImages, batch.PendingCount,
                    "pending limit");
                Assert(!batch.Reserve(703 + CaptureBatch.MaximumImages, new Size(1, 1)),
                    "the 101st pending reservation should be rejected");
                AssertEqual(fullVersion, batch.Version,
                    "pending-limit rejection should not mutate the batch");
            }
        }

        private static void FailedCapturesReleaseCapacity()
        {
            using (CaptureBatch batch = new CaptureBatch(1))
            {
                for (int i = 0; i < CaptureBatch.MaximumImages; i++)
                {
                    long sessionId = 800 + i;
                    Assert(batch.Reserve(sessionId, new Size(1, 1)),
                        "failed capture reservation " + i + " should fit");
                    batch.Fail(sessionId);
                }

                AssertEqual(0, batch.Count, "failed captures should not count as images");
                AssertEqual(0, batch.PendingCount, "failed captures should not remain pending");
                Assert(batch.Reserve(900, new Size(1, 1)),
                    "failed captures should release pending capacity");
            }
        }

        private static Bitmap CompleteSolid(CaptureBatch batch, long sessionId, Color color)
        {
            Bitmap image = CreateSolidBitmap(1, 1, color);
            Assert(batch.Reserve(sessionId, new Size(1, 1)),
                "capture " + sessionId + " should reserve");
            AssertEqual(CaptureBatchCompletionResult.Accepted,
                batch.Complete(sessionId, image), "capture " + sessionId + " should complete");
            return image;
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

        private static void AssertNotDisposed(Bitmap image, string message)
        {
            try
            {
                image.GetPixel(0, 0);
            }
            catch (ArgumentException)
            {
                throw new Exception(message);
            }
            catch (ObjectDisposedException)
            {
                throw new Exception(message);
            }
        }

        private static void AssertPixel(Color expected, Color actual, string message)
        {
            AssertEqual(expected.ToArgb(), actual.ToArgb(), message);
        }

        private static bool ContainsDarkPixel(Bitmap image, int left, int top,
            int right, int bottom)
        {
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    Color pixel = image.GetPixel(x, y);
                    if (pixel.R < 128 && pixel.G < 128 && pixel.B < 128)
                        return true;
                }
            }
            return false;
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
