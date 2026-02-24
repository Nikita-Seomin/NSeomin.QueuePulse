using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NSeomin.QueuePulse.Domain;
using NSeomin.QueuePulse.Abstractions;

namespace NSeomin.QueuePulse.Application;

public sealed class JobHandlerRegistry : IJobHandlerRegistry
{
    private readonly IServiceProvider _sp;
    private readonly ConcurrentDictionary<string, Type> _map = new(StringComparer.OrdinalIgnoreCase);

    public JobHandlerRegistry(IServiceProvider sp) => _sp = sp;

    public void Register(JobType type, Type handlerType)
    {
        if (!typeof(IJobHandler).IsAssignableFrom(handlerType))
            throw new ArgumentException($"{handlerType.Name} must implement IJobHandler");

        _map[type.Value] = handlerType;
    }

    public IJobHandler Resolve(JobType type)
    {
        if (!_map.TryGetValue(type.Value, out var handlerType))
            throw new InvalidOperationException($"No handler registered for job type '{type.Value}'");

        return (IJobHandler)_sp.GetRequiredService(handlerType);
    }
}