namespace TicketingPlatform.Infrastructure.Storage;

/// <summary>
/// S3/MinIO settings (section <c>FileStorage:S3</c>). The same client talks to AWS S3 in
/// production and a MinIO container in dev/CI - path-style addressing and an explicit service URL
/// are what make one implementation cover both.
/// </summary>
public sealed class S3StorageOptions
{
    public const string SectionName = "FileStorage:S3";

    /// <summary>Endpoint, e.g. <c>http://minio:9000</c> (MinIO) or empty for real AWS S3.</summary>
    public string ServiceUrl { get; init; } = "";

    public string BucketName { get; init; } = "ticketing-files";
    public string AccessKey { get; init; } = "";
    public string SecretKey { get; init; } = "";
    public string Region { get; init; } = "us-east-1";

    /// <summary>MinIO needs path-style (bucket in the path, not a vhost subdomain). Default on.</summary>
    public bool ForcePathStyle { get; init; } = true;
}
