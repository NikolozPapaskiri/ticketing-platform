using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TicketingPlatform.Infrastructure.Storage;

/// <summary>
/// Ensures the storage bucket exists before anything reads or writes it. Runs on every role that
/// uses S3 (both the API reader and the worker writer), and is idempotent - a bucket already owned
/// by us is a no-op, so two pods starting at once cannot fight over it. In real AWS the bucket is
/// usually provisioned by IaC; this keeps the MinIO dev/CI path zero-touch.
/// </summary>
public sealed class S3BucketInitializer : IHostedService
{
    private readonly IAmazonS3 _s3;
    private readonly S3StorageOptions _options;
    private readonly ILogger<S3BucketInitializer> _logger;

    public S3BucketInitializer(IAmazonS3 s3, S3StorageOptions options, ILogger<S3BucketInitializer> logger)
    {
        _s3 = s3;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureBucketAsync(cancellationToken);
                return;
            }
            // Object storage may not be reachable yet at startup (separate container/pod). Retry a
            // bounded number of times; the last attempt rethrows so the failure is visible and the
            // orchestrator restarts us rather than running blind against missing storage.
            catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Object storage not ready (attempt {Attempt}/{Max}); retrying", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (await AmazonS3Util.DoesS3BucketExistV2Async(_s3, _options.BucketName))
            return;

        try
        {
            await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _options.BucketName }, ct);
            _logger.LogInformation("Created storage bucket {Bucket}", _options.BucketName);
        }
        catch (BucketAlreadyOwnedByYouException)
        {
            // Another pod won the race - fine.
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Same race, reported as a 409 by some S3 implementations.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
