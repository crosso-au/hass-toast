using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using HassToast.Agent.Activation;
using HassToast.Agent.Config;
using HassToast.Agent.Security;
using HassToast.Agent.Toasts;
using HassToast.Agent.Transport;
using HassToast.Agent.Tray;

namespace HassToast.Agent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (HasFlag(args, "--help", "-h", "/?"))
        {
            PrintUsage();
            return 0;
        }

        if (HasFlag(args, "--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");
            return 0;
        }

        if (HasFlag(args, "--setup"))
        {
            return RunSetup();
        }

        if (HasFlag(args, "--rotate-key"))
        {
            return RotateSigningKey();
        }

        if (HasFlag(args, "--show-key"))
        {
            return ShowKey();
        }

        if (HasFlag(args, "--test-toast"))
        {
            return TestToast();
        }

        var selftestIndex = Array.FindIndex(args, a =>
            a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (selftestIndex >= 0)
        {
            // An optional payload file lets a real, interactive toast go through the full Home
            // Assistant path, so the running agent is the one that raises it.
            var payloadFile = selftestIndex + 1 < args.Length && !args[selftestIndex + 1].StartsWith('-')
                ? args[selftestIndex + 1]
                : null;
            return SelfTest(payloadFile);
        }

        var sampleIndex = Array.FindIndex(args, a =>
            a.Equals("--sample", StringComparison.OrdinalIgnoreCase));
        if (sampleIndex >= 0)
        {
            if (sampleIndex + 1 >= args.Length)
            {
                Console.WriteLine("--sample needs a path to a JSON payload file.");
                return 1;
            }
            return RenderSample(args[sampleIndex + 1]);
        }

        if (HasFlag(args, "--check-registration"))
        {
            return CheckRegistration(repair: HasFlag(args, "--repair"));
        }

        if (HasFlag(args, "--list-toasts"))
        {
            AppIdentity.Register();
            var all = Notifier().GetAll();
            Console.WriteLine($"{all.Count} notification(s) in the Action Center for this app:");
            foreach (var n in all)
                Console.WriteLine($"  tag='{n.Tag}' group='{n.Group}'");
            return 0;
        }

        if (HasFlag(args, "--clear-toasts"))
        {
            AppIdentity.Register();
            Notifier().Clear();
            Console.WriteLine("Removed this app's notifications from the Action Center.");
            return 0;
        }

        if (HasFlag(args, "--reset"))
        {
            return Reset();
        }

        if (HasFlag(args, "--quit"))
        {
            return Quit();
        }

        _bypassSingleInstance = HasFlag(args, "--no-single-instance");

        return RunAgent();
    }

    private static bool HasFlag(string[] args, params string[] names)
        => args.Any(a => names.Contains(a, StringComparer.OrdinalIgnoreCase));

    /// <summary>A notifier for the short-lived diagnostic commands, which have no host or DI.</summary>
    private static WindowsToastNotifier Notifier()
        => new(Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsToastNotifier>.Instance);

    /// <summary>Whether another agent process is running. See <see cref="AgentInstance"/>.</summary>
    private static bool IsAgentRunning() => AgentInstance.IsRunning();

    /// <summary>
    /// Asks a running agent to shut down cleanly, and waits for it to go.
    /// <para>
    /// Exists for the uninstaller. Killing the process would also work, but it skips the host's
    /// graceful stop and the COM revoke, and leaves the tray icon ghosted in the notification
    /// area until the shell next repaints — which looks exactly like an uninstall that failed.
    /// </para>
    /// </summary>
    private static int Quit()
    {
        if (!AgentInstance.IsRunning())
        {
            Console.WriteLine("No agent is running.");
            return 0;
        }

        var timeout = TimeSpan.FromSeconds(15);

        if (AgentInstance.RequestQuit(timeout))
        {
            Console.WriteLine("The agent has stopped.");
            return 0;
        }

        Console.WriteLine($"The agent was still running after {timeout.TotalSeconds:0} seconds.");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            HASS Windows Toast agent - renders Home Assistant notifications as Windows toasts.

              (no arguments)   Run the agent in the tray.

            Configuration
              --setup          Configure the connection. Safe to re-run: existing values are
                               offered as defaults and the signing key is kept unless you
                               explicitly choose to rotate it.
              --show-key       Print the signing key for the Home Assistant integration.
                               Writes a secret to stdout - run it only in your own terminal.
              --rotate-key     Replace the signing key, keeping the access token. Prints only
                               a fingerprint, never the key itself.
              --reset          Delete the stored configuration and credentials. Logs are kept.
              --quit           Ask a running agent to shut down cleanly, and wait for it to go.

            Testing toasts
              --sample <file>  Render a payload straight from a JSON file, with no Home
                               Assistant involved. See docs/test-payloads/.
              --selftest [f]   Sign an envelope and fire it through Home Assistant, so a running
                               agent receives it exactly as it would a real notification. The
                               only check that catches the two sides disagreeing about signing.
              --test-toast     Raise a toast locally and report what Windows did with it.
              --list-toasts    Show outstanding notifications by tag and group. Read-only.
              --clear-toasts   Remove this app's notifications from the Action Center.

            Diagnosing
              --check-registration   Report the Start Menu shortcut, AUMID and COM activator.
                --repair             ...and rebuild them. Stop the agent first.
              --version        Print the assembly version.
              --help           Show this help.

            Configuration lives in %LOCALAPPDATA%\HassToast. The bus event type is not asked
            for during setup - both halves default to the same name - but it can be edited
            there as `eventType` if it ever needs to be something else.
            """);
    }

    /// <summary>
    /// Starts file logging. Called before anything else in <see cref="RunAgent"/> so that a
    /// process cold-launched to deliver a toast click leaves a trace even though it redirects
    /// and exits within milliseconds — without this, "nothing happened" is indistinguishable
    /// from "Windows never launched anything".
    /// </summary>
    private static void ConfigureLogging()
    {
        var logDirectory = AgentConfig.LogDirectory;
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDirectory, "agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                // Several processes write here at once during an activation redirect.
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({ProcessId}) {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .CreateLogger();
    }

    private static int RunAgent()
    {
        // Tray mode runs in the background; the inherited console window would just be clutter.
        ConsoleSupport.HideWindow();

        ConfigureLogging();

        // The command line Windows used tells us how this process was started — a cold launch
        // for a toast click looks different from a user double-clicking the exe.
        Log.Information("Process {Pid} starting. Command line: {CommandLine}",
            Environment.ProcessId, Environment.CommandLine);

        // Single-instancing keeps a second agent from starting and carries nothing else. Toast
        // clicks do not travel through it: they arrive via the COM activator, which delivers them
        // to whichever process holds the registered class object. See AgentInstance.TryAcquire
        // for why a mutex is sound here even though it once was not.
        AgentInstance? instance = null;

        if (_bypassSingleInstance)
        {
            Log.Warning("Single-instancing bypassed.");
        }
        else
        {
            instance = AgentInstance.TryAcquire();

            if (instance is null)
            {
                Log.Information("An agent is already running; this instance is stepping aside.");
                Log.CloseAndFlush();
                return 0;
            }
        }

        using var single = instance;

        AgentConfig config;
        try
        {
            config = AgentConfig.Load();
            config.Validate();

            var probe = new SecretStore();
            if (!probe.Exists)
                throw new InvalidOperationException("No credentials are stored yet.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"""
                 HASS Windows Toast is not configured yet.

                 {ex.Message}

                 Run the agent once from a terminal with --setup to configure it.
                 """,
                "HASS Windows Toast", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        try
        {
            ApplicationConfiguration.Initialize();

            // Creates the Start Menu shortcut (carrying the AUMID and activator CLSID) and the
            // LocalServer32 entry. Without the shortcut Windows accepts toasts and never draws
            // them; without the activator, clicks reach nothing.
            AppIdentity.Register();

            _activationCancellation = new CancellationTokenSource();

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSerilog();
            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton<SecretStore>(_ => new SecretStore());
            builder.Services.AddSingleton<IToastSource, HaWebSocketClient>();
            RegisterToastPipeline(builder.Services);
            builder.Services.AddSingleton<ActivationRouter>();
            builder.Services.AddHostedService<AgentWorker>();

            using var host = builder.Build();

            // Publish the router before the host starts, so a toast clicked during startup has
            // somewhere to go.
            _activationRouter = host.Services.GetRequiredService<ActivationRouter>();

            // COM constructs the activator itself, so its dependencies are handed over statically.
            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            NotificationActivator.Log = loggerFactory.CreateLogger("Activation");
            NotificationActivator.Handler = activation =>
                _activationRouter.HandleAsync(activation, _activationCancellation?.Token ?? default);

            ComServer.StartServer(loggerFactory.CreateLogger("ComServer"));

            // Build the tray before starting the host so it is subscribed before the transport
            // can connect. Connecting to a local Home Assistant takes tens of milliseconds, so
            // the opposite order loses the transition that carries the host name.
            var source = host.Services.GetRequiredService<IToastSource>();
            using var tray = new TrayApplicationContext(
                source,
                onExit: () => host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(),
                sendTestToast: SendTestToastAsync);

            // Lets the uninstaller ask for exactly the shutdown the tray's Exit item performs.
            // A bypassed instance has no handle to listen on, which is right: it is not the agent
            // anyone means when they ask for the agent to stop.
            single?.ListenForQuit(tray.RequestExit);

            host.Start();

            Log.Information("Agent started. Device '{DeviceId}'.", config.DeviceId);

            Application.Run(tray);

            ComServer.StopServer();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Agent terminated unexpectedly.");
            MessageBox.Show($"HASS Windows Toast stopped unexpectedly:\n\n{ex.Message}",
                "HASS Windows Toast", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Registers the verify → policy → compose → show chain. Shared by the agent host and the
    /// --sample harness so the harness exercises the real path rather than a parallel one.
    /// </summary>
    private static void RegisterToastPipeline(IServiceCollection services)
    {
        services.AddSingleton(sp => new ContentPolicy(
            sp.GetRequiredService<AgentConfig>().Security));

        services.AddSingleton<ImageResolver>();
        services.AddSingleton<ToastRegistry>();
        services.AddSingleton<WindowsToastNotifier>();
        services.AddSingleton<ToastComposer>();

        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<SecretStore>();
            // Read the key lazily and hold it, rather than keeping the whole secret record
            // alive or hitting DPAPI on every inbound envelope.
            var cached = new Lazy<byte[]>(() => store.Load().SigningKey);
            return new PayloadVerifier(sp.GetRequiredService<AgentConfig>(), () => cached.Value);
        });

        services.AddSingleton<NotificationPipeline>();
    }

    private static ActivationRouter? _activationRouter;
    private static CancellationTokenSource? _activationCancellation;

    /// <summary>Diagnostic switch: skip single-instancing entirely.</summary>
    private static bool _bypassSingleInstance;


    /// <summary>
    /// The tray's "Send test toast" item. Carries a button so the activation path can be
    /// exercised from the tray without Home Assistant being involved.
    /// </summary>
    private static Task SendTestToastAsync()
    {
        var notifier = Notifier();

        var payload = new ToastPayload
        {
            Tag = "tray-test",
            Visual =
            {
                Text = ["HASS Windows Toast", "The agent is running and can raise toasts on this desktop."],
                Attribution = "Tray test",
            },
            Buttons =
            [
                new ToastButton
                {
                    Content = "Acknowledge",
                    Args = new Dictionary<string, string> { ["action"] = "tray_test_ack" },
                },
            ],
        };

        // A stale notification with the same tag would be replaced silently, with no banner.
        // Failing to remove it is not a reason to abandon the test.
        try
        {
            notifier.Remove(payload.Tag);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not remove the previous tray test toast; continuing.");
        }

        var shown = notifier.Show(ToastXmlWriter.Build(payload, new ResolvedImages()), payload);
        var setting = notifier.Setting;

        Log.Information("Tray test toast {Outcome} (display setting {Setting}, AUMID {Aumid}).",
            shown ? "handed to Windows" : "refused",
            setting?.ToString() ?? "unknown",
            AppIdentity.Aumid);

        if (!shown)
        {
            throw new InvalidOperationException(
                $"Windows refused the notification (display setting: {setting?.ToString() ?? "unknown"}). " +
                "Run --check-registration to see why.");
        }

        return Task.CompletedTask;
    }

    private static int RunSetup()
    {
        Console.WriteLine("HASS Windows Toast setup");
        Console.WriteLine("========================");
        Console.WriteLine();

        var existing = AgentConfig.Load();
        var store = new SecretStore();

        // Load what is already stored so the run can reuse rather than replace it.
        AgentSecrets? current = null;
        if (store.Exists)
        {
            try
            {
                current = store.Load();
                Console.WriteLine("Existing configuration found. Press Enter at any prompt to keep the current value.");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Stored credentials could not be read ({ex.Message}).");
                Console.WriteLine("They will be replaced.");
                Console.WriteLine();
            }
        }

        var url = ConsoleSupport.Prompt(
            "Home Assistant URL (e.g. https://ha.local:8123)",
            string.IsNullOrWhiteSpace(existing.HomeAssistant.Url) ? null : existing.HomeAssistant.Url);
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("A URL is required. Nothing was saved.");
            return 1;
        }

        var deviceId = ConsoleSupport.Prompt("Device id for this machine", existing.DeviceId);

        Console.WriteLine();
        Console.WriteLine("The display name appears in the toast header and in Windows notification");
        Console.WriteLine("settings. Windows takes it from the Start Menu shortcut, so changing it");
        Console.WriteLine("renames that shortcut.");
        var displayName = ConsoleSupport.Prompt("Display name", existing.DisplayName);

        // Keep the stored token unless a new one is offered, so changing only the URL or
        // device id does not force the user to dig out their token again.
        string token;
        Console.WriteLine();
        if (current is not null)
        {
            var entered = ConsoleSupport.ReadSecret(
                "Access token - press Enter to keep the stored one, or paste a new one: ");
            token = string.IsNullOrWhiteSpace(entered) ? current.AccessToken : entered.Trim();
        }
        else
        {
            Console.WriteLine("Create a long-lived access token in Home Assistant under");
            Console.WriteLine("  Profile -> Security -> Long-lived access tokens.");
            var entered = ConsoleSupport.ReadSecret("Paste the token (input hidden): ");
            if (string.IsNullOrWhiteSpace(entered))
            {
                Console.WriteLine("A token is required. Nothing was saved.");
                return 1;
            }
            token = entered.Trim();
        }

        // Rotating the signing key invalidates the Home Assistant side, so it must be a
        // deliberate choice rather than a side effect of re-running setup.
        byte[] signingKey;
        var rotated = false;
        if (current is not null)
        {
            var answer = ConsoleSupport.Prompt(
                "Rotate the signing key? Home Assistant stops accepting toasts until you re-paste it (y/N)", "N");
            if (answer.StartsWith('y') || answer.StartsWith('Y'))
            {
                signingKey = SecretStore.GenerateSigningKey();
                rotated = true;
            }
            else
            {
                signingKey = current.SigningKey;
            }
        }
        else
        {
            signingKey = SecretStore.GenerateSigningKey();
            rotated = true;
        }

        var config = existing;
        config.HomeAssistant.Url = url;
        config.DeviceId = deviceId;

        // Not prompted for. Both halves default to the same constant, so the only way they could
        // ever disagree was a person typing it into two places. It stays editable in config.json
        // for the rare case that the default bus event name collides with something else.
        if (string.IsNullOrWhiteSpace(config.EventType))
            config.EventType = AgentConfig.DefaultEventType;
        config.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? AppIdentity.DefaultDisplayName
            : displayName.Trim();

        try
        {
            config.Validate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Configuration is not valid: {ex.Message}");
            return 1;
        }

        store.Save(new AgentSecrets(token, signingKey));
        config.Save();

        // Rewrite the shortcut now rather than at next launch, so the new name is in place
        // before the next toast and any shortcut under the old name is cleaned up.
        AppIdentity.DisplayName = config.DisplayName;
        var shortcut = AppIdentity.EnsureShortcut();

        Console.WriteLine();
        Console.WriteLine($"Saved configuration to {AgentConfig.DefaultPath}");
        Console.WriteLine($"Start Menu shortcut    {shortcut ?? "could not be created"}");
        Console.WriteLine($"Saved credentials to   {store.FilePath} (encrypted for {Environment.UserName})");
        Console.WriteLine();
        Console.WriteLine(rotated
            ? "Signing key (NEW - update the hass_toast integration in Home Assistant):"
            : "Signing key (unchanged):");
        Console.WriteLine();
        Console.WriteLine($"    {Convert.ToBase64String(signingKey)}");
        Console.WriteLine();
        Console.WriteLine($"    device id: {config.DeviceId}");
        Console.WriteLine($"    websocket: {config.HomeAssistant.WebSocketUri()}");
        Console.WriteLine();
        Console.WriteLine("Restart the agent for the new settings to take effect.");
        Console.WriteLine();

        return 0;
    }

    /// <summary>
    /// Fires a correctly signed envelope at Home Assistant's event bus over the REST API, so the
    /// running agent receives it the same way a real notification would arrive. This exercises the
    /// whole chain — signing, transport, verification, policy, composition — rather than any
    /// single piece, and is the only check that can catch the two sides disagreeing.
    /// </summary>
    private static int SelfTest(string? payloadFile)
    {
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
            Console.WriteLine($"Not configured: {ex.Message}");
            return 1;
        }

        string payload;
        if (payloadFile is not null)
        {
            if (!File.Exists(payloadFile))
            {
                Console.WriteLine($"No such file: {payloadFile}");
                return 1;
            }

            // Reserialise rather than forwarding the file bytes: the exact string is what gets
            // signed, and a stray byte-order mark or trailing newline would change it.
            try
            {
                var parsed = JsonSerializer.Deserialize<ToastPayload>(
                    File.ReadAllText(payloadFile), ToastJson.Options);
                payload = JsonSerializer.Serialize(parsed, ToastJson.Options);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"{payloadFile} is not a valid payload: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"Sending {Path.GetFileName(payloadFile)} through Home Assistant.");
        }
        else
        {
            // Serialised rather than hand-written: the exact bytes are what gets signed, so they
            // must be produced the same way every time.
            payload = JsonSerializer.Serialize(new
            {
                visual = new
                {
                    text = new[]
                    {
                        "HASS Windows Toast self test",
                        $"Signed envelope delivered through Home Assistant at {DateTime.Now:HH:mm:ss}",
                    },
                    attribution = "--selftest",
                },
            });
        }

        var nonce = Guid.NewGuid().ToString("N");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signingInput = PayloadSigner.BuildSigningInput(
            PayloadVerifier.SupportedVersion, config.DeviceId, ToastOp.Send, nonce, ts, payload);
        var signature = Convert.ToBase64String(
            PayloadSigner.ComputeSignature(secrets.SigningKey, signingInput));

        var envelope = JsonSerializer.Serialize(new
        {
            v = PayloadVerifier.SupportedVersion,
            device_id = config.DeviceId,
            op = ToastOp.Send,
            nonce,
            ts,
            payload,
            sig = signature,
        });

        // Firing into the bus with nothing subscribed looks identical to a broken pipeline, so
        // say plainly that there is no listener rather than leaving it to be inferred.
        if (!IsAgentRunning())
        {
            Console.WriteLine("The agent is not running, so nothing is subscribed to receive this.");
            Console.WriteLine("Start it first (run the exe with no arguments), then try again.");
            return 1;
        }

        // Note where the log currently ends so only this run's lines are reported back.
        var logOffset = CurrentLogLength();

        var endpoint = new Uri(config.HomeAssistant.RestBaseUri(), $"/api/events/{config.EventType}");
        Console.WriteLine($"Firing '{config.EventType}' at {endpoint}");
        Console.WriteLine($"  device : {config.DeviceId}");
        Console.WriteLine($"  nonce  : {nonce}");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secrets.AccessToken);

            using var content = new StringContent(envelope, Encoding.UTF8, "application/json");
            using var response = http.PostAsync(endpoint, content).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Home Assistant returned {(int)response.StatusCode}: {body}");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("Event accepted by Home Assistant. Watching the agent log...");
            Console.WriteLine();

            // Report what the agent actually did with it. Without this the command can only
            // claim the event was sent, which is the least interesting half of the round trip.
            var reaction = WaitForAgentReaction(logOffset, TimeSpan.FromSeconds(8));
            if (reaction.Count == 0)
            {
                Console.WriteLine("The agent logged nothing. It may have lost its connection to");
                Console.WriteLine("Home Assistant - check the tray icon.");
                return 1;
            }

            foreach (var line in reaction) Console.WriteLine($"  {line}");

            var raised = reaction.Any(l => l.Contains("Raised toast", StringComparison.Ordinal));
            Console.WriteLine();
            Console.WriteLine(raised
                ? "Round trip complete: signed, delivered, verified and rendered."
                : "The agent received it but did not raise a toast - see the reason above.");
            return raised ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not reach Home Assistant: {ex.Message}");
            return 1;
        }
    }

    private static FileInfo? CurrentLogFile()
    {
        var directory = new DirectoryInfo(AgentConfig.LogDirectory);
        if (!directory.Exists) return null;

        return directory.GetFiles("agent-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Reads the live length by opening the file. <see cref="FileInfo.Length"/> comes from cached
    /// directory metadata, which Windows does not refresh while another process holds the file
    /// open for writing — so it reports a stale size for exactly the log we are trying to follow.
    /// </summary>
    private static long CurrentLogLength()
    {
        var file = CurrentLogFile();
        if (file is null) return 0;

        try
        {
            using var stream = new FileStream(
                file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads whatever the agent appended to its log after <paramref name="offset"/>, giving up
    /// once a decisive line appears or the deadline passes.
    /// </summary>
    private static List<string> WaitForAgentReaction(long offset, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lines = new List<string>();

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);

            var file = CurrentLogFile();
            if (file is null) continue;

            lines.Clear();
            try
            {
                // Share the handle: the agent has this file open for writing. The stream's own
                // length is authoritative, unlike the cached FileInfo.Length.
                using var stream = new FileStream(
                    file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length <= offset) continue;

                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);

                while (reader.ReadLine() is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.Trim());
                }
            }
            catch (IOException)
            {
                continue; // Mid-write; try again.
            }

            // Anything from the pipeline settles the question either way.
            if (lines.Any(l =>
                    l.Contains("Raised toast", StringComparison.Ordinal) ||
                    l.Contains("Rejected", StringComparison.Ordinal) ||
                    l.Contains("rejected", StringComparison.Ordinal) ||
                    l.Contains("Rate limit", StringComparison.Ordinal)))
            {
                break;
            }
        }

        return lines;
    }

    /// <summary>
    /// Renders a payload straight from disk, so every toast feature can be exercised and looked
    /// at without Home Assistant in the loop.
    /// <para>
    /// Signature verification is skipped — a local file is not a signed envelope — but the
    /// content policy still applies, so a sample that a real payload would be refused for is
    /// refused here too. That keeps the harness honest.
    /// </para>
    /// </summary>
    private static int RenderSample(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"No such file: {path}");
            return 1;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            ToastPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ToastPayload>(File.ReadAllText(path), ToastJson.Options);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"{path} is not a valid payload: {ex.Message}");
                return 1;
            }

            if (payload is null)
            {
                Console.WriteLine($"{path} deserialised to nothing.");
                return 1;
            }

            var services = new ServiceCollection();
            // A bare collection has no logging infrastructure; the host builder normally
            // supplies it, so ILogger<T> has to be registered explicitly here.
            services.AddLogging(logging => logging.AddSerilog(dispose: false));
            services.AddSingleton(AgentConfig.Load());
            services.AddSingleton<SecretStore>(_ => new SecretStore());
            RegisterToastPipeline(services);

            using var provider = services.BuildServiceProvider();

            AppIdentity.Register();
            try
            {
                // Windows treats a Show() carrying an existing tag as a silent in-place
                // replacement: the Action Center entry updates but no banner appears. That is
                // correct for production and useless for a tool whose whole purpose is to let
                // you look at the toast, so clear the tag first.
                if (!string.IsNullOrWhiteSpace(payload.Tag))
                {
                    var notifier = Notifier();
                    var existing = notifier.GetAll()
                        .Any(n => string.Equals(n.Tag, payload.Tag, StringComparison.Ordinal));

                    if (existing)
                    {
                        Console.WriteLine(
                            $"Removing the existing notification tagged '{payload.Tag}' first - " +
                            "Windows would otherwise replace it silently, with no banner.");

                        if (string.IsNullOrWhiteSpace(payload.Group))
                            notifier.Remove(payload.Tag);
                        else
                            notifier.Remove(payload.Tag, payload.Group);
                    }
                }

                var pipeline = provider.GetRequiredService<NotificationPipeline>();
                var shown = pipeline.SendAsync(payload, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Console.WriteLine();
                Console.WriteLine(shown
                    ? $"Rendered {Path.GetFileName(path)}."
                    : $"{Path.GetFileName(path)} was not rendered - see the reason above.");
                return shown ? 0 : 1;
            }
            finally
            {
                // Give Windows a moment to pick the notification up before this process exits.
                Thread.Sleep(500);
            }
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Raises a toast from the command line and reports exactly what Windows did with it,
    /// so "nothing appeared" can be told apart from "delivered but the banner was suppressed".
    /// </summary>
    private static int TestToast()
    {
        var shortcut = AppIdentity.EnsureShortcut();
        Console.WriteLine($"Start Menu shortcut : {shortcut ?? "MISSING - toasts will not display"}");
        Console.WriteLine($"AUMID               : {AppIdentity.Aumid}");

        var notifier = Notifier();

        // This is the check that was missing while notifications were being accepted and then
        // silently discarded: it reports whether Windows will actually draw them.
        Console.WriteLine($"Display setting     : {notifier.Setting}");
        Console.WriteLine();

        var payload = new ToastPayload
        {
            Visual =
            {
                Text = ["HASS Windows Toast", "Command-line test toast."],
                Attribution = "--test-toast",
            },
        };

        var shown = notifier.Show(ToastXmlWriter.Build(payload, new ResolvedImages()), payload);

        Console.WriteLine(shown ? "Toast handed to Windows." : "Windows refused the toast.");
        Console.WriteLine($"In Action Center    : {notifier.GetAll().Count}");

        if (notifier.Setting is { } setting && setting != Windows.UI.Notifications.NotificationSetting.Enabled)
        {
            Console.WriteLine();
            Console.WriteLine($"Windows reports notifications are '{setting}' for this app,");
            Console.WriteLine("so it will not be shown. Check Settings > System > Notifications.");
            return 1;
        }

        return shown ? 0 : 1;
    }

    /// <summary>
    /// Replaces the signing key, keeping the access token. Deliberately does <b>not</b> print the
    /// new key: rotation usually happens because the old one was exposed, and echoing the
    /// replacement into whatever captured the last one defeats the exercise. A short fingerprint
    /// is printed instead, which is enough to confirm both ends agree without revealing anything.
    /// </summary>
    private static int RotateSigningKey()
    {
        var store = new SecretStore();
        if (!store.Exists)
        {
            Console.WriteLine("Nothing is configured yet. Run --setup first.");
            return 1;
        }

        AgentSecrets current;
        try
        {
            current = store.Load();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }

        var previousFingerprint = Fingerprint(current.SigningKey);
        var rotated = SecretStore.GenerateSigningKey();
        store.Save(current with { SigningKey = rotated });

        Console.WriteLine("Signing key rotated. The access token was left unchanged.");
        Console.WriteLine();
        Console.WriteLine($"  was : {previousFingerprint}");
        Console.WriteLine($"  now : {Fingerprint(rotated)}");
        Console.WriteLine();
        Console.WriteLine("The previous key no longer verifies anything. Run --show-key in your own");
        Console.WriteLine("terminal when you need the new value for the Home Assistant integration.");

        if (IsAgentRunning())
        {
            Console.WriteLine();
            Console.WriteLine("NOTE: the running agent cached the old key at startup. Restart it");
            Console.WriteLine("      (tray > Exit, then launch again) before the change takes effect.");
        }

        return 0;
    }

    /// <summary>
    /// Reports whether Windows can actually deliver a toast click to this app, and optionally
    /// rebuilds the registration.
    /// <para>
    /// Clicking a toast reaches the app through a COM activator: the AUMID key carries a
    /// <c>CustomActivator</c> CLSID, and that CLSID needs a <c>LocalServer32</c> pointing at the
    /// executable. If either is missing the click goes nowhere at all — no error, no callback,
    /// nothing to see in a log. Checking the registry directly is the only way to tell that
    /// apart from a routing bug inside the agent.
    /// </para>
    /// </summary>
    private static int CheckRegistration(bool repair)
    {
        if (repair)
        {
            if (IsAgentRunning())
            {
                Console.WriteLine("Stop the agent first - repairing while it is running would");
                Console.WriteLine("rebuild the registration underneath it.");
                return 1;
            }

            // Rebuild rather than "unregister": the classic notification API has no runtime
            // registration to release. What matters is the Start Menu shortcut and the COM
            // activator entry, so both are removed and written afresh.
            Console.WriteLine("Rebuilding the Start Menu shortcut and COM activator...");

            ComServer.UnregisterFromRegistry();
            StartMenuShortcut.Delete(AppIdentity.DisplayName);

            // Sweep the abandoned Windows App SDK registrations too, so --check-registration
            // stops reporting activators that point at builds which no longer exist.
            foreach (var (clsid, _) in FindActivatorServers())
            {
                if (clsid.Contains(NotificationActivator.ClsidString, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                        $@"Software\Classes\CLSID\{clsid}", throwOnMissingSubKey: false);
                    Console.WriteLine($"  removed stale activator {clsid}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  could not remove {clsid}: {ex.Message}");
                }
            }

            AppIdentity.Register();

            Console.WriteLine($"  display name : {AppIdentity.DisplayName}");
            Console.WriteLine($"  icon         : {AppIdentity.EnsureIcon() ?? "(none)"}");
            Console.WriteLine("  done.");
            Console.WriteLine();
        }

        // The single most important line here. Without a Start Menu shortcut carrying the AUMID,
        // an unpackaged app's notifications are accepted and then never drawn.
        var shortcut = AppIdentity.EnsureShortcut();
        Console.WriteLine($"Start Menu shortcut : {shortcut ?? "MISSING"}");
        if (shortcut is null && StartMenuShortcut.LastError is { } error)
            Console.WriteLine($"  failed because      : {error}");
        Console.WriteLine();

        var healthy = shortcut is not null;

        // Read the shortcut back rather than trusting that writing it worked. Everything that
        // decides whether toasts appear lives inside the .lnk, where nothing else can see it.
        if (shortcut is not null)
        {
            var details = StartMenuShortcut.Read(shortcut);

            if (details is null)
            {
                Console.WriteLine($"  could not be read   : {StartMenuShortcut.LastError}");
                healthy = false;
            }
            else
            {
                Console.WriteLine($"  target              : {details.Target}");
                Console.WriteLine($"  AUMID               : {details.Aumid ?? "(none)"}");
                Console.WriteLine($"  activator CLSID     : {details.ActivatorClsid?.ToString() ?? "(none)"}");

                if (details.Aumid != AppIdentity.Aumid)
                {
                    Console.WriteLine($"  MISMATCH: expected AUMID '{AppIdentity.Aumid}'.");
                    healthy = false;
                }

                if (details.ActivatorClsid != NotificationActivator.Clsid)
                {
                    Console.WriteLine("  MISMATCH: the activator CLSID is not this build's - clicks will not arrive.");
                    healthy = false;
                }
            }
        }

        Console.WriteLine();

        // The registry side lets Windows start the app when nothing is running.
        using var serverKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\CLSID\{{{NotificationActivator.ClsidString}}}\LocalServer32");

        var localServer = serverKey?.GetValue(null) as string;
        Console.WriteLine($"COM activator server : {localServer ?? "MISSING"}");
        if (localServer is null) healthy = false;

        // Display setting is the only thing that answers "will Windows actually draw this".
        var setting = Notifier().Setting;
        Console.WriteLine($"Display setting      : {setting?.ToString() ?? "(unknown - shell has not indexed the shortcut yet)"}");

        if (setting is { } value && value != Windows.UI.Notifications.NotificationSetting.Enabled)
        {
            Console.WriteLine($"  Windows will not display notifications for this app: {value}");
            healthy = false;
        }

        Console.WriteLine();

        if (!healthy)
        {
            Console.WriteLine("""
                BROKEN. Stop the agent, then run:
                    .\hass-toast.cmd --check-registration --repair
                """);
            return 1;
        }

        Console.WriteLine("Everything needed to show toasts and receive clicks is in place.");
        Console.WriteLine();
        Console.WriteLine("Note that Snooze and Dismiss are handled entirely by Windows and never");
        Console.WriteLine("reach the app - only ordinary buttons and the toast body do.");

        // Leftovers from the abandoned Windows App SDK path, and from earlier build locations.
        var stale = FindActivatorServers()
            .Where(s => !s.Clsid.Contains(NotificationActivator.ClsidString, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (stale.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{stale.Count} stale activator registration(s) from earlier builds:");
            foreach (var (clsid, command) in stale) Console.WriteLine($"  {clsid} -> {command}");
            Console.WriteLine("Harmless, but --repair removes them.");
        }

        return 0;
    }

    /// <summary>
    /// Finds COM local servers registered to activate this executable for app notifications.
    /// </summary>
    private static List<(string Clsid, string Command)> FindActivatorServers()
    {
        var results = new List<(string, string)>();
        var exeName = Path.GetFileName(Environment.ProcessPath ?? "HassToast.Agent.exe");

        using var clsidRoot = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID");
        if (clsidRoot is null) return results;

        foreach (var clsid in clsidRoot.GetSubKeyNames())
        {
            using var server = clsidRoot.OpenSubKey($@"{clsid}\LocalServer32");
            if (server?.GetValue(null) is not string command) continue;

            if (command.Contains(exeName, StringComparison.OrdinalIgnoreCase))
                results.Add((clsid, command));
        }

        return results;
    }

    /// <summary>Short, non-reversible identifier for a key — safe to print and compare.</summary>
    private static string Fingerprint(byte[] key)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(key))[..12];

    /// <summary>
    /// Reprints the signing key. Without this, someone who mislaid it would have to rotate —
    /// needlessly breaking the Home Assistant side to recover a value already on disk.
    /// <para>
    /// This writes a secret to stdout. Run it in your own terminal, never anywhere the output
    /// gets captured or shared.
    /// </para>
    /// </summary>
    private static int ShowKey()
    {
        var store = new SecretStore();
        if (!store.Exists)
        {
            Console.WriteLine("Nothing is configured yet. Run --setup first.");
            return 1;
        }

        try
        {
            Console.WriteLine(Convert.ToBase64String(store.Load().SigningKey));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Reset()
    {
        var store = new SecretStore();
        var configPath = AgentConfig.DefaultPath;

        Console.WriteLine("This deletes:");
        Console.WriteLine($"  {configPath}");
        Console.WriteLine($"  {store.FilePath}");
        Console.WriteLine("Logs are left alone.");
        Console.WriteLine();

        if (!ConsoleSupport.Prompt("Type 'yes' to confirm", "no").Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Nothing was deleted.");
            return 1;
        }

        store.Delete();
        if (File.Exists(configPath)) File.Delete(configPath);

        Console.WriteLine("Removed. Run --setup to configure again.");
        return 0;
    }
}
