using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using HassToast.Agent.Config;
using HassToast.Agent.Toasts;
using HassToast.Agent.Transport;

namespace HassToast.Agent.Tray;

/// <summary>
/// Tray presence for the agent. Owns the message loop; the host runs alongside it.
/// The icon colour reflects connection state so a broken link is visible at a glance
/// rather than only in the log.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly IToastSource _source;
    private readonly Action _onExit;
    private readonly Func<Task> _sendTestToast;
    private readonly Dictionary<ConnectionState, Icon> _stateIcons = [];
    private readonly List<IntPtr> _iconHandles = [];
    private readonly Control _marshal = new();

    public TrayApplicationContext(IToastSource source, Action onExit, Func<Task> sendTestToast)
    {
        _source = source;
        _onExit = onExit;
        _sendTestToast = sendTestToast;

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };

        // CheckOnClick is off deliberately: the tick has to follow the registry, not the click.
        // Toggling it optimistically would leave it lying about the state if the write failed.
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Send test toast", null, async (_, _) => await SafeSendTestAsync());
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitAgent());

        // The setting can change behind our back — Task Manager's Startup tab writes the same
        // registration — so read it each time the menu is opened rather than only at startup.
        menu.Opening += (_, _) => RefreshStartupItem();

        _icon = new NotifyIcon
        {
            Icon = StateIcon(ConnectionState.Disconnected),
            Text = "HASS Windows Toast",
            ContextMenuStrip = menu,
            Visible = true,
        };

        // Force a handle on the UI thread purely to marshal callbacks onto it. NotifyIcon has
        // no handle of its own to invoke against, and the context menu's is not created until
        // the menu is first shown.
        _ = _marshal.Handle;

        _source.StateChanged += OnStateChanged;

        // Read the live detail rather than assuming none: the transport may already have
        // connected before this subscription was attached, in which case the transition
        // carrying the host name has already been and gone.
        Render(_source.State, _source.StateDetail);

        // A registration written by an earlier build points at wherever that build lived. Left
        // alone it silently starts the wrong binary, or nothing at all.
        try
        {
            if (StartupRegistration.EnsureCurrent())
                Serilog.Log.Information("Repointed the startup registration at {Path}.", Environment.ProcessPath);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not refresh the startup registration.");
        }

        RefreshStartupItem();
    }

    private void RefreshStartupItem()
    {
        try
        {
            _startupItem.Checked = StartupRegistration.IsEnabled;
            _startupItem.Enabled = true;
        }
        catch (Exception ex)
        {
            // Reading HKCU should not fail, but a menu item that cannot report the truth must
            // not offer to change it either.
            Serilog.Log.Warning(ex, "Could not read the startup registration.");
            _startupItem.Checked = false;
            _startupItem.Enabled = false;
        }
    }

    private void ToggleStartup()
    {
        var wanted = !_startupItem.Checked;

        try
        {
            if (wanted) StartupRegistration.Enable();
            else StartupRegistration.Disable();

            Serilog.Log.Information("Start with Windows {State}.", wanted ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Could not change the startup registration.");

            MessageBox.Show(
                $"Could not {(wanted ? "enable" : "disable")} starting with Windows.\n\n{Describe(ex)}",
                "HASS Windows Toast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Either way, show what the registry now says rather than what was asked for.
        RefreshStartupItem();
    }

    private void OnStateChanged(ConnectionState state, string? detail)
    {
        // StateChanged fires from the transport's thread.
        if (_marshal.IsDisposed) return;

        try
        {
            if (_marshal.InvokeRequired)
                _marshal.BeginInvoke(() => Render(state, detail));
            else
                Render(state, detail);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down between the check and the invoke.
        }
    }

    private void Render(ConnectionState state, string? detail)
    {
        var label = state switch
        {
            ConnectionState.Connected => $"Connected to {detail}",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Authenticating => "Authenticating…",
            ConnectionState.Unauthorized => "Access token rejected",
            _ => detail is null ? "Disconnected" : $"Disconnected - {detail}",
        };

        _statusItem.Text = label;
        _icon.Icon = StateIcon(state);

        // The tray tooltip is capped at 63 characters; anything longer is silently dropped.
        var tip = $"HASS Windows Toast - {label}";
        _icon.Text = tip.Length <= 63 ? tip : tip[..60] + "…";
    }

    /// <summary>
    /// The product mark with a connection-state badge burned into its corner.
    /// <para>
    /// The badge is composited here rather than shipped as four finished icons because the state
    /// is the part worth seeing at a glance, and four near-identical .ico files would be four
    /// chances for the mark to drift from itself.
    /// </para>
    /// </summary>
    private Icon StateIcon(ConnectionState state)
    {
        // Cached per state. Render churn on a flapping connection would otherwise allocate a
        // fresh icon on every transition, and the HICON behind Icon.FromHandle is not owned by
        // the Icon, so neither the handle nor the managed wrapper would ever come back.
        if (_stateIcons.TryGetValue(state, out var cached)) return cached;

        var colour = state switch
        {
            ConnectionState.Connected => Color.FromArgb(0x3F, 0xB9, 0x50),
            ConnectionState.Connecting or ConnectionState.Authenticating => Color.FromArgb(0xE0, 0xA3, 0x0C),
            ConnectionState.Unauthorized => Color.FromArgb(0xD1, 0x34, 0x38),
            _ => Color.FromArgb(0x8A, 0x8A, 0x8A),
        };

        // 32 px covers the tray up to 200% scaling; Windows downsamples for the smaller cases.
        const int size = 32;

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var mark = BrandAssets.LoadIcon(size))
            {
                if (mark is not null)
                {
                    g.DrawIcon(mark, new Rectangle(0, 0, size, size));
                }
                else
                {
                    // Nothing embedded to draw. A blank tray slot would look like a crash, so
                    // fall back to a plain slab and let the badge carry the meaning.
                    using var body = new SolidBrush(Color.FromArgb(0xF2, 0xF2, 0xF2));
                    g.FillRectangle(body, 4, 9, 24, 15);
                }
            }

            // Ringed in white so the badge stays legible against the mark's blue, which is close
            // enough to some of the state colours to swallow them.
            //
            // 13 of 32 is the size this was settled at by looking: smaller and the dot stops
            // reading once the tray scales it down to 16, larger and it covers enough of the
            // mark that the icon stops being recognisable as this product.
            const int badge = 13;
            var origin = size - badge;

            using var ring = new SolidBrush(Color.FromArgb(0xFA, 0xFA, 0xFA));
            g.FillEllipse(ring, origin, origin, badge, badge);

            using var dot = new SolidBrush(colour);
            g.FillEllipse(dot, origin + 2, origin + 2, badge - 4, badge - 4);
        }

        var handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);
        _stateIcons[state] = icon;
        _iconHandles.Add(handle);
        return icon;
    }

    private async Task SafeSendTestAsync()
    {
        try
        {
            await _sendTestToast();
        }
        catch (Exception ex)
        {
            // Always log the whole exception: the dialog only ever gets a summary, and an
            // exception with an empty Message would otherwise produce a dialog saying nothing
            // at all.
            Serilog.Log.Error(ex, "Tray test toast failed.");

            MessageBox.Show(
                $"Could not send the test toast.\n\n{Describe(ex)}\n\n" +
                "The full error is in the log - use 'Open log folder'.",
                "HASS Windows Toast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// A description that is never empty. Some exceptions — COM ones especially — carry a blank
    /// Message, and reporting only that leaves nothing to act on.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();

        for (var current = ex; current is not null; current = current.InnerException)
        {
            parts.Add(string.IsNullOrWhiteSpace(current.Message)
                ? current.GetType().Name
                : $"{current.GetType().Name}: {current.Message}");
        }

        return string.Join("\n  caused by ", parts);
    }

    private static void OpenLogFolder()
    {
        var dir = AgentConfig.LogDirectory;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Shuts the agent down as if Exit had been chosen from the menu, from any thread.
    /// <para>
    /// The quit signal an uninstaller sets arrives on a thread-pool thread, and every step of
    /// <see cref="ExitAgent"/> — hiding the icon, ending the message loop — belongs to the UI
    /// thread. Marshalling here rather than at the call site keeps that requirement with the
    /// code that has it.
    /// </para>
    /// </summary>
    public void RequestExit()
    {
        if (_marshal.IsDisposed) return;

        try
        {
            if (_marshal.InvokeRequired) _marshal.BeginInvoke(() => ExitAgent());
            else ExitAgent();
        }
        catch (ObjectDisposedException)
        {
            // Already shutting down by some other route. Nothing left to ask for.
        }
        catch (InvalidOperationException)
        {
            // The handle went away between the check and the call — same conclusion.
        }
    }

    private void ExitAgent()
    {
        _icon.Visible = false;
        _onExit();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.StateChanged -= OnStateChanged;
            _icon.Visible = false;
            _icon.Dispose();
            _marshal.Dispose();

            // Icon.FromHandle does not take ownership, so the HICON has to be destroyed
            // explicitly — disposing the wrapper alone leaks the GDI object.
            foreach (var icon in _stateIcons.Values) icon.Dispose();
            _stateIcons.Clear();
            foreach (var handle in _iconHandles) DestroyIcon(handle);
            _iconHandles.Clear();
        }
        base.Dispose(disposing);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
