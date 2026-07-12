using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.Infrastructure.Storage;

/// <summary>
/// S3/MinIO implementation of the storage port - genuinely shared blob storage, so a ticket PDF
/// written by a worker pod is readable by every API pod (unlike a ReadWriteOnce volume). Writes are
/// idempotent and atomic: <c>PutObject</c> to a deterministic key overwrites in place and an object
/// only becomes visible once fully written, so a retried ticket-issue never leaves a torn file.
/// </summary>
public sealed class S3FileStorage : IFileStorage
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3FileStorage(IAmazonS3 s3, S3StorageOptions options)
    {
        _s3 = s3;
        _bucket = options.BucketName;
    }

    public async Task SaveAsync(string path, byte[] content, CancellationToken ct)
    {
        using var body = new MemoryStream(content, writable: false);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = ToKey(path),
            InputStream = body,
            AutoCloseStream = false
        }, ct);
    }

    public async Task<Stream?> OpenReadAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _s3.GetObjectAsync(_bucket, ToKey(path), ct);

            // Copy into a self-contained, seekable stream so the caller owns it and the underlying
            // HTTP response is released here. Ticket PDFs/images are small, so buffering is fine.
            var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            return buffer;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Storage-relative paths are object keys; keep them forward-slashed and un-rooted.
    private static string ToKey(string path) => path.Replace('\\', '/').TrimStart('/');
}
