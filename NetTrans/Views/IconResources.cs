using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace NetTrans.Views;

/// <summary>
/// Turns an icon's path string into a <see cref="Geometry"/>.
///
/// Two WinUI facts shape this. First, markup cannot do it: a resource declared
/// `&lt;Geometry x:Key="…"&gt;M …&lt;/Geometry&gt;` compiles and then fails when the page
/// is parsed, because Geometry is abstract and nothing converts a string into
/// one; `Figures="M …"` does not compile at all. The single place the
/// conversion exists is `Path.Data`, which is the door used here.
///
/// Second, and the reason this returns a fresh object every time: a Geometry is
/// a DependencyObject, and one of those belongs to a single element. Handing
/// the same instance to two icons fails the second assignment, which is what a
/// shared resource would do the moment two rows showed the same glyph.
/// </summary>
public static class IconResources
{
    /// <summary>The icon's path data, as written in Icons.xaml.</summary>
    public static string Data(string key) =>
        Application.Current.Resources[key] as string
            ?? throw new ArgumentException($"资源里没有图标 {key}。", nameof(key));

    /// <summary>A geometry of its own, for one element to hold.</summary>
    public static Geometry Geometry(string key) => Parse(Data(key));

    /// <summary>The abbreviated path syntax, through the only property that accepts it.</summary>
    public static Geometry Parse(string data)
    {
        // Transcribed SVG: digits, letters, spaces, dots, minus signs. Nothing
        // needs escaping, but a stray quote would break the document, so it is
        // refused rather than concatenated.
        if (data.Contains('\'') || data.Contains('<') || data.Contains('&'))
        {
            throw new ArgumentException($"图标路径里有不该出现的字符：{data}", nameof(data));
        }

        var path = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load(
            $"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{data}'/>");

        var geometry = path.Data!;

        // The Path was scaffolding; cut the geometry loose from it.
        path.Data = null;

        return geometry;
    }
}
