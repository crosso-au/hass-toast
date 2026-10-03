using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HassToast.Agent.Security;
using HassToast.Agent.Toasts;

namespace HassToast.Agent.Updates;

/// <summary>
/// Checks GitHub for a newer release, offers it with a toast, and hands a verified installer to
/// the user when they choose to update.
/// <para>
/// Automatic checks run a few minutes after start, then once a day while the agent runs, and stay
/// silent when they fail. A check from the tray always answers. Updating downloads the installer,
/// refuses it unless its signature verifies, and starts it on its update page; the installer then
/// stops this agent, replaces it and starts the new one.
/// </para>
/// </summary>
public sealed class UpdateService : BackgroundService
{
    /// <summary>
    /// Toasts the agent raises about itself carry this group. Their clicks are handled here and
    /// never reported to Home Assistant, which is also why payloads may not use it.
    /// </summary>
    public const string ToastGroup = "hass-toast-agent";

    private const string OfferTagPrefix = "update-";
    private const string StatusTag = "update-status";

    internal const string ActionKey = "action";
    internal const string VersionKey = "version";
    internal const string UpdateAction = "update";
    internal const string LaterAction = "later";
    internal const string SkipAction = "skip";

    /// <summary>Banner on the update toast. Kept under the 200 KB Windows allows for a hero image.</summary>
    private const string HeroImage =
        "https://raw.githubusercontent.com/crosso-au/hass-toast/refs/heads/main/samples/images/hass-toast-logo-spin.gif";

    /// <summary>Kept clear of sign-in, when everything else is starting too.</summary>
    private static readonly TimeSpan FirstCheckMin = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FirstCheckMax = TimeSpan.FromMinutes(8);

    /// <summary>How often to see whether a check is due. Cheap: no network unless one is.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    private readonly ReleaseFeed _feed;
    private readonly WindowsToastNotifier _notifier;
    private readonly ImageResolver _images;
    private readonly ILogger<UpdateService> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Lock _stateGate = new();

    public UpdateService(
        ReleaseFeed feed, WindowsToastNotifier notifier, ImageResolver images, ILogger<UpdateService> log)
    {
        _feed = feed;
        _notifier = notifier;
        _images = images;
        _log = log;
    }

    private static Version Current => ReleaseFeed.CurrentVersion;

    /// <summary>The tray's "Check for updates automatically".</summary>
    public bool CheckAutomatically
    {
        get => UpdateState.Load().CheckAutomatically;
        set
        {
            Change(state => state.CheckAutomatically = value);
            _log.LogInformation("Automatic update checks {State}.", value ? "turned on" : "turned off");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TidyUp();

        try
        {
            var spread = (FirstCheckMax - FirstCheckMin).TotalSeconds;
            await Task.Delay(FirstCheckMin + TimeSpan.FromSeconds(Random.Shared.NextDouble() * spread), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (UpdateState.Load().IsCheckDue(DateTimeOffset.UtcNow))
                    await CheckAsync(manual: false, stoppingToken);

                await Task.Delay(Tick, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>The tray's "Check for updates". Always ends in a toast saying what it found.</summary>
    public Task CheckNowAsync() => CheckAsync(manual: true, CancellationToken.None);

    private async Task CheckAsync(bool manual, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct))
        {
            _log.LogInformation("An update check or download is already under way.");
            return;
        }

        try
        {
            _log.LogInformation("Checking for updates ({Kind}).", manual ? "requested" : "automatic");

            AvailableRelease? latest;
            try
            {
                latest = await _feed.GetLatestAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (ct.IsCancellationRequested) throw;

                _log.LogWarning(ex, "Could not check for updates.");
                if (manual)
                    ShowStatus("Couldn't check for updates", "Check your internet connection and try again later.");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var offer = false;

            Change(state =>
            {
                state.LastCheckUtc = now;
                offer = latest is not null && state.ShouldOffer(latest, Current, now, manual);

                // A "remind me tomorrow" that has come due is used up by this check.
                if (state.RemindAfterUtc is { } remind && now >= remind) state.RemindAfterUtc = null;
            });

            if (latest is null || latest.Version <= Current)
            {
                _log.LogInformation("Up to date ({Version}; latest release {Latest}).",
                    Current.ToString(3), latest?.VersionText ?? "none usable");
                if (manual)
                    ShowStatus("You're up to date", $"HASS Windows Toast {Current.ToString(3)} is the latest version.");
                return;
            }

            if (!offer)
            {
                _log.LogInformation("Version {Version} is available but skipped or snoozed.", latest.VersionText);
                return;
            }

            _log.LogInformation("Version {Version} is available; offering it.", latest.VersionText);
            await ShowOfferAsync(latest, ct);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// A click on one of this service's toasts. The arguments have been through the OS and are
    /// treated as untrusted: they choose between three actions and name a version to skip,
    /// and nothing else. What gets installed is always re-read from GitHub and must verify.
    /// </summary>
    public void HandleActivation(IReadOnlyDictionary<string, string> arguments)
    {
        arguments.TryGetValue(ActionKey, out var action);

        switch (action)
        {
            case UpdateAction:
                _ = Task.Run(InstallAsync);
                break;

            case LaterAction:
                Change(state => state.RemindAfterUtc = DateTimeOffset.UtcNow + UpdateState.CheckInterval);
                _log.LogInformation("Update reminder snoozed for a day.");
                break;

            case SkipAction:
                if (arguments.TryGetValue(VersionKey, out var text) && ReleaseFeed.ParseVersion(text) is { } version)
                {
                    Change(state => state.SkippedVersion = version.ToString(3));
                    _log.LogInformation("Version {Version} skipped.", version.ToString(3));
                }
                break;

            default:
                // The toast body opens the release notes by itself; nothing reaches here for it.
                break;
        }
    }

    private async Task InstallAsync()
    {
        if (!await _busy.WaitAsync(0))
        {
            _log.LogInformation("An update check or download is already under way.");
            return;
        }

        try
        {
            AvailableRelease? latest;
            try
            {
                latest = await _feed.GetLatestAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _log.LogWarning(ex, "Could not reach GitHub to update.");
                ShowStatus("Couldn't download the update", "Check your internet connection and try again later from the tray menu.");
                return;
            }

            if (latest is null || latest.Version <= Current)
            {
                RemoveOffers();
                ShowStatus("You're up to date", $"HASS Windows Toast {Current.ToString(3)} is the latest version.");
                return;
            }

            RemoveOffers();
            ShowStatus("Downloading the update", $"HASS Windows Toast {latest.VersionText} will be ready to install in a moment.");
            _log.LogInformation("Downloading {Installer}.", latest.InstallerName);

            string installer;
            try
            {
                installer = await _feed.DownloadVerifiedAsync(latest, UpdateState.DownloadDirectory, CancellationToken.None);
            }
            catch (UpdateRejectedException ex)
            {
                _log.LogError("Update refused: {Reason}", ex.Message);
                ShowStatus("The update was not installed", "The download could not be verified as a genuine HASS Windows Toast release.");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not download the update.");
                ShowStatus("Couldn't download the update", "Try again later from the tray menu.");
                return;
            }

            _log.LogInformation("Signature verified. Starting the installer for {Version}.", latest.VersionText);
            RemoveStatus();

            // The installer stops this agent itself, through the same quit signal the tray uses.
            Process.Start(new ProcessStartInfo(installer)
            {
                ArgumentList = { "--update" },
                WorkingDirectory = Path.GetDirectoryName(installer)!,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The update failed.");
            ShowStatus("The update was not installed", "Try again later from the tray menu.");
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task ShowOfferAsync(AvailableRelease release, CancellationToken ct)
    {
        var hero = new ToastImage { Src = HeroImage, Alt = "HASS Windows Toast" };
        var images = new ResolvedImages();

        // Fetched through the same cache and limits as any other toast image. Without it the
        // toast still says everything it needs to.
        var path = await _images.ResolveAsync(hero.Src, ImageRole.Hero, ct);
        if (path is not null) images.Add(hero, path);

        var payload = new ToastPayload
        {
            Tag = OfferTagPrefix + release.VersionText,
            Group = ToastGroup,
            Visual =
            {
                Hero = path is null ? null : hero,
                Text =
                [
                    $"HASS Windows Toast {release.VersionText} is available",
                    $"You have {Current.ToString(3)}. Updating keeps your settings. Click here to see what's new.",
                ],
            },
            // The release page, already checked to be this repository's.
            Launch = new ToastLaunch { Type = "protocol", Uri = release.PageUrl },
            Buttons =
            [
                Button("Update now", UpdateAction, release, style: "Success"),
                Button("Remind me tomorrow", LaterAction, release),
                Button("Skip this version", SkipAction, release),
            ],
        };

        // A replacement under the same tag would update silently, with no banner.
        RemoveOffers();
        Show(payload, images);
    }

    private static ToastButton Button(string content, string action, AvailableRelease release, string? style = null) => new()
    {
        Content = content,
        Style = style,
        Args = new Dictionary<string, string>
        {
            [ActionKey] = action,
            [VersionKey] = release.VersionText,
        },
    };

    private void ShowStatus(string title, string text)
    {
        RemoveStatus();
        Show(new ToastPayload
        {
            Tag = StatusTag,
            Group = ToastGroup,
            ExpiresIn = 3600,
            Visual = { Text = [title, text] },
        });
    }

    private void Show(ToastPayload payload, ResolvedImages? images = null)
    {
        try
        {
            if (!_notifier.Show(ToastXmlWriter.Build(payload, images ?? new ResolvedImages()), payload))
                _log.LogWarning("Windows refused the update toast '{Tag}'.", payload.Tag);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not show the update toast '{Tag}'.", payload.Tag);
        }
    }

    private void RemoveStatus() => Remove(n => n.Tag == StatusTag);

    private void RemoveOffers() => Remove(n => n.Tag.StartsWith(OfferTagPrefix, StringComparison.Ordinal) && n.Tag != StatusTag);

    private void Remove(Func<Windows.UI.Notifications.ToastNotification, bool> which)
    {
        try
        {
            foreach (var toast in _notifier.GetAll().Where(n => n.Group == ToastGroup && which(n)).ToList())
                _notifier.Remove(toast.Tag, ToastGroup);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not tidy update toasts.");
        }
    }

    /// <summary>
    /// After an update: the offer for the version now running, the "downloading" toast and the
    /// installer that did it are all leftovers. An offer for a version still newer is kept.
    /// </summary>
    private void TidyUp()
    {
        Remove(n => n.Tag == StatusTag
                    || (n.Tag.StartsWith(OfferTagPrefix, StringComparison.Ordinal)
                        && ReleaseFeed.ParseVersion(n.Tag[OfferTagPrefix.Length..]) is { } v
                        && v <= Current));

        try
        {
            if (Directory.Exists(UpdateState.DownloadDirectory))
                Directory.Delete(UpdateState.DownloadDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use, perhaps by the installer that started this agent. Next start.
            _log.LogDebug(ex, "Could not remove old update downloads.");
        }
    }

    /// <summary>Read, change and save the state under one lock, so tray and toast clicks cannot interleave.</summary>
    private void Change(Action<UpdateState> change)
    {
        lock (_stateGate)
        {
            var state = UpdateState.Load();
            change(state);
            try
            {
                state.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not save the update settings.");
            }
        }
    }
}
