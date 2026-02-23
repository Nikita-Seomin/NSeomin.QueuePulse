using System.Threading.Channels;
using NS.QueuePulse.Abstractions;

namespace NS.QueuePulse.Infrastructure.InMemory;

public sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<JobTicket> _channel;

    public ChannelJobQueue(int? capacity = null)
    {
        _channel = capacity is > 0
            ? Channel.CreateBounded<JobTicket>(new BoundedChannelOptions(capacity.Value)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            })
            : Channel.CreateUnbounded<JobTicket>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });
    }

    public ValueTask EnqueueAsync(JobTicket ticket, CancellationToken ct)
        => _channel.Writer.WriteAsync(ticket, ct);

    public ValueTask<JobTicket> DequeueAsync(CancellationToken ct)
        => _channel.Reader.ReadAsync(ct);
}