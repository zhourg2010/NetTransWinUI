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
  Net/
    IHttpTransport.cs       The HTTP surface a transfer needs
    HttpTransport.cs        HttpClient behind it: probe, then ranged reads
    RemoteFileInfo.cs       Length, range support, validators, filename
  Download/
    DownloadEngine.cs       The queue: concurrency, priority, global rate cap
    ITransferJob.cs         What the queue drives: a ranged file or a playlist
    DownloadJob.cs          One ranged transfer: probe, plan, fetch, write, resume
    PlaylistJob.cs          One segmented transfer: segments, in order, into files
    TorrentJob.cs           One torrent, plus resolving a magnet to a metainfo
    SegmentPlan.cs          How a file is split, and the 96-cell chunk map
    ResumeState.cs          The .nettrans sidecar behind 断点续传
    PlaylistResume.cs       The .nettrans-hls sidecar: whole segments, not offsets
    FileSink.cs             Concurrent disjoint writes via RandomAccess
    TokenBucket.cs          The 限速 dropdowns
    SpeedMeter.cs           Sliding-window throughput
    SpeedLimits.cs          Reads "512 KB/s" back into a rate
    IClock.cs               Time, injected, so the loop is testable
  Torrent/
    Bencode.cs              The format, with the byte spans an info hash needs
    TorrentMetainfo.cs      Files, pieces, hashes; a piece can span two files
    MagnetLink.cs           xt / dn / tr, hex or base32
    Announce.cs             The tracker request, and raw-byte query escaping
    HttpTracker.cs          The original announce
    UdpTracker.cs           BEP 15: connect, then announce
    TrackerPool.cs          All of them at once; a dead one is normal
    PeerWire.cs             Handshake and message framing
    PeerSession.cs          One peer: fetch pieces, and serve them back
    PiecePicker.cs          Rarest-first, sequential, endgame, file selection
    PieceStore.cs           Verify before writing; read back to upload
    TorrentSwarm.cs         Announce, connect, replace peers as they fail
    MetadataExchange.cs     BEP 9, which is how a magnet gets a file list
    TorrentOptions.cs       Seeding limits, file selection, force recheck

  Media/
    SegmentedStream.cs      What HLS and DASH agree on: ordered segments, a container
    M3U8.cs                 Master and media playlists, keys, byte ranges
    HlsPlaylist.cs          Follows a master to a rendition; refuses what it cannot do
    Mpd.cs                  DASH: SegmentTemplate / Timeline / List / Base, $Number$
    DashManifest.cs         Prefers a muxed track; otherwise video + audio
    StreamLoader.cs         Picks the reader by manifest kind
    HlsDecryptor.cs         AES-128 segments, with the key fetched once
    PlaylistUrl.cs          Whether a URL is a manifest, and which kind
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
  Services/                 IDownloadEngine, HttpDownloadEngine (the real one),
                            StubDownloadEngine (design mode), DockManager,
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

## CI

`.github/workflows/build.yml` runs on every push to a `claude/**` branch and
every PR into `main`:

| Job | Runner | What it covers |
| --- | --- | --- |
| `test` | `ubuntu-latest` | `dotnet test NetTrans.Core.Tests` — the whole portable half, in ~15s |
| `build` | `windows-latest` | `dotnet build NetTrans` for both target frameworks |

This is the only place the WinUI shell gets compiled, since it was authored
without a Windows toolchain to hand. It currently builds clean on both
`net8.0-windows10.0.19041.0` and `net10.0-windows10.0.19041.0`.

**Compiling is not running.** Nothing in CI launches the app — GitHub's hosted
runners are headless, so everything that only exists at runtime is still
unverified: the magnetic docking, the window regions and their squared-off
corners, the island following the task frame, edge-hide, the boss key, and
whether any of it looks like the design. That needs a real Windows desktop.

## Testing

```sh
dotnet test NetTrans.Core.Tests
```

290 tests, ~15 seconds, on Linux, macOS or Windows — it never touches WinUI.

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

The download engine's first CI run found four more, all of which would have
shipped:

- **Speed readouts could spike.** The meter divided by the span between the
  samples still inside its window; once only one was left that span collapses
  towards zero and the rate explodes. It divides by elapsed time now, capped at
  the window.
- **Pause could be dropped.** A pause asked for between a task being queued and
  its transfer loop starting was lost, so 暂停 right after adding a download
  kept downloading.
- **A queued task's action did nothing.** `Toggle` sent anything not
  `Downloading` to `Resume`, which put a queued task straight back in the queue.
- **The resume timer busy-looped under a fake clock**, and swamped the recorded
  backoff delays it shared a channel with.

The download engine is covered the same way, against a fake server and an
in-memory file: how a file is split, that the ranges tile it exactly, that a
dropped connection resumes from its own offset instead of restarting, that a
resumed transfer picks up from the sidecar, that a changed ETag throws the
partial file away, that pausing keeps its progress, and that the queue honours
concurrency and priority. The clock is injected, so backoff and rate limiting
are exercised without the suite ever sleeping.

What is *not* covered: anything that needs a window. `WindowChrome`,
`DockManager`'s timers, `ShellHost` and every XAML view are exercised only by
running the app. `DockGeometry` extracts the part of the docking behaviour that
is pure arithmetic, which is the part most likely to be subtly wrong.

## Downloading

`NetTrans.Core/Download` is a real multi-segment HTTP downloader. `HttpDownloadEngine`
in the shell is a thin adapter over it: the transfers run on the thread pool and
mutate their models there, and the UI reads those models on a 500ms timer tick,
which is the only place view models change.

- **Probing.** HEAD first, then a one-byte range GET, because plenty of servers
  answer HEAD with no length or advertise `Accept-Ranges` and then ignore the
  header. Only a 206 with a `Content-Range` counts as proof that ranges work.
- **Segmenting.** A file that can be ranged is split across the task's 连接数,
  never into pieces below 1 MB. A server that refuses ranges, or will not say how
  long the file is, gets a single connection reading to EOF.
- **Writing.** Connections write concurrently to disjoint offsets through
  `RandomAccess` on a shared handle — a `FileStream` carries one shared file
  pointer, so seek-then-write would race.
- **Resuming.** A `.nettrans` sidecar next to the target file records each
  segment's position, rewritten every few seconds. On restart it is only trusted
  if the length and the ETag (or Last-Modified) still match; otherwise the
  transfer starts over and says so in the log.
- **Retrying.** A dropped connection resumes from its own offset with
  exponential backoff, up to the retry budget. Asking for a range and getting
  `200` back is refused outright rather than silently corrupting the file.
- **Rate limiting.** 全局限速 and the per-task 速度上限 are token buckets, one
  shared and one per job, both driven by the injected clock. Changing either
  from the inspector reaches the running transfer rather than the next one.
- **Live detail.** The inspector's 分块, 连接 and 日志 tabs are fed by the real
  transfer: the chunk map comes from segment positions, the per-connection rates
  from a meter per segment.
- **What the site wants.** Cookies are kept for the life of the transport, so a
  session handed out while sniffing a page is still there when the media URL is
  fetched. The page a link was sniffed from is sent as its `Referer` — plenty of
  sites 403 anything else. `https://user:pass@host/file.iso` authenticates with
  Basic rather than dropping the credentials; the stored and displayed URL is the
  one without them. 代理 is 系统代理 by default and can be pointed at a
  `host:port`; changing it reaches transfers already running.

Running the app with `--demo` swaps the real engine for `StubDownloadEngine`,
which replays the handoff's seed data. That is how the UI is worked on without a
network or real files.

## The rest of the features

- **校验 SHA-256** streams the file so a 6 GB ISO does not have to fit in memory,
  and runs automatically on completion when 完成后校验 is on. Comparing against a
  published checksum tolerates the usual `<hash>  <filename>` shape.
- **批量下载** really crawls: 抓取深度, 仅限本站 and 后缀筛选 are honoured, every
  found link is probed for its size (which is what 最小文件 filters on), pages are
  never fetched twice, and pages that could not be read are reported rather than
  quietly shrinking the result.
- **视频嗅探** works from the page's own markup, since the portable build injects
  nothing into a browser: `<source>` elements with their labels, and the media
  URLs that players leave in script blobs. Best quality first, audio last.
- **HLS (`.m3u8`) and DASH (`.mpd`) download for real.** A manifest gets a
  segment transfer rather than a ranged one: follow it to its best rendition,
  fetch the segments a windowful at a time, and write them into one file
  strictly in the order the manifest named them. No remuxing is involved and
  none is needed — MPEG-TS segments concatenate into a playable `.ts` because
  that is what a TS stream is, and fMP4 segments concatenate onto their init
  segment into a playable `.mp4`. AES-128 segments are decrypted on the way in,
  with the key fetched once and the IV taken from the tag or, when it omits one,
  from the media sequence number. Pausing keeps whole segments and resuming
  appends; progress is counted in segments, since a manifest never states a byte
  count.
- **DASH splits audio from video, so one task can produce two files.**
  Interleaving two fMP4 streams into a single MP4 is a muxer, not a downloader.
  When the manifest offers a muxed Representation it is preferred and there is
  one file; when it does not, the video and its best audio are both fetched, as
  `名字-视频.mp4` and `名字-音频.mp4`, and the log says so. A silent file labelled
  as the video would be the dishonest alternative.
- Refused up front, with the reason on the row: a live manifest (no end to
  download to) and SAMPLE-AES (needs the codec, not a key).
- **新版本** re-probes the URL and compares ETag, then Last-Modified, then length
  against what was recorded when the file was fetched. It runs after every
  completed download and from 检查更新 in the context menu.
- **打开文件 / 在文件夹中显示 / 打开文件夹 / 重命名** hand off to the shell. Rename
  moves the file and drops the stale resume sidecar, and refuses while a
  transfer is live rather than leaving the writer pointed at the old handle.

### What the 设置 sheet actually changes

Every switch and dropdown in the sheet drives something; none of them are
decoration.

- **按分类建子文件夹** files a new task under 软件 / 视频 / 文档 / 音乐 / BT inside
  the save path, and picks `name (2).ext` when something is already headed for
  that path — a file on disk, or another queued task the disk cannot know about
  yet.
- **同时下载数** and **全局限速** are the queue's concurrency and its token
  bucket; both take effect without a restart.
- **夜间不限速** lifts the cap while the clock is inside **计划时段**, re-checked
  on the tick that already runs, so the window opens and closes on time. The
  window wraps past midnight, because the default 23:00 – 07:00 does.
- **失败自动重试** is the per-connection retry budget. A value that will not
  parse falls back to the sheet's default rather than to zero.
- **全部完成后** arms once per batch, on the queue draining rather than on any
  single file finishing — an idle queue is the app's normal state and must not
  shut the machine down by itself. 退出程序 / 休眠 / 关机 each get a 20-second bar
  with 取消 first; cancelling stands down that batch, not the setting.
- **完成后校验** hashes the finished file (SHA-256) off the UI thread.
- **完成后扫描** writes the `Zone.Identifier` mark of the web — the durable half,
  since SmartScreen and Protected View keep honouring it after the file moves —
  and asks Defender for a single-file scan where there is one. Remediation stays
  off: quarantining a file the user just asked for, silently, is not ours to do.
- **剪贴板监听**, **完成提示** and **贴边隐藏** drive the clipboard watcher, the
  completion banner and the edge-hide behaviour. The clipboard switch starts and
  stops the live subscription immediately, not at the next launch.
- **默认位置**, **计划时段** and **老板键** — the three rows with a chevron — are
  editable: a folder picker, a pair of 24-hour time pickers, and a key capture
  that re-registers the global hotkey as soon as it is pressed. A combination
  with no modifier is refused rather than stored, since Windows would swallow
  the bare key system-wide, and one another application already owns is
  reported instead of silently failing.

### BitTorrent

Torrents and magnet links download and **upload**, driven by the same queue as
everything else.

- **Bencode** records where each value sat in the file, because a torrent's info
  hash is the SHA-1 of its info dictionary *as written* — re-encoding a
  non-canonically-ordered torrent, which clients accept, gives a hash no tracker
  and no peer recognises.
- **Trackers**: HTTP and BEP 15 UDP, announced to in parallel with the peers
  pooled. A dead tracker is skipped, since a public torrent routinely lists a
  dozen with half of them gone. The announce URL's own query is preserved —
  that is where a private tracker's passkey lives.
- **Peers**: the wire protocol with an eight-deep request pipeline, rarest-first
  piece selection with sequential as an option, and an endgame that races the
  last pieces once every one of them is already assigned. A piece that does not
  hash is never written, and a peer that sends two bad ones is dropped.
- **Uploading is real** and reported honestly to the tracker. A finished torrent
  keeps serving rather than hanging up — that is when it is worth the most to
  the swarm. 做种限制 stops at a share ratio or a seeding time, checked on its
  own clock rather than only when a peer leaves: one leech that stays connected
  for hours is the ordinary case, and waiting for it to hang up is how a limit
  gets blown past.
- **限速 covers torrents too.** 全局限速 and the per-task cap are one budget for
  the whole swarm rather than one per connection, applied in both directions —
  上传限速 is a row on the 种子 sheet. The inspector's 连接 tab shows a rate per
  peer, which is where one stalled peer among eight is visible.
- **Magnet links** fetch their metainfo from peers (BEP 9) before anything else
  can happen, and the assembled result is checked against the hash the link
  named: the pieces come from several peers, so a whole that does not hash means
  one lied, and the only honest response is to start over.
- **Resuming** keeps a bitfield rather than an offset, since pieces arrive out
  of order and each is verified alone. With no sidecar but files on disk, they
  are hashed instead of fetched again — which is also what makes seeding an
  already-downloaded torrent work.
- **选择文件** narrows a multi-file torrent to what was asked for. The 种子 sheet
  lists the files of a .torrent it can read and each one can be unticked; a
  magnet has no list until peers have sent one, so it is not offered the choice.
  A piece straddling a wanted and an unwanted file is still fetched, since a
  piece is the smallest verifiable unit — so the size shown is the piece cost
  rather than the sum of the file sizes, which is also why unticking a small
  file next to a wanted one often saves nothing.
- **强制校验** is a row in the inspector's BT group. It runs the task again with
  the resume record ignored and the files on disk hashed instead — for a stale
  sidecar, for files moved in from elsewhere, and for cross-seeding, which
  begins with exactly that question. A finished torrent can be rechecked too,
  which is what `Restart` exists for: 继续 deliberately does nothing to a row
  that has already completed.

**Not implemented, and named in the sheet rather than failed quietly:** DHT and
PEX, so a magnet with no trackers cannot find peers; MSE/PE encryption; and µTP.
A private torrent is unaffected by the first of those — it is tracker-only by
definition — and `PeerDiscoveryAllowed` is the gate any of them has to pass
before being added. NetTrans is also not on any private tracker's client
whitelist, which some sites check at announce.

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
- Downloading is real: multi-segment HTTP with resume, retry and rate limiting.
  See **Downloading** above.
- `StubDownloadEngine` is still there behind `--demo`, seeded with the handoff's
  own `SEED` array and ticking on the same 900ms cadence and growth curve.
- Settings persist **portably**, next to the executable
  (`NetTrans.settings.json`), falling back to `%LOCALAPPDATA%\NetTrans` when that
  directory is read-only — matching the 设置 sheet's promise of no registry writes.
- Clipboard URL detection is live (`Clipboard.ContentChanged`).
- Still unimplemented: DHT / PEX, BitTorrent transport encryption, and live
  streaming for HLS and DASH. Each is called out in the UI with the reason
  rather than failing quietly.
