using System.Text.Json;
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Domain;
using NS.QueuePulse.Infrastructure.InMemory;

namespace NS.QueuePulse.Application;

public sealed class NullJobProgressPublisher : IJobProgressPublisher
{
    public static readonly NullJobProgressPublisher Instance = new();
    private NullJobProgressPublisher() { }
    public Task PublishAsync(JobId id, ProgressSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
}

public sealed class JobClient : IJobClient
{
    private readonly IJobQueue _queue;
    private readonly IJobRepository _repo;
    private readonly IJobProgressPublisher _publisher;
    private readonly IJobRuntimeRegistry _runtime;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly JsonSerializerOptions _json;

    public JobClient(
        IJobQueue queue,
        IJobRepository repo,
        IJobProgressPublisher publisher,
        IJobRuntimeRegistry runtime,
        JsonSerializerOptions? jsonOptions = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _queue = queue;
        _repo = repo;
        _publisher = publisher;
        _runtime = runtime;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _json = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public async Task<JobId> EnqueueAsync(JobType type, string? payloadJson = null, CancellationToken ct = default)
    {
        var now = _utcNow();
        var job = Job.Create(type, payloadJson, now);

        await _repo.AddAsync(job, ct).ConfigureAwait(false);
        await _queue.EnqueueAsync(new JobTicket(job.Id, job.Type, job.PayloadJson), ct).ConfigureAwait(false);

        // можно сразу опубликовать "Queued"
        _ = _publisher.PublishAsync(job.Id, job.Progress, CancellationToken.None);
        return job.Id;
    }

    public Task<JobId> EnqueueAsync<TArgs>(JobType type, TArgs args, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(args, _json);
        return EnqueueAsync(type, json, ct);
    }

    public Task PauseAsync(JobId id, CancellationToken ct = default)
        => _repo.UpdateAsync(id, job =>
        {
            job.Pause(_utcNow());
            var h = _runtime.GetOrCreate(id);
            h.Gate.Pause();
        }, ct);

    public Task ResumeAsync(JobId id, CancellationToken ct = default)
        => _repo.UpdateAsync(id, job =>
        {
            job.Resume(_utcNow());
            var h = _runtime.GetOrCreate(id);
            h.Gate.Resume();
        }, ct);

    public Task CancelAsync(JobId id, CancellationToken ct = default)
        => _repo.UpdateAsync(id, job =>
        {
            job.RequestCancel(_utcNow());
            var h = _runtime.GetOrCreate(id);
            h.Cts.Cancel(); // если job еще не стартовал — worker все равно пропустит по Status
            h.Gate.Resume(); // на всякий случай, чтобы не висеть в pause
        }, ct);

    public async Task<JobSnapshot?> GetAsync(JobId id, CancellationToken ct = default)
    {
        var job = await _repo.GetAsync(id, ct).ConfigureAwait(false);
        if (job is null) return null;
        return ToSnapshot(job);
    }

    public async Task<IReadOnlyList<JobSnapshot>> ListAsync(int take = 100, CancellationToken ct = default)
    {
        var jobs = await _repo.ListAsync(take, ct).ConfigureAwait(false);
        return jobs.Select(ToSnapshot).ToArray();
    }

    private static JobSnapshot ToSnapshot(Job job)
        => new(
            Id: job.Id.Value,
            Type: job.Type.Value,
            Status: job.Status,
            Progress: job.Progress,
            Error: job.Error,
            CreatedAtUtc: job.CreatedAtUtc,
            StartedAtUtc: job.StartedAtUtc,
            FinishedAtUtc: job.FinishedAtUtc
        );
}