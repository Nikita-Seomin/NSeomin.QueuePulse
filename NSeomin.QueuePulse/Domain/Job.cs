namespace NSeomin.QueuePulse.Domain;

public readonly record struct JobId(Guid Value)
{
    public static JobId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
    public static implicit operator Guid(JobId id) => id.Value;
    public static explicit operator JobId(Guid value) => new(value);
}

public readonly record struct JobType(string Value)
{
    public override string ToString() => Value;
}

public enum JobStatus
{
    Queued,
    Running,
    Paused,      // кооперативная пауза
    Canceling,   // отмена запрошена, job еще может завершаться
    Completed,
    Failed,
    Canceled
}

public sealed record JobError(string Code, string Message, string? StackTrace = null);

public readonly record struct ProgressSnapshot(
    string Stage,
    long Current,
    long Total,
    double StagePercent,
    double OverallPercent,
    DateTimeOffset UpdatedAtUtc
);

public sealed class Job
{
    public JobId Id { get; }
    public JobType Type { get; }
    public string Queue { get; }
    public string? PayloadJson { get; }

    public JobStatus Status { get; private set; }
    public ProgressSnapshot Progress { get; private set; }
    public JobError? Error { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    public bool HasStarted => StartedAtUtc is not null;

    private Job(JobId id, JobType type, string queueName, string? payloadJson, DateTimeOffset nowUtc)
    {
        Id = id;
        Type = type;
        Queue = string.IsNullOrWhiteSpace(queueName) ? "default" : queueName;
        PayloadJson = payloadJson;
        CreatedAtUtc = nowUtc;

        Status = JobStatus.Queued;
        Progress = new ProgressSnapshot(
            Stage: "Queued",
            Current: 0,
            Total: 1,
            StagePercent: 0,
            OverallPercent: 0,
            UpdatedAtUtc: nowUtc
        );
    }

    public static Job Create(JobType type, string queueName, string? payloadJson, DateTimeOffset nowUtc)
        => new(JobId.New(), type, queueName, payloadJson, nowUtc);

    public void MarkQueued(DateTimeOffset nowUtc)
    {
        if (Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled) return;
        Status = JobStatus.Queued;
        TouchProgress(nowUtc, stage: "Queued", current: 0, total: 1, stagePercent: 0, overallPercent: Progress.OverallPercent);
    }

    public void Start(DateTimeOffset nowUtc)
    {
        if (Status == JobStatus.Canceled) return;
        if (Status != JobStatus.Queued) throw new InvalidOperationException($"Cannot Start from {Status}");

        Status = JobStatus.Running;
        StartedAtUtc ??= nowUtc;
        TouchProgress(nowUtc, stage: "Running", current: Progress.Current, total: Progress.Total,
            stagePercent: Progress.StagePercent, overallPercent: Progress.OverallPercent);
    }

    public void Pause(DateTimeOffset nowUtc)
    {
        if (Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled) return;

        if (Status is JobStatus.Queued or JobStatus.Running or JobStatus.Canceling)
        {
            Status = JobStatus.Paused;
            TouchProgress(nowUtc, stage: "Paused", current: Progress.Current, total: Progress.Total,
                stagePercent: Progress.StagePercent, overallPercent: Progress.OverallPercent);
            return;
        }

        if (Status == JobStatus.Paused) return;
        throw new InvalidOperationException($"Cannot Pause from {Status}");
    }

    public void Resume(DateTimeOffset nowUtc)
    {
        if (Status != JobStatus.Paused) return;

        Status = HasStarted ? JobStatus.Running : JobStatus.Queued;
        TouchProgress(nowUtc, stage: HasStarted ? "Running" : "Queued",
            current: Progress.Current, total: Progress.Total,
            stagePercent: Progress.StagePercent, overallPercent: Progress.OverallPercent);
    }

    /// <summary>
    /// Запрос отмены. Для не начатых задач отменяем сразу. Для Running/Paused — ставим Canceling.
    /// </summary>
    public void RequestCancel(DateTimeOffset nowUtc)
    {
        if (Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled) return;

        if (!HasStarted && Status is JobStatus.Queued or JobStatus.Paused)
        {
            Cancel(nowUtc);
            return;
        }

        Status = JobStatus.Canceling;
        TouchProgress(nowUtc, stage: "Canceling", current: Progress.Current, total: Progress.Total,
            stagePercent: Progress.StagePercent, overallPercent: Progress.OverallPercent);
    }

    public void Complete(DateTimeOffset nowUtc)
    {
        if (Status == JobStatus.Canceled) return;
        Status = JobStatus.Completed;
        FinishedAtUtc = nowUtc;
        TouchProgress(nowUtc, stage: "Completed", current: 1, total: 1, stagePercent: 100, overallPercent: 100);
    }

    public void Cancel(DateTimeOffset nowUtc)
    {
        Status = JobStatus.Canceled;
        FinishedAtUtc = nowUtc;
        TouchProgress(nowUtc, stage: "Canceled", current: 1, total: 1, stagePercent: 100, overallPercent: Progress.OverallPercent);
    }

    public void Fail(DateTimeOffset nowUtc, Exception ex, string code = "Unhandled")
    {
        Status = JobStatus.Failed;
        FinishedAtUtc = nowUtc;
        Error = new JobError(code, ex.Message, ex.ToString());
        TouchProgress(nowUtc, stage: "Failed", current: Progress.Current, total: Progress.Total,
            stagePercent: Progress.StagePercent, overallPercent: Progress.OverallPercent);
    }

    public void UpdateProgress(ProgressSnapshot snapshot)
    {
        if (Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled) return;
        Progress = snapshot;
    }

    private void TouchProgress(
        DateTimeOffset nowUtc,
        string stage,
        long current,
        long total,
        double stagePercent,
        double overallPercent)
    {
        Progress = new ProgressSnapshot(
            Stage: stage,
            Current: current,
            Total: total <= 0 ? 1 : total,
            StagePercent: Math.Clamp(stagePercent, 0, 100),
            OverallPercent: Math.Clamp(overallPercent, 0, 100),
            UpdatedAtUtc: nowUtc
        );
    }
}