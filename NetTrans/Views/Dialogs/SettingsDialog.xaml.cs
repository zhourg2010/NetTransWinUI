using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetTrans.ViewModels;
using Windows.UI;

namespace NetTrans.Views.Dialogs;

public sealed partial class SettingsDialog : ContentDialog
{
    public ShellViewModel Shell { get; }
    public SettingsViewModel Settings { get; }

    public SettingsDialog(ShellViewModel shell, SettingsViewModel settings)
    {
        InitializeComponent();
        Shell = shell;
        Settings = settings;
        Bindings.Update();

        BuildSwatches();
        Loaded += (_, _) => { };
    }

    private void BuildSwatches()
    {
        SwatchesPanel.Children.Clear();
        foreach (var hex in ShellViewModel.AccentSwatches)
        {
            var color = HexToColor(hex);
            var button = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(Shell.AccentHex.Equals(hex, StringComparison.OrdinalIgnoreCase) ? 2 : 1),
                BorderBrush = Shell.AccentHex.Equals(hex, StringComparison.OrdinalIgnoreCase)
                    ? (Brush)Application.Current.Resources["AccentBrush"]
                    : (Brush)Application.Current.Resources["Stroke2Brush"],
                CornerRadius = new CornerRadius(13),
                Tag = hex,
            };
            button.Click += OnSwatchClick;
            SwatchesPanel.Children.Add(button);
        }
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            Shell.SetAccent(hex);
            BuildSwatches();
        }
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page })
        {
            Settings.CurrentPage = page;
        }
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string theme })
        {
            Shell.SetTheme(theme);
        }
    }

    private void OnDensityClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            Shell.SetDensity(toggle.IsChecked == true ? "compact" : "comfy");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private static Color HexToColor(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = System.Convert.ToByte(hex[..2], 16);
        byte g = System.Convert.ToByte(hex[2..4], 16);
        byte b = System.Convert.ToByte(hex[4..6], 16);
        return Color.FromArgb(255, r, g, b);
    }
}
