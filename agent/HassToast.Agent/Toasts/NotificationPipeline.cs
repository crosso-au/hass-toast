using System.Text.Json;
using Microsoft.Extensions.Logging;
using Windows.UI.Notifications;
using HassToast.Agent.Security;

namespace HassToast.Agent.Toasts;

/// <summary>
/// The single path an inbound event takes to become a toast: verify, then policy-check, then
/// compose, then show. Each stage can reject, and a rejection is always logged with its reason —
/// a notification that silently fails to appear is the hardest kind of bug to chase.
/// </summary>
public sealed class NotificationPipeline
{
    private readonly PayloadVerifier _verifier;
    private readonly ContentPolicy _policy;
    private readonly ToastComposer _composer;
    private readonly ToastRegistry _registry;
    private readonly WindowsToastNotifier _notifier;
    private readonly ILogger<NotificationPipeline> _log;

    public NotificationPipeline(
        PayloadVerifier verifier,
        ContentPolicy policy,
        ToastComposer composer,
        ToastRegistry registry,
        WindowsToastNotifier notifier,
        ILogger<NotificationPipeline> log)
    {
        _verifier = verifier;
        _policy = policy;
        _composer = composer;
        _registry = registry;
        _notifier = notifier;
        _log = log;
    }

    public async Task HandleAsync(JsonElement data, DateTimeOffset now, CancellationToken ct)
    {
        var result = _verifier.Verify(data, now);

        if (!result.Accepted)
        {
            LogRejection(result);
            return;
        }

        var envelope = result.Envelope!;

        switch (envelope.Op)
        {
            case ToastOp.Send:
                await SendAsync(result.Payload!, ct);
                break;

            case ToastOp.Update:
                Update(result.Payload!);
                break;

            case ToastOp.Remove:
                Remove(result.Payload!);
                break;

            case ToastOp.Clear:
                Clear();
                break;
        }
    }

    /// <summary>
    /// Advances an existing toast in place. Unlike a replacement this keeps its position in the
    /// Action Center and never re-raises a banner, which is what makes frequent progress updates
    /// tolerable rather than a stream of interruptions.
    /// </summary>
    public bool Update(ToastPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Tag))
        {
            _log.LogWarning("An update needs a tag to identify which toast to change.");
            return false;
        }

        if (payload.Visual.Progress is not { } progress)
        {
            // Only progress fields and top-level text are data-bindable, and only progress is
            // wired up here — anything else has to be a replacement.
            _log.LogWarning("An update must carry a progress object; nothing else is bindable.");
            return false;
        }

        var sequence = _registry.NextSequence(payload.Tag, payload.Group);
        if (sequence is null)
        {
            // The agent restarted, or the toast came from elsewhere. Adopt it at a high sequence
            // so the update outranks whatever Windows still holds.
            sequence = _registry.Adopt(payload.Tag, payload.Group);
            _log.LogInformation(
                "Tag '{Tag}' is not one this agent raised in this session; adopting it at sequence {Sequence}.",
                payload.Tag, sequence);
        }

        var data = ToastComposer.BuildProgressData(progress, sequence.Value);
        var result = _notifier.Update(data, payload.Tag, payload.Group);

        switch (result)
        {
            case NotificationUpdateResult.Succeeded:
                _log.LogInformation("Updated toast '{Tag}' to sequence {Sequence}.", payload.Tag, sequence);
                return true;

            case NotificationUpdateResult.NotificationNotFound:
                // Expected whenever the user dismissed it; not an error worth alarming about.
                _log.LogInformation(
                    "No toast tagged '{Tag}' is on screen - it was probably dismissed. Send it again to bring it back.",
                    payload.Tag);
                _registry.Forget(payload.Tag, payload.Group);
                return false;

            default:
                _log.LogWarning("Update of '{Tag}' returned {Result}.", payload.Tag, result);
                return false;
        }
    }

    public bool Remove(ToastPayload payload)
    {
        var hasTag = !string.IsNullOrWhiteSpace(payload.Tag);
        var hasGroup = !string.IsNullOrWhiteSpace(payload.Group);

        if (!hasTag && !hasGroup)
        {
            _log.LogWarning("A remove needs a tag, a group, or both.");
            return false;
        }

        if (hasTag && hasGroup)
        {
            _notifier.Remove(payload.Tag!, payload.Group!);
            _registry.Forget(payload.Tag!, payload.Group);
            _log.LogInformation("Removed toast '{Tag}' in group '{Group}'.", payload.Tag, payload.Group);
        }
        else if (hasTag)
        {
            _notifier.Remove(payload.Tag!);
            _registry.Forget(payload.Tag!, null);
            _log.LogInformation("Removed toast '{Tag}'.", payload.Tag);
        }
        else
        {
            _notifier.RemoveGroup(payload.Group!);
            _registry.ForgetGroup(payload.Group!);
            _log.LogInformation("Removed all toasts in group '{Group}'.", payload.Group);
        }

        return true;
    }

    public bool Clear()
    {
        _notifier.Clear();
        _registry.Clear();
        _log.LogInformation("Cleared this app's notifications from the Action Center.");
        return true;
    }

    /// <summary>Renders a payload directly, bypassing envelope verification. Used by --sample.</summary>
    public async Task<bool> SendAsync(ToastPayload payload, CancellationToken ct)
    {
        if (!_policy.TryConsumeToastBudget())
        {
            _log.LogWarning("Rate limit exceeded; dropping this toast.");
            return false;
        }

        var composed = await _composer.ComposeAsync(payload, ct);
        if (composed is null) return false;

        if (!_notifier.Show(composed.Xml, payload, composed.ProgressData)) return false;

        _log.LogInformation("Raised toast (tag '{Tag}', group '{Group}', display setting {Setting}).",
            payload.Tag ?? "", payload.Group ?? "", _notifier.Setting);
        return true;
    }

    private void LogRejection(VerificationResult result)
    {
        switch (result.Outcome)
        {
            case VerificationOutcome.NotForThisDevice:
                // Entirely normal on a shared bus; not worth more than a trace.
                _log.LogTrace("Ignoring envelope for device '{Device}'.", result.Detail);
                break;

            case VerificationOutcome.BadSignature:
                _log.LogWarning(
                    "Rejected an envelope with an invalid signature. If Home Assistant was just " +
                    "reconfigured, its signing key may no longer match this agent's - compare it " +
                    "against --show-key.");
                break;

            case VerificationOutcome.OutsideTimeWindow:
                _log.LogWarning(
                    "Rejected an envelope outside the accepted time window ({Detail}). This is " +
                    "usually clock drift between Home Assistant and this machine.", result.Detail);
                break;

            case VerificationOutcome.Replayed:
                _log.LogWarning("Rejected a replayed envelope (nonce '{Nonce}').", result.Detail);
                break;

            default:
                _log.LogWarning("Rejected an envelope: {Outcome} {Detail}", result.Outcome, result.Detail);
                break;
        }
    }
}
