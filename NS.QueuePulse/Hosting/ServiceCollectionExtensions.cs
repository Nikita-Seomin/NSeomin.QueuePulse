using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Application;
using NS.QueuePulse.Domain;
using NS.QueuePulse.Infrastructure.InMemory;

namespace NS.QueuePulse.Hosting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQueuePulseInMemory(
        this IServiceCollection services,
        Action<QueuePulseOptions>? configure = null,
        Action<QueuePulseClientOptions>? configureClient = null)
    {
        var options = new QueuePulseOptions();
        configure?.Invoke(options);

        var clientOptions = new QueuePulseClientOptions();
        configureClient?.Invoke(clientOptions);

        services.AddSingleton(options);
        services.AddSingleton(clientOptions);

        // queues
        services.TryAddSingleton<IQueueManager, InMemoryQueueManager>();

        // repo + runtime
        services.TryAddSingleton<IJobRepository, InMemoryJobRepository>();
        services.TryAddSingleton<IJobRuntimeRegistry, InMemoryJobRuntimeRegistry>();

        // registry
        services.TryAddSingleton<JobHandlerRegistry>();
        services.TryAddSingleton<IJobHandlerRegistry>(sp => sp.GetRequiredService<JobHandlerRegistry>());

        // progress publisher default no-op
        services.TryAddSingleton<IJobProgressPublisher>(NullJobProgressPublisher.Instance);

        // client
        services.TryAddSingleton<IJobClient, JobClient>();

        // worker
        services.AddHostedService<JobWorker>();

        // pre-create default queue at startup
        services.AddSingleton<IStartupHook>(sp =>
        {
            var qm = sp.GetRequiredService<IQueueManager>();
            qm.GetOrCreate(options.DefaultQueueName);
            return new StartupHook();
        });
        services.AddHostedService<StartupHookHostedService>();

        return services;
    }

    public static IServiceCollection AddJobHandler<THandler>(
        this IServiceCollection services,
        JobType type)
        where THandler : class, IJobHandler
    {
        services.AddTransient<THandler>();
        services.PostConfigureRegistry(reg => reg.Register(type, typeof(THandler)));
        return services;
    }

    private static IServiceCollection PostConfigureRegistry(this IServiceCollection services, Action<JobHandlerRegistry> cfg)
    {
        services.AddSingleton<IStartupHook>(sp =>
        {
            cfg(sp.GetRequiredService<JobHandlerRegistry>());
            return new StartupHook();
        });

        services.AddHostedService<StartupHookHostedService>();
        return services;
    }

    private interface IStartupHook { }
    private sealed class StartupHook : IStartupHook { }

    private sealed class StartupHookHostedService : Microsoft.Extensions.Hosting.IHostedService
    {
        public StartupHookHostedService(System.Collections.Generic.IEnumerable<IStartupHook> _) { }
        public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
    }
}