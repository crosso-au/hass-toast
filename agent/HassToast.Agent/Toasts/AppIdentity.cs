using HassToast.Agent.Config;

namespace HassToast.Agent.Toasts;

/// <summary>
/// Registers this app with the notification platform under a display name and icon.
/// <para>
/// The parameterless <c>Register()</c> asks the shell for the app's name and icon. An unpackaged
/// console app has no Start Menu presence for the shell to find, so that lookup yields nothing,
/// Windows never creates a notification settings record for the app, and every notification is
/// accepted by the API yet silently never shown — no banner, no Action Center entry, no error.
/// Supplying the name and icon explicitly is what makes the app real to the shell.
/// </para>
/// </summary>
public static class AppIdentity
{
    public const string DefaultDisplayName = "Home Assistant";

    private static string? _displayName;

    /// <summary>
    /// Name Windows shows in the toast header, taken from configuration.
    /// <para>
    /// Read lazily and cached: the short-lived CLI commands each call
    /// <see cref="Register"/> without a host or DI container to hand it in, and re-reading the
    /// config file on every access would be wasteful for a value that cannot change mid-process.
    /// </para>
    /// </summary>
    public static string DisplayName
    {
        get => _displayName ??= LoadDisplayName();
        set => _displayName = string.IsNullOrWhiteSpace(value) ? DefaultDisplayName : value;
    }

    private static string LoadDisplayName()
    {
        try
        {
            var configured = Config.AgentConfig.Load().DisplayName;
            return string.IsNullOrWhiteSpace(configured) ? DefaultDisplayName : configured.Trim();
        }
        catch (Exception)
        {
            // Not configured yet, or unreadable. A name is needed either way.
            return DefaultDisplayName;
        }
    }

    /// <summary>
    /// Makes the app known to the shell. This is the whole registration story now: the classic
    /// notification API needs no runtime registration call, only a Start Menu shortcut carrying
    /// the AUMID. Without that shortcut Windows accepts notifications and never draws them.
    /// </summary>
    public static void Register() => EnsureShortcut();

    /// <summary>
    /// Application User Model ID. Deliberately a fixed string rather than derived from the
    /// executable's path: a path-derived id changes whenever the binary moves, silently
    /// orphaning the Start Menu shortcut and every notification already in the Action Center.
    /// Since the agent creates the shortcut itself, both sides can simply agree on one value.
    /// </summary>
    public const string Aumid = "HassToast.Agent";

    /// <summary>Creates the Start Menu shortcut, returning its path when present.</summary>
    /// <param name="targetPath">
    /// The executable to register. Defaults to the running one — the installer passes the agent
    /// it has just laid down instead.
    /// </param>
    public static string? EnsureShortcut(string? iconPath = null, string? targetPath = null)
    {
        // The executable's own icon rather than the unpacked PNG. A shortcut icon has to come
        // from an .ico, .exe or .dll; pointed at a PNG, the shell has nothing it can draw and the
        // toast header shows a blank page icon instead.
        iconPath ??= targetPath ?? Environment.ProcessPath;

        // Recorded on the shortcut so Windows knows where to deliver clicks, and in the registry
        // so it can start the app when nothing is running.
        StartMenuShortcut.ToastActivatorClsid = Activation.NotificationActivator.Clsid;
        Activation.ComServer.RegisterInRegistry(targetPath);

        var created = StartMenuShortcut.Ensure(Aumid, DisplayName, iconPath, out var path, targetPath);

        // Clear out shortcuts left behind by an earlier display name, which would otherwise
        // keep claiming this AUMID and show the app twice under two names.
        StartMenuShortcut.RemoveOtherShortcutsFor(Aumid, DisplayName);

        return created ? path : null;
    }

    /// <summary>
    /// Path to the icon shown on every toast, written out on first use.
    /// <para>
    /// The shell reads this off disk rather than out of the executable — the Start Menu shortcut
    /// records a path, and the notification platform follows it — so the embedded mark has to be
    /// unpacked somewhere stable. The install directory is that somewhere, and
    /// <see cref="Setup.InstallFootprint"/> knows to remove it again.
    /// </para>
    /// </summary>
    public static string? EnsureIcon()
    {
        try
        {
            var path = Path.Combine(AgentConfig.DefaultDirectory, "icon.png");
            var wanted = BrandAssets.IconPng();
            if (wanted.Length == 0) return File.Exists(path) ? path : null;

            // Compared rather than merely checked for existence: earlier builds drew a
            // placeholder to this same path, and an upgrade that left it alone would keep
            // showing the old mark on every toast forever. The shortcut points at the path, so
            // rewriting the file in place is all it takes.
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(wanted))
                return path;

            Directory.CreateDirectory(AgentConfig.DefaultDirectory);
            File.WriteAllBytes(path, wanted);
            return path;
        }
        catch (Exception)
        {
            // An icon is desirable, not essential; registration should still proceed.
            return null;
        }
    }
}
