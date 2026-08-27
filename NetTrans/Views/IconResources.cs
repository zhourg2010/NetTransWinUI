using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;


namespace NetTrans.Views;

/// <summary>
/// Turns the icon path strings in Icons.xaml into real <see cref="Geometry"/>
/// objects, once, before any window is built.
///
/// WinUI will not do this from markup. `&lt;Geometry x:Key="…"&gt;M …&lt;/Geometry&gt;`
/// compiles and then fails when the page is parsed, since Geometry is abstract
/// and there is no type converter behind it; `Figures="M …"` will not compile
/// at all, since that property is a collection of figures rather than a path
/// string. The one conversion WinUI does offer is on `Path.Data`, so that is
/// the door this goes through.
///
/// Doing it here rather than at each call site keeps every consumer -- the
/// XAML `Data="{StaticResource IconPlus}"`, the row's KindGlyph, the popover's
/// check mark -- reading a Geometry exactly as before.
/// </summary>
public static class IconResources
{
    /// <summary>Every icon key starts with this.</summary>
    private const string Prefix = "Icon";

    /// <summary>Replaces the icon strings in a dictionary, and in everything it merges.</summary>
    public static int Materialise(ResourceDictionary resources)
    {
        int converted = 0;

        foreach (var merged in resources.MergedDictionaries) converted += Materialise(merged);

        // Snapshot: the dictionary is written to inside the loop.
        var keys = resources.Keys.OfType<string>().Where(key => key.StartsWith(Prefix, StringComparison.Ordinal)).ToList();

        foreach (var key in keys)
        {
            if (resources[key] is not string data) continue;

            resources[key] = Parse(data);
            converted++;
        }

        return converted;
    }

    /// <summary>
    /// The abbreviated path syntax, through the only property that accepts it.
    /// </summary>
    private static Geometry Parse(string data)
    {
        // The icon data is transcribed SVG: digits, letters, spaces, dots and
        // minus signs. Nothing in it needs escaping, but a stray quote would
        // break the document, so it is refused rather than concatenated.
        if (data.Contains('\'') || data.Contains('<') || data.Contains('&'))
        {
            throw new ArgumentException($"图标路径里有不该出现的字符：{data}", nameof(data));
        }

        // Fully qualified: with implicit usings, `Path` is also System.IO.Path.
        var path = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load(
            $"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{data}'/>");

        return path.Data!;
    }
}
