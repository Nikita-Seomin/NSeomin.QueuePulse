// See https://aka.ms/new-console-template for more information

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Hosting;
using QueuePulse.Sample.Example;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddQueuePulseInMemory(o => o.WorkerCount = 1);

        services.AddJobHandler<ImportRasterJob>(new NS.QueuePulse.Domain.JobType("import-raster"));
    })
    .Build();

await host.StartAsync();

var client = host.Services.GetRequiredService<IJobClient>();

var jobId = await client.EnqueueAsync(new NS.QueuePulse.Domain.JobType("import-raster"));
Console.WriteLine($"Enqueued: {jobId}");

for (int i = 0; i < 30; i++)
{
    var snap = await client.GetAsync(jobId);
    if (snap is null) break;

    Console.WriteLine($"i: {i} | {snap.Status} | {snap.Progress.Stage} | {snap.Progress.StagePercent:0.0}% | overall {snap.Progress.OverallPercent:0.0}%");

    if (i == 5) await client.PauseAsync(jobId);
    if (i == 10) await client.ResumeAsync(jobId);

    await Task.Delay(200);
}

await host.StopAsync();