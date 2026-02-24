# QueuePulse
**QueuePulse** — лёгкая **in-process** библиотека для фоновых job’ов в .NET: очереди в памяти, воркер(ы), **Pause/Resume/Cancel** (кооперативно) и **progress** (стадии/веса).

* **Одна строка для запуска воркера** через IHostedService / BackgroundService.
* Очереди на базе **System.Threading.Channels** (producer/consumer).
* Прогресс: ```StartStage(...)``` + ```Tick/Report/ReportPercent```.

⚠️ In-memory версия: очередь и состояния исчезают при перезапуске процесса.

---

## Установка
```
dotnet add package NS.QueuePulse
```

---

## Quick start (1 очередь по умолчанию)

**Program.cs**

```
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Domain;
using NS.QueuePulse.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(s =>
    {
        s.AddQueuePulseInMemory(o => { o.DefaultQueueName = "default"; o.DefaultWorkersPerQueue = 1; });
        s.AddJobHandler<ImportJob>(new JobType("import"));
    })
    .Build();

await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobClient>();
var id = await jobs.EnqueueAsync(new JobType("import"));
Console.WriteLine($"Job: {id}");
```

DI-регистрация handler’ов — это “composition root”: воркер по ```JobType``` должен понимать, **какой** ```IJobHandler``` **запускать**. Это стандартный подход в DI: без регистрации контейнер не сможет создать нужный сервис.

---

## Пишем Job Handler с прогрессом

```
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Application;

public sealed class ImportJob : IJobHandler
{
  public async Task ExecuteAsync(JobContext ctx, CancellationToken ct)
  {
    using var stage = ctx.Progress.StartStage("Uploading", total: 100, weight: 1.0);
    for (var p = 0; p <= 100; p++)
    {
      await ctx.WaitIfPausedAsync(ct);
      ct.ThrowIfCancellationRequested();
      await Task.Delay(20, ct);
      stage.ReportPercent(p);
    }
  }
}
```

---

## Пишем Job Handler: несколько стадий + веса + total (байты)

```
using NS.QueuePulse.Abstractions;
using NS.QueuePulse.Application;

public sealed class ImportJob : IJobHandler
{
  public async Task ExecuteAsync(JobContext ctx, CancellationToken ct)
  {
    using (var s = ctx.Progress.StartStage("Read input", total: inputBytes, weight: 0.25))
      await CopyWithProgressAsync(input, Stream.Null, s.Report, ctx, ct);

    using (var s = ctx.Progress.StartStage("Transform", total: 100, weight: 0.60))
      await RunToolAsync(p01 => s.ReportPercent(p01 * 100), ct); // tool gives 0..1

    using (var s = ctx.Progress.StartStage("Upload", total: uploadBytes, weight: 0.15))
      await CopyWithProgressAsync(output, uploadStream, s.Report, ctx, ct);
  }
}
```

### Helper: копирование с прогрессом по байтам (total != iterations)

```
static async Task CopyWithProgressAsync(
  Stream src, Stream dst, Action<long> report, JobContext ctx, CancellationToken ct)
{
  var buf = new byte[81920]; 
  long done = 0;
  for (int n; (n = await src.ReadAsync(buf, ct)) > 0;)
  {
    await ctx.WaitIfPausedAsync(ct);
    ct.ThrowIfCancellationRequested();
    await dst.WriteAsync(buf.AsMemory(0, n), ct);
    done += n; 
    report(done); // current bytes
  }
}
```

### Пример вывода
```
[44792e6a...] Read input 12.4% | overall 3.1%
[44792e6a...] Read input 98.7% | overall 24.7%
[44792e6a...] Transform 10.0% | overall 30.7%
[44792e6a...] Transform 70.0% | overall 66.7%
[44792e6a...] Upload 55.0% | overall 93.2%
[44792e6a...] Upload 100.0% | overall 100.0%
```

---

## Наблюдаем прогресс (polling)
```
var snap = await jobs.GetAsync(id);
Console.WriteLine($"{snap!.Status} | {snap.Progress.Stage} | {snap.Progress.OverallPercent:0.0}%");
```

---

## Pause / Resume / Cancel
```
await jobs.PauseAsync(id);
await Task.Delay(1000);
await jobs.ResumeAsync(id);

await jobs.CancelAsync(id); // кооперативно: job должна проверять token/WaitIfPausedAsync
```
(Отмена/пауза в .NET обычно кооперативные: код job периодически проверяет CancellationToken и “точки ожидания”.)

---

## Несколько очередей + несколько воркеров на очередь
```
services.AddQueuePulseInMemory(o =>
{
  o.DefaultQueueName = "default";
  o.DefaultWorkersPerQueue = 1;
  o.WorkersPerQueue["heavy"] = 2;
  o.WorkersPerQueue["realtime"] = 1;
});

await jobs.EnqueueAsync("heavy", new JobType("import"));
await jobs.EnqueueAsync("realtime", new JobType("import"));
```
Модель “много producers / много consumers” — типичный сценарий для очередей (в т.ч. Channels).

---

## Прогресс “по байтам” для последовательной записи файлов

Идея: ```total``` = сумма размеров файлов, а ```current = doneBytes + bytesReadInCurrentFile```.
```
using var stage = ctx.Progress.StartStage("Saving files", total: totalBytes, weight: 1);

await using var tracked = new ProgressReadStream(input,
  bytesRead => stage.Report(doneBytes + bytesRead));

await storage.WriteAsync(tracked, ct); // внутри может быть CopyToAsync
doneBytes += fileSize;
stage.Report(doneBytes);
```
```CopyToAsync``` не даёт прогресс сам по себе, поэтому проще всего — оборачивать входной ```Stream``` и считать реально прочитанные байты.

---

## Как пушить прогресс в UI (SignalR и т.п.)

QueuePulse предоставляет порт ```IJobProgressPublisher```.
По умолчанию — no-op. Хочешь real-time: сделай свою реализацию, которая отправляет snapshot в SignalR/лог/метрики.

---

## Ограничения

* In-memory очередь: без durability (после рестарта всё пропадает).
* Pause/Cancel работают кооперативно: job должна вызывать ```WaitIfPausedAsync``` и проверять ```ct```.