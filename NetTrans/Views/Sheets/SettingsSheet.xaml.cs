using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NetTrans.Services;
using NetTrans.ViewModels;
using Windows.System;

namespace NetTrans.Views.Sheets;

/// <summary>设置: the three groups from the handoff, backed by AppSettings.</summary>
public sealed partial class SettingsSheet : UserControl
{
    private readonly ShellViewModel _viewModel;
    private bool _loading = true;

    public SettingsSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        var settings = viewModel.Settings;

        SavePathRow.Value = settings.DefaultSavePath;
        ScheduleRow.Value = $"{settings.OffPeakStart} – {settings.OffPeakEnd}";
        BossKeyRow.Value = viewModel.BossKey;

        OffPeakStartPicker.SelectedTime = TimeOnly.TryParse(settings.OffPeakStart, out var from)
            ? from.ToTimeSpan()
            : new TimeSpan(23, 0, 0);

        OffPeakEndPicker.SelectedTime = TimeOnly.TryParse(settings.OffPeakEnd, out var to)
            ? to.ToTimeSpan()
            : new TimeSpan(7, 0, 0);

        FoldersSwitch.IsOn = settings.FoldersByCategory;
        NightSwitch.IsOn = settings.UncappedAtNight;
        ClipboardSwitch.IsOn = viewModel.WatchClipboard;
        NotifySwitch.IsOn = settings.NotifyOnCompletion;
        VerifySwitch.IsOn = settings.VerifyChecksums;
        ScanSwitch.IsOn = settings.ScanOnCompletion;
        EdgeSwitch.IsOn = viewModel.EdgeHide;
        IslandSwitch.IsOn = viewModel.ShowIsland;

        Select(ConcurrencyBox, settings.MaxSimultaneousDownloads.ToString());
        Select(GlobalLimitBox, settings.GlobalSpeedLimit);
        Select(RetryBox, settings.RetryPolicy);
        Select(AfterBox, settings.WhenAllComplete);

        _loading = false;
    }

    private static void Select(ComboBox box, string value)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] as string != value) continue;
            box.SelectedIndex = i;
            return;
        }

        box.SelectedIndex = 0;
    }

    private void OnFoldersToggled(object? sender, bool value) => Update(s => s.FoldersByCategory = value);

    private void OnNightToggled(object? sender, bool value) => Update(s => s.UncappedAtNight = value);

    private void OnNotifyToggled(object? sender, bool value) => Update(s => s.NotifyOnCompletion = value);

    private void OnVerifyToggled(object? sender, bool value) => Update(s => s.VerifyChecksums = value);

    private void OnScanToggled(object? sender, bool value) => Update(s => s.ScanOnCompletion = value);

    private void OnClipboardToggled(object? sender, bool value)
    {
        if (_loading) return;

        // Through the view model, which starts or stops the live subscription
        // as well as persisting the setting.
        _viewModel.WatchClipboard = value;
        _viewModel.Say(value ? "已开始监视剪贴板" : "已停止监视剪贴板");
    }

    private void OnEdgeToggled(object? sender, bool value)
    {
        if (_loading) return;
        _viewModel.EdgeHide = value;
    }

    private void OnIslandToggled(object? sender, bool value)
    {
        if (_loading) return;
        _viewModel.ShowIsland = value;
    }

    private void OnConcurrencyChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s.MaxSimultaneousDownloads = int.TryParse(ConcurrencyBox.SelectedItem as string, out int n) ? n : 3);

    private void OnGlobalLimitChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s.GlobalSpeedLimit = GlobalLimitBox.SelectedItem as string ?? "不限");

    private void OnRetryChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s.RetryPolicy = RetryBox.SelectedItem as string ?? "3 次");

    private void OnAfterChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s.WhenAllComplete = AfterBox.SelectedItem as string ?? "无操作");

    // ── the three rows with a chevron ─────────────────────────────────────

    private async void OnSavePathTapped(object sender, TappedRoutedEventArgs e)
    {
        string? chosen = await FolderPrompt.PickAsync();
        if (chosen is null) return;

        Update(s => s.DefaultSavePath = chosen);
        SavePathRow.Value = chosen;
    }

    private void OnScheduleTapped(object sender, TappedRoutedEventArgs e) =>
        ScheduleEditor.Visibility = ScheduleEditor.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void OnScheduleChanged(object? sender, TimePickerSelectedValueChangedEventArgs e)
    {
        if (_loading) return;

        string start = Format(OffPeakStartPicker.SelectedTime);
        string end = Format(OffPeakEndPicker.SelectedTime);

        Update(s =>
        {
            s.OffPeakStart = start;
            s.OffPeakEnd = end;
        });

        ScheduleRow.Value = $"{start} – {end}";

        // 夜间不限速 reads the window on the next tick, but saying nothing about
        // a zero-width window would leave the switch looking broken.
        if (OffPeakWindow.Parse(start, end) is null) _viewModel.Say("起止时间相同，夜间不限速不会生效");
    }

    /// <summary>Always HH:mm, which is what AppSettings stores and OffPeakWindow reads.</summary>
    private static string Format(TimeSpan? time) =>
        time is { } value ? $"{value.Hours:D2}:{value.Minutes:D2}" : "00:00";

    private void OnBossKeyTapped(object sender, TappedRoutedEventArgs e)
    {
        BossKeyCapture.Visibility = Visibility.Visible;
        BossKeyCapture.Focus(FocusState.Programmatic);
    }

    private void OnBossKeyCaptured(object sender, KeyRoutedEventArgs e)
    {
        // A modifier on its own is half a combination; keep waiting for the key.
        if (e.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or
            VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        var modifiers = HotKeyModifiers.None;
        if (IsDown(VirtualKey.Control)) modifiers |= HotKeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) modifiers |= HotKeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) modifiers |= HotKeyModifiers.Shift;

        if (e.Key == VirtualKey.Escape)
        {
            BossKeyCapture.Visibility = Visibility.Collapsed;
            return;
        }

        // Refuse rather than store something RegisterHotKey will reject: an
        // unmodified key would be swallowed system-wide.
        var candidate = new HotKeyBinding(modifiers, (int)e.Key);
        if (modifiers == HotKeyModifiers.None || HotKeyBinding.Parse(candidate.ToString()) is not { } binding)
        {
            _viewModel.Say("请按下带 Ctrl / Alt / Shift 的组合键");
            return;
        }

        BossKeyCapture.Visibility = Visibility.Collapsed;
        BossKeyRow.Value = binding.ToString();

        // The shell listens for this and re-registers, reporting a clash.
        _viewModel.BossKey = binding.ToString();
    }

    private void OnBossKeyCaptureLostFocus(object sender, RoutedEventArgs e) =>
        BossKeyCapture.Visibility = Visibility.Collapsed;

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnOptimizeClick(object sender, RoutedEventArgs e) =>
        _viewModel.Say("并发数优化会改动系统设置，已记录为待执行");

    private void OnClosed(object? sender, EventArgs e) => _viewModel.ActiveSheet = null;

    private void Update(Action<Models.AppSettings> change)
    {
        if (_loading) return;
        change(_viewModel.Settings);
        _viewModel.Persist();
    }
}
