using Microsoft.Extensions.Configuration;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.Infrastructure.Storage;

/// <summary>
/// Filesystem implementation of the storage port (root from FileStorage:Root). Swapping to
/// MinIO/S3 later means one new class, zero caller changes - that is what the port buys.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;

    public LocalFileStorage(IConfiguration configuration)
    {
        _root = Path.GetFullPath(configuration["FileStorage:Root"] ?? "data");
        if (!_root.EndsWith(Path.DirectorySeparatorChar))
            _root += Path.DirectorySeparatorChar;
    }

    public async Task SaveAsync(string path, byte[] content, CancellationToken ct)
    {
        var fullPath = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, content, ct);
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct)
    {
        var fullPath = Resolve(path);
        return Task.FromResult<Stream?>(File.Exists(fullPath) ? File.OpenRead(fullPath) : null);
    }

    private string Resolve(string path)
    {
        // Path-traversal guard: a stored path must never escape the storage root.
        var full = Path.GetFullPath(Path.Combine(_root, path));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path '{path}' escapes the storage root.");
        return full;
    }
}
