using HassToast.Agent.Config;

namespace HassToast.Agent.Security;

public sealed record PolicyResult(bool Allowed, string? Reason = null)
{
    public static readonly PolicyResult Ok = new(true);
    public static PolicyResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Decides what a verified payload is permitted to do. A valid signature proves an envelope came
/// from Home Assistant; it says nothing about whether the contents are safe to act on. Anything
/// that reaches outside the toast — launching a URI, fetching a URL, reading a local file — is
/// gated here.
/// </summary>
public sealed class ContentPolicy
{
    private readonly SecurityConfig _config;
    private readonly TokenBucket _rateLimiter;

    public ContentPolicy(SecurityConfig config, TimeProvider? timeProvider = null)
    {
        _config = config;
        _rateLimiter = new TokenBucket(
            capacity: Math.Max(1, config.MaxToastsPerMinute),
            refillPerSecond: Math.Max(1, config.MaxToastsPerMinute) / 60.0,
            timeProvider ?? TimeProvider.System);
    }

    /// <summary>
    /// Gates a protocol-activation target. This is the sharpest edge in the whole system: whatever
    /// is allowed here gets handed to the shell when the user clicks a button, so the default
    /// allowlist deliberately excludes <c>file:</c>, <c>ms-settings:</c> and every custom handler.
    /// </summary>
    public PolicyResult CheckActivationUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return PolicyResult.Deny("activation uri is empty");

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return PolicyResult.Deny($"activation uri is not absolute: '{Truncate(value)}'");

        if (!_config.AllowedUriSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return PolicyResult.Deny(
                $"scheme '{uri.Scheme}' is not in security.allowedUriSchemes " +
                $"[{string.Join(", ", _config.AllowedUriSchemes)}]");
        }

        return PolicyResult.Ok;
    }

    /// <summary>Gates an image source before the agent will fetch or read it.</summary>
    public PolicyResult CheckImageSource(string? value, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(value))
            return PolicyResult.Deny("image source is empty");

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
            return PolicyResult.Deny($"image source is not absolute: '{Truncate(value)}'");

        if (!_config.AllowedImageSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return PolicyResult.Deny(
                $"image scheme '{parsed.Scheme}' is not in security.allowedImageSchemes " +
                $"[{string.Join(", ", _config.AllowedImageSchemes)}]");
        }

        // A UNC path reached through file: would pull credentials to an arbitrary host.
        if (parsed.IsFile && parsed.IsUnc)
            return PolicyResult.Deny("UNC image paths are not permitted");

        uri = parsed;
        return PolicyResult.Ok;
    }

    /// <summary>
    /// Windows enforces its own ceilings — roughly 200 KB for hero and app-logo images, 3 MB
    /// inline — and silently drops anything larger. Checking first turns that into a logged
    /// reason instead of an image that mysteriously fails to appear.
    /// </summary>
    public int MaxBytesFor(ImageRole role) => role switch
    {
        ImageRole.Inline => _config.MaxInlineImageBytes,
        _ => _config.MaxLogoImageBytes,
    };

    /// <summary>Consumes one unit of the toast budget. False means the caller is over its rate.</summary>
    public bool TryConsumeToastBudget() => _rateLimiter.TryConsume();

    private static string Truncate(string value)
        => value.Length <= 64 ? value : value[..61] + "...";
}

public enum ImageRole
{
    AppLogo,
    Hero,
    Inline,
}

/// <summary>Classic token bucket: a sustained rate with room for short bursts.</summary>
public sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private double _tokens;
    private long _lastTicks;

    public TokenBucket(double capacity, double refillPerSecond, TimeProvider time)
    {
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _time = time;
        _tokens = capacity;
        _lastTicks = time.GetTimestamp();
    }

    public bool TryConsume(double amount = 1.0)
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(_lastTicks, now).TotalSeconds;
            _lastTicks = now;

            _tokens = Math.Min(_capacity, _tokens + elapsed * _refillPerSecond);

            if (_tokens < amount) return false;
            _tokens -= amount;
            return true;
        }
    }
}
