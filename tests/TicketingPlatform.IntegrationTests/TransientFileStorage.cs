using Microsoft.Extensions.Configuration;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Storage;

namespace TicketingPlatform.IntegrationTests;

/// <summary>Test-only storage decorator that deterministically fails the next N saves.</summary>
internal sealed class TransientFileStorage : IFileStorage
{
    private readonly LocalFileStorage _inner;
    private int _failuresRemaining;
    private int _saveAttempts;

    public TransientFileStorage(IConfiguration configuration) => _inner = new LocalFileStorage(configuration);

    public int SaveAttempts => Volatile.Read(ref _saveAttempts);

    public void FailNextSaves(int count)
    {
        Interlocked.Exchange(ref _saveAttempts, 0);
        Interlocked.Exchange(ref _failuresRemaining, count);
    }

    public async Task SaveAsync(string path, byte[] content, CancellationToken ct)
    {
        Interlocked.Increment(ref _saveAttempts);
        if (TryConsumeFailure())
            throw new IOException("Deterministic transient storage failure.");

        await _inner.SaveAsync(path, content, ct);
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct) => _inner.OpenReadAsync(path, ct);

    private bool TryConsumeFailure()
    {
        while (true)
        {
            var remaining = Volatile.Read(ref _failuresRemaining);
            if (remaining <= 0)
                return false;
            if (Interlocked.CompareExchange(ref _failuresRemaining, remaining - 1, remaining) == remaining)
                return true;
        }
    }
}
