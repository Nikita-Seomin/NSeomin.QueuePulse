using System.Collections.Concurrent;
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Domain;

namespace NS.QueuePulse.Infrastructure.InMemory;

public sealed class InMemoryJobRepository : IJobRepository
{
    private readonly ConcurrentDictionary<JobId, Job> _jobs = new();

    public Task AddAsync(Job job, CancellationToken ct)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task<Job?> GetAsync(JobId id, CancellationToken ct)
    {
        _jobs.TryGetValue(id, out var job);
        return Task.FromResult(job);
    }

    public Task UpdateAsync(JobId id, Action<Job> mutate, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(id, out var job))
            return Task.CompletedTask;

        lock (job) // лок на конкретный агрегат
        {
            mutate(job);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Job>> ListAsync(int take, CancellationToken ct)
    {
        var list = _jobs.Values
            .OrderByDescending(j => j.CreatedAtUtc)
            .Take(take <= 0 ? 100 : take)
            .ToArray();

        return Task.FromResult<IReadOnlyList<Job>>(list);
    }
}