using System.Net;
using System.Text;

namespace NetTrans.Net;

/// <summary>
/// What a site wants before it will hand over a file.
///
/// A direct link is the easy case. The awkward ones are everywhere: a forum
/// attachment that only serves a request carrying the thread's Referer, a
/// members' area behind a session cookie, a NAS behind Basic auth. Every
/// download manager grows these fields because without them the link works in
/// the browser and 403s here.
/// </summary>
/// <param name="Referer">Sent as the Referer header. Usually the page the link was on.</param>
/// <param name="Cookie">A raw Cookie header, as copied out of the browser.</param>
/// <param name="UserAgent">Overrides the default, for a site that sniffs it.</param>
/// <param name="User">Username for Basic auth. Null when the site wants none.</param>
/// <param name="Password">Password for Basic auth.</param>
public sealed record RequestProfile(
    string? Referer = null,
    string? Cookie = null,
    string? UserAgent = null,
    string? User = null,
    string? Password = null)
{
    public bool HasCredentials => !string.IsNullOrEmpty(User);

    /// <summary>The Authorization header value, or null when there is nothing to send.</summary>
    public string? BasicHeader() =>
        HasCredentials
            ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"))
            : null;

    /// <summary>
    /// The credentials a URL carries itself.
    ///
    /// https://user:pass@host/file.iso is what half the world pastes, and
    /// HttpClient drops the userinfo silently rather than authenticating with
    /// it, so it is read out here instead.
    /// </summary>
    public static RequestProfile? FromUserInfo(Uri url)
    {
        if (!url.IsAbsoluteUri || string.IsNullOrEmpty(url.UserInfo)) return null;

        string[] parts = url.UserInfo.Split(':', 2);

        return new RequestProfile(
            User: Uri.UnescapeDataString(parts[0]),
            Password: parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
    }

    /// <summary>The same URL without its userinfo, which is what should be shown and stored.</summary>
    public static Uri WithoutUserInfo(Uri url)
    {
        if (!url.IsAbsoluteUri || string.IsNullOrEmpty(url.UserInfo)) return url;

        return new UriBuilder(url) { UserName = "", Password = "" }.Uri;
    }
}

/// <summary>
/// Per-site request settings, looked up by host.
///
/// Host rather than exact URL: a session cookie is good for the whole site, and
/// a file's URL is rarely the one the settings were entered against -- it has
/// been redirected to a CDN by the time the bytes move.
/// </summary>
public sealed class RequestProfiles
{
    private readonly Dictionary<string, RequestProfile> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Remembers a profile for a URL's host, replacing any earlier one.</summary>
    public void Set(Uri url, RequestProfile profile)
    {
        if (!url.IsAbsoluteUri) return;

        lock (_gate) _byHost[url.Host] = profile;
    }

    /// <summary>Remembers the page a link was found on, leaving anything else in place.</summary>
    public void SetReferer(Uri url, Uri page)
    {
        if (!url.IsAbsoluteUri) return;

        lock (_gate)
        {
            var existing = _byHost.GetValueOrDefault(url.Host) ?? new RequestProfile();
            _byHost[url.Host] = existing with { Referer = page.AbsoluteUri };
        }
    }

    /// <summary>
    /// The profile for a URL: what was stored for its host, with anything the
    /// URL itself carries taking precedence.
    /// </summary>
    public RequestProfile? For(Uri url)
    {
        if (!url.IsAbsoluteUri) return null;

        RequestProfile? stored;
        lock (_gate) stored = _byHost.GetValueOrDefault(url.Host);

        if (RequestProfile.FromUserInfo(url) is not { } inline) return stored;

        return stored is null
            ? inline
            : stored with { User = inline.User, Password = inline.Password };
    }

    public void Clear()
    {
        lock (_gate) _byHost.Clear();
    }
}

/// <summary>
/// The 代理 setting, as something the handler can hold on to.
///
/// A handler's proxy is fixed once its first request has gone out, so pointing
/// it at this instead means the dropdown can be changed without restarting the
/// app or tearing down transfers that are already running.
/// </summary>
public sealed class LiveProxy : IWebProxy
{
    private volatile IWebProxy? _explicit;
    private volatile bool _direct;

    /// <summary>Credentials for a proxy that asks for them.</summary>
    public ICredentials? Credentials { get; set; }

    /// <summary>What the dropdown currently says, for the log and the tests.</summary>
    public string Label { get; private set; } = "系统代理";

    /// <summary>Reads the settings label: 系统代理, 不使用代理, or a host:port / URL.</summary>
    public void Set(string? label)
    {
        string text = (label ?? "").Trim();
        Label = text.Length == 0 ? "系统代理" : text;

        if (Label == "系统代理")
        {
            _explicit = null;
            _direct = false;
            return;
        }

        if (Label == "不使用代理")
        {
            _explicit = null;
            _direct = true;
            return;
        }

        string url = Label.Contains("://", StringComparison.Ordinal) ? Label : "http://" + Label;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _explicit = new WebProxy(uri);
            _direct = false;
            return;
        }

        // Unreadable: the system's own setting is a better answer than none.
        _explicit = null;
        _direct = false;
    }

    /// <summary>Whether requests currently go straight out.</summary>
    public bool IsDirect => _direct;

    public Uri? GetProxy(Uri destination) =>
        _direct ? null : Current()?.GetProxy(destination);

    public bool IsBypassed(Uri host) =>
        _direct || (Current()?.IsBypassed(host) ?? true);

    private IWebProxy? Current() => _explicit ?? WebRequest.DefaultWebProxy;
}
