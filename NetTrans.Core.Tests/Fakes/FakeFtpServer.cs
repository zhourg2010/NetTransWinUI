using System.Globalization;
using System.Text;
using NetTrans.Net.Ftp;

namespace NetTrans.Tests.Fakes;

/// <summary>
/// An FTP server in memory: the subset a download uses, over the same duplex
/// pipes the BitTorrent fakes run on.
///
/// Enough of a server to be worth testing against -- it refuses a bad login,
/// answers SIZE and MDTM, honours REST, and sends the file down a second
/// connection like the real thing.
/// </summary>
public sealed class FakeFtpServer : IFtpConnector
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<int, TaskCompletionSource<Stream>> _pending = new();
    private readonly object _gate = new();

    private int _nextPort = 40000;

    /// <summary>Commands the server received, in order, for a test to assert on.</summary>
    public List<string> Commands { get; } = new();

    /// <summary>Credentials it will accept. Null means anonymous is welcome.</summary>
    public (string User, string Password)? Requires { get; set; }

    /// <summary>Whether FEAT lists REST, which is what decides if the file can be split.</summary>
    public bool Restartable { get; set; } = true;

    /// <summary>Whether the server answers FEAT at all; old ones do not.</summary>
    public bool KnowsFeat { get; set; } = true;

    /// <summary>Whether EPSV is understood. Ancient servers only have PASV.</summary>
    public bool KnowsEpsv { get; set; } = true;

    /// <summary>Whether MDTM is understood.</summary>
    public bool KnowsMdtm { get; set; } = true;

    /// <summary>How many control sessions have been opened.</summary>
    public int Sessions { get; private set; }

    public FakeFtpServer Add(string path, byte[] content)
    {
        _files[path] = content;
        return this;
    }

    public Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        // A data connection: the port was handed out by PASV/EPSV a moment ago.
        lock (_gate)
        {
            if (_pending.Remove(port, out var waiting))
            {
                var (theirs, ours) = DuplexPipe.Create();
                waiting.SetResult(ours);

                return Task.FromResult(theirs);
            }
        }

        var (client, server) = DuplexPipe.Create();

        Sessions++;
        _ = Task.Run(() => ServeAsync(server, cancellationToken), CancellationToken.None);

        return Task.FromResult(client);
    }

    private async Task ServeAsync(Stream control, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(control, "220 NetTrans fake FTP", cancellationToken).ConfigureAwait(false);

            string? user = null;
            long restart = 0;
            Task<Stream>? data = null;
            bool authenticated = Requires is null;

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await ReadLineAsync(control, cancellationToken).ConfigureAwait(false);
                if (line is null) return;

                lock (Commands) Commands.Add(line);

                string verb = line.Split(' ')[0].ToUpperInvariant();
                string argument = line.Length > verb.Length ? line[(verb.Length + 1)..].Trim() : "";

                switch (verb)
                {
                    case "USER":
                        user = argument;
                        await SendAsync(control, "331 Password required", cancellationToken).ConfigureAwait(false);
                        break;

                    case "PASS":
                        authenticated = Requires is not { } wanted ||
                            (user == wanted.User && argument == wanted.Password);

                        await SendAsync(
                            control,
                            authenticated ? "230 Logged in" : "530 Login incorrect",
                            cancellationToken).ConfigureAwait(false);

                        break;

                    case "TYPE":
                        await SendAsync(control, "200 Type set to I", cancellationToken).ConfigureAwait(false);
                        break;

                    case "FEAT":
                        if (!KnowsFeat)
                        {
                            await SendAsync(control, "500 Unknown command", cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        await SendAsync(control, "211-Features:", cancellationToken).ConfigureAwait(false);
                        await SendAsync(control, " SIZE", cancellationToken).ConfigureAwait(false);
                        if (Restartable) await SendAsync(control, " REST STREAM", cancellationToken).ConfigureAwait(false);
                        await SendAsync(control, "211 End", cancellationToken).ConfigureAwait(false);
                        break;

                    case "SIZE":
                        await SendAsync(
                            control,
                            _files.TryGetValue(argument, out var sized)
                                ? $"213 {sized.Length}"
                                : "550 No such file",
                            cancellationToken).ConfigureAwait(false);

                        break;

                    case "MDTM":
                        await SendAsync(
                            control,
                            KnowsMdtm && _files.ContainsKey(argument) ? "213 20260101123000" : "500 Unknown command",
                            cancellationToken).ConfigureAwait(false);

                        break;

                    case "EPSV":
                        if (!KnowsEpsv)
                        {
                            await SendAsync(control, "502 Not implemented", cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        data = Listen(out int extended);
                        await SendAsync(control, $"229 Entering Extended Passive Mode (|||{extended}|)", cancellationToken)
                            .ConfigureAwait(false);

                        break;

                    case "PASV":
                        data = Listen(out int passive);
                        await SendAsync(
                            control,
                            $"227 Entering Passive Mode (127,0,0,1,{passive / 256},{passive % 256})",
                            cancellationToken).ConfigureAwait(false);

                        break;

                    case "REST":
                        restart = long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out long offset)
                            ? offset
                            : 0;

                        await SendAsync(
                            control,
                            Restartable ? $"350 Restarting at {restart}" : "502 REST not understood",
                            cancellationToken).ConfigureAwait(false);

                        break;

                    case "RETR":
                        if (!authenticated)
                        {
                            await SendAsync(control, "530 Not logged in", cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        if (!_files.TryGetValue(argument, out var content) || data is null)
                        {
                            await SendAsync(control, "550 No such file", cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        await SendAsync(control, "150 Opening data connection", cancellationToken).ConfigureAwait(false);

                        var sink = await data.ConfigureAwait(false);

                        if (restart < content.Length)
                        {
                            await sink.WriteAsync(content.AsMemory((int)restart), cancellationToken).ConfigureAwait(false);
                            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        await sink.DisposeAsync().ConfigureAwait(false);

                        data = null;
                        restart = 0;

                        await SendAsync(control, "226 Transfer complete", cancellationToken).ConfigureAwait(false);
                        break;

                    case "QUIT":
                        await SendAsync(control, "221 Goodbye", cancellationToken).ConfigureAwait(false);
                        return;

                    default:
                        await SendAsync(control, "500 Unknown command", cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // The client hung up, which a segmented download does on purpose.
        }
        finally
        {
            control.Dispose();
        }
    }

    /// <summary>Reserves a port the client is about to connect back to.</summary>
    private Task<Stream> Listen(out int port)
    {
        var waiting = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            port = _nextPort++;
            _pending[port] = waiting;
        }

        return waiting.Task;
    }

    private static async Task SendAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];

        while (true)
        {
            int read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) return bytes.Count == 0 ? null : Line(bytes);

            if (one[0] == (byte)'\n') return Line(bytes);

            bytes.Add(one[0]);
        }

        static string Line(List<byte> bytes) => Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
    }
}
