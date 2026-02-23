using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Application;
using NS.QueuePulse.Domain;
using NS.QueuePulse.Infrastructure.InMemory;

namespace NS.QueuePulse.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// In-memory QueuePulse: Channels + InMemory repo + BackgroundService worker.
    /// </summary>
    public static IServiceCollection AddQueuePulseInMemory(
        this IServiceCollection services,
        Action<QueuePulseOptions>? configure = null)
    {
        var options = new QueuePulseOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);

        services.TryAddSingleton<IJobQueue>(_ => new ChannelJobQueue());
        services.TryAddSingleton<IJobRepository, InMemoryJobRepository>();
        services.TryAddSingleton<IJobRuntimeRegistry, InMemoryJobRuntimeRegistry>();

        // Registry
        services.TryAddSingleton<JobHandlerRegistry>();
        services.TryAddSingleton<IJobHandlerRegistry>(sp => sp.GetRequiredService<JobHandlerRegistry>());

        // Progress publisher (по умолчанию - no-op)
        services.TryAddSingleton<IJobProgressPublisher>(NullJobProgressPublisher.Instance);

        // Client
        services.TryAddSingleton<IJobClient, JobClient>();

        // Worker
        services.AddHostedService<JobWorker>();

        return services;
    }

    public static IServiceCollection AddJobHandler<THandler>(
        this IServiceCollection services,
        JobType type)
        where THandler : class, IJobHandler
    {
        services.AddTransient<THandler>();

        // Регистрируем mapping "type -> handlerType" после построения provider:
        services.PostConfigureRegistry(registry => registry.Register(type, typeof(THandler)));
        return services;
    }

    // маленький хак без Options: делаем "post-build" регистрацию через фабрику singleton
    private static IServiceCollection PostConfigureRegistry(this IServiceCollection services, Action<JobHandlerRegistry> cfg)
    {
        services.AddSingleton<IStartupHook>(sp =>
        {
            var reg = sp.GetRequiredService<JobHandlerRegistry>();
            cfg(reg);
            return new StartupHook();
        });

        // гарантируем создание hook при старте
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