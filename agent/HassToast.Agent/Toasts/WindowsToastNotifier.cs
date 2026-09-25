using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace HassToast.Agent.Toasts;

/// <summary>
/// Shows toasts through the classic <see cref="ToastNotificationManager"/> rather than the
/// Windows App SDK's AppNotificationManager.
/// <para>
/// The SDK path was abandoned after it was proved non-functional here: it accepted every
/// notification, returned a non-zero id and listed it via GetAllAsync, yet Windows never drew a
/// single one — no banner, no Action Center entry, no error. The same machine, same AUMID and
/// same XML display correctly through this API, so the failure was in the SDK layer rather than
/// in the payload or the app's identity.
/// </para>
/// <para>
/// This API also takes raw toast XML, which is what <see cref="ToastXmlWriter"/> already
/// produces — so the adaptive layout, progress and interactivity work carries over unchanged.
/// </para>
/// </summary>
public sealed class WindowsToastNotifier
{
    private readonly ILogger<WindowsToastNotifier> _log;

    public WindowsToastNotifier(ILogger<WindowsToastNotifier> log) => _log = log;

    private static ToastNotifier Notifier => ToastNotificationManager.CreateToastNotifier(AppIdentity.Aumid);

    /// <summary>Raises a toast. Returns false when Windows will not accept it.</summary>
    public bool Show(XDocument payload, ToastPayload options, NotificationData? initialData = null)
    {
        var xml = payload.ToString(SaveOptions.DisableFormatting);

        XmlDocument document;
        try
        {
            document = new XmlDocument();
            document.LoadXml(xml);
        }
        catch (Exception ex)
        {
            // Malformed XML would otherwise surface as a toast that simply never appears.
            _log.LogError(ex, "Windows rejected the generated payload. XML was: {Xml}", xml);
            return false;
        }

        var toast = new ToastNotification(document);

        if (!string.IsNullOrWhiteSpace(options.Tag)) toast.Tag = Sanitise(options.Tag);
        if (!string.IsNullOrWhiteSpace(options.Group)) toast.Group = Sanitise(options.Group);

        if (options.ExpiresIn is { } seconds && seconds > 0)
            toast.ExpirationTime = DateTimeOffset.Now.AddSeconds(seconds);

        toast.SuppressPopup = options.SuppressPopup;

        if (initialData is not null) toast.Data = initialData;

        try
        {
            Notifier.Show(toast);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Windows refused the notification.");
            return false;
        }

        // Reports whether the platform will actually display it — the check that was missing
        // when notifications were being accepted and silently discarded.
        if (Setting is { } setting && setting != NotificationSetting.Enabled)
        {
            _log.LogWarning(
                "The toast was accepted but Windows reports notifications are {Setting} for this app, " +
                "so it may not be displayed.", setting);
        }

        return true;
    }

    public NotificationUpdateResult Update(NotificationData data, string tag, string? group)
        => string.IsNullOrWhiteSpace(group)
            ? Notifier.Update(data, Sanitise(tag))
            : Notifier.Update(data, Sanitise(tag), Sanitise(group));

    public void Remove(string tag) => ToastNotificationManager.History.Remove(Sanitise(tag));

    public void Remove(string tag, string group)
        => ToastNotificationManager.History.Remove(Sanitise(tag), Sanitise(group));

    public void RemoveGroup(string group)
        => ToastNotificationManager.History.RemoveGroup(Sanitise(group));

    public void Clear() => ToastNotificationManager.History.Clear(AppIdentity.Aumid);

    public IReadOnlyList<ToastNotification> GetAll()
        => ToastNotificationManager.History.GetHistory(AppIdentity.Aumid);

    /// <summary>
    /// Reports whether Windows will display this app's notifications at all — the distinction
    /// between "accepted" and "shown" that cost this project a great deal of time.
    /// <para>
    /// Throws ERROR_NOT_FOUND when the shell has no record of the AUMID yet, which happens for a
    /// short window after the Start Menu shortcut is first written. That is a normal transient
    /// state, not a failure, so it is reported rather than thrown.
    /// </para>
    /// </summary>
    public NotificationSetting? Setting
    {
        get
        {
            try
            {
                return Notifier.Setting;
            }
            catch (Exception ex)
            {
                _log.LogDebug("Notification setting unavailable: {Message}", ex.Message);
                return null;
            }
        }
    }

    /// <summary>Tags and groups are capped at 64 characters and rejected outright if longer.</summary>
    private static string Sanitise(string value)
        => value.Length <= 64 ? value : value[..64];
}
