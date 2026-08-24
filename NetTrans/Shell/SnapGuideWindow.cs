using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetTrans.Interop;
using Windows.Graphics;

namespace NetTrans.Shell;

/// <summary>
/// The `.snapline` guide: a 3px blue bar shown along the edge the inspector is
/// about to bond to. It is a real top-most window because it has to draw
/// outside both frames.
///
/// The CSS also puts a 12px blue glow around it. A WinUI 3 window cannot be
/// partially transparent, so the glow is dropped and only the solid bar is
/// drawn -- the one place the shell knowingly departs from the handoff.
/// </summary>
internal sealed class SnapGuideWindow : IDisposable
{
    private readonly Window _window;
    private readonly WindowChrome _chrome;
    private bool _visible;

    public SnapGuideWindow()
    {
        _window = new Window
        {
            Content = new Grid
            {
                Background = (Brush)Application.Current.Resources["BlueBrush"],
            },
        };

        _chrome = new WindowChrome(_window);
        _chrome.MakeFrameless(resizable: false, keepShadow: false);
        _chrome.MakeUtilityWindow(noActivate: true, topMost: true);
    }

    public void ShowAt(RectInt32 rectPx)
    {
        _window.AppWindow.MoveAndResize(rectPx);
        if (!_visible)
        {
            _window.AppWindow.Show(activateWindow: false);
            _visible = true;
        }
    }

    public void Hide()
    {
        if (!_visible) return;
        _window.AppWindow.Hide();
        _visible = false;
    }

    public void Dispose()
    {
        _chrome.Dispose();
        _window.Close();
    }
}
