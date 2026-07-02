# NetTrans

A WinUI 3 download manager, built from a Claude-designed HTML/CSS/JS mockup (see `mediation/` handoff bundle references in the original design). Targets `net8.0-windows10.0.19041.0` and `net10.0-windows10.0.19041.0` side by side, unpackaged (no MSIX required to run).

## Project layout

```
NetTrans/
  App.xaml(.cs)             App entry point, service wiring
  MainWindow.xaml(.cs)      Mica-backed window, custom title bar hookup
  Views/
    MainPage.xaml(.cs)      Shell composition (title bar, menu, command bar, nav, list, detail)
    Controls/               TitleBar, MenuBar, NavRail, CommandBar, PageHeader,
                             ActiveRow/CompletedRow, FileBadge, ProgressTrack,
                             DetailPane, Sparkline, SegmentMap, PasteInfoBar
    Dialogs/                NewDownloadDialog, SettingsDialog
  ViewModels/                ShellViewModel, DownloadItemViewModel, NewDownloadViewModel, SettingsViewModel
  Models/                    DownloadItem, DownloadStatus, FileKind, MirrorSource, AppSettings
  Services/                  IDownloadEngine/StubDownloadEngine (fake timer-driven progress),
                             IClipboardWatcher/ClipboardWatcher, ISettingsStore/JsonSettingsStore
  Resources/                 Tokens.xaml (design tokens as ThemeDictionaries), Icons.xaml,
                             Styles/ (Buttons, NavItem, Row, Toggle, TextBox, Modal, Text)
  Converters/                BindingHelpers (x:Bind function converters) + IValueConverters
```

## Building

Requires Visual Studio 2022/2026 with the **Windows App SDK** / **WinUI application development** workload (or the .NET 8/10 SDK + Windows App SDK tooling on the command line). This project was authored without access to a Windows toolchain, so give it a first build pass on Windows before relying on it — likely fixes are typo-level XAML/C# issues, not architectural ones.

```
dotnet build -f net8.0-windows10.0.19041.0 -r win-x64
```

or open `NetTrans.sln`-equivalent (add one, or open the `.csproj` directly) in Visual Studio and F5.

## What's real vs. stubbed

- UI is fully wired end-to-end (MVVM, CommunityToolkit.Mvvm) — no mock ViewModels.
- `StubDownloadEngine` fakes progress with a 250ms timer (light noise, one scripted error at 30s) so the UI has something live to show immediately. Swap it for a real multi-segment HTTP engine behind `IDownloadEngine` when you're ready.
- Settings persist to `%LOCALAPPDATA%\NetTrans\settings.json`.
- Clipboard URL detection is live (`Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged`).
