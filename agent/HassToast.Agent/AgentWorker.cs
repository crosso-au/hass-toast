using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HassToast.Agent.Transport;

namespace HassToast.Agent;

/// <summary>
/// Joins the transport to the toast pipeline: the source fills a bounded channel, this
/// drains it. Bounding matters — a burst from Home Assistant must not grow unboundedly
/// in memory, and dropping the oldest pending toast is better than falling behind forever.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private const int QueueCapacity = 256;

    private readonly IToastSource _source;
    private readonly Toasts.NotificationPipeline _pipeline;
    private readonly ILogger<AgentWorker> _log;

    public AgentWorker(IToastSource source, Toasts.NotificationPipeline pipeline, ILogger<AgentWorker> log)
    {
        _source = source;
        _pipeline = pipeline;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = Channel.CreateBounded<InboundEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        var producer = _source.RunAsync(channel.Writer, stoppingToken);
        var consumer = ConsumeAsync(channel.Reader, stoppingToken);

        await Task.WhenAll(producer, consumer);
    }

    private async Task ConsumeAsync(ChannelReader<InboundEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var inbound in reader.ReadAllAsync(ct))
            {
                try
                {
                    await _pipeline.HandleAsync(inbound.Data, inbound.ReceivedAt, ct);
                }
                catch (Exception ex)
                {
                    // One malformed payload must never take down the pump.
                    _log.LogError(ex, "Failed to handle an inbound event; dropping it.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
