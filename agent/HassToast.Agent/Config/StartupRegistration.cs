using Microsoft.Win32;

namespace HassToast.Agent.Config;

/// <summary>
/// Per-user "start with Windows" registration, via the HKCU Run key.
/// <para>
/// Deliberately per-user rather than a service or an HKLM entry: the agent must run in the
/// signed-in user's session to raise toasts at all, and it needs no elevation to register itself
/// here. The Run key is also where Task Manager's Startup tab looks, so the toggle in the tray
/// and the switch the user already knows about control the same thing.
/// </para>
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Where Explorer records startup entries the user switched off in Task Manager. The Run
    /// value survives that, so reading only the Run key would report "enabled" for an entry
    /// Windows will never launch.
    /// </summary>
    private const string ApprovalKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// Value name under Run. Fixed rather than derived from the configured display name: renaming
    /// the app would otherwise leave the old entry behind, launching the agent twice.
    /// </summary>
    public const string ValueName = "HassToast.Agent";

    /// <summary>Whether Windows will launch the agent when this user signs in.</summary>
    public static bool IsEnabled => RegisteredCommand is not null && IsApproved();

    /// <summary>The command currently registered, or null if there is no entry.</summary>
    public static string? RegisteredCommand
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
    }

    /// <summary>What the entry should say for the executable running right now.</summary>
    public static string ExpectedCommand => CommandFor(
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Environment.ProcessPath is empty, so there is no path to register."));

    /// <summary>The Run value for a given executable.</summary>
    /// <remarks>Quoted: an unquoted path containing spaces is parsed as a command plus arguments.</remarks>
    public static string CommandFor(string exePath) => $"\"{exePath}\"";

    /// <param name="exePath">
    /// What to register. Defaults to the running executable; the installer passes the agent it
    /// has just installed, because registering the installer would start the wrong thing at
    /// sign-in — and only ever be noticed by the agent not being there.
    /// </param>
    public static void Enable(string? exePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, exePath is null ? ExpectedCommand : CommandFor(exePath), RegistryValueKind.String);

        // If the entry was switched off in Task Manager, writing the Run value alone changes
        // nothing — Explorer keeps honouring its own record. Removing that record restores the
        // default, which is "approved".
        if (!IsApproved())
        {
            using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKeyPath, writable: true);
            approval?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static void Disable()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
        {
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        // Leaving a disabled-approval record behind would be harmless but confusing: Task
        // Manager would keep listing the entry as a disabled startup app that no longer exists.
        using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKeyPath, writable: true);
        approval?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Repoints an existing registration at the running executable, returning true if it had to
    /// be rewritten. Without this, moving or rebuilding the agent elsewhere leaves an entry that
    /// launches a binary which may no longer exist — a failure visible only by the agent not
    /// being there at the next sign-in.
    /// </summary>
    public static bool EnsureCurrent()
    {
        if (!IsEnabled) return false;

        if (string.Equals(RegisteredCommand, ExpectedCommand, StringComparison.OrdinalIgnoreCase))
            return false;

        Enable();
        return true;
    }

    private static bool IsApproved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovalKeyPath);
        return IsApproved(key?.GetValue(ValueName) as byte[]);
    }

    /// <summary>
    /// Decodes Explorer's approval record. The value is a 12-byte blob whose first byte carries
    /// the state — even means enabled, odd means the user switched it off — with the remainder
    /// holding the timestamp of the change. No entry at all means approved.
    /// </summary>
    internal static bool IsApproved(byte[]? value)
        => value is not { Length: > 0 } || (value[0] & 1) == 0;
}
