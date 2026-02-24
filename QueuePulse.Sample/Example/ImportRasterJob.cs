using NSeomin.QueuePulse.Abstractions;
using NSeomin.QueuePulse.Application;

namespace QueuePulse.Sample.Example;

public sealed class ImportRasterJob : IJobHandler
{
    public async Task ExecuteAsync(JobContext ctx, CancellationToken ct)
    {
        // Пример 3 стадий с весами
        using (var stage = ctx.Progress.StartStage("Reading blocks", total: 50, weight: 0.30))
        {
            for (int i = 0; i < 50; i++)
            {
                await ctx.WaitIfPausedAsync(ct);
                ct.ThrowIfCancellationRequested();

                await Task.Delay(20, ct); // имитация работы
                stage.Tick();
            }
        }

        using (var stage = ctx.Progress.StartStage("GDAL warp", total: 100, weight: 0.60))
        {
            for (int p = 0; p <= 100; p++)
            {
                await ctx.WaitIfPausedAsync(ct);
                ct.ThrowIfCancellationRequested();

                await Task.Delay(10, ct);
                stage.ReportPercent(p);
            }
        }

        using (var stage = ctx.Progress.StartStage("Uploading", total: 1000, weight: 0.10))
        {
            long uploaded = 0;
            while (uploaded < 1000)
            {
                await ctx.WaitIfPausedAsync(ct);
                ct.ThrowIfCancellationRequested();

                await Task.Delay(10, ct);
                uploaded += 50;
                stage.Report(uploaded);
            }
        }
    }
}