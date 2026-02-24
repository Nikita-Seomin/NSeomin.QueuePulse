using System.Text.Json;
using NSeomin.QueuePulse.Domain;
using NSeomin.QueuePulse.Abstractions;
using NSeomin.QueuePulse.Infrastructure.InMemory;

namespace NSeomin.QueuePulse.Application;

public sealed class NullJobProgressPublisher : IJobProgressPublisher
{
    public static readonly NullJobProgressPublisher Instance = new();
    private NullJobProgressPublisher() { }
    public Task PublishAsync(JobId id, ProgressSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
}

public sealed class QueuePulseClientOptions
{
    public string DefaultQueueName { get; set; } = "default";
}

public sealed class JobClient : IJobClient
{
    private readonly IQueueManager _queues;
    private readonly IJobRepository _repo;
    private readonly IJobProgressPublisher _publisher;
    private readonly IJobRuntimeRegistry _runtime;
    private readonly QueuePulseClientOptions _clientOptions;
    private readonly JsonSerializerOptions _json;

    public JobClient(
        IQueueManager queues,
        IJobRepository repo,
        IJobProgressPublisher publisher,
        IJobRuntimeRegistry runtime,
        QueuePulseClientOptions? clientOptions = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        _queues = queues;
        _repo = repo;
        _publisher = publisher;
        _runtime = runtime;
        _clientOptions = clientOptions ?? new QueuePulseClientOptions();
        _json = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public Task<JobId> EnqueueAsync(JobType type, string? payloadJson = null, CancellationToken ct = default)
        => EnqueueAsync(_clientOptions.DefaultQueueName, type, payloadJson, ct);

    public Task<JobId> EnqueueAsync<TArgs>(JobType type, TArgs args, CancellationToken ct = default)
        => EnqueueAsync(_clientOptions.DefaultQueueName, type, args, ct);

    public async Task<JobId> EnqueueAsync(string queueName, JobType type, string? payloadJson = null, CancellationToken ct = default)
    {
        queueName = string.IsNullOrWhiteSpace(queueName) ? _clientOptions.DefaultQueueName : queueName;

        var now = DateTimeOffset.UtcNow;
        var job = Job.Create(type, queueName, payloadJson, now);

        await _repo.AddAsync(job, ct).ConfigureAwait(false);

        // auto-create queue if missing
        var queue = _queues.GetOrCreate(queueName);
        await queue.EnqueueAsync(new JobTicket(queueName, job.Id, job.Type, job.PayloadJson), ct).ConfigureAwait(false);

        _ = _publisher.PublishAsync(job.Id, job.Progress, CancellationToken.None);
        return job.Id;
    }

    public Task<JobId> EnqueueAsync<TArgs>(string queueName, JobType type, TArgs args, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(args, _json);
        return EnqueueAsync(queueName, type, json, ct);
    }

    public Task PauseAsync(JobId id, CancellationToken ct = default)
        => _repo.UpdateAsync(id, job =>
        {
            job.Pause(DateTimeOffset.UtcNow);
            var h = _runtime.GetOrCreate(id);
            h.Gate.Pause();
        }, ct);

    public Task ResumeAsync(JobId id, CancellationToken ct = default)
        => _repo.UpdateAsync(id, job =>
        {
            job.Resume(DateTimeOffset.UtcNow);
            var h = _runtime.GetOrCreate(id);
            h.Gate.Resume();
        }, ct);

    public Task CancelAsync(JobId id, CancellationToken ct = default)
        => _repo.UpdateAsync(id, job =>
        {
            job.RequestCancel(DateTimeOffset.UtcNow);
            var h = _runtime.GetOrCreate(id);
            h.Cts.Cancel();
            h.Gate.Resume();
        }, ct);

    public async Task<JobSnapshot?> GetAsync(JobId id, CancellationToken ct = default)
    {
        var job = await _repo.GetAsync(id, ct).ConfigureAwait(false);
        return job is null ? null : ToSnapshot(job);
    }

    public async Task<IReadOnlyList<JobSnapshot>> ListAsync(int take = 100, CancellationToken ct = default)
    {
        var jobs = await _repo.ListAsync(take, ct).ConfigureAwait(false);
        return jobs.Select(ToSnapshot).ToArray();
    }

    private static JobSnapshot ToSnapshot(Job job)
        => new(
            Id: job.Id.Value,
            Queue: job.Queue,
            Type: job.Type.Value,
            Status: job.Status,
            Progress: job.Progress,
            Error: job.Error,
            CreatedAtUtc: job.CreatedAtUtc,
            StartedAtUtc: job.StartedAtUtc,
            FinishedAtUtc: job.FinishedAtUtc
        );
}