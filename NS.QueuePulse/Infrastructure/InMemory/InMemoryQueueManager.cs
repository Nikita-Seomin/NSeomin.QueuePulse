using System.Collections.Concurrent;
using NS.QueuePulse.Abstractions;

namespace NS.QueuePulse.Infrastructure.InMemory;

public sealed class InMemoryQueueManager : IQueueManager
{
    private readonly ConcurrentDictionary<string, IJobQueue> _queues = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? QueueCreated;

    public IReadOnlyCollection<string> Names => _queues.Keys.ToArray();

    public IJobQueue GetOrCreate(string queueName, int? capacity = null)
    {
        queueName = string.IsNullOrWhiteSpace(queueName) ? "default" : queueName;

        var created = false;
        var q = _queues.GetOrAdd(queueName, _ =>
        {
            created = true;
            return new ChannelJobQueue(capacity);
        });

        if (created) QueueCreated?.Invoke(queueName);
        return q;
    }

    public bool TryGet(string queueName, out IJobQueue queue)
        => _queues.TryGetValue(queueName, out queue!);
}