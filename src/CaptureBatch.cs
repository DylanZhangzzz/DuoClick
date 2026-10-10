using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DuoClick
{
    internal enum CaptureBatchCompletionResult
    {
        Accepted,
        Rejected,
        IgnoredStale
    }

    /// <summary>
    /// Keeps completed captures in reservation order and creates owned snapshots
    /// for composition away from the UI thread.
    /// </summary>
    internal sealed class CaptureBatch : IDisposable
    {
        internal const int MaximumImages = 100;
        internal const int MaximumDimension = 32767;
        internal const long MaximumPixels = 40000000L;

        private readonly object _sync = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<long, Entry> _entriesBySession =
            new Dictionary<long, Entry>();
        private long _version;
        private long _lastReservedSessionId;
        private int _count;
        private int _pendingCount;
        private bool _disposed;

        internal long Version
        {
            get { lock (_sync) return _version; }
        }

        internal int Count
        {
            get { lock (_sync) return _count; }
        }

        internal int PendingCount
        {
            get { lock (_sync) return _pendingCount; }
        }

        /// <summary>
        /// Reserves the crop's place before crop work is queued. The supplied
        /// size is used to reject a batch that cannot fit before allocating it.
        /// </summary>
        internal bool Reserve(long sessionId, Size expectedSize)
        {
            lock (_sync)
            {
                if (_disposed || sessionId <= _lastReservedSessionId ||
                    expectedSize.Width <= 0 || expectedSize.Height <= 0 ||
                    _count + _pendingCount >= MaximumImages ||
                    !FitsWithReservation(expectedSize))
                    return false;

                Entry entry = new Entry(sessionId, expectedSize);
                _entries.Add(entry);
                _entriesBySession.Add(sessionId, entry);
                _lastReservedSessionId = sessionId;
                _pendingCount++;
                _version++;
                return true;
            }
        }

        /// <summary>
        /// Takes ownership of image on every path. A completion for a cleared,
        /// failed, or otherwise unknown reservation is disposed and ignored.
        /// </summary>
        internal CaptureBatchCompletionResult Complete(long sessionId, Bitmap image)
        {
            bool keepImage = false;
            CaptureBatchCompletionResult result;

            lock (_sync)
            {
                Entry entry;
                if (_disposed || !_entriesBySession.TryGetValue(sessionId, out entry) ||
                    entry.State != EntryState.Pending)
                {
                    result = CaptureBatchCompletionResult.IgnoredStale;
                }
                else
                {
                    Size actualSize;
                    if (TryGetSize(image, out actualSize) && IsValidSize(actualSize) &&
                        FitsWithCompletion(entry, actualSize))
                    {
                        entry.Image = image;
                        entry.ExpectedSize = actualSize;
                        entry.State = EntryState.Completed;
                        _count++;
                        _pendingCount--;
                        _version++;
                        keepImage = true;
                        result = CaptureBatchCompletionResult.Accepted;
                    }
                    else
                    {
                        _pendingCount--;
                        RemoveEntry(entry);
                        _version++;
                        result = CaptureBatchCompletionResult.Rejected;
                    }
                }
            }

            if (!keepImage && image != null)
                image.Dispose();
            return result;
        }

        /// <summary>Marks a reserved crop as failed, allowing later crops to proceed.</summary>
        internal void Fail(long sessionId)
        {
            lock (_sync)
            {
                Entry entry;
                if (_disposed || !_entriesBySession.TryGetValue(sessionId, out entry) ||
                    entry.State != EntryState.Pending)
                    return;

                _pendingCount--;
                RemoveEntry(entry);
                _version++;
            }
        }

        /// <summary>
        /// Creates a stable copy once every reservation currently in the batch
        /// has either completed or failed.
        /// </summary>
        internal CaptureBatchSnapshot CreateSnapshot()
        {
            lock (_sync)
            {
                if (_disposed || _pendingCount != 0 || _count == 0)
                    return null;

                List<Bitmap> images = new List<Bitmap>(_count);
                int width = 0;
                long height = 0;
                long throughSessionId = 0;
                try
                {
                    for (int i = 0; i < _entries.Count; i++)
                    {
                        Entry entry = _entries[i];
                        if (entry.SessionId > throughSessionId)
                            throughSessionId = entry.SessionId;
                        if (entry.State != EntryState.Completed)
                            continue;

                        Bitmap copy = (Bitmap)entry.Image.Clone();
                        images.Add(copy);
                        if (copy.Width > width)
                            width = copy.Width;
                        height += copy.Height;
                    }

                    return new CaptureBatchSnapshot(this, _version, throughSessionId,
                        images.ToArray(), width, checked((int)height));
                }
                catch
                {
                    DisposeImages(images);
                    throw;
                }
            }
        }

        /// <summary>
        /// Stacks snapshot images without resizing. Narrower images leave white
        /// space to their right; every image starts at the left edge.
        /// </summary>
        internal static Bitmap Compose(CaptureBatchSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException("snapshot");
            if (snapshot.IsDisposed)
                throw new ObjectDisposedException("CaptureBatchSnapshot");
            if (snapshot.Count == 0 || snapshot.Width <= 0 || snapshot.Height <= 0)
                throw new ArgumentException("The snapshot contains no images.", "snapshot");

            Bitmap composite = new Bitmap(snapshot.Width, snapshot.Height,
                PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(composite))
                {
                    graphics.Clear(Color.White);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.SmoothingMode = SmoothingMode.None;

                    int top = 0;
                    Bitmap[] images = snapshot.Images;
                    for (int i = 0; i < images.Length; i++)
                    {
                        graphics.DrawImageUnscaled(images[i], 0, top);
                        top += images[i].Height;
                    }
                }
                return composite;
            }
            catch
            {
                composite.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Returns whether a finished background composition still represents
        /// the current batch. Any mutation invalidates older snapshots.
        /// </summary>
        internal bool IsSnapshotCurrent(CaptureBatchSnapshot snapshot)
        {
            if (snapshot == null || snapshot.IsDisposed)
                return false;

            lock (_sync)
            {
                return !_disposed && Object.ReferenceEquals(snapshot.Owner, this) &&
                    snapshot.Version == _version;
            }
        }

        /// <summary>Removes captures through a published session boundary.</summary>
        internal void ResetThrough(long boundarySessionId)
        {
            lock (_sync)
            {
                bool changed = false;
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    Entry entry = _entries[i];
                    if (entry.SessionId > boundarySessionId)
                        continue;

                    if (entry.Image != null)
                    {
                        entry.Image.Dispose();
                        entry.Image = null;
                        _count--;
                    }
                    if (entry.State == EntryState.Pending)
                        _pendingCount--;

                    entry.State = EntryState.Removed;
                    _entriesBySession.Remove(entry.SessionId);
                    _entries.RemoveAt(i);
                    changed = true;
                }

                if (changed)
                    _version++;
            }
        }

        /// <summary>Starts a new group and invalidates all pending completions.</summary>
        internal void Clear()
        {
            lock (_sync)
            {
                ClearEntries();
                _version++;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                ClearEntries();
                _version++;
                _disposed = true;
            }
        }

        private bool FitsWithReservation(Size expectedSize)
        {
            int width = expectedSize.Width;
            long height = expectedSize.Height;

            if (!IsValidSize(expectedSize))
                return false;

            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (entry.State == EntryState.Failed || entry.State == EntryState.Removed)
                    continue;
                if (entry.ExpectedSize.Width > width)
                    width = entry.ExpectedSize.Width;
                height += entry.ExpectedSize.Height;
                if (height > MaximumDimension)
                    return false;
            }

            return IsValidOutput(width, height);
        }

        private bool FitsWithCompletion(Entry target, Size actualSize)
        {
            int width = 0;
            long height = 0;

            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (entry.State == EntryState.Failed || entry.State == EntryState.Removed)
                    continue;

                Size size = Object.ReferenceEquals(entry, target)
                    ? actualSize : entry.ExpectedSize;
                if (size.Width > width)
                    width = size.Width;
                height += size.Height;
                if (height > MaximumDimension)
                    return false;
            }

            return IsValidOutput(width, height);
        }

        private static bool IsValidSize(Size size)
        {
            return size.Width > 0 && size.Height > 0 &&
                size.Width <= MaximumDimension && size.Height <= MaximumDimension;
        }

        private static bool IsValidOutput(int width, long height)
        {
            return width > 0 && width <= MaximumDimension &&
                height > 0 && height <= MaximumDimension &&
                (long)width * height <= MaximumPixels;
        }

        private static bool TryGetSize(Bitmap image, out Size size)
        {
            size = Size.Empty;
            if (image == null)
                return false;

            try
            {
                size = new Size(image.Width, image.Height);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        private void ClearEntries()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Image != null)
                {
                    _entries[i].Image.Dispose();
                    _entries[i].Image = null;
                }
                _entries[i].State = EntryState.Removed;
            }
            _entries.Clear();
            _entriesBySession.Clear();
            _count = 0;
            _pendingCount = 0;
        }

        private void RemoveEntry(Entry entry)
        {
            entry.State = EntryState.Removed;
            _entriesBySession.Remove(entry.SessionId);
            _entries.Remove(entry);
        }

        private static void DisposeImages(List<Bitmap> images)
        {
            for (int i = 0; i < images.Count; i++)
                images[i].Dispose();
        }

        private enum EntryState
        {
            Pending,
            Completed,
            Failed,
            Removed
        }

        private sealed class Entry
        {
            internal readonly long SessionId;
            internal Size ExpectedSize;
            internal Bitmap Image;
            internal EntryState State;

            internal Entry(long sessionId, Size expectedSize)
            {
                SessionId = sessionId;
                ExpectedSize = expectedSize;
                State = EntryState.Pending;
            }
        }
    }

    /// <summary>A disposable clone of the resolved images used for one compose job.</summary>
    internal sealed class CaptureBatchSnapshot : IDisposable
    {
        private Bitmap[] _images;

        internal CaptureBatch Owner { get; private set; }
        internal long Version { get; private set; }
        internal long ThroughSessionId { get; private set; }
        internal int Count { get { return _images == null ? 0 : _images.Length; } }
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal bool IsDisposed { get { return _images == null; } }
        internal Bitmap[] Images { get { return _images; } }

        internal CaptureBatchSnapshot(CaptureBatch owner, long version,
            long throughSessionId, Bitmap[] images, int width, int height)
        {
            Owner = owner;
            Version = version;
            ThroughSessionId = throughSessionId;
            _images = images;
            Width = width;
            Height = height;
        }

        public void Dispose()
        {
            Bitmap[] images = _images;
            if (images == null)
                return;

            _images = null;
            for (int i = 0; i < images.Length; i++)
                images[i].Dispose();
        }
    }
}
