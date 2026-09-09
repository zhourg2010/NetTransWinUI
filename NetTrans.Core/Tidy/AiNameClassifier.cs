using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NetTrans.Tidy;

/// <summary>Whatever can put a category on a file name. The rules do it for free; this is for what is left.</summary>
public interface INameClassifier
{
    Task<IReadOnlyDictionary<string, TidyCategory>> ClassifyAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default);
}

public sealed record AiOptions
{
    /// <summary>
    /// Any OpenAI-compatible /chat/completions endpoint. The default is Ollama
    /// on this machine, which is the only kind of free that stays free and
    /// sends nothing anywhere.
    /// </summary>
    public string BaseUrl { get; init; } = "http://localhost:11434/v1";

    public string Model { get; init; } = "qwen2.5:3b";

    /// <summary>Empty for a local model. Never written to any NetTrans file -- it comes from the command line or the environment each run.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Names per request. Small enough to answer quickly, large enough that a Downloads folder is one or two calls.</summary>
    public int Batch { get; init; } = 40;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Endpoint, model and key from the environment.
    ///
    /// The key is read each run and never written to any NetTrans file: it is
    /// somebody's paid credential, and a portable app that quietly stores one
    /// in a JSON next to its executable is a leak waiting for a shared folder.
    /// </summary>
    public static AiOptions FromEnvironment()
    {
        var fallback = new AiOptions();

        return new AiOptions
        {
            BaseUrl = Environment.GetEnvironmentVariable("NETTRANS_AI_URL") is { Length: > 0 } url ? url : fallback.BaseUrl,
            Model = Environment.GetEnvironmentVariable("NETTRANS_AI_MODEL") is { Length: > 0 } model ? model : fallback.Model,
            ApiKey = Environment.GetEnvironmentVariable("NETTRANS_AI_KEY"),
        };
    }
}

/// <summary>
/// 让 AI 认一下剩下的文件.
///
/// Three things about this are deliberate:
///
/// **Only names are sent.** Never a byte of any file's contents. A file name is
/// already on the screen of anyone standing behind you; a file's contents are
/// not, and no tidying convenience is worth uploading them.
///
/// **It is the last resort, not the first.** The extension decides almost
/// everything for free and instantly; asking a model to confirm that .mp4 is a
/// video is a waste of somebody's quota and of the user's time.
///
/// **Every failure is silent and harmless.** No endpoint configured, no model
/// running, a timeout, a refusal, a reply that is not JSON, a category nobody
/// has heard of: all of it means those files stay 其他, which is exactly where
/// they would have been anyway.
/// </summary>
public sealed class AiNameClassifier : INameClassifier, IDisposable
{
    /// <summary>
    /// Relaxed escaping, because the names being sent are Chinese: the default
    /// encoder turns 报销单.zzz into \u62A5\u9500\u5355, which is the same
    /// request at six times the tokens and unreadable in any log.
    /// </summary>
    private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string Instruction =
        "你是文件整理助手。根据文件名判断每个文件属于哪一类，只能从这些 id 里选：" +
        string.Join(", ", TidyCategories.Names) + "。" +
        "只输出一个 JSON 对象，键是原样的文件名，值是类别 id，不要解释，不要代码块。" +
        "拿不准就用 unknown。";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly AiOptions _options;

    public AiNameClassifier(AiOptions options, HttpClient? client = null)
    {
        _options = options;
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = options.Timeout };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    /// <summary>How many requests were made and how many answers came back, for the run's summary.</summary>
    public int Calls { get; private set; }

    public int Answered { get; private set; }

    public string? LastError { get; private set; }

    public async Task<IReadOnlyDictionary<string, TidyCategory>> ClassifyAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, TidyCategory>(StringComparer.OrdinalIgnoreCase);

        for (int at = 0; at < names.Count; at += _options.Batch)
        {
            var batch = names.Skip(at).Take(_options.Batch).ToArray();

            foreach (var (name, category) in await AskAsync(batch, cancellationToken).ConfigureAwait(false))
            {
                found[name] = category;
            }
        }

        Answered = found.Count;
        return found;
    }

    private async Task<IReadOnlyDictionary<string, TidyCategory>> AskAsync(string[] names, CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, TidyCategory>();

        var request = new
        {
            model = _options.Model,
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = Instruction },
                new { role = "user", content = string.Join('\n', names) },
            },
        };

        string reply;
        try
        {
            Calls++;

            var response = await _client
                .PostAsJsonAsync($"{_options.BaseUrl.TrimEnd('/')}/chat/completions", request, Wire, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)response.StatusCode}";
                return empty;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            reply = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = failure.Message;
            return empty;
        }

        return Read(reply, names);
    }

    /// <summary>
    /// Reads the model's answer, keeping only what was asked about and only
    /// categories that exist. A model that invents a bucket, renames a file or
    /// answers about something it was not given is simply ignored -- this is
    /// the boundary where a language model's output stops being trusted.
    /// </summary>
    internal static IReadOnlyDictionary<string, TidyCategory> Read(string reply, IReadOnlyCollection<string> asked)
    {
        var found = new Dictionary<string, TidyCategory>(StringComparer.OrdinalIgnoreCase);

        string json = Unfence(reply);
        if (json.Length == 0) return found;

        var wanted = new HashSet<string>(asked, StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return found;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!wanted.Contains(property.Name)) continue;
                if (property.Value.ValueKind != JsonValueKind.String) continue;

                var category = TidyCategories.Parse(property.Value.GetString());
                if (category != TidyCategory.Unknown) found[property.Name] = category;
            }
        }
        catch (JsonException)
        {
            // Prose instead of JSON. Nothing is classified, nothing is harmed.
        }

        return found;
    }

    /// <summary>Models add ```json fences however firmly they are asked not to.</summary>
    private static string Unfence(string reply)
    {
        int open = reply.IndexOf('{');
        int close = reply.LastIndexOf('}');

        return open >= 0 && close > open ? reply[open..(close + 1)] : "";
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
