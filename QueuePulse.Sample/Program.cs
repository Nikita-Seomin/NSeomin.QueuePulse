using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Hosting;
using QueuePulse.Sample.Example;
using NS.QueuePulse.Domain;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("ASSERT FAILED: " + message);
}

static async Task WaitUntilAsync(
    Func<Task<bool>> predicate,
    TimeSpan timeout,
    TimeSpan pollInterval,
    string failureMessage)
{
    var start = DateTimeOffset.UtcNow;
    while (DateTimeOffset.UtcNow - start < timeout)
    {
        if (await predicate()) return;
        await Task.Delay(pollInterval);
    }
    throw new TimeoutException(failureMessage);
}

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        // Настройка "нового функционала": named queues + workers per queue
        services.AddQueuePulseInMemory(o =>
        {
            o.DefaultQueueName = "default";
            o.DefaultWorkersPerQueue = 1;

            // важное: на heavy дадим 2 воркера, чтобы увидеть параллельное выполнение
            o.WorkersPerQueue["heavy"] = 2;

            // отдельная очередь, 1 воркер
            o.WorkersPerQueue["realtime"] = 1;

            // очередь будет создана динамически при enqueue, даже если не прописали тут
            // (проверим в тесте "dynamic")
        });

        services.AddJobHandler<ImportRasterJob>(new JobType("import-raster"));
    })
    .Build();

await host.StartAsync();

var client = host.Services.GetRequiredService<IJobClient>();

Console.WriteLine("=== QueuePulse Sample: tests ===");

// -----------------------------
// TEST 1: enqueue в named queue + snapshot.Queue
// -----------------------------
{
    var jobId = await client.EnqueueAsync("heavy", new JobType("import-raster"));
    Console.WriteLine($"[T1] Enqueued heavy: {jobId}");

    await WaitUntilAsync(
        async () => (await client.GetAsync(jobId))?.Status is not null,
        timeout: TimeSpan.FromSeconds(3),
        pollInterval: TimeSpan.FromMilliseconds(100),
        failureMessage: "[T1] job not visible in repository");

    var snap = await client.GetAsync(jobId);
    Assert(snap is not null, "[T1] snapshot is null");
    Assert(string.Equals(snap!.Queue, "heavy", StringComparison.OrdinalIgnoreCase),
        $"[T1] expected Queue='heavy' but was '{snap.Queue}'");

    Console.WriteLine("[T1] OK");
}

// -----------------------------
// TEST 2: динамическая очередь (создаётся на лету при enqueue)
// -----------------------------
{
    var jobId = await client.EnqueueAsync("dynamic", new JobType("import-raster"));
    Console.WriteLine($"[T2] Enqueued dynamic: {jobId}");

    await WaitUntilAsync(
        async () => (await client.GetAsync(jobId))?.Status is not null,
        timeout: TimeSpan.FromSeconds(3),
        pollInterval: TimeSpan.FromMilliseconds(100),
        failureMessage: "[T2] job not visible in repository");

    var snap = await client.GetAsync(jobId);
    Assert(snap is not null, "[T2] snapshot is null");
    Assert(string.Equals(snap!.Queue, "dynamic", StringComparison.OrdinalIgnoreCase),
        $"[T2] expected Queue='dynamic' but was '{snap.Queue}'");

    Console.WriteLine("[T2] OK");
}

// -----------------------------
// TEST 3: pause/resume реально стопит прогресс (после того как job стала Paused)
// -----------------------------
{
    var jobId = await client.EnqueueAsync("realtime", new JobType("import-raster"));
    Console.WriteLine($"[T3] Enqueued realtime: {jobId}");

    // дождёмся что job стартанула
    await WaitUntilAsync(
        async () => (await client.GetAsync(jobId))?.Status is NS.QueuePulse.Domain.JobStatus.Running,
        timeout: TimeSpan.FromSeconds(5),
        pollInterval: TimeSpan.FromMilliseconds(100),
        failureMessage: "[T3] job didn't reach Running");

    await client.PauseAsync(jobId);

    // дождёмся состояния Paused (кооперативно — job дойдёт до ближайшего чекпойнта)
    await WaitUntilAsync(
        async () => (await client.GetAsync(jobId))?.Status is NS.QueuePulse.Domain.JobStatus.Paused,
        timeout: TimeSpan.FromSeconds(5),
        pollInterval: TimeSpan.FromMilliseconds(100),
        failureMessage: "[T3] job didn't reach Paused");

    var paused1 = await client.GetAsync(jobId);
    Assert(paused1 is not null, "[T3] snapshot is null after pause");
    var p1 = paused1!.Progress;

    await Task.Delay(800);

    var paused2 = await client.GetAsync(jobId);
    Assert(paused2 is not null, "[T3] snapshot is null after delay");
    var p2 = paused2!.Progress;

    // после того как Paused — прогресс не должен меняться
    Assert(Math.Abs(p2.OverallPercent - p1.OverallPercent) < 0.001,
        $"[T3] progress changed while paused: {p1.OverallPercent:0.###} -> {p2.OverallPercent:0.###}");

    await client.ResumeAsync(jobId);

    // после resume прогресс должен снова двигаться
    await WaitUntilAsync(
        async () =>
        {
            var s = await client.GetAsync(jobId);
            return s is not null && s.Progress.OverallPercent > p2.OverallPercent + 0.1;
        },
        timeout: TimeSpan.FromSeconds(5),
        pollInterval: TimeSpan.FromMilliseconds(150),
        failureMessage: "[T3] progress didn't advance after resume");

    Console.WriteLine("[T3] OK");
}

// -----------------------------
// TEST 4: cancel переводит задачу в Canceled
// -----------------------------
{
    var jobId = await client.EnqueueAsync("default", new JobType("import-raster"));
    Console.WriteLine($"[T4] Enqueued default: {jobId}");

    await WaitUntilAsync(
        async () => (await client.GetAsync(jobId))?.Status is NS.QueuePulse.Domain.JobStatus.Running,
        timeout: TimeSpan.FromSeconds(5),
        pollInterval: TimeSpan.FromMilliseconds(100),
        failureMessage: "[T4] job didn't reach Running");

    await client.CancelAsync(jobId);

    await WaitUntilAsync(
        async () => (await client.GetAsync(jobId))?.Status is NS.QueuePulse.Domain.JobStatus.Canceled,
        timeout: TimeSpan.FromSeconds(5),
        pollInterval: TimeSpan.FromMilliseconds(100),
        failureMessage: "[T4] job didn't reach Canceled");

    Console.WriteLine("[T4] OK");
}

// -----------------------------
// TEST 5: несколько воркеров на одну очередь (heavy=2) => одновременно >=2 Running
// -----------------------------
{
    var ids = new List<JobId>();
    for (int i = 0; i < 3; i++)
        ids.Add(await client.EnqueueAsync("heavy", new JobType("import-raster")));

    Console.WriteLine($"[T5] Enqueued 3 jobs to heavy: {string.Join(", ", ids)}");

    await WaitUntilAsync(
        async () =>
        {
            var list = await client.ListAsync(200);
            var running = list.Count(x => ids.Any(id => x.Id == id.Value) &&
                                          x.Status == NS.QueuePulse.Domain.JobStatus.Running);
            return running >= 2;
        },
        timeout: TimeSpan.FromSeconds(6),
        pollInterval: TimeSpan.FromMilliseconds(150),
        failureMessage: "[T5] didn't observe >=2 Running concurrently (check heavy workers=2)");

    Console.WriteLine("[T5] OK");
}

Console.WriteLine("=== All tests OK ===");

// (опционально) показать один job в live-режиме
var demoId = await client.EnqueueAsync("default", new JobType("import-raster"));
Console.WriteLine($"Demo Enqueued: {demoId}");

for (int i = 0; i < 30; i++)
{
    var snap = await client.GetAsync(demoId);
    if (snap is null) break;

    Console.WriteLine($"i: {i} | q:{snap.Queue} | {snap.Status} | {snap.Progress.Stage} | {snap.Progress.StagePercent:0.0}% | overall {snap.Progress.OverallPercent:0.0}%");
    await Task.Delay(200);
}

await host.StopAsync();