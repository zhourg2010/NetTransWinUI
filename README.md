# NetTrans

A WinUI 3 download manager built to the **FlashGet Mini v2** design handoff
(`FlashgetMini.zip` → `design_handoff_flashget_mini_v2/`): an iOS-idiom desktop
shell made of two independent 536 × 680 frames that snap together Winamp-style,
with a Dynamic-Island-like speed capsule floating above them.

Targets `net8.0-windows10.0.19041.0` and `net10.0-windows10.0.19041.0` side by
side, unpackaged (no MSIX required to run).

The solution is split so that the half of the app with no dependency on WinUI
can be built and tested anywhere:

| Project | Target | Runs on |
| --- | --- | --- |
| `NetTrans` | `net8.0/net10.0-windows10.0.19041.0` | Windows only — the WinUI shell |
| `NetTrans.Core` | `net8.0` | anywhere — model, formatting, list, docking and progress rules |
| `NetTrans.Core.Tests` | `net8.0` | anywhere — xunit tests over `NetTrans.Core` |

## What the design asks for, and where it lives

| Design piece | Implementation |
| --- | --- |
| `.frame.win2` — task frame, 536 × 680, 16px corners | `Views/MainShell.xaml` in a borderless window built by `Shell/ShellHost.cs` |
| `.frame.sidewin` — inspector frame, same size | `Views/InspectorShell.xaml`, its own window |
| Magnetic docking, 18px threshold, blue guide, .24s settle | `Services/DockManager.cs` + `Shell/SnapGuideWindow.cs` |
| `.bond-r/-l/-t/-b` — corners squared on the bonded edge | `Interop/WindowChrome.ApplyCorners` (a Win32 window region) |
| `.nav` drag-to-move | `WindowChrome.BeginDrag` → `WM_NCLBUTTONDOWN` / `HTCAPTION` |
| `.seg` — sliding segmented control | `Views/Controls/SegmentedControl.xaml` |
| `.row` + `.swipe` + `.card.dense` | `Views/Controls/TaskRow.xaml` |
| `.morerow` — five-row fold | `ShellViewModel.VisibleTasks` + the fold button in `MainShell.xaml` |
| `.bar` — the seven toolbar icons | `MainShell.xaml`, bottom row |
| Inspector 概览 / 分块 / 连接 / 日志 | `Views/InspectorShell.xaml` with `RingProgress`, `BlockGrid`, `SessionBars`, `ConnectionList` |
| `.newver` — 服务器上有更新版本 | `NewVersionInfo` on the model, notice at the top of the inspector body |
| `.sheet` — iOS half sheets | `Views/Controls/SheetHost.xaml` + `Views/Sheets/*` |
| `.ctx` — 244px popovers (context / 新建 / 显示与排序 / 托盘) | `Views/Controls/PopoverControl.xaml` |
| `.island` — the 灵动岛 | `Views/Controls/IslandControl.xaml`, its own top-most window |
| `.toast`, `.banner`, `.drop` | overlay layer in `MainShell.xaml` |
| 贴边隐藏, 老板键 Ctrl+Alt+H | `Shell/ShellHost.cs` (`RegisterHotKey`, edge slide) |
| Design tokens, icon set | `Resources/Tokens.xaml`, `Resources/Icons.xaml` |

## Project layout

```
NetTrans.Core/              No WinUI, no Windows — buildable and testable anywhere
  Models/                   DownloadItem, DownloadStatus, FileKind, TaskPriority,
                            NewVersionInfo, LogEntry, AppSettings, DockSide
  Services/
    FormatHelpers.cs        mb() / spd() / eta() from the handoff
    TaskPresenter.cs        Every string the design derives from a task
    TaskQuery.cs            Category, tab, search, sort and the five-row fold
    DockGeometry.cs         Dock positions, the 18px threshold, the guide rect
    ProgressSimulator.cs    The stub engine's arithmetic, randomness injected
    Easing.cs               cubic-bezier(.32,.72,0,1)

NetTrans.Core.Tests/        xunit over NetTrans.Core; see "Testing" below
  Golden/golden.json        Expectations generated from the handoff's own source

NetTrans/
  App.xaml(.cs)             Entry point; builds ShellHost
  Shell/
    ShellHost.cs            The three frames, docking, island follow, edge-hide, boss key
    SnapGuideWindow.cs      The 3px blue snap guide
  Interop/
    NativeMethods.cs        Win32 surface (subclassing, regions, hotkeys, DWM)
    WindowChrome.cs         One frameless, 16px-rounded, drag-anywhere window
  Views/
    MainShell.xaml(.cs)     Task frame
    InspectorShell.xaml(.cs) Inspector frame
    Controls/               StrokeIcon, SegmentedControl, TaskRow, IosSwitch, FormRow,
                            CheckRow, RingProgress, BlockGrid, SessionBars,
                            ConnectionList, SheetHost, PopoverControl, IslandControl
    Sheets/                 Add / Batch / Torrent / Sniff / Settings
  ViewModels/               ShellViewModel, DownloadItemViewModel
  Services/                 IDownloadEngine/StubDownloadEngine, DockManager,
                            ThemeBrushes, clipboard + settings stores
  Resources/                Tokens.xaml (iOS tokens as ThemeDictionaries), Icons.xaml,
                            Styles/ (Text, Surfaces, Buttons, Inputs, Shadows)
  Converters/               BindingHelpers (x:Bind function bindings)
```

## Building

Requires Visual Studio 2022/2026 with the **Windows App SDK** / **WinUI
application development** workload (or the .NET 8/10 SDK plus Windows App SDK
tooling on the command line).

```
dotnet build -f net8.0-windows10.0.19041.0 -r win-x64
```

`NetTrans.Core` and its tests need none of that and build with a bare .NET 8
SDK on any OS.

This rewrite, like the original scaffold, was authored without access to a
Windows toolchain, so **give the shell a first build pass on Windows before
relying on it**. Expect typo-level XAML/C# fixes rather than architectural
ones. The two places most worth checking first are the
`CommunityToolkit.WinUI.Media` shadow API in `Resources/Styles/Shadows.xaml`
and the Win32 interop in `Interop/`.

## Testing

```sh
dotnet test NetTrans.Core.Tests
```

Runs on Linux, macOS or Windows — it never touches WinUI.

The expectations are not hand-written. `tools/golden/generate-golden.mjs` pulls
`mb()`, `spd()`, `eta()`, `STATE_CN`, `SEED` and the `SORT` map **out of the
design handoff's own source** with regexes, evaluates them under Node, and
writes the results to `NetTrans.Core.Tests/Golden/golden.json`. The tests assert
against that file, so a disagreement with the prototype fails the build instead
of both sides being wrong in the same way. See `tools/golden/README.md` for how
to regenerate it.

Writing them this way immediately caught three bugs in the first pass of the
rewrite:

- **Mid-point rounding.** JavaScript's `Math.round` and `toFixed` round halves
  away from zero; .NET's `Math.Round` rounds them to even. 62.5 MB rendered as
  "62 MB" instead of the design's "63 MB".
- **Descending sort.** The prototype negates its comparator rather than
  reversing the sorted list, and `Array.prototype.sort` is stable, so tied rows
  keep their original order in *both* directions. Sorting ascending and
  reversing put the five stalled tasks in the wrong order.
- **加入时间 order.** `SORT.added` is `a.id - b.id` — creation order, not queue
  position. The implementation was sorting by queue index, so 移到队首 appeared
  to reorder the list when the design says it does not.

What is *not* covered: anything that needs a window. `WindowChrome`,
`DockManager`'s timers, `ShellHost` and every XAML view are exercised only by
running the app. `DockGeometry` extracts the part of the docking behaviour that
is pure arithmetic, which is the part most likely to be subtly wrong.

## Deliberate departures from the handoff

Everything else is pixel-for-pixel; these could not be, and each is commented at
the site:

- **Snap-guide glow.** The design puts a 12px blue glow around the `.snapline`.
  A WinUI 3 window cannot be partially transparent, so the guide is the solid
  3px bar only (`Shell/SnapGuideWindow.cs`).
- **Island shadow.** `.island`'s `0 10px 30px` shadow would be clipped by its own
  window region; it uses the OS window shadow instead.
- **Tray pill.** The prototype floats a separate tray pill on the desktop layer
  showing the same total speed as the island. Rather than duplicate it, the
  island carries the tray menu — click it to get 显示主窗口 / 贴边隐藏 / 老板键 / 退出.
- **Green traffic light.** The frame is a fixed 536 × 680, so there is nothing to
  zoom to; the green dot opens and closes the inspector frame, the only thing
  that changes the shell's footprint.
- **Typeface.** Windows has no SF Pro. The handoff's own fallback chain is
  Inter Tight → system-ui, and Segoe UI Variable is the closest system face, so
  it leads the stack in `Tokens.xaml`.
- **Dark theme.** The handoff only specifies a light palette. `Tokens.xaml` adds
  the iOS system-dark counterpart of every token so the app can follow the
  Windows theme without inventing new semantics.
- **Tabular figures.** WinUI has no font-feature API on `TextBlock`; Segoe UI
  Variable's lining figures are already fixed-width, so numbers still align.

## What's real vs. stubbed

- UI is wired end to end (MVVM, CommunityToolkit.Mvvm) — no mock view models.
- `StubDownloadEngine` is seeded with the handoff's own `SEED` array and ticks on
  the same 900ms cadence and growth curve. Swap it for a real multi-segment HTTP
  engine behind `IDownloadEngine`.
- Settings persist **portably**, next to the executable
  (`NetTrans.settings.json`), falling back to `%LOCALAPPDATA%\NetTrans` when that
  directory is read-only — matching the 设置 sheet's promise of no registry writes.
- Clipboard URL detection is live (`Clipboard.ContentChanged`).
- 打开文件 / 在文件夹中显示 / 重命名 / 校验 SHA-256 report through the toast lane; they
  need the real engine to do anything.
