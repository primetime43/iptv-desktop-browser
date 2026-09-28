using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace DesktopApp.Services;

public readonly record struct ThumbnailSize(int Width, int Height)
{
    public static ThumbnailSize Create(int width, int height) => new(Bound(width), Bound(height));
    private static int Bound(int value) => value <= 0 ? 512 : Math.Min(2048, ((Math.Min(value, 2048) + 31) / 32) * 32);
}

internal static class ThumbnailImage
{
    public static string SourceKey(ISessionService session, string url) =>
        "image_v2_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            session.Mode, session.Host, session.Port, session.UseSsl, session.Username, session.Password, Url = url
        }))));

    public static BitmapImage Decode(byte[] bytes, ThumbnailSize size)
    {
        using var stream = new ImageStream(bytes);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var scale = Math.Min(1d, Math.Min((double)size.Width / frame.PixelWidth, (double)size.Height / frame.PixelHeight));
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Floor(frame.PixelWidth * scale));
        bitmap.DecodePixelHeight = Math.Max(1, (int)Math.Floor(frame.PixelHeight * scale));
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // BitmapImage keeps StreamSource after OnLoad. Release the encoded source buffer
    // on disposal instead of retaining the entire download alongside every thumbnail.
    private sealed class ImageStream(byte[] bytes) : Stream
    {
        private MemoryStream? _source = new(bytes, writable: false);
        private MemoryStream Source => _source ?? throw new ObjectDisposedException(nameof(ImageStream));
        public override bool CanRead => _source?.CanRead == true;
        public override bool CanSeek => _source?.CanSeek == true;
        public override bool CanWrite => false;
        public override long Length => Source.Length;
        public override long Position { get => Source.Position; set => Source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Source.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => Source.Seek(offset, origin);
        public override void Flush() => Source.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _source?.Dispose(); _source = null; }
            base.Dispose(disposing);
        }
    }
}

// Count alone is insufficient when thumbnails have different dimensions. Bound decoded pixels
// as well, including disk hits, and evict the least recently used variant first.
internal sealed class ThumbnailMemoryCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (BitmapImage Image, long Used)> _entries = new();
    private long _sequence;
    private long _bytes;
    public long Bytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _entries.Count; } }
    public bool TryGetValue(string key, out BitmapImage image)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                image = entry.Image;
                _entries[key] = (image, ++_sequence);
                return true;
            }
            image = null!;
            return false;
        }
    }
    public void Store(string key, BitmapImage image)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out var old)) _bytes -= Size(old.Image);
            var bytes = Size(image);
            while (_entries.Count > 0 && (_entries.Count >= 500 || _bytes + bytes > 64 * 1024 * 1024))
            {
                var oldest = _entries.MinBy(e => e.Value.Used);
                _entries.Remove(oldest.Key);
                _bytes -= Size(oldest.Value.Image);
            }
            _entries[key] = (image, ++_sequence);
            _bytes += bytes;
        }
    }
    public void Clear() { lock (_gate) { _entries.Clear(); _bytes = 0; } }
    private static long Size(BitmapImage image) => (long)image.PixelWidth * image.PixelHeight * 4;
}
