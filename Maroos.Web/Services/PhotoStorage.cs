using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Maroos.Web.Services;

public sealed class PhotoStorageOptions
{
    /// <summary><c>Local</c> (a folder on disk; development) or <c>S3</c> (any S3-compatible bucket; production).</summary>
    public string Storage { get; set; } = "Local";

    /// <summary>Folder for <c>Local</c> storage; relative paths are under the content root.</summary>
    public string Path { get; set; } = "wwwroot/uploads";

    public S3PhotoOptions S3 { get; set; } = new();

    /// <summary>
    /// Reads <c>Uploads</c> settings, filling S3 gaps from the standard variables hosts set when they attach a bucket
    /// (Fly.io's <c>fly storage create</c>: BUCKET_NAME, AWS_ENDPOINT_URL_S3, AWS_REGION; keys come from AWS_ACCESS_KEY_ID /
    /// AWS_SECRET_ACCESS_KEY through the SDK's default credentials).
    /// </summary>
    public static PhotoStorageOptions From(IConfiguration configuration)
    {
        var options = configuration.GetSection("Uploads").Get<PhotoStorageOptions>() ?? new PhotoStorageOptions();
        var s3 = configuration.GetSection("Uploads:S3");
        if (string.IsNullOrWhiteSpace(options.S3.Bucket))
            options.S3.Bucket = configuration["BUCKET_NAME"] ?? "";
        if (string.IsNullOrWhiteSpace(options.S3.ServiceUrl))
            options.S3.ServiceUrl = configuration["AWS_ENDPOINT_URL_S3"];
        if (s3["Region"] is null && configuration["AWS_REGION"] is { Length: > 0 } region)
            options.S3.Region = region;
        return options;
    }
}

public sealed class S3PhotoOptions
{
    public string Bucket { get; set; } = "";

    /// <summary>Key prefix inside the bucket, e.g. <c>auction-photos/</c>.</summary>
    public string Prefix { get; set; } = "auction-photos/";

    /// <summary>Endpoint for non-AWS providers: Cloudflare R2, Backblaze B2, Fly Tigris, MinIO. Empty means AWS.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>AWS region, or the provider's region name (R2 uses <c>auto</c>).</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>MinIO and some providers need path-style URLs.</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>Leave both empty to use the standard AWS credential chain (env vars, instance roles).</summary>
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
}

/// <summary>Where finished photo bytes live. Keys are generated file names like <c>3f2c….jpg</c>.</summary>
public interface IPhotoBlobStore
{
    Task PutAsync(string key, byte[] jpeg, CancellationToken ct = default);

    /// <summary>The stored photo, or null if there's no such key.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed class LocalPhotoBlobStore(string root) : IPhotoBlobStore
{
    public string Root { get; } = System.IO.Path.GetFullPath(root);

    public async Task PutAsync(string key, byte[] jpeg, CancellationToken ct = default)
    {
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
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    private string PathFor(string key) => System.IO.Path.Combine(Root, key);
}

/// <summary>Stores photos in an S3-compatible bucket. The bucket can stay private: the app serves the photos.</summary>
public sealed class S3PhotoBlobStore(IAmazonS3 s3, S3PhotoOptions options) : IPhotoBlobStore
{
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

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        s3.DeleteObjectAsync(options.Bucket, options.Prefix + key, ct);

    public static IAmazonS3 CreateClient(S3PhotoOptions options)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            // Non-AWS providers (R2, B2, MinIO) don't all support the SDK's newer default checksums.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return !string.IsNullOrEmpty(options.AccessKey) && !string.IsNullOrEmpty(options.SecretKey)
            ? new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
            : new AmazonS3Client(config);
    }
}
