// =====================================================================================================
// FILE: Maroos.Web/Services/PhotoStorage.cs
// PURPOSE: WHERE auction photos are kept. (HOW they're cleaned up is AuctionPhotoStore.cs.)
//
//   IPhotoBlobStore       — the interface: Put / OpenRead / Delete a photo by key ("3f2c...e1.jpg")
//   LocalPhotoBlobStore   — development: files in a folder (wwwroot/uploads)
//   S3PhotoBlobStore      — production: any S3-compatible bucket (AWS S3, Cloudflare R2, Backblaze B2,
//                           Fly Tigris, MinIO/Versity locally)
//   PhotoStorageOptions   — the "Uploads" settings that choose between them (Uploads:Storage = Local | S3)
//
// WHY AN INTERFACE: containers lose their disk on every redeploy, so production needs a bucket. The rest of
// the app only talks to IPhotoBlobStore and never knows which one is in use. The app itself serves photos
// at /uploads/{key}, so the bucket can stay private.
// =====================================================================================================

using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Maroos.Web.Services;

// PhotoStorageOptions: bound from the "Uploads" section of configuration (appsettings.json / environment
// variables such as Uploads__Storage=S3, Uploads__S3__Bucket=maroos-photos).
public sealed class PhotoStorageOptions
{
    /// <summary><c>Local</c> (a folder on disk; development) or <c>S3</c> (any S3-compatible bucket; production).</summary>
    public string Storage { get; set; } = "Local";

    /// <summary>Folder for <c>Local</c> storage; relative paths are under the content root.</summary>
    public string Path { get; set; } = "wwwroot/uploads";

    // S3: bucket settings (only used when Storage is S3).
    public S3PhotoOptions S3 { get; set; } = new();

    /// <summary>
    /// Reads <c>Uploads</c> settings, filling S3 gaps from the standard variables hosts set when they attach a bucket
    /// (Fly.io's <c>fly storage create</c>: BUCKET_NAME, AWS_ENDPOINT_URL_S3, AWS_REGION; keys come from AWS_ACCESS_KEY_ID /
    /// AWS_SECRET_ACCESS_KEY through the SDK's default credentials).
    /// </summary>
    // From: explicit Uploads:* settings win; the host's standard variables only fill in what's missing.
    public static PhotoStorageOptions From(IConfiguration configuration)
    {
        // Bind the section into an object (defaults if the section is absent).
        var options = configuration.GetSection("Uploads").Get<PhotoStorageOptions>() ?? new PhotoStorageOptions();
        var s3 = configuration.GetSection("Uploads:S3");
        if (string.IsNullOrWhiteSpace(options.S3.Bucket))
            options.S3.Bucket = configuration["BUCKET_NAME"] ?? "";
        if (string.IsNullOrWhiteSpace(options.S3.ServiceUrl))
            options.S3.ServiceUrl = configuration["AWS_ENDPOINT_URL_S3"];
        // Region has a default ("us-east-1"), so check whether it was *configured*, not whether it's empty.
        if (s3["Region"] is null && configuration["AWS_REGION"] is { Length: > 0 } region)
            options.S3.Region = region;
        return options;
    }
}

// S3PhotoOptions: the settings for one S3-compatible bucket.
public sealed class S3PhotoOptions
{
    // Bucket: the bucket name (required when Storage is S3; Program.cs fails fast without it).
    public string Bucket { get; set; } = "";

    /// <summary>Key prefix inside the bucket, e.g. <c>auction-photos/</c>.</summary>
    public string Prefix { get; set; } = "auction-photos/";

    /// <summary>Endpoint for non-AWS providers: Cloudflare R2, Backblaze B2, Fly Tigris, MinIO. Empty means AWS.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>AWS region, or the provider's region name (R2 uses <c>auto</c>).</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>MinIO and some providers need path-style URLs.</summary>
    // Path style: http://host/bucket/key instead of http://bucket.host/key.
    public bool ForcePathStyle { get; set; }

    /// <summary>Leave both empty to use the standard AWS credential chain (env vars, instance roles).</summary>
    // Secrets: set these through environment variables / the host's secret store, never in appsettings.json.
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
}

/// <summary>Where finished photo bytes live. Keys are generated file names like <c>3f2c….jpg</c>.</summary>
public interface IPhotoBlobStore
{
    // PutAsync: store (or overwrite) the JPEG bytes under `key`.
    Task PutAsync(string key, byte[] jpeg, CancellationToken ct = default);

    /// <summary>The stored photo, or null if there's no such key.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);

    // DeleteAsync: remove the photo (no error if it's already gone).
    Task DeleteAsync(string key, CancellationToken ct = default);
}

// LocalPhotoBlobStore: photos as files in one folder (`root`). Used in development and tests.
public sealed class LocalPhotoBlobStore(string root) : IPhotoBlobStore
{
    // Root: the folder as a full absolute path.
    public string Root { get; } = System.IO.Path.GetFullPath(root);

    public async Task PutAsync(string key, byte[] jpeg, CancellationToken ct = default)
    {
        // Create the folder the first time (does nothing if it exists).
        Directory.CreateDirectory(Root);
        await File.WriteAllBytesAsync(PathFor(key), jpeg, ct);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        // File.Delete doesn't throw when the file doesn't exist.
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    // PathFor: the file path for a key. Keys are validated by AuctionPhotoStore first, so no "../" can get here.
    private string PathFor(string key) => System.IO.Path.Combine(Root, key);
}

/// <summary>Stores photos in an S3-compatible bucket. The bucket can stay private: the app serves the photos.</summary>
// s3: the AWS SDK client (created once by CreateClient, below). options: bucket and prefix.
public sealed class S3PhotoBlobStore(IAmazonS3 s3, S3PhotoOptions options) : IPhotoBlobStore
{
    // PutAsync: upload the bytes as an object named Prefix + key ("auction-photos/3f2c...jpg").
    public async Task PutAsync(string key, byte[] jpeg, CancellationToken ct = default)
    {
        using var body = new MemoryStream(jpeg);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = options.Prefix + key,
            InputStream = body,
            ContentType = "image/jpeg",
        }, ct);
    }

    // OpenReadAsync: download stream for the object; S3's 404 becomes null ("no such photo").
    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await s3.GetObjectAsync(options.Bucket, options.Prefix + key, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // DeleteAsync: S3 deletes succeed even when the object doesn't exist.
    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        s3.DeleteObjectAsync(options.Bucket, options.Prefix + key, ct);

    // CreateClient: builds the S3 client from the options (called once at startup in Program.cs).
    public static IAmazonS3 CreateClient(S3PhotoOptions options)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            // Non-AWS providers (R2, B2, MinIO) don't all support the SDK's newer default checksums.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        // Non-AWS provider: talk to its URL, sign requests for its region name.
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
        }
        // Real AWS: just pick the region.
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        // Explicit keys if both were given; otherwise the SDK finds credentials itself (AWS_* env vars, IAM roles).
        return !string.IsNullOrEmpty(options.AccessKey) && !string.IsNullOrEmpty(options.SecretKey)
            ? new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
            : new AmazonS3Client(config);
    }
}
