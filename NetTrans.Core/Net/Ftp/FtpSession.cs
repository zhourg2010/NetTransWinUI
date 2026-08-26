using System.Globalization;
using System.Text;

namespace NetTrans.Net.Ftp;

/// <summary>One reply from the server: its code and everything it said.</summary>
public sealed record FtpReply(int Code, string Text)
{
    public bool IsPositive => Code is >= 200 and < 400;

    public override string ToString() => $"{Code} {Text}";
}

/// <summary>
/// An FTP control session: log in, ask questions, open a data connection.
///
/// Written out rather than taken from FtpWebRequest, which is obsolete, cannot
/// be given a cancellation token, and offers no way to test any of this without
/// a real server. The subset here is what downloading needs -- no uploads, no
/// directory manipulation.
/// </summary>
public sealed class FtpSession : IAsyncDisposable
{
    private readonly IFtpConnector _connector;
    private readonly IFtpTls _tls;
    private readonly string _host;
    private readonly int _port;

    private Stream? _control;
    private bool _secure;

    public FtpSession(string host, int port, IFtpConnector? connector = null, IFtpTls? tls = null)
    {
        _host = host;
        _port = port <= 0 ? 21 : port;
        _connector = connector ?? TcpFtpConnector.Instance;
        _tls = tls ?? SslFtpTls.Instance;
    }

    /// <summary>What the server said it can do, from FEAT. Empty when it does not answer FEAT.</summary>
    public IReadOnlyList<string> Features { get; private set; } = Array.Empty<string>();

    /// <summary>Whether the server will restart a transfer at an offset, which is what makes ranges possible.</summary>
    public bool SupportsRestart { get; private set; }

    /// <summary>
    /// Connects and logs in.
    /// </summary>
    /// <param name="user">Null for anonymous, which is what most public mirrors want.</param>
    /// <param name="secure">FTPS: AUTH TLS before the credentials go across.</param>
    public async Task OpenAsync(string? user, string? password, bool secure, CancellationToken cancellationToken)
    {
        _control = await _connector.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);

        var greeting = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        if (greeting.Code != 220) throw Refused("连接被拒绝", greeting);

        if (secure)
        {
            var auth = await SendAsync("AUTH TLS", cancellationToken).ConfigureAwait(false);
            if (!auth.IsPositive) throw Refused("服务器不支持 FTPS（AUTH TLS）", auth);

            _control = await _tls.AuthenticateAsync(_control, _host, cancellationToken).ConfigureAwait(false);
            _secure = true;

            // Protect the data connections too, or the file itself still goes
            // across in the clear -- which is the whole point of asking.
            await SendAsync("PBSZ 0", cancellationToken).ConfigureAwait(false);
            await SendAsync("PROT P", cancellationToken).ConfigureAwait(false);
        }

        var login = await SendAsync($"USER {user ?? "anonymous"}", cancellationToken).ConfigureAwait(false);

        if (login.Code == 331)
        {
            // Anonymous FTP wants an address by convention; anything works, and
            // saying what we are is more polite than a blank.
            login = await SendAsync($"PASS {password ?? "nettrans@example.invalid"}", cancellationToken)
                .ConfigureAwait(false);
        }

        if (login.Code != 230) throw Refused("登录失败", login);

        // Binary, always. ASCII mode rewrites line endings, which corrupts every
        // file that is not text.
        var type = await SendAsync("TYPE I", cancellationToken).ConfigureAwait(false);
        if (!type.IsPositive) throw Refused("无法切换到二进制模式", type);

        await ReadFeaturesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The file's size in bytes, or -1 when the server will not say.</summary>
    public async Task<long> SizeAsync(string path, CancellationToken cancellationToken)
    {
        var reply = await SendAsync($"SIZE {path}", cancellationToken).ConfigureAwait(false);

        return reply.Code == 213 && long.TryParse(reply.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long size)
            ? size
            : -1;
    }

    /// <summary>
    /// The file's modification time as an HTTP-style date, or null.
    ///
    /// It stands in for Last-Modified: the resume check wants something that
    /// changes when the file does, and MDTM is all FTP has.
    /// </summary>
    public async Task<string?> ModifiedAsync(string path, CancellationToken cancellationToken)
    {
        var reply = await SendAsync($"MDTM {path}", cancellationToken).ConfigureAwait(false);
        if (reply.Code != 213) return null;

        string stamp = reply.Text.Trim();

        return DateTime.TryParseExact(
            stamp.Length > 14 ? stamp[..14] : stamp,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var moment)
            ? moment.ToString("R", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Opens a data connection and starts sending the file from
    /// <paramref name="from"/>.
    ///
    /// The caller owns the returned stream and closing it ends the session:
    /// a segmented download stops reading as soon as its own range is full, and
    /// there is no way to tell the server "that is enough" other than hanging up.
    /// </summary>
    public async Task<Stream> RetrieveAsync(string path, long from, CancellationToken cancellationToken)
    {
        var data = await OpenDataAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (from > 0)
            {
                var rest = await SendAsync($"REST {from}", cancellationToken).ConfigureAwait(false);

                // 350 is "send me the command it applies to". Anything else means
                // the offset was not taken, and reading on would write the start
                // of the file into the middle of ours.
                if (rest.Code != 350) throw Refused($"服务器不接受从第 {from} 字节续传", rest);
            }

            var retr = await SendAsync($"RETR {path}", cancellationToken).ConfigureAwait(false);

            // 125/150 mean the data connection is live; everything else is a no.
            if (retr.Code is not (125 or 150)) throw Refused("无法读取文件", retr);

            return data;
        }
        catch
        {
            await data.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Says goodbye, so the server frees the session rather than waiting for a timeout.</summary>
    public async Task QuitAsync()
    {
        if (_control is null) return;

        try
        {
            await SendAsync("QUIT", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A courtesy; the socket is about to close anyway.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await QuitAsync().ConfigureAwait(false);

        if (_control is not null) await _control.DisposeAsync().ConfigureAwait(false);
        _control = null;
    }

    /// <summary>
    /// Asks for a passive data connection: EPSV first, then PASV.
    ///
    /// EPSV is the one that survives NAT and IPv6, and PASV is the one every
    /// ancient server has. Active mode (PORT) is not offered: it asks the server
    /// to connect back, which no home router allows.
    /// </summary>
    private async Task<Stream> OpenDataAsync(CancellationToken cancellationToken)
    {
        int port = 0;
        string host = _host;

        var epsv = await SendAsync("EPSV", cancellationToken).ConfigureAwait(false);

        if (epsv.Code == 229 && TryReadExtendedPort(epsv.Text, out int extended))
        {
            port = extended;
        }
        else
        {
            var pasv = await SendAsync("PASV", cancellationToken).ConfigureAwait(false);

            if (pasv.Code != 227 || !TryReadPassive(pasv.Text, out string? passiveHost, out int passivePort))
            {
                throw Refused("服务器没有给出可用的数据连接地址", pasv);
            }

            // The address the server names is often its own idea of itself
            // behind NAT, so the host we already reached is the one to trust.
            host = passiveHost is null || passiveHost.StartsWith("10.", StringComparison.Ordinal) ? _host : passiveHost;
            port = passivePort;
        }

        var data = await _connector.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

        if (!_secure) return data;

        return await _tls.AuthenticateAsync(data, _host, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadFeaturesAsync(CancellationToken cancellationToken)
    {
        var feat = await SendAsync("FEAT", cancellationToken).ConfigureAwait(false);

        if (feat.Code != 211)
        {
            // No FEAT is not a refusal to restart -- plenty of old servers do
            // both. REST is asked for when it is needed, and refused loudly then.
            Features = Array.Empty<string>();
            SupportsRestart = true;
            return;
        }

        Features = feat.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        SupportsRestart = Features.Any(line => line.StartsWith("REST", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>227 Entering Passive Mode (h1,h2,h3,h4,p1,p2).</summary>
    internal static bool TryReadPassive(string text, out string? host, out int port)
    {
        host = null;
        port = 0;

        int open = text.IndexOf('(');
        int close = text.IndexOf(')', open + 1);
        string inside = open >= 0 && close > open ? text[(open + 1)..close] : text;

        var parts = inside.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 6) return false;

        var numbers = new int[6];

        for (int i = 0; i < 6; i++)
        {
            if (!int.TryParse(parts[^(6 - i)], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
            if (numbers[i] is < 0 or > 255) return false;
        }

        host = string.Join('.', numbers.Take(4));
        port = numbers[4] * 256 + numbers[5];

        return port > 0;
    }

    /// <summary>229 Entering Extended Passive Mode (|||port|).</summary>
    internal static bool TryReadExtendedPort(string text, out int port)
    {
        port = 0;

        int open = text.IndexOf('(');
        int close = text.IndexOf(')', open + 1);
        if (open < 0 || close <= open) return false;

        var parts = text[(open + 1)..close].Split('|');

        return parts.Length >= 4
            && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port > 0;
    }

    private async Task<FtpReply> SendAsync(string command, CancellationToken cancellationToken)
    {
        if (_control is null) throw new FtpException("控制连接已经关闭。");

        byte[] line = Encoding.UTF8.GetBytes(command + "\r\n");

        await _control.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await _control.FlushAsync(cancellationToken).ConfigureAwait(false);

        return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one reply, however many lines it takes.
    ///
    /// A multi-line reply opens with "213-" and ends with a line starting
    /// "213 " -- the same code and a space. Stopping at the first line would
    /// leave the rest to be read as the answer to the next command.
    /// </summary>
    private async Task<FtpReply> ReadReplyAsync(CancellationToken cancellationToken)
    {
        string first = await ReadLineAsync(cancellationToken).ConfigureAwait(false);

        int code = ReadCode(first);
        if (code < 0) throw new FtpException($"无法理解服务器的回应：{Trim(first)}");

        string rest = first.Length > 4 ? first[4..] : "";
        if (first.Length <= 3 || first[3] != '-') return new FtpReply(code, rest);

        var builder = new StringBuilder(rest);
        string terminator = code.ToString(CultureInfo.InvariantCulture) + " ";

        while (true)
        {
            string line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line.StartsWith(terminator, StringComparison.Ordinal))
            {
                builder.Append('\n').Append(line[4..]);
                return new FtpReply(code, builder.ToString());
            }

            builder.Append('\n').Append(line.Trim());
        }
    }

    /// <summary>
    /// One CRLF-terminated line, read a byte at a time.
    ///
    /// Buffering would be faster and wrong: the control channel can be upgraded
    /// to TLS mid-session, and a reader that had read ahead would have those
    /// bytes stuck in it.
    /// </summary>
    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_control is null) throw new FtpException("控制连接已经关闭。");

        var bytes = new List<byte>(64);
        var one = new byte[1];

        while (true)
        {
            int read = await _control.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                if (bytes.Count == 0) throw new FtpException("服务器中断了控制连接。");
                break;
            }

            if (one[0] == (byte)'\n') break;

            bytes.Add(one[0]);

            // A server that never sends a newline would otherwise grow this
            // forever.
            if (bytes.Count > 8192) throw new FtpException("服务器的回应过长。");
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    private static int ReadCode(string line) =>
        line.Length >= 3 && int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code)
            ? code
            : -1;

    private static string Trim(string line) => line.Length > 120 ? line[..120] + "…" : line;

    private static FtpException Refused(string what, FtpReply reply) =>
        new($"{what}：{reply.Code} {Trim(reply.Text.Replace('\n', ' ').Trim())}") { Code = reply.Code };
}
