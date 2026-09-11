using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using NetTrans.Views;

namespace NetTrans.Views.Controls;

/// <summary>One row of a popover menu.</summary>
/// <param name="Label">Row text.</param>
/// <param name="Glyph">Trailing icon, or null.</param>
/// <param name="IsChecked">Draws a check instead of the trailing icon.</param>
/// <param name="IsDestructive">Renders in --red (the 删除 / 退出 rows).</param>
/// <param name="SeparatorBefore">Emits the CSS's `hr` above this row.</param>
/// <param name="Invoke">What the row does; the popover closes afterwards unless <paramref name="KeepOpen"/>.</param>
/// <param name="KeepOpen">Sort rows flip direction in place rather than dismissing.</param>
public sealed record PopoverItem(
    string Label,
    string? Glyph = null,
    bool IsChecked = false,
    bool IsDestructive = false,
    bool SeparatorBefore = false,
    Action? Invoke = null,
    bool KeepOpen = false);

/// <summary>
/// The handoff's `.ctx` popover, used for the row context menu, 新建, 显示与排序
/// and the tray menu. It lives inside the frame rather than in a Popup so it can
/// never escape the 536x680 window.
/// </summary>
public sealed partial class PopoverControl : UserControl
{
    /// <summary>Raised when the popover should be taken off screen.</summary>
    public event EventHandler? Dismissed;

    /// <summary>菜单想待的位置；量出实际高度之后再据此夹进窗口里。</summary>
    private Point _wanted;

    public PopoverControl()
    {
        InitializeComponent();
        Card.SizeChanged += (_, _) => ClampIntoView();
    }

    /// <summary>
    /// 把菜单整个收进窗口。
    ///
    /// 调用方原先是拿 ActualHeight - 380 去夹的 —— 380 是一个写死的"菜单
    /// 大概多高"。行右键菜单在下载中的任务上有 11 项，远不止 380，于是它
    /// 从列表顶一直压到窗口底下去。菜单有多高只有量过才知道，所以夹这一步
    /// 挪到 SizeChanged 里做。
    /// </summary>
    private void ClampIntoView()
    {
        const double margin = 8;

        double room = Layer.ActualWidth;
        if (room > 0 && Card.ActualWidth > 0)
        {
            double most = Math.Max(margin, room - Card.ActualWidth - margin);
            Card.Margin = new Thickness(
                Math.Clamp(_wanted.X, margin, most), Card.Margin.Top, 0, 0);
        }

        room = Layer.ActualHeight;
        if (room > 0 && Card.ActualHeight > 0)
        {
            double most = Math.Max(margin, room - Card.ActualHeight - margin);
            Card.Margin = new Thickness(
                Card.Margin.Left, Math.Clamp(_wanted.Y, margin, most), 0, 0);
        }
    }

    /// <summary>Fills the menu and places its top-left corner at <paramref name="position"/>.</summary>
    public void Show(IEnumerable<PopoverItem> items, Point position, double width = 232)
    {
        Card.Width = width;
        _wanted = position;
        Card.Margin = new Thickness(position.X, position.Y, 0, 0);

        Items.Children.Clear();

        foreach (var item in items)
        {
            if (item.SeparatorBefore)
            {
                Items.Children.Add(new Border
                {
                    Height = 0.5,
                    Background = Services.ThemeBrushes.Get("PopoverSepBrush"),
                });
            }

            Items.Children.Add(BuildRow(item));
        }

        // `pop`: .16s scale from .94.
        var storyboard = new Storyboard();
        foreach (string property in new[] { "ScaleX", "ScaleY" })
        {
            var frames = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0.94 });
            frames.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)),
                Value = 1,
                KeySpline = Animations.Standard,
            });
            Storyboard.SetTarget(frames, CardScale);
            Storyboard.SetTargetProperty(frames, property);
            storyboard.Children.Add(frames);
        }

        Card.Opacity = 0;
        storyboard.Begin();
        Animations.Fade(Card, 1, 160).Begin();
    }

    private Button BuildRow(PopoverItem item)
    {
        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = item.Label,
            FontSize = 15,
            CharacterSpacing = -13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        layout.Children.Add(label);

        var glyph = item.IsChecked ? IconResources.Data("IconCheck") : item.Glyph;
        if (glyph is not null)
        {
            var icon = new StrokeIcon
            {
                Data = glyph,
                IconSize = 17,
                Thickness = item.IsChecked ? 2.6 : 1.7,
                Opacity = item.IsChecked ? 1 : 0.85,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 1);
            layout.Children.Add(icon);
        }

        var button = new Button
        {
            Style = (Style)Application.Current.Resources[item.IsDestructive ? "PopoverDestructiveItemStyle" : "PopoverItemStyle"],
            Content = layout,
        };

        button.Click += (_, _) =>
        {
            item.Invoke?.Invoke();
            if (!item.KeepOpen) Dismissed?.Invoke(this, EventArgs.Empty);
        };

        return button;
    }

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);

    private void OnCardTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;
}
