using System.Text.Json;
using NS.QueuePulse.Domain;

namespace NS.QueuePulse.Application;

/// <summary>
/// Runtime-контекст выполнения job (Application layer).
/// </summary>
public sealed class JobContext
{
    public JobId JobId { get; }
    public JobType Type { get; }
    public string? PayloadJson { get; }

    public ProgressTracker Progress { get; }
    public PauseGate PauseGate { get; }
    public CancellationToken Token { get; }

    internal JobContext(
        JobId jobId,
        JobType type,
        string? payloadJson,
        ProgressTracker progress,
        PauseGate pauseGate,
        CancellationToken token)
    {
        JobId = jobId;
        Type = type;
        PayloadJson = payloadJson;
        Progress = progress;
        PauseGate = pauseGate;
        Token = token;
    }

    public TArgs? GetArgs<TArgs>(JsonSerializerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(PayloadJson)) return default;
        return JsonSerializer.Deserialize<TArgs>(PayloadJson!, options);
    }

    public System.Threading.Tasks.Task WaitIfPausedAsync(CancellationToken ct = default)
        => PauseGate.WaitIfPausedAsync(ct);

    public void ThrowIfCancellationRequested() => Token.ThrowIfCancellationRequested();
}