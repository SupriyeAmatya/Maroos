using SkiaSharp;

namespace Maroos.Web.Services;

public sealed class PhotoStorageOptions
{
    /// <summary>Folder for uploaded photos; relative paths are under the content root.</summary>
    public string Path { get; set; } = "wwwroot/uploads";

    /// <summary>URL prefix the photos are served from.</summary>
    public string RequestPath { get; set; } = "/uploads";
}

public sealed class PhotoRejectedException(string message) : Exception(message);

/// <summary>
/// Saves auction photos. Every upload is decoded and re-encoded as a JPEG, which drops EXIF metadata
/// (phone photos carry GPS location), turns rotated phone photos upright, shrinks very large images,
/// and rejects anything that isn't really a JPG or PNG.
/// </summary>
public sealed class AuctionPhotoStore(PhotoStorageOptions options, IWebHostEnvironment env)
{
    public const long MaxUploadBytes = 5 * 1024 * 1024;
    public const int MaxDimension = 1600;
    public static readonly IReadOnlyList<string> AcceptedTypes = ["image/jpeg", "image/png"];

    public string Root => System.IO.Path.GetFullPath(System.IO.Path.Combine(env.ContentRootPath, options.Path));

    /// <summary>Stores the photo and returns its site-relative URL, e.g. <c>uploads/3f2c….jpg</c>.</summary>
    public async Task<string> SaveAsync(Stream upload, CancellationToken ct = default)
    {
        var bytes = await ReadLimitedAsync(upload, ct);
        if (!IsJpeg(bytes) && !IsPng(bytes))
            throw new PhotoRejectedException("Upload a JPG or PNG photo.");

        var jpeg = Normalize(bytes);

        Directory.CreateDirectory(Root);
        var fileName = $"{Guid.NewGuid():N}.jpg";
        await File.WriteAllBytesAsync(System.IO.Path.Combine(Root, fileName), jpeg, ct);
        return $"{options.RequestPath.Trim('/')}/{fileName}";
    }

    /// <summary>Deletes a photo saved by <see cref="SaveAsync"/>; ignores anything else.</summary>
    public void Delete(string? photoPath)
    {
        var prefix = options.RequestPath.Trim('/') + "/";
        if (photoPath is null || !photoPath.StartsWith(prefix, StringComparison.Ordinal))
            return;

        // Only a bare file name we generated: never follow "..", slashes or anything else from the path.
        var fileName = photoPath[prefix.Length..];
        if (fileName != System.IO.Path.GetFileName(fileName) || !fileName.EndsWith(".jpg", StringComparison.Ordinal))
            return;

        File.Delete(System.IO.Path.Combine(Root, fileName));
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream upload, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await upload.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxUploadBytes)
                throw new PhotoRejectedException("Photos can be up to 5 MB.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsPng(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    private static byte[] Normalize(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes))
                          ?? throw new PhotoRejectedException("That file isn't a readable image.");
        using var source = SKBitmap.Decode(codec)
                           ?? throw new PhotoRejectedException("That file isn't a readable image.");

        var origin = codec.EncodedOrigin;
        var (width, height) = OrientedSize(origin, source.Width, source.Height);
        var scale = Math.Min(1f, (float)MaxDimension / Math.Max(width, height));
        var outWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outHeight = Math.Max(1, (int)Math.Round(height * scale));

        using var surface = SKSurface.Create(new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
                            ?? throw new PhotoRejectedException("That photo is too large to process.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White); // transparent PNG areas become white, not black
        canvas.Scale(scale);
        canvas.Concat(OrientationMatrix(origin, source.Width, source.Height));
        using (var image = SKImage.FromBitmap(source))
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    /// <summary>Upright size: origins 5–8 are rotated a quarter turn, so width and height swap.</summary>
    public static (int Width, int Height) OrientedSize(SKEncodedOrigin origin, int width, int height) =>
        (int)origin >= 5 ? (height, width) : (width, height);

    /// <summary>
    /// Maps stored pixels (w × h) to upright pixels, per the EXIF orientation:
    /// x' = ScaleX·x + SkewX·y + TransX, y' = SkewY·x + ScaleY·y + TransY.
    /// </summary>
    public static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // mirrored
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // rotated 180°
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // flipped
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // transposed
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // needs 90° clockwise
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // transverse
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // needs 90° counter-clockwise
        _ => SKMatrix.Identity,
    };
}
