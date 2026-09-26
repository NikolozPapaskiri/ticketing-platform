using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Testcontainers.Minio;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Storage;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// PR 6 §6.3 - shared object storage behind the same <see cref="IFileStorage"/> port, exercised
/// against a real MinIO container. Proves the round-trip, idempotent/atomic overwrite (a retried
/// ticket-issue is safe), and the null-on-missing contract the callers rely on.
/// </summary>
public sealed class S3FileStorageTests : IAsyncLifetime
{
    private const string User = "minioadmin";
    private const string Password = "minioadmin";
    private const string Bucket = "ticketing-files";

    // MinIO stopped publishing public images (minio/minio and quay.io/minio/minio no longer pull),
    // which broke the module's default image. Chainguard's maintained MinIO fork is a drop-in:
    // same binary, env vars, and health endpoints. Its free tier only publishes a moving `latest`,
    // so the digest pins what CI runs; keep it in sync with docker-compose.yml and k8s/minio.yaml.
    private const string MinioImage =
        "cgr.dev/chainguard/minio:latest@sha256:bd014394a80898e68c149f2311fdf8d5a2c2f3bb2c33b9327ae6d02b4b065ae1";

    private readonly MinioContainer _minio = new MinioBuilder(MinioImage)
        .WithUsername(User)
        .WithPassword(Password)
        .Build();

    private IAmazonS3 _s3 = null!;
    private IFileStorage _storage = null!;

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();

        var config = new AmazonS3Config
        {
            ServiceURL = _minio.GetConnectionString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(User, Password), config);
        await _s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });

        _storage = new S3FileStorage(_s3, new S3StorageOptions { BucketName = Bucket });
    }

    public async Task DisposeAsync()
    {
        _s3?.Dispose();
        await _minio.DisposeAsync();
    }

    [Fact]
    public async Task SaveThenOpenRead_RoundTripsTheBytes()
    {
        var content = Encoding.UTF8.GetBytes("%PDF-1.7 ticket credential");

        await _storage.SaveAsync("tickets/2026/ORD-1.pdf", content, CancellationToken.None);

        Assert.Equal(content, await ReadAllAsync("tickets/2026/ORD-1.pdf"));
    }

    [Fact]
    public async Task Save_IsIdempotent_OverwritingInPlace()
    {
        const string key = "event-images/tenant-a/event-1.jpg";
        var first = Encoding.UTF8.GetBytes("image-v1");
        var second = Encoding.UTF8.GetBytes("image-v2-superseded");

        // A deterministic key + overwrite is how we avoid orphaned, superseded blobs.
        await _storage.SaveAsync(key, first, CancellationToken.None);
        await _storage.SaveAsync(key, second, CancellationToken.None);

        Assert.Equal(second, await ReadAllAsync(key));

        // Exactly one object under that key - the overwrite replaced, it did not accumulate.
        var listing = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Prefix = key });
        Assert.Single(listing.S3Objects);
    }

    [Fact]
    public async Task OpenRead_MissingKey_ReturnsNull()
    {
        Assert.Null(await _storage.OpenReadAsync("tickets/does-not-exist.pdf", CancellationToken.None));
    }

    private async Task<byte[]> ReadAllAsync(string path)
    {
        await using var stream = await _storage.OpenReadAsync(path, CancellationToken.None);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        await stream!.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
