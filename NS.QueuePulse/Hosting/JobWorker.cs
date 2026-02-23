using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Application;
using NS.QueuePulse.Domain;
using NS.QueuePulse.Infrastructure.InMemory;

namespace NS.QueuePulse.Hosting;

public sealed class QueuePulseOptions
{
    public int WorkerCount { get; set; } = 1;
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
        // 1) сохраняем в репо (атомарно)
        _repo.UpdateAsync(_jobId, job => job.UpdateProgress(snapshot), CancellationToken.None).GetAwaiter().GetResult();

        // 2) пушим наружу (SignalR/лог/что угодно) — fire-and-forget
        _ = SafePublishAsync(snapshot);
    }

    private async Task SafePublishAsync(ProgressSnapshot snapshot)
    {
        try
        {
            await _publisher.PublishAsync(_jobId, snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Progress publish failed for job {JobId}", _jobId);
        }
    }
}

public sealed class JobWorker : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly IJobRepository _repo;
    private readonly IJobHandlerRegistry _handlers;
    private readonly IJobProgressPublisher _publisher;
    private readonly IJobRuntimeRegistry _runtime;
    private readonly QueuePulseOptions _options;
    private readonly ILogger<JobWorker> _log;

    public JobWorker(
        IJobQueue queue,
        IJobRepository repo,
        IJobHandlerRegistry handlers,
        IJobProgressPublisher publisher,
        IJobRuntimeRegistry runtime,
        QueuePulseOptions options,
        ILogger<JobWorker> log)
    {
        _queue = queue;
        _repo = repo;
        _handlers = handlers;
        _publisher = publisher;
        _runtime = runtime;
        _options = options;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Math.Max(1, _options.WorkerCount);
        Task[] tasks = new Task[workers];
        for (int i = 0; i < workers; i++)
        {
            tasks[i] = ConsumeLoopAsync(i + 1, stoppingToken);
        }
        return Task.WhenAll(tasks);
    }

    private async Task ConsumeLoopAsync(int workerNo, CancellationToken stoppingToken)
    {
        _log.LogInformation("QueuePulse worker #{WorkerNo} started", workerNo);

        while (!stoppingToken.IsCancellationRequested)
        {
            JobTicket ticket;
            try
            {
                ticket = await _queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessOneAsync(ticket, stoppingToken).ConfigureAwait(false);
        }

        _log.LogInformation("QueuePulse worker #{WorkerNo} stopped", workerNo);
    }

    private async Task ProcessOneAsync(JobTicket ticket, CancellationToken stoppingToken)
    {
        var job = await _repo.GetAsync(ticket.JobId, stoppingToken).ConfigureAwait(false);
        if (job is null) return;

        // уже финализировано — просто пропускаем
        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled)
            return;

        // runtime handle (pause/cancel)
        var handle = _runtime.GetOrCreate(ticket.JobId);

        // если job отменена до старта — пропустить
        if (job.Status == JobStatus.Canceled)
            return;

        // если поставили на паузу до старта — подождать
        if (job.Status == JobStatus.Paused)
            handle.Gate.Pause();

        // если уже Canceling — отменяем токен, чтобы обработчик не стартовал
        if (job.Status == JobStatus.Canceling)
            await handle.Cts.CancelAsync();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, handle.Cts.Token);
        var ct = linkedCts.Token;

        // стартуем только из Queued
        if (job.Status == JobStatus.Queued)
        {
            await _repo.UpdateAsync(ticket.JobId, j => j.Start(DateTimeOffset.UtcNow), stoppingToken).ConfigureAwait(false);
        }

        // кооперативная пауза (в том числе “до старта”)
        await handle.Gate.WaitIfPausedAsync(ct).ConfigureAwait(false);

        var reporter = new JobProgressReporter(ticket.JobId, _repo, _publisher, _log);
        var tracker = new ProgressTracker(reporter);

        // “живой” контекст
        var ctx = new JobContext(
            jobId: ticket.JobId,
            type: ticket.Type,
            payloadJson: ticket.PayloadJson,
            progress: tracker,
            pauseGate: handle.Gate,
            token: ct
        );

        IJobHandler handler;
        try
        {
            handler = _handlers.Resolve(ticket.Type);
        }
        catch (Exception ex)
        {
            await _repo.UpdateAsync(ticket.JobId, j => j.Fail(DateTimeOffset.UtcNow, ex, "NoHandler"), stoppingToken).ConfigureAwait(false);
            _runtime.Remove(ticket.JobId);
            return;
        }

        try
        {
            // минимальный прогресс по умолчанию
            using (var stage = tracker.StartStage("Executing", total: 1, weight: 1.0))
            {
                await handler.ExecuteAsync(ctx, ct).ConfigureAwait(false);
                stage.Report(1);
            }

            // если успели запросить cancel, считаем cancel, а не complete
            var after = await _repo.GetAsync(ticket.JobId, stoppingToken).ConfigureAwait(false);
            if (after?.Status == JobStatus.Canceling || ct.IsCancellationRequested)
            {
                await _repo.UpdateAsync(ticket.JobId, j => j.Cancel(DateTimeOffset.UtcNow), stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await _repo.UpdateAsync(ticket.JobId, j => j.Complete(DateTimeOffset.UtcNow), stoppingToken).ConfigureAwait(false);
            }
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