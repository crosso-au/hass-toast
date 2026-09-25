using Microsoft.Extensions.Logging;
using HassToast.Agent.Config;
using HassToast.Agent.Toasts;
using HassToast.Agent.Transport;

namespace HassToast.Agent.Activation;

/// <summary>
/// Turns a user interacting with a toast into an event on the Home Assistant bus.
/// <para>
/// Everything arriving here has round-tripped through the OS and is treated as untrusted: the
/// arguments are decoded and reported as data, never interpreted as instructions. The agent does
/// not act on them — deciding what a button means is Home Assistant's job, which keeps the
/// blast radius of a forged or replayed activation to "an event was reported".
/// </para>
/// </summary>
public sealed class ActivationRouter
{
    /// <summary>Event fired on the Home Assistant bus when a user interacts with a toast.</summary>
    public const string ResponseEventType = "hass_toast_response";

    private readonly IToastSource _source;
    private readonly AgentConfig _config;
    private readonly ILogger<ActivationRouter> _log;

    public ActivationRouter(IToastSource source, AgentConfig config, ILogger<ActivationRouter> log)
    {
        _source = source;
        _config = config;
        _log = log;
    }

    /// <summary>Handles a click delivered by the COM activator.</summary>
    public Task HandleAsync(ToastActivation activation, CancellationToken ct)
        => ReportAsync(activation.Arguments, activation.Inputs, ct);

    private async Task ReportAsync(
        string rawArguments, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        var arguments = ActivationArguments.Decode(rawArguments);

        arguments.TryGetValue(ActivationArguments.TagKey, out var tag);
        arguments.TryGetValue(ActivationArguments.GroupKey, out var group);

        // The reserved identity keys are lifted out so the reported arguments contain only what
        // the payload author actually put there.
        arguments.Remove(ActivationArguments.TagKey);
        arguments.Remove(ActivationArguments.GroupKey);

        _log.LogInformation(
            "Toast activated (tag '{Tag}', {ArgCount} argument(s), {InputCount} input(s)).",
            tag ?? "", arguments.Count, inputs.Count);

        // A click can start the agent from cold, in which case the connection is still being
        // established. Giving it a moment is the difference between the response arriving and
        // being dropped on the floor.
        await WaitForConnectionAsync(TimeSpan.FromSeconds(15), ct);

        var sent = await _source.FireEventAsync(ResponseEventType, new
        {
            device_id = _config.DeviceId,
            tag = tag ?? "",
            group = group ?? "",
            arguments,
            inputs,
        }, ct);

        if (!sent)
        {
            _log.LogWarning(
                "The activation for tag '{Tag}' could not be delivered to Home Assistant.", tag ?? "");
        }
    }

    private async Task WaitForConnectionAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_source.State == ConnectionState.Connected) return;

        _log.LogInformation("Waiting for the Home Assistant connection before sending the response...");

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct);
            if (_source.State == ConnectionState.Connected) return;

            // Retrying is pointless while the credentials are being rejected.
            if (_source.State == ConnectionState.Unauthorized) return;
        }
    }

}
