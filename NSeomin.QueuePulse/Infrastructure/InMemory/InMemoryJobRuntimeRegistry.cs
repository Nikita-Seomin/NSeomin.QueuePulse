using System.Collections.Concurrent;
using NSeomin.QueuePulse.Domain;
using NSeomin.QueuePulse.Application;

namespace NSeomin.QueuePulse.Infrastructure.InMemory;

public interface IJobRuntimeRegistry
{
    PauseGateHandle GetOrCreate(JobId id);
    bool TryGet(JobId id, out PauseGateHandle handle);
    void Remove(JobId id);
}

public sealed class PauseGateHandle
{
    public PauseGate Gate { get; } = new();
    public CancellationTokenSource Cts { get; } = new();
}

public sealed class InMemoryJobRuntimeRegistry : IJobRuntimeRegistry
{
    private readonly ConcurrentDictionary<JobId, PauseGateHandle> _map = new();

    public PauseGateHandle GetOrCreate(JobId id)
        => _map.GetOrAdd(id, _ => new PauseGateHandle());

    public bool TryGet(JobId id, out PauseGateHandle handle)
        => _map.TryGetValue(id, out handle!);

    public void Remove(JobId id)
    {
        if (_map.TryRemove(id, out var h))
        {
            h.Cts.Dispose();
        }
    }
}