using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace NetTrans.Views.Controls;

/// <summary>
/// The handoff's `Segmented`: equal-width buttons over a thumb that slides on
/// the shared .26s cubic-bezier(.32,.72,0,1).
/// </summary>
public sealed partial class SegmentedControl : UserControl
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IList<SegmentItem>), typeof(SegmentedControl),
            new PropertyMetadata(null, (d, _) => ((SegmentedControl)d).BuildButtons()));

    public static readonly DependencyProperty SelectedIdProperty =
        DependencyProperty.Register(nameof(SelectedId), typeof(string), typeof(SegmentedControl),
            new PropertyMetadata(null, (d, _) => ((SegmentedControl)d).UpdateSelection(animate: true)));

    /// <summary>Raised on click with the newly selected id.</summary>
    public event EventHandler<string>? SelectionChanged;

    public IList<SegmentItem>? Items
    {
        get => (IList<SegmentItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string? SelectedId
    {
        get => (string?)GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    private readonly List<Button> _buttons = new();

    public SegmentedControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateSelection(animate: false);
    }

    private void BuildButtons()
    {
        Buttons.Children.Clear();
        Buttons.ColumnDefinitions.Clear();
        _buttons.Clear();

        if (Items is null) return;

        for (int i = 0; i < Items.Count; i++)
        {
            Buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var item = Items[i];
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["SegmentButtonStyle"],
                Content = item.Label,
                Tag = item.Id,
            };

            // Labels carry live counts ("进行中 3"), so they have to track the item.
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SegmentItem.Label)) button.Content = item.Label;
            };

            button.Click += (_, _) =>
            {
                SelectedId = item.Id;
                SelectionChanged?.Invoke(this, item.Id);
            };

            Grid.SetColumn(button, i);
            Buttons.Children.Add(button);
            _buttons.Add(button);
        }

        UpdateSelection(animate: false);
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSelection(animate: false);

    private void UpdateSelection(bool animate)
    {
        if (Items is null || Items.Count == 0 || Track.ActualWidth <= 0) return;

        int index = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id != SelectedId) continue;
            index = i;
            break;
        }

        // CSS: slot = 100%/N, thumb is that slot inset 2px on each side.
        double slot = Track.ActualWidth / Items.Count;
        Thumb.Width = Math.Max(0, slot - 4);
        double target = index * slot + 2;

        for (int i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].FontWeight = i == index ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium;
        }

        if (!animate)
        {
            ThumbOffset.X = target;
            return;
        }

        Animations.Slide(ThumbOffset, "X", target, 260).Begin();
    }
}
