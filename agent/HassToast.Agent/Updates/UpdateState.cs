using System.Text.Json;
using System.Text.Json.Serialization;
using HassToast.Agent.Config;

namespace HassToast.Agent.Updates;

/// <summary>
/// The update checker's preferences and memory, in <c>update.json</c> beside the configuration.
/// <para>
/// Kept out of <c>config.json</c> because the installer rewrites that file, and the agent writes
/// this one from the tray and from toast buttons. Two writers on one file would sooner or later
/// lose each other's changes.
/// </para>
/// </summary>
public sealed class UpdateState
{
    /// <summary>The tray's "Check for updates automatically". Manual checks work either way.</summary>
    public bool CheckAutomatically { get; set; } = true;

    /// <summary>When GitHub last answered. Saved so a restart does not cause another check.</summary>
    public DateTimeOffset? LastCheckUtc { get; set; }

    /// <summary>"Remind me tomorrow": automatic offers stay quiet until then.</summary>
    public DateTimeOffset? RemindAfterUtc { get; set; }

    /// <summary>"Skip this version": never offered automatically. A newer release still is.</summary>
    public string? SkippedVersion { get; set; }

    public static TimeSpan CheckInterval { get; } = TimeSpan.FromHours(24);

    public static string DefaultPath => Path.Combine(AgentConfig.DefaultDirectory, "update.json");

    /// <summary>Where installers are downloaded to before they run.</summary>
    public static string DownloadDirectory => Path.Combine(Path.GetTempPath(), "HassToast", "updates");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Never throws. An unreadable file means starting from the defaults.</summary>
    public static UpdateState Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new UpdateState();
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path), JsonOptions) ?? new UpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateState();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// Whether an automatic check should run now: a day since the last one, or a "remind me
    /// tomorrow" that has come due. A last check in the future means the clock moved; check.
    /// </summary>
    public bool IsCheckDue(DateTimeOffset now)
    {
        if (!CheckAutomatically) return false;
        if (LastCheckUtc is not { } last || last > now || now - last >= CheckInterval) return true;
        return RemindAfterUtc is { } remind && now >= remind;
    }

    /// <summary>Whether to show the update toast for <paramref name="release"/>.</summary>
    public bool ShouldOffer(AvailableRelease release, Version current, DateTimeOffset now, bool manual)
    {
        if (release.Version <= current) return false;

        // Asked for from the tray: always answer, whatever was skipped or snoozed.
        if (manual) return true;

        if (string.Equals(SkippedVersion, release.VersionText, StringComparison.Ordinal)) return false;
        return RemindAfterUtc is not { } remind || now >= remind;
    }
}
