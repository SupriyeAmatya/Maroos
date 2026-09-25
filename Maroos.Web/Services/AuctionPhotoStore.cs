// =====================================================================================================
// FILE: Maroos.Web/Services/AuctionPhotoStore.cs
// PURPOSE: Makes uploaded auction photos SAFE before storing them.
//
// THE PROCESS (SaveAsync)
//   1. Read the upload, stopping at 5 MB.
//   2. Check the first bytes ("magic numbers"): only real JPEG or PNG files pass — not a renamed .exe/.html.
//   3. Decode the image with SkiaSharp and draw it onto a fresh canvas:
//        * rotated upright according to the phone's EXIF orientation tag,
//        * shrunk so the longest side is at most 1600 px,
//        * transparent areas filled white.
//   4. Encode the canvas as a brand-new JPEG (quality 85). The new file contains ONLY pixels — the original
//      EXIF metadata, including GPS location from phone cameras, is gone.
//   5. Store it under a random key (32 hex characters + ".jpg") and return "uploads/{key}".
//
// Keys are the only thing ever accepted back (OpenAsync/DeleteAsync check the pattern), so a crafted URL
// can never read or delete any other file.
// =====================================================================================================

using System.Text.RegularExpressions;
using SkiaSharp;

namespace Maroos.Web.Services;

// PhotoRejectedException: thrown with a user-friendly message; ListCard.razor shows it next to the upload box.
public sealed class PhotoRejectedException(string message) : Exception(message);

/// <summary>
/// Saves auction photos. Every upload is decoded and re-encoded as a JPEG, which drops EXIF metadata
/// (phone photos carry GPS location), turns rotated phone photos upright, shrinks very large images,
/// and rejects anything that isn't really a JPG or PNG. The bytes go to an <see cref="IPhotoBlobStore"/>
/// (disk in development, an S3 bucket in production); the app serves them at <c>/uploads/{key}</c>,
/// so stored paths are the same whatever the backend.
/// </summary>
// `partial` is needed for the [GeneratedRegex] below (the compiler writes the regex code at build time).
public sealed partial class AuctionPhotoStore(IPhotoBlobStore blobs)
{
    // Upload limit: 5 MB.
    public const long MaxUploadBytes = 5 * 1024 * 1024;
    // Longest side of the stored photo, in pixels.
    public const int MaxDimension = 1600;
    // Stored paths look like "uploads/<key>" (Auction.PhotoPath), served at /uploads/<key>.
    public const string UrlPrefix = "uploads/";
    // What the file picker offers (the "accept" attribute); the magic-number check is the real test.
    public static readonly IReadOnlyList<string> AcceptedTypes = ["image/jpeg", "image/png"];

    /// <summary>Only keys this class generates: 32 hex characters and ".jpg". Nothing else is ever read or deleted.</summary>
    [GeneratedRegex("^[0-9a-f]{32}\\.jpg$")]
    private static partial Regex KeyPattern();

    // IsValidKey: true only for keys shaped like the ones SaveAsync creates.
    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>Stores the photo and returns its site-relative URL, e.g. <c>uploads/3f2c….jpg</c>.</summary>
    public async Task<string> SaveAsync(Stream upload, CancellationToken ct = default)
    {
        // Step 1: read at most 5 MB.
        var bytes = await ReadLimitedAsync(upload, ct);
        // Step 2: is it really a JPEG or PNG?
        if (!IsJpeg(bytes) && !IsPng(bytes))
            throw new PhotoRejectedException("Upload a JPG or PNG photo.");

        // Steps 3–4: decode, orient, resize, re-encode.
        var jpeg = Normalize(bytes);
        // Step 5: random key (Guid "N" format = 32 hex digits, no dashes), then store.
        var key = $"{Guid.NewGuid():N}.jpg";
        await blobs.PutAsync(key, jpeg, ct);
        return UrlPrefix + key;
    }

    /// <summary>The photo for a key from a <c>/uploads/{key}</c> URL, or null if it's not one of ours.</summary>
    public Task<Stream?> OpenAsync(string key, CancellationToken ct = default) =>
        IsValidKey(key) ? blobs.OpenReadAsync(key, ct) : Task.FromResult<Stream?>(null);

    /// <summary>Deletes a photo saved by <see cref="SaveAsync"/>; ignores anything else.</summary>
    // Used when a listing fails to save after its photo was stored (so no orphan photo is left behind).
    public async Task DeleteAsync(string? photoPath, CancellationToken ct = default)
    {
        if (photoPath is null || !photoPath.StartsWith(UrlPrefix, StringComparison.Ordinal))
            return;
        var key = photoPath[UrlPrefix.Length..];
        if (IsValidKey(key))
            await blobs.DeleteAsync(key, ct);
    }

    // ReadLimitedAsync: copy the upload into memory in 80 KB chunks, failing as soon as it passes 5 MB
    // (so a huge upload is never fully read into memory).
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

    // IsJpeg: every JPEG starts with the bytes FF D8 FF.
    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    // IsPng: every PNG starts with the 8-byte signature 89 'P' 'N' 'G' \r \n 1A \n.
    private static bool IsPng(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    // Normalize: steps 3–4 of the process above. Returns the new JPEG bytes.
    private static byte[] Normalize(byte[] bytes)
    {
        // Decode. A file with valid magic bytes but broken content fails here.
        using var codec = SKCodec.Create(new SKMemoryStream(bytes))
                          ?? throw new PhotoRejectedException("That file isn't a readable image.");
        using var source = SKBitmap.Decode(codec)
                           ?? throw new PhotoRejectedException("That file isn't a readable image.");

        // origin: the EXIF orientation (how the camera was held).
        var origin = codec.EncodedOrigin;
        // The upright size, then the scale factor so the longest side is <= 1600 (never enlarge: at most 1).
        var (width, height) = OrientedSize(origin, source.Width, source.Height);
        var scale = Math.Min(1f, (float)MaxDimension / Math.Max(width, height));
        var outWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outHeight = Math.Max(1, (int)Math.Round(height * scale));

        // A blank canvas of the final size.
        using var surface = SKSurface.Create(new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
                            ?? throw new PhotoRejectedException("That photo is too large to process.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White); // transparent PNG areas become white, not black
        // Shrink, then rotate/flip upright, then draw the photo with smooth (linear + mipmap) sampling.
        canvas.Scale(scale);
        canvas.Concat(OrientationMatrix(origin, source.Width, source.Height));
        using (var image = SKImage.FromBitmap(source))
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

        // Encode the canvas as a fresh JPEG at quality 85 — no metadata is copied.
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
    // One 3x3 transform matrix per EXIF orientation value (1–8). The arguments are
    // (ScaleX, SkewX, TransX, SkewY, ScaleY, TransY, Persp0, Persp1, Persp2). Tested with a pixel-by-pixel test.
    public static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // mirrored
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // rotated 180°
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // flipped
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // transposed
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // needs 90° clockwise
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // transverse
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // needs 90° counter-clockwise
        // TopLeft (already upright) or unknown: no change.
        _ => SKMatrix.Identity,
    };
}
