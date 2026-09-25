using Microsoft.Win32;
using HassToast.Agent.Activation;
using HassToast.Agent.Config;
using HassToast.Agent.Toasts;

namespace HassToast.Agent.Setup;

/// <summary>What sort of thing an artifact is. Reporting only — it changes nothing.</summary>
public enum FootprintKind
{
    File,
    Directory,
    RegistryValue,
    RegistryKey,

    /// <summary>State Windows owns on the app's behalf, such as Action Center notifications.</summary>
    Platform,
}

/// <summary>One thing the product leaves on the machine, with how to find and remove it.</summary>
/// <param name="Name">What it is, in the words the audit table prints.</param>
/// <param name="Location">Where it lives, for a person reading the report.</param>
/// <param name="Kind">Classification, for the report only.</param>
/// <param name="Probe">Whether it is currently present.</param>
/// <param name="Remove">Removes it. Must succeed silently when it is already gone.</param>
public sealed record FootprintItem(
    string Name,
    string Location,
    FootprintKind Kind,
    Func<bool> Probe,
    Action Remove)
{
    /// <summary>
    /// Whether the artifact is present. A probe that throws is reported as present rather than
    /// absent: "I could not tell" and "it is gone" are different answers, and only one of them
    /// should let an uninstaller claim the machine is clean.
    /// </summary>
    public bool Exists()
    {
        try
        {
            return Probe();
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Removes the artifact, returning the failure description rather than throwing.</summary>
    public string? TryRemove()
    {
        try
        {
            Remove();
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}

/// <summary>
/// The single inventory of everything this product leaves behind, and the only place it is
/// written down. Install registers against it, uninstall reverses it, and <c>--verify-clean</c>
/// audits it.
/// <para>
/// One list rather than three because three drifted. Before this existed, <c>--reset</c> removed
/// two of the nine artifacts the agent actually creates, and the ones it missed — the log folder,
/// the app icon, the image cache, a renamed Start Menu shortcut — were missed precisely
/// because nothing held the complete list in one place to be checked against.
/// </para>
/// <para>
/// Anything added later that outlives the process belongs here, in the same change that adds it.
/// </para>
/// </summary>
public static class InstallFootprint
{
    /// <summary>
    /// Where the installer lays the binaries down. Under LOCALAPPDATA rather than Program Files
    /// so that installing needs no elevation — which matters more than usual here, because the
    /// agent must not run elevated at all: Windows does not deliver notifications for elevated
    /// processes.
    /// </summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "HassToast");

    public static string AgentExecutablePath => Path.Combine(InstallDirectory, "HassToast.Agent.exe");

    public static string UninstallerPath => Path.Combine(InstallDirectory, "HassToast.Setup.exe");

    /// <summary>Add/Remove Programs. Under HKCU, so the entry lists for this user alone.</summary>
    public const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HassToast";

    /// <summary>
    /// Where Windows records this app's notification settings, keyed on the AUMID.
    /// <para>
    /// The app never writes this — the shell does, once it has indexed the Start Menu shortcut.
    /// It is a trace all the same: left behind, the app keeps its own row under
    /// Settings → Notifications long after every file of it has gone, which is exactly the
    /// residue people mean when they say an uninstall was not clean.
    /// </para>
    /// </summary>
    public static string NotificationSettingsKeyPath =>
        $@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{AppIdentity.Aumid}";

    /// <summary>The COM activator registration the agent re-creates on every start.</summary>
    public static string ComActivatorKeyPath =>
        $@"Software\Classes\CLSID\{{{NotificationActivator.ClsidString}}}";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string StartupApprovalKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// Everything to check for and remove, in no particular order — ordering is the
    /// <see cref="Uninstaller"/>'s business, and it matters there.
    /// </summary>
    /// <param name="keepLogs">
    /// Leave the log folder in place. The config directory is then removed only if it empties,
    /// which is the honest outcome rather than a special case: asking to keep the logs is asking
    /// to keep the folder they are in.
    /// </param>
    public static IReadOnlyList<FootprintItem> All(bool keepLogs = false)
    {
        var items = new List<FootprintItem>
        {
            new("Application files",
                InstallDirectory,
                FootprintKind.Directory,
                () => Directory.Exists(InstallDirectory),
                () => DeleteDirectory(InstallDirectory)),

            new("Configuration",
                AgentConfig.DefaultPath,
                FootprintKind.File,
                () => File.Exists(AgentConfig.DefaultPath),
                () => DeleteFile(AgentConfig.DefaultPath)),

            new("Stored credentials",
                Path.Combine(AgentConfig.DefaultDirectory, "secrets.dat"),
                FootprintKind.File,
                () => File.Exists(Path.Combine(AgentConfig.DefaultDirectory, "secrets.dat")),
                () => DeleteFile(Path.Combine(AgentConfig.DefaultDirectory, "secrets.dat"))),

            new("App icon",
                Path.Combine(AgentConfig.DefaultDirectory, "icon.png"),
                FootprintKind.File,
                () => File.Exists(Path.Combine(AgentConfig.DefaultDirectory, "icon.png")),
                () => DeleteFile(Path.Combine(AgentConfig.DefaultDirectory, "icon.png"))),
        };

        if (!keepLogs)
        {
            items.Add(new("Logs",
                AgentConfig.LogDirectory,
                FootprintKind.Directory,
                () => Directory.Exists(AgentConfig.LogDirectory),
                () => DeleteDirectory(AgentConfig.LogDirectory)));
        }

        items.AddRange(new FootprintItem[]
        {
            new("Configuration folder",
                AgentConfig.DefaultDirectory,
                FootprintKind.Directory,
                () => Directory.Exists(AgentConfig.DefaultDirectory),
                // Only when empty. With logs kept it will not be, and reporting that honestly is
                // better than deleting what the user asked to keep.
                () => DeleteDirectoryIfEmpty(AgentConfig.DefaultDirectory)),

            new("Cached toast images",
                AgentConfig.ImageCacheDirectory,
                FootprintKind.Directory,
                () => Directory.Exists(AgentConfig.ImageCacheDirectory),
                () => DeleteDirectory(AgentConfig.ImageCacheDirectory)),

            new("Start Menu shortcut",
                $@"%APPDATA%\Microsoft\Windows\Start Menu\Programs\*.lnk (AUMID {AppIdentity.Aumid})",
                FootprintKind.File,
                () => StartMenuShortcut.FindShortcutsFor(AppIdentity.Aumid).Count > 0,
                () => StartMenuShortcut.RemoveAllShortcutsFor(AppIdentity.Aumid)),

            new("COM activator registration",
                $@"HKCU\{ComActivatorKeyPath}",
                FootprintKind.RegistryKey,
                () => KeyExists(ComActivatorKeyPath),
                ComServer.UnregisterFromRegistry),

            new("Start with Windows",
                $@"HKCU\{RunKeyPath}\{StartupRegistration.ValueName}",
                FootprintKind.RegistryValue,
                () => ValueExists(RunKeyPath, StartupRegistration.ValueName),
                StartupRegistration.Disable),

            new("Startup approval record",
                $@"HKCU\{StartupApprovalKeyPath}\{StartupRegistration.ValueName}",
                FootprintKind.RegistryValue,
                () => ValueExists(StartupApprovalKeyPath, StartupRegistration.ValueName),
                // Disable() clears both; keeping them as separate rows means the audit can say
                // which one is actually left when something goes wrong.
                StartupRegistration.Disable),

            new("Windows notification settings",
                $@"HKCU\{NotificationSettingsKeyPath}",
                FootprintKind.RegistryKey,
                () => KeyExists(NotificationSettingsKeyPath),
                () => DeleteKeyTree(NotificationSettingsKeyPath)),

            new("Add/Remove Programs entry",
                $@"HKCU\{UninstallKeyPath}",
                FootprintKind.RegistryKey,
                () => KeyExists(UninstallKeyPath),
                () => DeleteKeyTree(UninstallKeyPath)),
        });

        return items;
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }

    private static bool KeyExists(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key is not null;
    }

    private static bool ValueExists(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name) is not null;
    }

    private static void DeleteKeyTree(string path)
        => Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
}
