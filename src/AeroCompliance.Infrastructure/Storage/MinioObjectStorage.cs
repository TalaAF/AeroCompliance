using Microsoft.Extensions.Configuration;
using Minio;
using Minio.DataModel.Args;

namespace AeroCompliance.Infrastructure.Storage;

public interface IObjectStorage
{
    string Bucket { get; }
    Task EnsureBucketAsync(CancellationToken ct = default);
    Task UploadFileAsync(string objectKey, string filePath, string contentType, CancellationToken ct = default);

    /// <summary>
    /// Uploads an in-memory payload. Used for artefacts the API generates itself (e.g. report PDFs),
    /// which never touch the local filesystem.
    /// </summary>
    Task UploadBytesAsync(string objectKey, byte[] content, string contentType, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct = default);
    Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default);
    Task DeleteAsync(string objectKey, CancellationToken ct = default);
}

public sealed class MinioObjectStorage : IObjectStorage
{
    private readonly IMinioClient client;
    public string Bucket { get; }

    public MinioObjectStorage(IConfiguration configuration)
    {
        var endpoint = configuration["MinIO:Endpoint"] ?? throw new InvalidOperationException("MinIO:Endpoint is required.");
        var accessKey = configuration["MinIO:AccessKey"] ?? throw new InvalidOperationException("MinIO:AccessKey is required.");
        var secretKey = configuration["MinIO:SecretKey"] ?? throw new InvalidOperationException("MinIO:SecretKey is required.");
        Bucket = configuration["MinIO:Bucket"] ?? "aerocompliance-images";
        var uri = new Uri(endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : $"http://{endpoint}");
        client = new MinioClient().WithEndpoint(uri.Host, uri.Port).WithCredentials(accessKey, secretKey).WithSSL(uri.Scheme == "https").Build();
    }

    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        if (!await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket), ct))
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket), ct);
    }

    public async Task UploadFileAsync(string objectKey, string filePath, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await client.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(objectKey)
            .WithStreamData(stream).WithObjectSize(stream.Length).WithContentType(contentType), ct);
    }

    public async Task UploadBytesAsync(string objectKey, byte[] content, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        using var stream = new MemoryStream(content, writable: false);
        await client.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(objectKey)
            .WithStreamData(stream).WithObjectSize(stream.Length).WithContentType(contentType), ct);
    }

    public async Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerocompliance-read-{Guid.NewGuid():N}.tmp");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            // WithCallbackStream takes an Action<Stream>. An async lambda here becomes async void:
            // MinIO closes its response stream before the copy finishes and can crash the process.
            await client.GetObjectAsync(new GetObjectArgs().WithBucket(Bucket).WithObject(objectKey)
                .WithCallbackStream(source => source.CopyTo(stream)), ct);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default)
    {
        try { await client.StatObjectAsync(new StatObjectArgs().WithBucket(Bucket).WithObject(objectKey), ct); return true; }
        catch (Minio.Exceptions.ObjectNotFoundException) { return false; }
    }

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        => client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(objectKey), ct);
}
