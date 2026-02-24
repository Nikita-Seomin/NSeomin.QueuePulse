using NS.QueuePulse.Application;
using NS.QueuePulse.Domain;

namespace NS.QueuePulse.Abstractions;

public sealed record JobTicket(string QueueName, JobId JobId, JobType Type, string? PayloadJson);

public sealed record JobSnapshot(
    Guid Id,
    string Queue,
    string Type,
    JobStatus Status,
    ProgressSnapshot Progress,
    JobError? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc
);

public interface IJobHandler
{
    Task ExecuteAsync(JobContext ctx, CancellationToken ct);
}

public interface IJobClient
{
    // default queue
    Task<JobId> EnqueueAsync(JobType type, string? payloadJson = null, CancellationToken ct = default);
    Task<JobId> EnqueueAsync<TArgs>(JobType type, TArgs args, CancellationToken ct = default);

    // named queue
    Task<JobId> EnqueueAsync(string queueName, JobType type, string? payloadJson = null, CancellationToken ct = default);
    Task<JobId> EnqueueAsync<TArgs>(string queueName, JobType type, TArgs args, CancellationToken ct = default);

    Task PauseAsync(JobId id, CancellationToken ct = default);
    Task ResumeAsync(JobId id, CancellationToken ct = default);
    Task CancelAsync(JobId id, CancellationToken ct = default);

    Task<JobSnapshot?> GetAsync(JobId id, CancellationToken ct = default);
    Task<IReadOnlyList<JobSnapshot>> ListAsync(int take = 100, CancellationToken ct = default);
}

// очередь конкретного имени
public interface IJobQueue
{
    ValueTask EnqueueAsync(JobTicket ticket, CancellationToken ct);
    ValueTask<JobTicket> DequeueAsync(CancellationToken ct);
}

// менеджер очередей (создаёт/выдаёт named queues)
public interface IQueueManager
{
    IJobQueue GetOrCreate(string queueName, int? capacity = null);
    bool TryGet(string queueName, out IJobQueue queue);
    IReadOnlyCollection<string> Names { get; }

    // чтобы воркер мог стартовать consumers для новых очередей
    event Action<string>? QueueCreated;
}

public interface IJobRepository
{
    Task AddAsync(Job job, CancellationToken ct);
    Task<Job?> GetAsync(JobId id, CancellationToken ct);
    Task UpdateAsync(JobId id, Action<Job> mutate, CancellationToken ct);
    Task<IReadOnlyList<Job>> ListAsync(int take, CancellationToken ct);
}

public interface IJobProgressPublisher
{
    Task PublishAsync(JobId id, ProgressSnapshot snapshot, CancellationToken ct);
}

public interface IJobHandlerRegistry
{
    IJobHandler Resolve(JobType type);
}