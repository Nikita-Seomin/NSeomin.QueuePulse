using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Application;
using NS.QueuePulse.Domain;
using NS.QueuePulse.Infrastructure.InMemory;

namespace NS.QueuePulse.Hosting;

public sealed class QueuePulseOptions
{
    public string DefaultQueueName { get; set; } = "default";
    public int DefaultWorkersPerQueue { get; set; } = 1;

    // например: { ["heavy"]=2, ["realtime"]=4 }
    public ConcurrentDictionary<string, int> WorkersPerQueue { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class JobProgressReporter : IProgressSink
{
    private readonly JobId _jobId;
    private readonly IJobRepository _repo;
    private readonly IJobProgressPublisher _publisher;
    private readonly ILogger _log;

    public JobProgressReporter(JobId jobId, IJobRepository repo, IJobProgressPublisher publisher, ILogger log)
    {
        _jobId = jobId;
        _repo = repo;
        _publisher = publisher;
        _log = log;
    }

    public void Report(ProgressSnapshot snapshot)
    {
        _repo.UpdateAsync(_jobId, job => job.UpdateProgress(snapshot), CancellationToken.None).GetAwaiter().GetResult();
        _ = SafePublishAsync(snapshot);
    }

    private async Task SafePublishAsync(ProgressSnapshot snapshot)
    {
        try { await _publisher.PublishAsync(_jobId, snapshot, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "Progress publish failed for job {JobId}", _jobId); }
    }
}

public sealed class JobWorker : BackgroundService
{
    private readonly IQueueManager _queues;
    private readonly IJobRepository _repo;
    private readonly IJobHandlerRegistry _handlers;
    private readonly IJobProgressPublisher _publisher;
    private readonly IJobRuntimeRegistry _runtime;
    private readonly QueuePulseOptions _options;
    private readonly ILogger<JobWorker> _log;

    private readonly ConcurrentDictionary<string, bool> _startedQueues = new(StringComparer.OrdinalIgnoreCase);

    public JobWorker(
        IQueueManager queues,
        IJobRepository repo,
        IJobHandlerRegistry handlers,
        IJobProgressPublisher publisher,
        IJobRuntimeRegistry runtime,
        QueuePulseOptions options,
        ILogger<JobWorker> log)
    {
        _queues = queues;
        _repo = repo;
        _handlers = handlers;
        _publisher = publisher;
        _runtime = runtime;
        _options = options;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1) гарантируем default queue
        _queues.GetOrCreate(_options.DefaultQueueName);

        // 2) стартуем consumers для уже существующих очередей
        foreach (var q in _queues.Names)
            StartQueueConsumers(q, stoppingToken);

        // 3) динамические очереди: как только создана — запускаем consumers
        _queues.QueueCreated += name => StartQueueConsumers(name, stoppingToken);

        // keep alive
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private void StartQueueConsumers(string queueName, CancellationToken stoppingToken)
    {
        if (!_startedQueues.TryAdd(queueName, true))
            return;

        var workers = _options.WorkersPerQueue.TryGetValue(queueName, out var w)
            ? Math.Max(1, w)
            : Math.Max(1, _options.DefaultWorkersPerQueue);

        _log.LogInformation("QueuePulse: start {Workers} worker(s) for queue '{Queue}'", workers, queueName);

        var queue = _queues.GetOrCreate(queueName);

        for (int i = 0; i < workers; i++)
        {
            _ = Task.Run(() => ConsumeLoopAsync(queueName, i + 1, queue, stoppingToken), stoppingToken);
        }
    }

    private async Task ConsumeLoopAsync(string queueName, int workerNo, IJobQueue queue, CancellationToken stoppingToken)
    {
        _log.LogInformation("QueuePulse worker {WorkerNo} started for queue '{Queue}'", workerNo, queueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            JobTicket ticket;
            try
            {
                ticket = await queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessOneAsync(ticket, stoppingToken).ConfigureAwait(false);
        }

        _log.LogInformation("QueuePulse worker {WorkerNo} stopped for queue '{Queue}'", workerNo, queueName);
    }

    private async Task ProcessOneAsync(JobTicket ticket, CancellationToken stoppingToken)
    {
        var job = await _repo.GetAsync(ticket.JobId, stoppingToken).ConfigureAwait(false);
        if (job is null) return;

        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled)
            return;

        var handle = _runtime.GetOrCreate(ticket.JobId);

        if (job.Status == JobStatus.Paused) handle.Gate.Pause();
        if (job.Status == JobStatus.Canceling) handle.Cts.Cancel();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, handle.Cts.Token);
        var ct = linkedCts.Token;

        if (job.Status == JobStatus.Queued)
            await _repo.UpdateAsync(ticket.JobId, j => j.Start(DateTimeOffset.UtcNow), stoppingToken).ConfigureAwait(false);

        await handle.Gate.WaitIfPausedAsync(ct).ConfigureAwait(false);

        IJobHandler handler;
        try { handler = _handlers.Resolve(ticket.Type); }
        catch (Exception ex)
        {
            await _repo.UpdateAsync(ticket.JobId, j => j.Fail(DateTimeOffset.UtcNow, ex, "NoHandler"), stoppingToken).ConfigureAwait(false);
            _runtime.Remove(ticket.JobId);
            return;
        }

        var reporter = new JobProgressReporter(ticket.JobId, _repo, _publisher, _log);
        var tracker = new ProgressTracker(reporter);

        var ctx = new JobContext(
            jobId: ticket.JobId,
            type: ticket.Type,
            payloadJson: ticket.PayloadJson,
            progress: tracker,
            pauseGate: handle.Gate,
            token: ct
        );

        try
        {
            using (var stage = tracker.StartStage("Executing", total: 1, weight: 1.0))
            {
                await handler.ExecuteAsync(ctx, ct).ConfigureAwait(false);
                stage.Report(1);
            }

            var after = await _repo.GetAsync(ticket.JobId, stoppingToken).ConfigureAwait(false);
            if (after?.Status == JobStatus.Canceling || ct.IsCancellationRequested)
                await _repo.UpdateAsync(ticket.JobId, j => j.Cancel(DateTimeOffset.UtcNow), stoppingToken).ConfigureAwait(false);
            else
                await _repo.UpdateAsync(ticket.JobId, j => j.Complete(DateTimeOffset.UtcNow), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _repo.UpdateAsync(ticket.JobId, j => j.Cancel(DateTimeOffset.UtcNow), stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _repo.UpdateAsync(ticket.JobId, j => j.Fail(DateTimeOffset.UtcNow, ex), stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _runtime.Remove(ticket.JobId);
        }
    }
}