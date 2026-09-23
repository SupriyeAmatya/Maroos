using System.Text;
using Maroos.Web.Services;
using SkiaSharp;

namespace Maroos.Tests.Auctions;

public sealed class AuctionPhotoStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"maroos-photos-{Guid.NewGuid():N}");
    private readonly AuctionPhotoStore _store;

    public AuctionPhotoStoreTests() => _store = new AuctionPhotoStore(new LocalPhotoBlobStore(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    public static byte[] Image(int width, int height, SKEncodedImageFormat format, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color ?? SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private string FileFor(string path) => Path.Combine(_root, path["uploads/".Length..]);

    [Fact]
    public async Task Saves_a_png_as_a_jpeg_and_returns_its_url()
    {
        var path = await _store.SaveAsync(new MemoryStream(Image(40, 30, SKEncodedImageFormat.Png)));

        Assert.Matches(@"^uploads/[0-9a-f]{32}\.jpg$", path);
        var saved = await File.ReadAllBytesAsync(FileFor(path));
        Assert.Equal([0xFF, 0xD8, 0xFF], saved[..3]);
        using var bitmap = SKBitmap.Decode(saved);
        Assert.Equal((40, 30), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public async Task Shrinks_large_photos_to_1600_pixels_on_the_long_side()
    {
        var path = await _store.SaveAsync(new MemoryStream(Image(3200, 2000, SKEncodedImageFormat.Jpeg)));

        using var bitmap = SKBitmap.Decode(FileFor(path));
        Assert.Equal((1600, 1000), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public async Task Removes_exif_metadata_such_as_gps_location()
    {
        var jpeg = Image(20, 20, SKEncodedImageFormat.Jpeg);
        // Insert an APP1 "Exif" segment right after the start-of-image marker, carrying a recognisable payload.
        var payload = Encoding.ASCII.GetBytes("Exif\0\0GPS-LATITUDE-51.5007-LONGITUDE--0.1246");
        var length = payload.Length + 2;
        byte[] withExif = [.. jpeg[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];

        var path = await _store.SaveAsync(new MemoryStream(withExif));

        var saved = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(FileFor(path)));
        Assert.DoesNotContain("GPS-LATITUDE", saved);
        Assert.DoesNotContain("Exif", saved);
    }

    [Fact]
    public async Task Turns_a_sideways_phone_photo_upright()
    {
        // A 40 × 20 JPEG whose EXIF orientation (6) says "rotate 90° clockwise to view", like a phone held upright.
        var jpeg = Image(40, 20, SKEncodedImageFormat.Jpeg);
        byte[] tiff =
        [
            .. "Exif\0\0"u8, (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // big-endian TIFF, first IFD at 8
            0x00, 0x01,                                                                 // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x00,     // Orientation (SHORT) = 6
            0x00, 0x00, 0x00, 0x00,                                                     // no next IFD
        ];
        var length = tiff.Length + 2;
        byte[] rotated = [.. jpeg[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. tiff, .. jpeg[2..]];

        var path = await _store.SaveAsync(new MemoryStream(rotated));

        using var bitmap = SKBitmap.Decode(FileFor(path));
        Assert.Equal((20, 40), (bitmap.Width, bitmap.Height));
    }

    [Theory]
    [InlineData("GIF89a not allowed")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("just some text pretending to be a photo")]
    public async Task Rejects_files_that_are_not_jpeg_or_png(string content)
    {
        var error = await Assert.ThrowsAsync<PhotoRejectedException>(() => _store.SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes(content))));

        Assert.Equal("Upload a JPG or PNG photo.", error.Message);
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFiles(_root).Any());
    }

    [Fact]
    public async Task Rejects_a_file_with_a_jpeg_header_but_broken_image_data()
    {
        byte[] fake = [0xFF, 0xD8, 0xFF, .. Encoding.ASCII.GetBytes("not really a jpeg")];

        await Assert.ThrowsAsync<PhotoRejectedException>(() => _store.SaveAsync(new MemoryStream(fake)));
    }

    [Fact]
    public async Task Rejects_uploads_over_5_mb()
    {
        var tooBig = new byte[AuctionPhotoStore.MaxUploadBytes + 1];
        tooBig[0] = 0xFF; tooBig[1] = 0xD8; tooBig[2] = 0xFF;

        var error = await Assert.ThrowsAsync<PhotoRejectedException>(() => _store.SaveAsync(new MemoryStream(tooBig)));
        Assert.Equal("Photos can be up to 5 MB.", error.Message);
    }

    [Fact]
    public async Task Delete_removes_saved_photos_but_ignores_other_paths()
    {
        var path = await _store.SaveAsync(new MemoryStream(Image(10, 10, SKEncodedImageFormat.Png)));
        var outsideName = $"{Guid.NewGuid():N}.jpg";
        var outside = Path.Combine(_root, "..", outsideName);
        await File.WriteAllTextAsync(outside, "keep me");

        await _store.DeleteAsync($"uploads/../{outsideName}");
        await _store.DeleteAsync("uploads/");
        await _store.DeleteAsync("https://example.com/a.jpg");
        await _store.DeleteAsync(null);
        Assert.True(File.Exists(outside));
        File.Delete(outside);

        await _store.DeleteAsync(path);
        Assert.False(File.Exists(FileFor(path)));
    }

    [Theory]
    [InlineData("../secret.jpg")]
    [InlineData("..%2Fsecret.jpg")]
    [InlineData("0123456789abcdef0123456789abcdef.png")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.jpg")]
    [InlineData("web.config")]
    public async Task Open_only_serves_keys_it_generated(string key)
    {
        Assert.Null(await _store.OpenAsync(key));
    }

    [Fact]
    public async Task Open_returns_a_saved_photo()
    {
        var path = await _store.SaveAsync(new MemoryStream(Image(10, 10, SKEncodedImageFormat.Png)));

        await using var photo = await _store.OpenAsync(path["uploads/".Length..]);
        Assert.NotNull(photo);
        Assert.Equal(0xFF, photo!.ReadByte());
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 0, 0, 0, 0)]
    [InlineData(SKEncodedOrigin.RightTop, 0, 0, 20, 0)]      // rotate 90° clockwise: top-left goes to top-right
    [InlineData(SKEncodedOrigin.LeftBottom, 0, 0, 0, 40)]    // rotate 90° counter-clockwise: top-left goes to bottom-left
    [InlineData(SKEncodedOrigin.BottomRight, 0, 0, 40, 20)]  // rotate 180°
    [InlineData(SKEncodedOrigin.TopRight, 0, 0, 40, 0)]      // mirror
    [InlineData(SKEncodedOrigin.LeftTop, 40, 0, 0, 40)]      // transpose
    public void OrientationMatrix_turns_stored_pixels_upright(SKEncodedOrigin origin, float x, float y, float expectedX, float expectedY)
    {
        // Stored image is 40 × 20.
        var point = AuctionPhotoStore.OrientationMatrix(origin, 40, 20).MapPoint(x, y);

        Assert.Equal(expectedX, point.X, precision: 3);
        Assert.Equal(expectedY, point.Y, precision: 3);
    }

    [Fact]
    public void OrientedSize_swaps_width_and_height_for_quarter_turns()
    {
        Assert.Equal((20, 40), AuctionPhotoStore.OrientedSize(SKEncodedOrigin.RightTop, 40, 20));
        Assert.Equal((40, 20), AuctionPhotoStore.OrientedSize(SKEncodedOrigin.BottomRight, 40, 20));
    }
}
