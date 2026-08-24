using CommunityToolkit.Mvvm.ComponentModel;

namespace NetTrans.Views.Controls;

/// <summary>One tab of a <see cref="SegmentedControl"/>. The label is observable because the counts move.</summary>
public sealed partial class SegmentItem : ObservableObject
{
    public string Id { get; }

    [ObservableProperty]
    private string _label;

    public SegmentItem(string id, string label)
    {
        Id = id;
        _label = label;
    }
}
