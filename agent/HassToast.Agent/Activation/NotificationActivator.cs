using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace HassToast.Agent.Activation;

/// <summary>
/// COM server that receives toast clicks from Windows.
/// <para>
/// The classic notification API delivers activations through a COM class whose CLSID is recorded
/// on the Start Menu shortcut as <c>ToastActivatorCLSID</c>. Protocol activation would be simpler,
/// but it cannot carry the contents of a text box or selection — quick replies only arrive
/// through this interface, so COM is the only option for an app with inputs.
/// </para>
/// <para>
/// A running process that has registered its class object receives clicks in-process. If nothing
/// is running, COM starts the executable from the <c>LocalServer32</c> registration and calls
/// through once it registers.
/// </para>
/// </summary>
[ClassInterface(ClassInterfaceType.None)]
[ComVisible(true)]
[Guid(ClsidString)]
public sealed class NotificationActivator : INotificationActivationCallback
{
    /// <summary>
    /// Fixed identifier shared by the shortcut, the registry and this class. Changing it orphans
    /// every toast already in the Action Center, whose activator CLSID is baked in at send time.
    /// </summary>
    public const string ClsidString = "7F9A2B31-4C6D-4E8F-9A1B-2C3D4E5F6A7B";

    public static readonly Guid Clsid = new(ClsidString);

    /// <summary>
    /// Set once at startup. Static because COM constructs this class itself, so there is no
    /// opportunity to inject anything through the constructor.
    /// </summary>
    public static Func<ToastActivation, Task>? Handler { get; set; }

    public static ILogger? Log { get; set; }

    public void Activate(string appUserModelId, string invokedArgs,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 3)]
        NotificationUserInput[] data, uint count)
    {
        try
        {
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < count && i < data.Length; i++)
                inputs[data[i].Key] = data[i].Value ?? "";

            Log?.LogInformation(
                "COM activation received: {ArgumentLength} chars of arguments, {InputCount} input(s).",
                invokedArgs?.Length ?? 0, inputs.Count);

            var activation = new ToastActivation(appUserModelId, invokedArgs ?? "", inputs);

            var handler = Handler;
            if (handler is null)
            {
                Log?.LogWarning("A toast was activated before the agent was ready to handle it.");
                return;
            }

            // Windows expects this callback to return promptly, and the handler does network I/O.
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(activation);
                }
                catch (Exception ex)
                {
                    Log?.LogError(ex, "Failed to handle a toast activation.");
                }
            });
        }
        catch (Exception ex)
        {
            // An exception escaping into COM would surface as an opaque failure in the shell.
            Log?.LogError(ex, "Toast activation callback failed.");
        }
    }
}

/// <summary>What the user did to a toast, decoded from the COM callback.</summary>
public sealed record ToastActivation(
    string AppUserModelId,
    string Arguments,
    IReadOnlyDictionary<string, string> Inputs);

[ComImport]
[Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INotificationActivationCallback
{
    void Activate(
        [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [In, MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 3)]
        NotificationUserInput[] data,
        [In, MarshalAs(UnmanagedType.U4)] uint count);
}

/// <summary>One key/value pair from a text box or selection on the toast.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct NotificationUserInput
{
    [MarshalAs(UnmanagedType.LPWStr)] public string Key;
    [MarshalAs(UnmanagedType.LPWStr)] public string Value;
}
