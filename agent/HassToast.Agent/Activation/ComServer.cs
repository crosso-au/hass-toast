using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace HassToast.Agent.Activation;

/// <summary>
/// Publishes <see cref="NotificationActivator"/> to COM: a registry entry so Windows can start
/// the app to deliver a click, and a runtime class object so a process that is already running
/// receives clicks in-process instead of a second one being launched.
/// </summary>
public static class ComServer
{
    private const int ClassEMultipleUse = 1;    // REGCLS_MULTIPLEUSE
    private const int ClsCtxLocalServer = 4;

    private static uint _registrationCookie;

    /// <summary>
    /// Records the LocalServer32 entry Windows uses to launch this executable for an activation.
    /// Per-user, so it needs no elevation.
    /// </summary>
    /// <param name="exePath">
    /// The executable COM should launch. Defaults to the running one; the installer passes the
    /// agent it has just installed, since registering the installer here would leave Windows
    /// launching something that no longer exists.
    /// </param>
    public static void RegisterInRegistry(string? exePath = null)
    {
        var exe = exePath ?? Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        using var key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Classes\CLSID\{{{NotificationActivator.ClsidString}}}\LocalServer32");

        // Quoted: the path routinely contains spaces, and COM splits on them otherwise.
        key.SetValue(null, $"\"{exe}\"");
    }

    public static void UnregisterFromRegistry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\CLSID\{{{NotificationActivator.ClsidString}}}", throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // Best effort: a leftover key is harmless.
        }
    }

    /// <summary>
    /// Registers the running process as the class object for the activator, so clicks are
    /// delivered here rather than starting a second copy of the app.
    /// </summary>
    public static bool StartServer(ILogger log)
    {
        if (_registrationCookie != 0) return true;

        try
        {
            var clsid = NotificationActivator.Clsid;
            var factory = new ClassFactory();

            var hr = CoRegisterClassObject(
                ref clsid, factory, ClsCtxLocalServer, ClassEMultipleUse, out _registrationCookie);

            if (hr != 0)
            {
                log.LogError("CoRegisterClassObject failed with HRESULT 0x{Hr:X8}.", hr);
                return false;
            }

            // Required after registering: until this runs, COM will not route calls to us.
            CoResumeClassObjects();

            log.LogInformation("COM activator registered; toast clicks will arrive in this process.");
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not register the COM activator.");
            return false;
        }
    }

    public static void StopServer()
    {
        if (_registrationCookie == 0) return;

        try
        {
            CoRevokeClassObject(_registrationCookie);
        }
        catch (Exception)
        {
            // Shutting down anyway.
        }
        finally
        {
            _registrationCookie = 0;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        ref Guid clsid,
        [MarshalAs(UnmanagedType.IUnknown)] object factory,
        int context, int flags, out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoResumeClassObjects();

    /// <summary>Hands COM a <see cref="NotificationActivator"/> when it asks for one.</summary>
    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    private sealed class ClassFactory : IClassFactory
    {
        private const int ClassENoAggregation = unchecked((int)0x80040110);
        private const int ENoInterface = unchecked((int)0x80004002);

        public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance)
        {
            instance = IntPtr.Zero;

            // Aggregation is not supported, and saying so is required rather than optional.
            if (outer != IntPtr.Zero) return ClassENoAggregation;

            var activator = new NotificationActivator();
            var unknown = Marshal.GetIUnknownForObject(activator);

            try
            {
                return Marshal.QueryInterface(unknown, in iid, out instance) == 0 ? 0 : ENoInterface;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int LockServer(bool @lock) => 0;
    }

    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool @lock);
    }
}
