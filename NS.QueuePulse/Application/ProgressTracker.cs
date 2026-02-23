using NS.QueuePulse.Domain;

namespace NS.QueuePulse.Application;

public interface IProgressSink
{
    void Report(ProgressSnapshot snapshot);
}

/// <summary>
/// Прогресс со стадиями и весами.
/// Один трекер на выполнение job.
/// </summary>
public sealed class ProgressTracker
{
    private readonly object _lock = new();
    private readonly IProgressSink? _sink;
    private readonly Func<DateTimeOffset> _utcNow;

    private readonly List<StageState> _stages = new();
    private StageState? _current;

    public ProgressTracker(IProgressSink? sink, Func<DateTimeOffset>? utcNow = null)
    {
        _sink = sink;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public StageScope StartStage(string name, long total, double weight = 1.0)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Stage";
        if (total <= 0) total = 1;
        if (weight <= 0) weight = 1.0;

        lock (_lock)
        {
            var s = new StageState(name, total, weight);
            _stages.Add(s);
            _current = s;
            EmitLocked();
            return new StageScope(this, s);
        }
    }

    internal void Report(StageState stage, long current)
    {
        lock (_lock)
        {
            stage.Current = Math.Clamp(current, 0, stage.Total);
            EmitLocked();
        }
    }

    internal void ReportPercent(StageState stage, double percent0_100)
    {
        lock (_lock)
        {
            var p = Math.Clamp(percent0_100, 0.0, 100.0) / 100.0;
            stage.Current = (long)Math.Round(p * stage.Total);
            EmitLocked();
        }
    }

    internal void Tick(StageState stage, long delta = 1)
    {
        lock (_lock)
        {
            stage.Current = Math.Clamp(stage.Current + delta, 0, stage.Total);
            EmitLocked();
        }
    }

    internal void Complete(StageState stage)
    {
        lock (_lock)
        {
            stage.Current = stage.Total;
            EmitLocked();
        }
    }

    private void EmitLocked()
    {
        if (_sink is null) return;
        if (_stages.Count == 0) return;

        double wSum = 0, wfSum = 0;
        foreach (var s in _stages)
        {
            var frac = (double)s.Current / s.Total;
            wSum += s.Weight;
            wfSum += s.Weight * frac;
        }

        var overall = wSum <= 0 ? 0 : (wfSum / wSum * 100.0);

        var c = _current ?? _stages.Last();
        var stagePercent = (double)c.Current / c.Total * 100.0;

        _sink.Report(new ProgressSnapshot(
            Stage: c.Name,
            Current: c.Current,
            Total: c.Total,
            StagePercent: stagePercent,
            OverallPercent: overall,
            UpdatedAtUtc: _utcNow()
        ));
    }

    public sealed class StageScope : IDisposable
    {
        private readonly ProgressTracker _owner;
        private readonly StageState _stage;
        private bool _disposed;

        internal StageScope(ProgressTracker owner, StageState stage)
        {
            _owner = owner;
            _stage = stage;
        }

        public void Tick(long delta = 1) => _owner.Tick(_stage, delta);
        public void Report(long current) => _owner.Report(_stage, current);
        public void ReportPercent(double percent0_100) => _owner.ReportPercent(_stage, percent0_100);
        public void Complete() => _owner.Complete(_stage);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Complete(_stage);
        }
    }

    internal sealed class StageState
    {
        public string Name { get; }
        public long Total { get; }
        public double Weight { get; }
        public long Current { get; set; }

        public StageState(string name, long total, double weight)
        {
            Name = name;
            Total = total;
            Weight = weight;
            Current = 0;
        }
    }
}