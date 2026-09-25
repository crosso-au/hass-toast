using System.Globalization;
using System.Xml.Linq;

namespace HassToast.Agent.Toasts;

/// <summary>
/// Builds the toast XML payload as a document tree.
/// <para>
/// <b>Why not AppNotificationBuilder.</b> The Windows App SDK builder has no API for adaptive
/// groups or subgroups, and <c>hint-style</c> text styling is only honoured by Windows inside a
/// subgroup — so the entire styled, multi-column layout is unreachable through it.
/// </para>
/// <para>
/// <b>Why this is still injection-safe.</b> Values are attached as <see cref="XElement"/> content
/// and <see cref="XAttribute"/> values, never spliced into a string. Escaping is performed by the
/// XML writer when the tree is serialised, so a message containing <c>&lt;/text&gt;&lt;action/&gt;</c>
/// is emitted as escaped character data and renders as those literal characters. The safety comes
/// from the writer rather than from remembering to escape, which is the point.
/// </para>
/// </summary>
public static class ToastXmlWriter
{
    /// <summary>Windows renders at most three top-level text elements.</summary>
    public const int MaxTopLevelTextLines = 3;

    /// <summary>Windows allows five inputs, and five buttons and context-menu items combined.</summary>
    public const int MaxInputs = 5;
    public const int MaxActions = 5;

    /// <summary>Windows allows at most five options in a selection input.</summary>
    public const int MaxSelectionItems = 5;

    public static XDocument Build(ToastPayload payload, ResolvedImages images)
        => Build(payload, images, bindProgress: false);

    /// <param name="bindProgress">
    /// Emit the progress bar with data-binding placeholders instead of literal values, so it can
    /// later be advanced in place by <c>NotificationPipeline.Update</c> rather than replaced.
    /// </param>
    public static XDocument Build(ToastPayload payload, ResolvedImages images, bool bindProgress)
    {
        var toast = new XElement("toast");

        SetIfPresent(toast, "scenario", NormaliseScenario(payload.Scenario));
        SetIfPresent(toast, "duration", NormaliseDuration(payload.Duration));

        if (payload.Timestamp is { } stamp)
        {
            toast.SetAttributeValue("displayTimestamp",
                stamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }

        WriteLaunch(toast, payload);

        // Coloured buttons only render when the toast opts in.
        if (payload.Buttons.Concat(payload.ContextMenu)
            .Any(b => NormaliseButtonStyle(b.Style) is not null))
        {
            toast.SetAttributeValue("useButtonStyle", "true");
        }

        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"));
        toast.Add(new XElement("visual", binding));

        WriteText(binding, payload.Visual);
        WriteAttribution(binding, payload.Visual);
        WriteImages(binding, payload.Visual, images);
        WriteProgress(binding, payload.Visual.Progress, bindProgress);
        WriteGroups(binding, payload.Visual.Groups, images);

        WriteActions(toast, payload, images);
        WriteAudio(toast, payload.Audio);
        WriteHeader(toast, payload.Header);

        return new XDocument(toast);
    }

    private static void WriteLaunch(XElement toast, ToastPayload payload)
    {
        var launch = payload.Launch;

        // Even with no launch configured, the body stays clickable — so it must still carry the
        // toast's identity. Without this a body click arrives with empty arguments and Home
        // Assistant cannot tell which toast the user acted on.
        if (launch is null)
        {
            if (string.IsNullOrWhiteSpace(payload.Tag) && string.IsNullOrWhiteSpace(payload.Group))
                return;

            toast.SetAttributeValue("launch",
                ActivationArguments.Encode(
                    ActivationArguments.WithIdentity(null, payload.Tag, payload.Group)));
            return;
        }

        if (string.Equals(launch.Type, "protocol", StringComparison.OrdinalIgnoreCase))
        {
            // Policy has already vetted the scheme; an empty Uri here means it was rejected,
            // in which case the body simply falls back to activating the agent.
            if (string.IsNullOrWhiteSpace(launch.Uri)) return;

            toast.SetAttributeValue("activationType", "protocol");
            toast.SetAttributeValue("launch", launch.Uri);
            return;
        }

        toast.SetAttributeValue("activationType", "foreground");
        toast.SetAttributeValue("launch",
            ActivationArguments.Encode(
                ActivationArguments.WithIdentity(launch.Args, payload.Tag, payload.Group)));
    }

    /// <summary>
    /// Scenarios Windows only honours when the toast carries at least one button. Without one it
    /// silently downgrades the notification to an ordinary toast, so a payload asking to stay on
    /// screen quietly fades away instead.
    /// </summary>
    internal static bool ScenarioNeedsAButton(string? scenario)
        => NormaliseScenario(scenario) is "reminder" or "alarm" or "incomingCall";

    private static void WriteActions(XElement toast, ToastPayload payload, ResolvedImages images)
    {
        var inputs = payload.Inputs.Take(MaxInputs).ToList();

        // Buttons and context-menu items share one budget of five.
        var buttons = payload.Buttons.Take(MaxActions).ToList();
        var contextMenu = payload.ContextMenu.Take(Math.Max(0, MaxActions - buttons.Count)).ToList();

        // Supply the button the scenario requires rather than letting Windows quietly ignore
        // the scenario. A Dismiss button is the least surprising thing to add: it is what the
        // user would otherwise reach for, and Windows labels and localises it itself.
        if (buttons.Count == 0 && ScenarioNeedsAButton(payload.Scenario))
        {
            buttons.Add(new ToastButton { Type = "dismiss" });
        }

        if (inputs.Count == 0 && buttons.Count == 0 && contextMenu.Count == 0) return;

        var actions = new XElement("actions");

        foreach (var input in inputs) WriteInput(actions, input);

        // Icons are all-or-nothing: if any button has one, Windows switches the whole toast to
        // icon buttons and the rest render blank. Only honour them when every button has one.
        var allButtonsHaveIcons = buttons.Count > 0 && buttons
            .Where(b => !b.IsSystem)
            .All(b => !string.IsNullOrWhiteSpace(b.Icon) && images.TryGet(b, out _));

        foreach (var button in buttons) WriteAction(actions, button, payload, images, allButtonsHaveIcons, false);
        foreach (var item in contextMenu) WriteAction(actions, item, payload, images, false, true);

        if (actions.HasElements) toast.Add(actions);
    }

    private static void WriteInput(XElement actions, ToastInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Id)) return;

        var isSelection = string.Equals(input.Type, "selection", StringComparison.OrdinalIgnoreCase);

        var element = new XElement("input",
            new XAttribute("id", input.Id),
            new XAttribute("type", isSelection ? "selection" : "text"));

        SetIfPresent(element, "title", input.Title);
        SetIfPresent(element, "defaultInput", input.Default);

        if (isSelection)
        {
            foreach (var item in input.Items.Take(MaxSelectionItems))
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                element.Add(new XElement("selection",
                    new XAttribute("id", item.Id),
                    new XAttribute("content", item.Content ?? "")));
            }
        }
        else
        {
            SetIfPresent(element, "placeHolderContent", input.Placeholder);
        }

        actions.Add(element);
    }

    private static void WriteAction(
        XElement actions, ToastButton button, ToastPayload payload,
        ResolvedImages images, bool useIcons, bool contextMenu)
    {
        if (button.IsSystem)
        {
            WriteSystemAction(actions, button);
            return;
        }

        var isProtocol = string.Equals(button.Activation, "protocol", StringComparison.OrdinalIgnoreCase);

        // A protocol button whose target was rejected by policy would launch nothing at all;
        // dropping it is clearer than leaving a dead control on the toast.
        if (isProtocol && string.IsNullOrWhiteSpace(button.Uri)) return;

        var element = new XElement("action",
            new XAttribute("content", button.Content ?? ""),
            new XAttribute("arguments", isProtocol
                ? button.Uri!
                : ActivationArguments.Encode(
                    ActivationArguments.WithIdentity(button.Args, payload.Tag, payload.Group))));

        element.SetAttributeValue("activationType",
            isProtocol ? "protocol" : NormaliseActivation(button.Activation) ?? "foreground");

        if (contextMenu) element.SetAttributeValue("placement", "contextMenu");

        SetIfPresent(element, "hint-buttonStyle", NormaliseButtonStyle(button.Style));
        SetIfPresent(element, "hint-inputId", button.InputId);
        SetIfPresent(element, "hint-toolTip", button.Tooltip);

        if (useIcons && images.TryGet(button, out var iconPath))
            element.SetAttributeValue("imageUri", iconPath);

        actions.Add(element);
    }

    /// <summary>
    /// Snooze and dismiss are handled entirely by Windows, including localisation of the default
    /// label, so they carry a reserved argument rather than one of ours.
    /// </summary>
    private static void WriteSystemAction(XElement actions, ToastButton button)
    {
        var element = new XElement("action",
            new XAttribute("activationType", "system"),
            new XAttribute("arguments", button.Type == "snooze" ? "snooze" : "dismiss"),
            // An empty content attribute tells Windows to use its own localised label.
            new XAttribute("content", button.Content ?? ""));

        if (button.Type == "snooze") SetIfPresent(element, "hint-inputId", button.InputId);

        actions.Add(element);
    }

    private static void WriteHeader(XElement toast, ToastHeader? header)
    {
        if (header is null || string.IsNullOrWhiteSpace(header.Id)) return;

        toast.Add(new XElement("header",
            new XAttribute("id", header.Id),
            new XAttribute("title", header.Title ?? ""),
            new XAttribute("arguments", ActivationArguments.Encode(header.Args))));
    }

    private static void WriteText(XElement binding, ToastVisual visual)
    {
        // Text must precede every other element in the binding; Windows pulls stray text to the
        // top or drops it outright.
        foreach (var line in visual.Text
                     .Where(l => !string.IsNullOrWhiteSpace(l))
                     .Take(MaxTopLevelTextLines))
        {
            binding.Add(new XElement("text", line));
        }
    }

    private static void WriteAttribution(XElement binding, ToastVisual visual)
    {
        if (string.IsNullOrWhiteSpace(visual.Attribution)) return;

        binding.Add(new XElement("text",
            new XAttribute("placement", "attribution"),
            visual.Attribution));
    }

    private static void WriteImages(XElement binding, ToastVisual visual, ResolvedImages images)
    {
        if (visual.AppLogo is { } logo && images.TryGet(logo, out var logoPath))
        {
            var element = new XElement("image",
                new XAttribute("placement", "appLogoOverride"),
                new XAttribute("src", logoPath));
            SetIfPresent(element, "hint-crop", NormaliseCrop(logo.Crop));
            SetIfPresent(element, "alt", logo.Alt);
            binding.Add(element);
        }

        if (visual.Hero is { } hero && images.TryGet(hero, out var heroPath))
        {
            var element = new XElement("image",
                new XAttribute("placement", "hero"),
                new XAttribute("src", heroPath));
            SetIfPresent(element, "alt", hero.Alt);
            binding.Add(element);
        }

        if (visual.Inline is { } inline && images.TryGet(inline, out var inlinePath))
        {
            var element = new XElement("image", new XAttribute("src", inlinePath));
            SetIfPresent(element, "hint-crop", NormaliseCrop(inline.Crop));
            SetIfPresent(element, "alt", inline.Alt);
            binding.Add(element);
        }
    }

    // Reserved binding names that a NotificationData update writes into. They are fixed by the
    // platform, so an update only lands if the original XML used exactly these.
    internal const string ProgressValueBinding = "{progressValue}";
    internal const string ProgressStatusBinding = "{progressStatus}";
    internal const string ProgressTitleBinding = "{progressTitle}";
    internal const string ProgressOverrideBinding = "{progressValueStringOverride}";

    private static void WriteProgress(XElement binding, ToastProgress? progress, bool bind)
    {
        if (progress is null) return;

        if (bind)
        {
            // Every field becomes a placeholder so a later update can change any of them.
            // Windows fills these from the NotificationData attached to the toast.
            binding.Add(new XElement("progress",
                new XAttribute("status", ProgressStatusBinding),
                new XAttribute("value", ProgressValueBinding),
                new XAttribute("title", ProgressTitleBinding),
                new XAttribute("valueStringOverride", ProgressOverrideBinding)));
            return;
        }

        var element = new XElement("progress",
            // status is required by the schema; an empty one renders as a blank line rather
            // than failing, which is friendlier than refusing the whole toast.
            new XAttribute("status", progress.Status ?? ""),
            new XAttribute("value", FormatProgressValue(progress.Value)));

        SetIfPresent(element, "title", progress.Title);
        SetIfPresent(element, "valueStringOverride", progress.ValueOverride);

        binding.Add(element);
    }

    internal static string FormatProgressValue(double? value)
        => value is { } v
            ? Math.Clamp(v, 0.0, 1.0).ToString("0.####", CultureInfo.InvariantCulture)
            : "indeterminate";

    private static void WriteGroups(XElement binding, List<ToastGroup> groups, ResolvedImages images)
    {
        foreach (var group in groups)
        {
            var groupElement = new XElement("group");

            foreach (var column in group.Columns)
            {
                var subgroup = new XElement("subgroup");

                if (column.Weight is { } weight && weight > 0)
                {
                    subgroup.SetAttributeValue("hint-weight",
                        weight.ToString(CultureInfo.InvariantCulture));
                }

                SetIfPresent(subgroup, "hint-textStacking", NormaliseTextStacking(column.TextStacking));

                foreach (var child in column.Children)
                    WriteColumnChild(subgroup, child, images);

                // An empty subgroup renders as dead space; skip it.
                if (subgroup.HasElements) groupElement.Add(subgroup);
            }

            if (groupElement.HasElements) binding.Add(groupElement);
        }
    }

    private static void WriteColumnChild(XElement subgroup, ToastColumnChild child, ResolvedImages images)
    {
        if (child.IsImage)
        {
            if (!images.TryGet(child, out var path)) return;

            var image = new XElement("image", new XAttribute("src", path));
            SetIfPresent(image, "hint-crop", NormaliseCrop(child.Crop));
            SetIfPresent(image, "alt", child.Alt);
            if (child.RemoveMargin == true) image.SetAttributeValue("hint-removeMargin", "true");
            subgroup.Add(image);
            return;
        }

        if (string.IsNullOrWhiteSpace(child.Text)) return;

        var text = new XElement("text", child.Text);
        SetIfPresent(text, "hint-style", NormaliseTextStyle(child.Style));
        SetIfPresent(text, "hint-align", NormaliseAlign(child.Align));
        if (child.Wrap is { } wrap) text.SetAttributeValue("hint-wrap", wrap ? "true" : "false");
        if (child.MaxLines is { } max && max > 0)
            text.SetAttributeValue("hint-maxLines", max.ToString(CultureInfo.InvariantCulture));
        if (child.MinLines is { } min && min > 0)
            text.SetAttributeValue("hint-minLines", min.ToString(CultureInfo.InvariantCulture));

        subgroup.Add(text);
    }

    private static void WriteAudio(XElement toast, ToastAudio? audio)
    {
        if (audio is null) return;

        var element = new XElement("audio");

        if (audio.Silent)
        {
            element.SetAttributeValue("silent", "true");
            toast.Add(element);
            return;
        }

        // Only ms-winsoundevent survives for an unpackaged app, which has no ms-appx or
        // ms-resource of its own. Anything else is dropped rather than passed through, the same
        // way the normalisers below drop unrecognised values — Windows would silently ignore it,
        // and an omitted src at least falls back to the system default sound.
        if (NormaliseAudioSource(audio.Src) is { } src) element.SetAttributeValue("src", src);
        if (audio.Loop) element.SetAttributeValue("loop", "true");

        if (element.HasAttributes) toast.Add(element);
    }

    private static void SetIfPresent(XElement element, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) element.SetAttributeValue(name, value);
    }

    // Normalisers map payload values onto the exact spellings Windows expects, and return null
    // for anything unrecognised so a bad value degrades rather than emitting invalid XML.

    internal static string? NormaliseScenario(string? value) => value?.ToLowerInvariant() switch
    {
        "reminder" => "reminder",
        "alarm" => "alarm",
        "incomingcall" => "incomingCall",
        "urgent" => "urgent",
        _ => null, // includes "default", which is the absence of the attribute
    };

    internal static string? NormaliseDuration(string? value) => value?.ToLowerInvariant() switch
    {
        "long" => "long",
        "short" => "short",
        _ => null,
    };

    internal static string? NormaliseActivation(string? value) => value?.ToLowerInvariant() switch
    {
        "foreground" => "foreground",
        "background" => "background",
        "protocol" => "protocol",
        _ => null,
    };

    /// <summary>Windows compares these case-sensitively and ignores anything else.</summary>
    internal static string? NormaliseButtonStyle(string? value) => value?.ToLowerInvariant() switch
    {
        "success" or "green" => "Success",
        "critical" or "red" => "Critical",
        _ => null,
    };

    /// <summary>
    /// Windows accepts only <c>ms-winsoundevent:*</c> here for an unpackaged app. The sound name
    /// itself is left alone — Windows ignores one it does not know, and enumerating the whole
    /// catalogue would only go stale.
    /// </summary>
    internal static string? NormaliseAudioSource(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.StartsWith("ms-winsoundevent:", StringComparison.OrdinalIgnoreCase)
            ? value
            : null;

    internal static string? NormaliseCrop(string? value) => value?.ToLowerInvariant() switch
    {
        "circle" => "circle",
        "none" => "none",
        _ => null,
    };

    internal static string? NormaliseAlign(string? value) => value?.ToLowerInvariant() switch
    {
        "auto" => "auto",
        "left" => "left",
        "center" or "centre" => "center",
        "right" => "right",
        _ => null,
    };

    internal static string? NormaliseTextStacking(string? value) => value?.ToLowerInvariant() switch
    {
        "top" => "top",
        "center" or "centre" => "center",
        "bottom" => "bottom",
        _ => null,
    };

    /// <summary>
    /// Windows matches these case-sensitively, so the payload's free-form spelling is mapped
    /// onto the exact camelCase the schema defines.
    /// </summary>
    internal static string? NormaliseTextStyle(string? value) => value?.ToLowerInvariant() switch
    {
        "caption" => "caption",
        "captionsubtle" => "captionSubtle",
        "body" => "body",
        "bodysubtle" => "bodySubtle",
        "base" => "base",
        "basesubtle" => "baseSubtle",
        "subtitle" => "subtitle",
        "subtitlesubtle" => "subtitleSubtle",
        "title" => "title",
        "titlesubtle" => "titleSubtle",
        "titlenumeral" => "titleNumeral",
        "subheader" => "subheader",
        "subheadersubtle" => "subheaderSubtle",
        "subheadernumeral" => "subheaderNumeral",
        "header" => "header",
        "headersubtle" => "headerSubtle",
        "headernumeral" => "headerNumeral",
        _ => null,
    };
}
