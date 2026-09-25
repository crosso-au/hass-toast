using System.Text.Json;
using System.Threading.Channels;

namespace HassToast.Agent.Transport;

/// <summary>A raw, unverified inbound event. Nothing has been trusted or parsed yet.</summary>
/// <param name="Data">The <c>event.data</c> object as delivered by the source.</param>
public sealed record InboundEvent(JsonElement Data, DateTimeOffset ReceivedAt);

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
    /// <summary>Terminal for the current credentials — retrying will not help until they change.</summary>
    Unauthorized,
}

/// <summary>
/// Source of inbound toast requests.
/// <para>
/// This is the seam that keeps the toast layer independent of how requests arrive. Today the
/// only implementation dials out to Home Assistant over a WebSocket from the user's session.
/// If the transport later moves into a Windows service (which cannot itself display toasts,
/// being confined to Session 0), the per-user agent gains a named-pipe implementation of this
/// same interface and nothing downstream changes.
/// </para>
/// </summary>
public interface IToastSource
{
    ConnectionState State { get; }

    /// <summary>
    /// Human-readable detail for the current state — the host when connected, the reason when
    /// not. Exposed alongside <see cref="State"/> so a consumer that attaches after a
    /// transition can still render complete status instead of guessing.
    /// </summary>
    string? StateDetail { get; }

    /// <summary>Raised on every state transition. <c>detail</c> carries a human-readable reason.</summary>
    event Action<ConnectionState, string?>? StateChanged;

    /// <summary>
    /// Runs until <paramref name="ct"/> is cancelled, maintaining the connection and writing
    /// every inbound event to <paramref name="sink"/>. Reconnection is the implementation's
    /// responsibility; this should not return merely because the link dropped.
    /// </summary>
    Task RunAsync(ChannelWriter<InboundEvent> sink, CancellationToken ct);

    /// <summary>
    /// Sends an event back to Home Assistant — how a button click or typed reply gets home.
    /// Returns false when there is no live connection to send it on, which is a normal outcome
    /// rather than an error: the user can click a toast long after the link has dropped.
    /// </summary>
    Task<bool> FireEventAsync(string eventType, object data, CancellationToken ct);
}
