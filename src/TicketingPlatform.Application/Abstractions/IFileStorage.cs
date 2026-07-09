namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for blob storage. Local filesystem in dev; the same interface fronts MinIO/S3 later -
/// callers only ever see storage-relative paths. Files never go into the relational database.
/// </summary>
public interface IFileStorage
{
    Task SaveAsync(string path, byte[] content, CancellationToken ct);

    /// <summary>Null when the file does not exist. Caller owns disposing the stream.</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken ct);
}
