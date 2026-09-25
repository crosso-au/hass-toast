using Microsoft.Extensions.Logging;
using HassToast.Agent.Security;

namespace HassToast.Agent.Toasts;

/// <summary>
/// Local paths for the images in one payload, keyed by the payload object that referenced them.
/// Fetching is asynchronous and XML construction is not, so resolution happens up front and the
/// writer only ever consults this.
/// </summary>
public sealed class ResolvedImages
{
    private readonly Dictionary<object, string> _paths = new(ReferenceEqualityComparer.Instance);

    public void Add(object key, string path) => _paths[key] = path;

    public bool TryGet(object key, out string path)
    {
        if (_paths.TryGetValue(key, out var found))
        {
            path = found;
            return true;
        }
        path = "";
        return false;
    }

    public int Count => _paths.Count;
}

/// <summary>
/// Turns a verified payload into a Windows notification: resolve images, build the XML tree,
/// hand it to the platform.
/// </summary>
public sealed class ToastComposer
{
    private readonly ContentPolicy _policy;
    private readonly ImageResolver _images;
    private readonly ToastRegistry _registry;
    private readonly ILogger<ToastComposer> _log;

    public ToastComposer(
        ContentPolicy policy, ImageResolver images, ToastRegistry registry, ILogger<ToastComposer> log)
    {
        _policy = policy;
        _images = images;
        _registry = registry;
        _log = log;
    }

    /// <summary>
    /// Clears any protocol target the policy refuses. This is the sharpest edge in the system —
    /// whatever survives here is handed to the shell when the user clicks — so a rejected target
    /// is blanked rather than passed along, and the writer drops the control entirely.
    /// </summary>
    private void VetActivationTargets(ToastPayload payload)
    {
        if (payload.Launch is { } launch &&
            string.Equals(launch.Type, "protocol", StringComparison.OrdinalIgnoreCase))
        {
            var verdict = _policy.CheckActivationUri(launch.Uri);
            if (!verdict.Allowed)
            {
                _log.LogWarning("Blocked launch target: {Reason}", verdict.Reason);
                launch.Uri = null;
            }
        }

        foreach (var button in payload.Buttons.Concat(payload.ContextMenu))
        {
            if (!string.Equals(button.Activation, "protocol", StringComparison.OrdinalIgnoreCase)) continue;

            var verdict = _policy.CheckActivationUri(button.Uri);
            if (verdict.Allowed) continue;

            _log.LogWarning("Blocked button '{Content}': {Reason}", button.Content, verdict.Reason);
            button.Uri = null;
        }
    }

    /// <summary>The XML for a toast, plus the initial data behind any bound progress fields.</summary>
    public sealed record ComposedToast(
        System.Xml.Linq.XDocument Xml,
        Windows.UI.Notifications.NotificationData? ProgressData);

    public async Task<ComposedToast?> ComposeAsync(ToastPayload payload, CancellationToken ct)
    {
        if (!HasAnythingToShow(payload))
        {
            _log.LogWarning("Payload has no text, progress or groups; refusing to raise an empty toast.");
            return null;
        }

        // Strip anything the policy refuses before it reaches the writer, so the writer only
        // ever deals with values that are already cleared for use.
        VetActivationTargets(payload);

        var images = await ResolveImagesAsync(payload, ct);

        // A progress bar can only be advanced in place if it was emitted with binding
        // placeholders, and updates are addressed by tag — so binding is only worth it when
        // the payload actually carries one.
        var bindProgress = payload.Visual.Progress is not null && !string.IsNullOrWhiteSpace(payload.Tag);

        if (payload.Buttons.Count == 0 && ToastXmlWriter.ScenarioNeedsAButton(payload.Scenario))
        {
            _log.LogInformation(
                "Scenario '{Scenario}' only stays on screen when the toast has at least one " +
                "button - Windows downgrades it to an ordinary toast otherwise. A Dismiss button " +
                "was added; supply your own to replace it.",
                payload.Scenario);
        }

        var xml = ToastXmlWriter.Build(payload, images, bindProgress);

        _log.LogDebug("Toast payload: {Xml}",
            xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));

        // The initial values behind the binding placeholders. Without this the bound progress
        // bar renders with empty fields until the first update arrives.
        Windows.UI.Notifications.NotificationData? progressData = null;
        if (bindProgress && payload.Visual.Progress is { } progress)
        {
            var sequence = _registry.Register(payload.Tag!, payload.Group);
            progressData = BuildProgressData(progress, sequence);
        }

        return new ComposedToast(xml, progressData);
    }

    /// <summary>
    /// Packs progress values into the name/value pairs the bound placeholders read. The names are
    /// fixed by the platform and must match what <see cref="ToastXmlWriter"/> emitted.
    /// </summary>
    public static Windows.UI.Notifications.NotificationData BuildProgressData(
        ToastProgress progress, uint sequence)
    {
        var data = new Windows.UI.Notifications.NotificationData { SequenceNumber = sequence };

        data.Values["progressTitle"] = progress.Title ?? "";
        data.Values["progressValue"] = ToastXmlWriter.FormatProgressValue(progress.Value);
        data.Values["progressValueStringOverride"] = progress.ValueOverride ?? "";
        data.Values["progressStatus"] = progress.Status ?? "";

        return data;
    }

    private static bool HasAnythingToShow(ToastPayload payload)
        => payload.Visual.Text.Any(t => !string.IsNullOrWhiteSpace(t))
           || payload.Visual.Progress is not null
           || payload.Visual.Groups.Count > 0;

    /// <summary>
    /// Fetches every image the payload references. A rejected or unreachable image is skipped
    /// rather than failing the toast — a notification missing a picture still conveys its message.
    /// </summary>
    private async Task<ResolvedImages> ResolveImagesAsync(ToastPayload payload, CancellationToken ct)
    {
        var resolved = new ResolvedImages();

        await ResolveInto(resolved, payload.Visual.AppLogo, ImageRole.AppLogo, ct);
        await ResolveInto(resolved, payload.Visual.Hero, ImageRole.Hero, ct);
        await ResolveInto(resolved, payload.Visual.Inline, ImageRole.Inline, ct);

        foreach (var child in payload.Visual.Groups
                     .SelectMany(g => g.Columns)
                     .SelectMany(c => c.Children)
                     .Where(c => c.IsImage))
        {
            var path = await _images.ResolveAsync(child.Src, ImageRole.Inline, ct);
            if (path is not null) resolved.Add(child, path);
        }

        // Button icons are small monochrome glyphs, so they share the tighter logo ceiling.
        foreach (var button in payload.Buttons.Concat(payload.ContextMenu)
                     .Where(b => !string.IsNullOrWhiteSpace(b.Icon)))
        {
            var path = await _images.ResolveAsync(button.Icon, ImageRole.AppLogo, ct);
            if (path is not null) resolved.Add(button, path);
        }

        return resolved;
    }

    private async Task ResolveInto(ResolvedImages target, ToastImage? image, ImageRole role, CancellationToken ct)
    {
        if (image is null) return;

        var path = await _images.ResolveAsync(image.Src, role, ct);
        if (path is not null) target.Add(image, path);
    }

}
