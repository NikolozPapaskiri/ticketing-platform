namespace TicketingPlatform.IntegrationTests.Support;

/// <summary>
/// Deterministic coordination primitive for race tests. A caller "passes through" the gate;
/// when the gate is armed for N passers, the first N block until the test explicitly releases
/// them, and the test can wait until all N have arrived. This makes the interleaving a choice
/// the test makes, not an accident of timing - a WireMock delay only makes a race *likely*,
/// a barrier makes it *exact*.
/// </summary>
public sealed class AsyncGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _release = CreateCompleted();
    private SemaphoreSlim _arrivals = new(0);
    private int _blockRemaining;

    private static TaskCompletionSource CreateCompleted()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    /// <summary>Arm the gate so the next <paramref name="count"/> passers block until <see cref="Release"/>.</summary>
    public void Arm(int count)
    {
        lock (_lock)
        {
            _blockRemaining = count;
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _arrivals = new SemaphoreSlim(0);
        }
    }

    /// <summary>Let every currently-blocked passer through.</summary>
    public void Release()
    {
        lock (_lock)
            _release.TrySetResult();
    }

    /// <summary>Block until <paramref name="count"/> passers have reached the gate.</summary>
    public async Task WaitForArrivalsAsync(int count, TimeSpan timeout)
    {
        for (var i = 0; i < count; i++)
        {
            if (!await _arrivals.WaitAsync(timeout))
                throw new TimeoutException($"Only {i} of {count} callers reached the gate within {timeout}.");
        }
    }

    /// <summary>Called from inside the code under test (via a seam). Blocks if the gate is armed.</summary>
    public async Task PassAsync(CancellationToken ct)
    {
        TaskCompletionSource release;
        bool block;
        SemaphoreSlim arrivals;
        lock (_lock)
        {
            block = _blockRemaining > 0;
            if (block) _blockRemaining--;
            release = _release;
            arrivals = _arrivals;
        }

        if (!block)
            return;

        arrivals.Release();               // signal the test that this caller has arrived
        await release.Task.WaitAsync(ct); // ...and hold here until the test releases
    }
}
