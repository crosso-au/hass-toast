using System.Diagnostics;
using Windows.UI.Notifications;
using HassToast.Agent.Config;
using HassToast.Agent.Security;
using HassToast.Agent.Toasts;
using HassToast.Agent.Transport;

namespace HassToast.Agent.Setup;

/// <summary>What the person asked for on the way out.</summary>
public sealed record UninstallOptions
{
    /// <summary>
    /// Leave the log folder behind. Off by default: "no traces" has to mean no traces, or the
    /// claim is worthless. Worth offering all the same, because the one time logs matter most is
    /// when the uninstall itself went wrong.
    /// </summary>
    public bool KeepLogs { get; init; }

    /// <summary>
    /// Also delete this machine's config entry in Home Assistant. Off by default, and scoped to
    /// this machine only — the integration itself and the HACS repository are never touched, so
    /// other machines keep working and re-installing here does not mean re-installing there.
    /// </summary>
    public bool RemoveHomeAssistantEntry { get; init; }
}

/// <summary>One thing the uninstaller tried, and how it went.</summary>
public sealed record UninstallStep(string Name, bool Succeeded, string? Detail = null);

/// <summary>The outcome, in the shape the final page and <c>--verify-clean</c> both print.</summary>
public sealed record UninstallReport(
    IReadOnlyList<UninstallStep> Steps,
    IReadOnlyList<FootprintItem> Remaining)
{
    /// <summary>Nothing of this product is left on the machine.</summary>
    public bool Clean => Remaining.Count == 0;
}

/// <summary>
/// Removes the product, in the order that actually works.
/// <para>
/// Order is the whole difficulty here, and the obvious order is wrong. The agent re-creates its
/// Start Menu shortcut, app icon and CLSID registration on every start — and several of
/// its own CLI verbs do too, <c>--clear-toasts</c> among them, which calls
/// <see cref="AppIdentity.Register"/> before clearing. So an uninstaller that removed
/// registration first and stopped the agent afterwards would watch its own work be undone, and
/// report success.
/// </para>
/// </summary>
public static class Uninstaller
{
    /// <summary>
    /// Whether the running executable sits inside the directory that is about to be deleted.
    /// A process cannot delete its own image, so it has to move first.
    /// </summary>
    public static bool RunningFromInstallDirectory
    {
        get
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self)) return false;

            return Path.GetFullPath(self)
                .StartsWith(Path.GetFullPath(InstallFootprint.InstallDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Copies this executable to a temporary directory and starts it there with the same
    /// arguments, so the copy can delete the installation the original is standing in.
    /// </summary>
    /// <returns>The temporary executable path, for the caller to exit after.</returns>
    public static string RelaunchFromTemp(IEnumerable<string> arguments)
    {
        var self = Environment.ProcessPath
                   ?? throw new InvalidOperationException("Environment.ProcessPath is empty.");

        var directory = RelocationDirectory;
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, Path.GetFileName(self));
        File.Copy(self, target, overwrite: true);

        var start = new ProcessStartInfo(target) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(RelocatedFlag);

        Process.Start(start);
        return target;
    }

    /// <summary>Where <see cref="RelaunchFromTemp"/> puts the temporary copy.</summary>
    private static string RelocationDirectory => Path.Combine(Path.GetTempPath(), "HassToast-uninstall");

    /// <summary>Marks a run that is already executing from the temporary copy.</summary>
    public const string RelocatedFlag = "--relocated";

    public static async Task<UninstallReport> RunAsync(
        UninstallOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var steps = new List<UninstallStep>();

        // 1. Home Assistant first, while the configuration and credentials that reach it still
        //    exist. Every later step is local, so nothing is lost by doing the remote work first
        //    — whereas doing it last would mean reading a token out of a file already deleted.
        if (options.RemoveHomeAssistantEntry)
            steps.Add(await RemoveHomeAssistantEntryAsync(progress, ct));

        // 2. Stop the agent. Until it is gone it will keep re-creating registration, and its exe
        //    cannot be deleted while it is mapped.
        steps.Add(StopAgent(progress));

        // 3. Clear the Action Center while the AUMID still means something. Called directly
        //    rather than through the agent's --clear-toasts, which registers the app first and
        //    would put back the shortcut, icon and CLSID key this is about to remove.
        steps.Add(ClearNotifications(progress));

        // 4. Everything else, from the one inventory.
        progress?.Report("Removing files and registry entries…");

        foreach (var item in InstallFootprint.All(options.KeepLogs))
        {
            if (!item.Exists())
            {
                steps.Add(new UninstallStep(item.Name, true, "not present"));
                continue;
            }

            var failure = item.TryRemove();
            steps.Add(new UninstallStep(item.Name, failure is null, failure));
        }

        // 5. Audit rather than assert. Re-probing after the fact is the only way the final page
        //    can claim the machine is clean and be believed.
        var remaining = InstallFootprint.All(options.KeepLogs)
            .Where(item => item.Exists())
            .ToList();

        progress?.Report(remaining.Count == 0
            ? "Nothing of HASS Windows Toast is left on this machine."
            : $"{remaining.Count} item(s) could not be removed.");

        return new UninstallReport(steps, remaining);
    }

    /// <summary>Re-probes the whole footprint without changing anything.</summary>
    public static IReadOnlyList<(FootprintItem Item, bool Present)> Audit(bool keepLogs = false)
        => InstallFootprint.All(keepLogs).Select(item => (item, item.Exists())).ToList();

    private static async Task<UninstallStep> RemoveHomeAssistantEntryAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        const string name = "Home Assistant entry";
        progress?.Report("Removing this machine from Home Assistant…");

        AgentConfig config;
        AgentSecrets secrets;

        try
        {
            config = AgentConfig.Load();
            config.Validate();
            secrets = new SecretStore().Load();
        }
        catch (Exception ex)
        {
            return new UninstallStep(name, false,
                $"the stored configuration could not be read, so Home Assistant was left alone ({ex.Message})");
        }

        try
        {
            using var admin = new HomeAssistantAdmin(config.HomeAssistant, secrets.AccessToken);

            // Prefer the id the wizard recorded. Falling back to the title is a weaker match, so
            // it is a fallback rather than the route: a hand-configured entry has no recorded id.
            var entryId = config.HomeAssistant.ConfigEntryId
                          ?? (await admin.FindEntryAsync(config.DeviceId, ct))?.EntryId;

            if (entryId is null)
                return new UninstallStep(name, true, "no entry for this machine was found");

            return await admin.DeleteEntryAsync(entryId, ct)
                ? new UninstallStep(name, true, $"deleted entry {entryId}")
                : new UninstallStep(name, false, "Home Assistant refused the deletion");
        }
        catch (Exception ex)
        {
            return new UninstallStep(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the running agent, asking first and ending the process if it does not respond.
    /// Also used by the installer, which cannot replace the agent's files while it is running.
    /// </summary>
    internal static UninstallStep StopAgent(IProgress<string>? progress)
    {
        const string name = "Stop the agent";

        if (!AgentInstance.IsRunning())
            return new UninstallStep(name, true, "was not running");

        progress?.Report("Asking the agent to stop…");

        if (AgentInstance.RequestQuit(TimeSpan.FromSeconds(15)))
            return new UninstallStep(name, true, "stopped cleanly");

        // Falling back rather than giving up: a stuck agent must not be able to block an
        // uninstall. The cost is a tray icon that lingers until the shell repaints, which is
        // cosmetic, where a half-removed install is not.
        progress?.Report("The agent did not respond; ending the process…");

        var killed = 0;
        foreach (var process in Process.GetProcessesByName(AgentInstance.ProcessName))
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                process.Kill();
                process.WaitForExit(5000);
                killed++;
            }
            catch (Exception)
            {
                // Already gone, or not ours to end.
            }
            finally
            {
                process.Dispose();
            }
        }

        return new UninstallStep(name, !AgentInstance.IsRunning(),
            killed > 0 ? $"ended {killed} process(es) after no response" : "could not be stopped");
    }

    private static UninstallStep ClearNotifications(IProgress<string>? progress)
    {
        const string name = "Action Center notifications";
        progress?.Report("Clearing notifications…");

        try
        {
            ToastNotificationManager.History.Clear(AppIdentity.Aumid);
            return new UninstallStep(name, true);
        }
        catch (Exception ex)
        {
            // Windows refuses this once the app is no longer registered, which is a state this
            // very method exists to precede — but a failure here leaves at most a stale entry
            // the platform prunes itself, so it must not stop the rest.
            return new UninstallStep(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the temporary copy of this executable after it has exited.
    /// <para>
    /// A detached <c>cmd</c> rather than anything cleverer: the process cannot outlive itself to
    /// delete its own image, so something outside it has to wait and then remove it.
    /// </para>
    /// </summary>
    public static void ScheduleSelfDelete()
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self)) return;

        var directory = Path.GetDirectoryName(self);
        if (directory is null) return;

        // Only ever the temporary copy. Uninstalling can also be started from the downloaded
        // installer, and that file, and the folder it sits in, belong to the user.
        if (!string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(RelocationDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                // ping is the portable sleep: timeout.exe fails without a console to attach to,
                // which is exactly the situation here.
                Arguments = $"/c ping 127.0.0.1 -n 4 >nul & del /f /q \"{self}\" & rmdir /q \"{directory}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception)
        {
            // Worst case a few hundred kilobytes sit in TEMP until Windows clears it.
        }
    }
}
