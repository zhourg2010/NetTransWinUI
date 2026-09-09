using System.Text;
using System.Text.RegularExpressions;

namespace NetTrans.Tidy;

/// <summary>
/// Reducing a file name to what it is about.
///
/// 建筑报告.png and 建筑报告v1.0.1.docx are one piece of work in two files, and
/// the only evidence of that on disk is the name. So the version, the date, the
/// "(2)" and the "最终" come off, and what is left is compared.
///
/// No dictionary and no word segmentation: tokens are runs of one character
/// class (Han, Latin, digits) separated by the punctuation people actually type.
/// That is enough for 建筑报告-立面图 and nowhere near enough for a name that is
/// a whole sentence -- which is why every group this produces is shown with its
/// reason and can be rejected with one click.
/// </summary>
public static partial class NameStem
{
    /// <summary>Words too common to identify anything. A group keyed on one of these would collect the whole desktop.</summary>
    public static IReadOnlySet<string> Stopwords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "报告", "文档", "文件", "图片", "照片", "截图", "新建", "未命名", "无标题", "下载", "资料", "附件",
        "微信图片", "qq图片", "屏幕截图", "屏幕快照",
        "document", "documents", "doc", "file", "files", "image", "images", "img", "photo", "pic",
        "picture", "screenshot", "untitled", "new", "download", "downloads", "copy", "video", "audio",
        "data", "test", "tmp", "temp", "output", "export", "final", "draft",
    };

    [GeneratedRegex(@"[-_. ]*[vV]?\d+(\.\d+)+")]
    private static partial Regex DottedVersion();

    [GeneratedRegex(@"[-_. ]*[vV]\d+")]
    private static partial Regex ShortVersion();

    [GeneratedRegex(@"[-_. ]*(rev|ver|version)[-_. ]?\d+", RegexOptions.IgnoreCase)]
    private static partial Regex WordyVersion();

    [GeneratedRegex(@"第\s*\d+\s*[版稿次]")]
    private static partial Regex ChineseVersion();

    [GeneratedRegex(@"[-_. ]*\d{4}[-_.年/]\d{1,2}[-_.月/]\d{1,2}日?")]
    private static partial Regex LongDate();

    [GeneratedRegex(@"[-_. ]*(19|20)\d{6}([-_. ]?\d{6})?")]
    private static partial Regex CompactDate();

    [GeneratedRegex(@"[-_. ]*(\(\d+\)|（\d+）|- ?副本|副本|-\s?copy|_copy)", RegexOptions.IgnoreCase)]
    private static partial Regex CopyMark();

    [GeneratedRegex(@"[-_. ]*(最终版?|终版|定稿|修改稿|修订版?|final|latest)$", RegexOptions.IgnoreCase)]
    private static partial Regex FinalMark();

    [GeneratedRegex(@"[-_ ]\d{1,3}$")]
    private static partial Regex TrailingSerial();

    /// <summary>
    /// The name with everything that distinguishes one revision from another
    /// taken off. Lower-cased, so Latin names compare the way a file system
    /// does on Windows.
    /// </summary>
    public static string Key(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);

        // Order matters: dotted versions before compact dates, or 2026.09.09
        // reads as a version and only half of it comes off.
        stem = DottedVersion().Replace(stem, "");
        stem = LongDate().Replace(stem, "");
        stem = CompactDate().Replace(stem, "");
        stem = WordyVersion().Replace(stem, "");
        stem = ShortVersion().Replace(stem, "");
        stem = ChineseVersion().Replace(stem, "");
        stem = CopyMark().Replace(stem, "");
        stem = FinalMark().Replace(stem, "");
        stem = TrailingSerial().Replace(stem, "");

        return stem.Trim(' ', '-', '_', '.', '（', '）', '(', ')', '【', '】').ToLowerInvariant();
    }

    /// <summary>
    /// Splits a key into tokens: runs of Han, of Latin letters, or of digits,
    /// with the punctuation between them dropped.
    /// </summary>
    public static IReadOnlyList<string> Tokens(string key)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        int kind = 0;

        foreach (var character in key)
        {
            int next = Kind(character);

            if (next == 0 || next != kind)
            {
                if (current.Length > 0) tokens.Add(current.ToString());
                current.Clear();
                kind = next;
            }

            if (next != 0) current.Append(character);
        }

        if (current.Length > 0) tokens.Add(current.ToString());

        return tokens;
    }

    /// <summary>The first token specific enough to group on, or null when the name says nothing.</summary>
    public static string? Lead(string key)
    {
        foreach (var token in Tokens(key))
        {
            if (IsSpecific(token)) return token;
        }

        return null;
    }

    /// <summary>
    /// Two Han characters, or three Latin/digit ones, and not a word every
    /// second file uses.
    /// </summary>
    public static bool IsSpecific(string token)
    {
        if (Stopwords.Contains(token)) return false;

        // Han runs are one token -- there is no space in 新建文档 to split on --
        // so a token built entirely out of stopwords has to be recognised as
        // the sum of its parts, or "新建文档" would key a group that swallows
        // every untitled file on the desktop.
        if (AllStopwords(token)) return false;

        bool han = token.Length > 0 && Kind(token[0]) == 2;
        return token.Length >= (han ? 2 : 3);
    }

    private static bool AllStopwords(string token)
    {
        var rest = token.AsSpan();

        while (rest.Length > 0)
        {
            int matched = 0;

            foreach (var word in Stopwords)
            {
                // Longest first, so 屏幕截图 is not read as 截图 with 屏幕 left over.
                if (word.Length > matched && rest.StartsWith(word, StringComparison.OrdinalIgnoreCase)) matched = word.Length;
            }

            if (matched == 0) return false;

            rest = rest[matched..];
        }

        return true;
    }

    /// <summary>
    /// The longest leading run of whole tokens every key shares. This is what a
    /// group gets called, so it has to end on a token boundary: 建筑报告 rather
    /// than 建筑报.
    /// </summary>
    public static string Common(IEnumerable<string> keys)
    {
        var lists = keys.Select(key => Tokens(key)).ToList();
        if (lists.Count == 0) return "";

        var shared = new List<string>();

        for (int i = 0; i < lists[0].Count; i++)
        {
            string token = lists[0][i];
            if (lists.Any(tokens => i >= tokens.Count || tokens[i] != token)) break;

            shared.Add(token);
        }

        return string.Join("", shared);
    }

    /// <summary>0 = separator, 1 = Latin letters, 2 = Han, 3 = digits.</summary>
    private static int Kind(char character)
    {
        if (char.IsAsciiLetter(character)) return 1;
        if (character >= 0x4E00 && character <= 0x9FFF) return 2;
        if (char.IsAsciiDigit(character)) return 3;

        // Everything else -- space, punctuation, kana, emoji -- separates.
        return 0;
    }
}
