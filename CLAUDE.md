# WaveDude Next — project notes for Claude

Last updated 2026-10-23. The long history is in git (`git show a6f85dc:CLAUDE.md`). Keep this file short.

## What this is

A 2D wave-equation simulator (field and intensity views, scenes from images, point and composite sources, probes
with live graphs, video export): a .NET 10 library (`WaveDudeNext/`) plus a WPF app (`WaveDudeNext.WPF/`), the
successor of Jeroen Vleggaar's (Huygens Optics) **Wave Dude 2D**, GPL-3.0-or-later, by Alexander Graef. Jeroen's
VB source and the C# port of his binary live in git history up to `d3ac0d8`; read his VB there when his intent
matters. The harnesses are `_tools/library_check` (console, not in the `.sln`) and `_tools/ui_check` (PowerShell).
The user works in Visual Studio with Claude Code in the chat, permissions skipped, often at the machine while
harnesses run.

## How to work here (the user's rules)

- **Git only on his word for the change at hand.** "Commit" in his latest message means commit now; otherwise
  report the work as uncommitted. Never commit, push, reset, amend, stash, checkout, rebase or merge on your own.
  Enforced by `.claude/hooks/git-guard.py` (needs Python on the PATH, else every shell command is blocked); a shell
  command whose text merely mentions git plus such a word is denied too. End commit messages with
  `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- **No design changes on your own.** Anything beyond a mechanical change goes to him as options with pros and
  cons, complete, with links when naming a project, then built as chosen. Never invent a feature nobody asked for
  (the scratch budget that skipped "too expensive" frames was one; torn out). Bugs you find: fix and prove.
- **Verification matches the change**: a clean build (0 warnings; `///` docs and the header are enforced) plus one
  targeted probe or screenshot for a local change, `library_check` (20 s) for a library change, a UI harness only
  when its flow changed, **once**. Never loop a harness for a flake, never run the suite after an edit.
- **Measure properly**: warm the JIT; never poll UI Automation while measuring (it loads the UI thread it
  measures); the app's own log lines are the measurement. **A hot loop measured alone may be a tier-1 figure the app never
  reaches** (2026-11-03: the part painter took 31 ms in a probe and 115–460 ms in the app, the same call; in a busy WPF process
  the runtime kept its per-pixel methods at tier-0 — the probe pinned there with `DOTNET_TC_CallCounting=0` reproduced the
  app's 212 ms): compare a probe against the app's log, and take a hot path out of tiering with
  `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` where the gap is that.
- **Harnesses are keyboard-only**: post `WM_KEYDOWN`/`WM_CHAR` to the window, read state through UIA properties,
  never UIA patterns, the mouse, `SendKeys` or `SetForegroundWindow` (each activates the window). Modifiers cannot
  be posted, so every command a harness presses has an unmodified key. Everything a harness needs has an
  `AutomationId`. Keep a failing run's output; a filtered run cannot be read afterwards.
- **Keyboard first in the app**: every control reachable with Tab and the arrows, focus visible; a mouse-only
  control is a bug. The menu bar is out of the Tab cycle (F10).
- **Commands writing outside the repo** hit the sandbox prompt: set `dangerouslyDisableSandbox` up front.
- **No scratch files in the repo tree** (2026-10-19, after screenshots and probe scripts were left behind for the user to
  clean up): edit scripts, one-off probes, test projects and screenshots live under `%TEMP%\WaveDudeNext` (create it,
  `dangerouslyDisableSandbox`), are named once with their full path when the user should look, and are deleted by the
  session that made them; a harness project file beside `Common.ps1` is deleted in the script's `finally`.
- **He writes his own prose** (README, mails): offer verified facts and screenshots; never "I" sentences for him,
  never claims about him he has not made.
- **Spelling**: British in the UI, except **"license"** everywhere (the GPL's own word). No talk of BOMs or line
  endings.
- **Dependencies**: no package named after a person, no AGPL/GPL libraries, flag vibe-coded candidates; in-house
  preferred (docking, encoders, colour picker are in-house). Microsoft packages are fine.
- **WPF idioms**: commands not `_Click`, bindings not hand-pushed state, `DynamicResource` for every theme brush and
  string, `XmlSerializer` with sparse XML (attributes, repeated children, `[DefaultValue]`), Segoe UI 9 pt, the
  dnSpy look (its sources are GPL: look, do not copy). Inside a `DataTemplate` reference the string keys directly
  (`{DynamicResource Hint.Type.Member}`): the `meta:Label`/`meta:Hint` extensions wrap a resource reference a template
  rejects for a string property and shows as text for an object one (2026-10-22, found on the fibre's node rows). An
  The XAML designer never runs a window's constructor: what the preview must show lives in XAML, as the dock host's
  default arrangement does (`DockHost.DefaultLayout` in `MainWindow.xaml`, the one source for the first start and the
  designer, which then shows every hidden pane at its default side, Messages at the bottom, 2026-10-26; nested types
  cannot be XML names, so the layout nodes are top-level classes). An
  implicit style never reaches a class derived from `Window`, so every window names the theme's
  (`Style="{StaticResource {x:Type Window}}"`, 2026-10-23: without its layout rounding the pictures sat at fractional
  pixels and WPF resampled them; its Display text formatting had never applied either).
- **Model conventions**: a new property gets its XML attribute with a default, `[Category]`/`[DisplayName]`/
  `[Description]`, a `///` summary, a clamp in `Normalize()` with the matching `[Range]` beside the default (2026-10-21:
  the bounds are readable from outside, `library_check` proves each against `Normalize()`; a bound that depends on another
  value stays in code alone and is named in its `dependentBounds`; since 2026-10-22 it finds every part kind itself and
  fails a persisted number that `Normalize()` clamps without a `[Range]`), and `Label.`/`Hint.`/`Enum.<Type>.<Member>` keys in all four string tables. Randomness from
  `Project.Seed` through `Pcg32`; generated data is never persisted.
- **C# style**: modern and concise, expression-bodied accessors, no `this.`, no `IDisposable` on interfaces,
  `out` not `ref`, tabs, Allman; `///` docs state threading, units and coordinate systems. `grep -E`, not `-P`.
- **Log decisions at Debug** (scratch requests, stale marks, postponed re-runs, export steps). Info and up reach
  the status bar; Trace only in Debug builds. Log messages are not localised, and their numbers are invariant (NLog's
  DefaultCultureInfo, 2026-10-27, the user's rule: a report's reader or tool must not trip over a decimal comma). **The log must let a report be
  diagnosed** (2026-10-27, the user's rule after Jeroen's log named the property edited but not the value, nor the
  grid): every engine logs its grid, canvas, rim, edges, stride, sub-steps and work groups when built; a GPU device's
  facts (versions, units, clock, memory, limits) once per process; the scratch request names the newest edit with its
  value (after the debounce, so a typed number shows as typed); every change at Trace as old → new
  (`PropertyValueChangedEventArgs` from `SetProperty`, carried by `ProjectChange.Values`, `library_check` proves it). **A malformed project, colour scheme or settings file is
  quoted in its error's entry** (2026-11-03, the user's rule): the catch logs an `UnreadableFileException` wrapping the error,
  whose `ToString` appends the file's text, the first 64 K characters and the line an XML error names beyond them, so the
  log file and the Messages pane's tooltip carry it while the status bar and the dialog keep the one-line message; the
  settings file needs it most, since the next save writes the defaults over it (moving it aside is still open).
- **Do not resurrect**: a probe signal lane, timeline tracks, zoom and pan of the axis, thumbnails, a run progress
  bar, the scratch budget and its "not computed" note, a GPU JPEG front end, a Direct3D display path, a bent
  rotation lever. **Do not change**: the readout showing both frame and step (undecided).
- Collect the reusable rules for other projects only when he asks.

## Build and run

- `dotnet build WaveDudeNext.sln` → 0 warnings. Harnesses use `WaveDudeNext.WPF\bin\Debug\net10.0-windows\WaveDudeNext.WPF.exe`.
- `dotnet run --project _tools/library_check -c Release` (`-- export`, `-- bench [w h]`, `-- bench-render [w h]`,
  `-- bench-export [w h]`); `powershell -ExecutionPolicy Bypass -File _tools/ui_check/<script>.ps1` (`-Trace`).
- Publish: `dotnet publish WaveDudeNext.WPF/WaveDudeNext.WPF.csproj -p:PublishProfile=SingleFile`. Version in
  `Directory.Build.props`; the commit hash lands in About. The license header is the `.editorconfig`
  `file_header_template` (its warranty sentence ends in a full stop: Roslyn cuts the value at `;`). LICENSE.md is
  the FSF's GPL-3.0 Markdown verbatim, nothing else (GitHub's detector rejects additions); the About dialog shows
  it as a document built by `Build/Documents.targets`, whose converter `Build/MarkdownToFlowDocument.cs` (an inline
  MSBuild task, C# 7.3 and netstandard2.0 for Visual Studio's MSBuild; excluded from the app's compile) makes MANUAL.md
  the manual's document too.
- The library is compiled optimised in every configuration (flip it locally to step through engine code). The user
  settings are one sparse XML file, `%LOCALAPPDATA%\WaveDudeNext\Settings.xml` (`Settings.cs`, `XmlSerializer`, 2026-10-26, the
  user's choice: .NET's `ApplicationSettingsBase` kept a `user.config` per exe path and version under a folder named after
  the assembly's company, his name by default; Authors and Company are now the product's name, nothing personal in folder
  names). Folders under `%LOCALAPPDATA%\Alexander_Graef` and `%LOCALAPPDATA%\WaveDudeNext.WPF` are leftovers of that. Close Visual Studio before renaming a
  project folder.

## Architecture

```
Project (Model/) --RunSpec.Create--> RunSpec (immutable snapshot); the scene pixels come from MainWindow.Live's
   LoadSceneImage: the decoded image (or a blank canvas) with the enabled optical parts painted into red by
   PartPainter (Parts/), cached by (image, parts version, size, background) so an unchanged scene is the same reference
StartRun: memory budget -> DiscardRun (returns the old store's release task) -> FrameStore -> SimulationRun
SimulationRun: stepping loop + writer threads; IWaveEngine (WaveSimulator CPU | GpuWaveSimulator OpenCL);
   CaptureFrame/AwaitFrame -> FrameStore.Write; one heartbeat post (<= 30/s) at Background priority; final post sets
   FrameCount, then State
ScratchRunner: one kept engine; "frame k of the project as it is now" from step 0, probes sampled; newest request
   wins: one superseded while stepping is abandoned within 16 steps (logged), the newest is computed in full, however long
FramePresenter: CopyLayer -> 8-bit: FrameRenderer.RenderIndexed at the store's size, WPF scales; 16-bit: ScreenRenderer at
   the picture's size on screen -> the WriteableBitmap, copied on the pool between Lock/Unlock on the UI thread; only
   the newest request is rendered
ExportJob (own STA thread): CopyLayer -> FrameRenderer -> outline -> probe boxes/markers -> sinks (PNG, JPEG, MPEG-1, AVI, MP4, MOV)
FrameStore: native memory up to the budget, the rest in a temp file (%LOCALAPPDATA%\WaveDudeNext\cache, DeleteOnClose)
```

- **Engine contract**: padded grid (`Stride = Width + 2 + 2 × Rim`: a negative damping edge is a rim of that many cells
  outside the canvas, simulated and never shown, the canvas starting at (Rim, Rim); `Scene.Edges` Open puts Mur's
  first-order absorbing condition on the ring, Reflective is Jeroen's wall; a new project is Open with a 32 px rim outside
  (2026-10-22, the user's choice, after the rim measurements below; no damping edge from 2026-10-19 until then, when the
  open ring was measured on a long-wave pulse: the open ring alone returns 0.05 to 0.09 % of the
  amplitude, a rim at its best the same, a rim damped to a standstill 1 %, the reflective box 72 %). The rim's loss rises
  with the square of the depth to a half per step at the outer ring (2026-10-22, the user's choice: at wavelengths of a few
  cells, Jeroen's usual, the ring's Mur condition assumes a speed the stencil does not have and reflects almost
  everything, so the rim absorbs alone; measured against a reference canvas too wide to reflect, Example 3's beam got
  1 % back from the earlier linear ramp to a tenth at -64, 0.09 % from the quadratic one, 0.03 % at -128; the measuring
  method is in `library_check`); a version-1 file loads as
  Reflective, a version-2 file without a damping edge with none (its default then; files are version 3 since 2026-10-22), an
  import as Reflective with his band; one driver thread at a time, no locking; the kernels over the grid and the captures run in explicit 32 × 8 work groups over a global size rounded up to whole groups (2026-10-27, Jeroen's report: left to the driver, the group had to divide the grid, and a prime width, 1789 for a 1725 px canvas with the rim, got groups one cell wide, 13–19× slower on the RTX 2080; every candidate shape from 16 × 16 to 256 × 1 measured the same); captures
  deliver planes in the spec's `FrameShape` (halves or palette index bytes, the GPU converting on the card) into snapshots
  the engine made (`IWaveEngine.CreateSnapshot`, 2026-11-03, the user's choice): the GPU's planes are page-locked host
  memory (`CL_MEM_ALLOC_HOST_PTR`, mapped for the engine's life, released with it, a later read throws), so the capture's
  read is a DMA the stepping thread does not wait for, on a transfer queue of its own so it overlaps the next steps (two
  staging sets in turn, the capture kernel waiting for the read that last left its set, the read for the kernel, each
  queue flushed before the other waits on its event, as the spec requires; 1.9 → 1.3 ms a full-HD frame on the card); a
  .NET-pinned array is pageable to the driver, which drained the queue and copied on the stepping thread inside the
  "non-blocking" enqueue, 2.9 of a full-HD frame's 2.96 ms (the old note blamed the single queue: a second queue alone
  changed nothing). The scratch runner's snapshots stay ordinary, since a result outlives its engine;
  `Reset`, `SetEmitters`, `SetProbes`, `TryReplaceScene` (another scene of the same grid, edges and image-source count
  swapped into the kept engine: the GPU re-uploads c², damping and the source tables into its buffers and keeps the
  compiled program, the sub-steps re-derived; 2026-11-03, the user's choice: the scratch runner uses it, 55 ms for
  Example 16 against the 180–350 ms OpenCL build every part edit had cost; `library_check` proves a replaced engine
  steps like a fresh one on both engines). The scene reaches the card as compact maps (2026-11-03, the user's choice: the
  step kernel is bound by the card's bandwidth, about 400 GB/s at 84 bytes a cell a step at 4th order with intensity): a
  byte index into the table of the scene's at most 256 distinct c² values (the image's red levels) and a 16-bit index
  into the table of its damping values (the blue levels and the rim's depths, a few hundred; room for 4096, a scene with
  more rebuilds), the tables holding the CPU's floats, so the arithmetic is unchanged; and the intensity's blend of its
  two stages is made where it is read (the captures, the probes, `PrepareFrame`), not written every step: 69 bytes a
  cell, the step 0.47 → 0.38 ms at full HD and 4th order, 0.25 → 0.21 at 9-point. `PrepareFrame`, which no path of the
  app uses, reads both stages and blends on the host. Numerics are Jeroen's (dx = dt = 1, leapfrog, 9-point or 4th-order
  Laplacian, soft sources, EMA intensity; a scene pixel with red 0 is a mirror, n = 1000, his V0.30 rule,
  2026-10-18), verified CPU = GPU by the harness; seeds from `Pcg32` make CPU = GPU
  bit for bit on random gaps. A source's frequency level, amplitude and phase take decimals (2026-10-19, Jeroen's point 10); the
  level is capped at 255, above which the grid cannot sample the wave; object sources carry their frequency as a float on
  the GPU, the 256-entry table serves the image sources. The importer's fallbacks differ from the model's defaults on purpose.
- **Frames vs steps**: frame k is the state after step `k × VisualizeEvery`.
- **The engine choice is a user setting** (Simulation > Use the GPU, default on, CPU fallback logged).
- **Frame store**: budget = the lesser of free physical memory and available commit (2026-10-19: Windows refuses an
  allocation at the commit limit first, and a run that filled the physical measure left no room for the preview's
  bitmap, which took the app down; a failed bitmap is now logged and the picture stays empty) minus 2 GB, capped by
  Simulation > RAM cache size (Auto or a share); the
  outgoing run's frames count as reclaimed and the new store waits for their release. No memory mapping, no OS
  cache reliance (both wrote at disk speed). Disposed on the thread pool. Full HD, 2500 steps, both planes: 10.4 GB
  in 16-bit, half in 8-bit. The producer touches a fresh block's pages on up to four cores (2026-11-03: Windows zeroes
  pages at 5 GB/s for one thread, 1.7 ms a full-HD block, which had been the writer's clock); the writer's copy into the
  block stays one plain `CopyTo`, the clock of a 16-bit run in RAM, since every parallel copy slowed the card's DMA by more
  than it gained (see Numbers). **Do not resurrect**: a parallel or dedicated-thread copy in `FrameStore.Write`.
- **Reduced preview** (View menu): resolution 1/2/4, frames 1/2/4 (the timeline never changes), samples 16-bit or
  **8-bit palette index, the default**: the stored samples at the palette's 255 colours and the captured brightness,
  half the store; 16-bit keeps the brightness turnable after a run and shows every level of the gradient. In 8-bit a
  brightness edit is a simulation edit. **A 16-bit intensity plane holds √I** (2026-10-23, the user's choice: the
  renderer colours √I, and as I the dark end lay among half's subnormals, below 6.1e-5, up to a palette level between
  neighbours; as √I the floor is at I = 3.7e-9, the brightest parts a bit coarser): `FrameShape.IntensityRoot`, from the
  run's `PreviewQuality`, produced by `FrameConversion` and `capture_half`, reported by every `IFrameSource` through its
  `Shape`; the renderer, the screen path (which blends it as energy) and the fit go by its `SampleKind`. **The 8-bit
  index is the palette index of the half a 16-bit frame would store** (the field, √I), computed on the CPU and in the
  kernel (a bit-level rounding there, the GPU program built with a correctly rounded square root where the card has one),
  so CPU and GPU store the same bytes (2026-10-19) and both previews start from one value (2026-10-23, the user's choice:
  rounding I to a half first had merged the darkest indices at a high brightness, 2e-8 at 30 coming out 0 instead of 1).
  Every export needs the 16-bit samples (2026-10-23, the user's choice, so a picture has every level of the gradient):
  an 8-bit run, a reduced resolution or skipped frames make the export run the simulation again at full quality first.
  Only an OpenEXR export of I makes a run that stores I itself (squaring √I back would lose a bit); as coded, that run is
  then the one the preview shows until the next edit or run (undecided whether it should be a side run).
- **First start** (2026-10-31, the user's design): with no settings file, the three quality settings and the dithering are
  chosen once from the machine (`PreviewDefaults.TierOf` on `MachineFacts.Read`; `library_check` proves the table): 32 GB
  or more → 16-bit samples dithered to 16 bits; else any of under 16 GB, a cache drive with a seek penalty (`StorageDevice`:
  `IOCTL_STORAGE_QUERY_PROPERTY` on the volume, then on its disks, no elevation; unknown counts as none; on the workstation
  the HDD D: answers yes, the SSD E: no, and C: on the PERC H310's RAID virtual disk nothing, as Windows itself calls its media
  type unspecified), no OpenCL GPU or ≤ 4 threads →
  half resolution, every second frame, 8-bit; else the fixed defaults. Tested at 30 and 15 GiB, since Windows reports
  under the nominal size. The settings file is written at once, so deleting it redoes the choice; the fixed defaults stay
  the initialisers (the figure harness strips the attributes, the designer); one Info line after "ready" names the facts
  and the choice. The levers buy memory and readback, never step time: the divisors shrink the captured frames, not the
  grid (at the defaults 11 of the 24 examples exceed 6 GB, Example 7 needs 19 GB; at Low the largest is 2.4 GB).

## The app's rules

- **Live editing**: a simulation edit (scene, solver, seed, sources) marks the run stale, **stops playback and any
  active run**, requests a scratch frame for the position (120 ms debounce; the scratch runner keeps its engine across a
  scene edit of the same grid, `TryReplaceScene`, and rebuilds only for another grid, edges or options) and re-runs
  automatically after 1.5 s
  of quiet (setting, default on); an automatic run never moves the preview. A probe edit never starts a run: only
  the recordings are stale, the scratch frame refreshes the graph; except that an active probe the run does not
  record (switched on, added) is a simulation edit (2026-10-18). No scratch frame while playback runs. Any run
  is paused while a scratch frame computes. The newest scratch request is computed in full, however long (no time
  budget, 2026-10-16); a request superseded while stepping is abandoned within 16 steps and logged (the word "every" here
  misled a reader into offering that as new, 2026-11-03).
- **A picture changes only when a frame for its position arrives**; otherwise the last picture stays. A long wait
  dims the pictures under a spinner, mirrored on the taskbar.
- **Brightness** (2026-10-20, Jeroen's point 9) sits in each picture's header strip (`HeaderStrip`: title, a caption that
  gives way first, the box and **Auto** at the far end; turned on its side with the title); Auto fits once to the frame
  shown (the 99.5th percentile of |field| or √intensity at the palette's top, the user's choice of the variants), an
  8-bit preview computing that frame in 16-bit first; the context menu has Brightness and Auto brightness. The box
  steps by a factor (`NumberBox.Logarithmic`, 2026-10-26): two percent a key, a fifth a page, three significant digits.
- **Edge line** (2026-10-25, the user's choice): a 1 px line around each preview picture in the colour of what a wave
  meets at the canvas edge: pure red for a reflective wall (the index's channel), pure blue for a damping rim outside
  the canvas (the absorbers' channel; `Scene.HasRim`; a rim wins over a wall), nothing for open edges or a damping band
  inside the canvas; the scene's colours, not the theme's; preview only. For it the picture's cell is sized to the
  picture (the overlays measure to the uniform fit and remeasure on a canvas change) while `PictureFrame` fills its
  cell, so the heading stays at the top.
- **Play means "show me the simulation"**: it starts an automatic run when needed and holds at the newest frame
  until the run catches up. Live follows a running run until the user scrubs; it ends with a last follow to the
  final frame. A replaced project goes to frame 0; a discarded run keeps the position. In/out points are steps.
- **A new source, probe or part lands at the centre of the canvas the preview shows** (2026-10-26, Jeroen's report: one
  at the corner went unnoticed inside the rim); the model's parameterless adders take the scene's size, the window passes
  the shown canvas's centre, which an image scene sizes; a new probe's line end lies 100 px to the right.
- **Handles and overlays**: sources, probes, emitter dots and the rotation handle are drawn over the pictures in
  canvas pixels, over a dark halo, clipped to the picture, in a light grey, the selected item's in a bright yellow
  (`PictureHandle`, `PictureSelection` in `Controls.xaml`, 2026-10-26, Jeroen's tester: the accent blue was duller than
  the rest and lost on a blue scheme and to colour-blind eyes; the keyboard focus keeps the accent); a probe's handles and line appear only on the picture of
  the layer it measures (2026-10-18), sources on both; graph boxes lie over every handle and dot, the selected
  item is drawn last among its kind, except the handles themselves (Panel.ZIndex breaks the Tab cycle, 2026-10-18);
  arrows nudge, Del on a handle or a graph box removes its item or the selection it belongs to as the tree's Del does
  (2026-10-26), selection syncs with the tree, a click on empty space deselects. **Display > Handles in the preview and
  Labels in the preview** (2026-10-29, the user's request, both on by default) hide every item's handles, lines, emitter
  dots and levers, or the names beside the handles, except a selected item's, which shows them either way (the tree is
  where hidden items are picked; collapsed handles are out of the Tab cycle); the graph boxes are not affected. **Group selection** (2026-10-19): one shared `SelectedItems` list on the window beside the
  current `SelectedItem` (the properties pane shows the current one); in the tree Ctrl+click, Insert or Ctrl+Space
  toggles a row, Shift+click or Shift+arrows selects the range from the anchor, Ctrl+A everything, the row's context
  menu has both; on the pictures a drag on empty space is a marquee (Shift adds, Ctrl toggles), Ctrl+click on a handle
  toggles, Shift+click adds; a drag of the handle of one of several selected items, or the arrows on it, moves them all
  through `IMovable` with the delta limited so none leaves the canvas (the formation holds); Escape during a drag puts
  things back; Remove, Duplicate and Enabled act on the whole selection when their target is in it; the lever, grips
  and point handles belong to the current item alone. Probe graph boxes follow their probe while attached, resize by a grip, keep their text
  size; an amplitude axis is symmetric about zero; an intensity graph's value axis can be logarithmic (2026-10-20,
  Jeroen's point 8: `Decades` below the fitted top, zeros on the bottom line, powers-of-ten ticks; the field stays linear).
- **Undo and redo** (2026-10-22, the user's request, unlimited): `ProjectHistory` keeps one snapshot per step, the
  project file's XML without the scene image plus the image's bytes by reference (equal bytes shared). A step ends after
  0.5 s of quiet with no mouse button held (`MainWindow.History`); Undo and Redo (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z; a text
  box keeps its own) write the step back into the same project object, the lists refilled and the settings objects
  replaced, so the watcher reports it as an edit (stale run, scratch frame, re-run) and nothing is recorded; the
  selection follows by name, then by index; the title's asterisk clears at the saved step. The Edit menu (between File
  and View) holds Undo, Redo, Cut, Copy, Paste and Delete; the UI harnesses step over it.
- **Copy, cut, paste, drag and drop** (2026-10-23, the user's choices): `ProjectClipboard` writes sources, parts,
  probes and schemes (the `INamed` items) and the Scene, Simulation and Display objects as a fragment of their own file
  elements, in the app's format and as plain text (read back from text too, so a text editor takes and gives the XML).
  Paste goes behind the current item, before it for a drop on a row's upper half, renames only on a clash, emitters
  from the target's seed; a settings object replaces only on its own row or the project root. A tree drag carries the
  same text and nothing else; the target decides (2026-10-23, the user's choice, replacing a live item in the drag data
  and a refusal of the own drag by the pictures): the own tree reorders the item it keeps in a field, another window's
  tree pastes, and a picture of this window or another pastes and moves the objects as a group onto the drop pixel
  (`PlaceAt`, held inside the canvas), so a drag from the tree to the picture duplicates. `ObjectsData` reads a drag or
  the clipboard and parses each text once; the window reads the clipboard once per change of Windows' clipboard
  sequence number. The tree and the pictures claim a drag only when they take it; a file dropped anywhere is the window's,
  which opens it (2026-10-26, Jeroen's report: for three days a file over the tree or a picture was refused).
- **Icons** (2026-10-22/23): a menu entry gets one through the attached `MenuIcon.Glyph` (a code point of Segoe Fluent
  Icons that Segoe MDL2 Assets has too, for Windows 10; the `IconGlyphText` style, greyscale) or `MenuIcon.Drawing`, the
  name of an `Icon<Name>` (and `Icon<Name>Fill`) `StreamGeometry` in `Themes/Controls.xaml`; both make an `IconView`,
  which the tree's rows and toolbar use directly (`PlusBadge` on the add buttons) and which draws in the inherited text
  colour, so a disabled row's icon greys with its name through one trigger on the row. A lens row's icon is its own
  profile (2026-10-29, the user's design): nine `IconLens<Left><Right>` drawings, each surface convex, flat or concave by
  its sag at the rim (`LensPart.ShapeOf`: flat under half a pixel, 2026-10-29, the user's report: Example 1's mirror of radii
  14401 on 104 px showed as biconvex), chosen by `LensIconConverter` from R1, R2 and the diameter in a template scoped to
  the row's icon, so it follows an
  edit; the Part menu's `IconLens` is the lens-and-mirror pair the user drew, a biconvex lens beside a concave mirror with a
  flat back, which no row shows. The Mirror box renames the part, Lens to Mirror and back
  (`LensPart.MirrorName`, run by the box's command and never by the setter, so a loaded or undone name stays). Drawings are 16 px, 1 px strokes
  on half pixels, ink in rows and columns 0 to 14 (odd, so it centres like text); every tree row, toolbar button and
  menu entry is 23 px, the menu bar's headers through their padding. The pictures' handle glyphs stay in
  `PreviewPane.xaml`, odd-sized (21, 15, 13 px) with the point at the centre pixel (2026-10-26, Jeroen's report: at 20 px the
  window's layout rounding put the dot, its halo and the crosshair half a pixel apart).
- **Colour schemes are gradients** (2026-10-23, the user's design): the model holds the stops and no table; how many
  colours are taken is the renderer's (`ColorLut`): 255 at i/254 for 8-bit frames (the author's table, truncated as in
  his Python), and for 16-bit frames a table of the 65,536 half values per layer and brightness, the gradient at each
  value's position, rounded (the 255 entries had left visible patches two to five levels apart in dark gradients).
- **Two preview paths** (2026-10-23, the user's design): 8-bit samples are the fast preview, coloured at the store's
  size and scaled by WPF, as before. 16-bit samples are drawn at each picture's size on screen (`ScreenRenderer`, the
  pane reports the Image's rounded `RenderSize` in device pixels): resampled in value space, never colours (a box mean
  when shrinking, bilinear when enlarging, so every pixel shows a real value in a gradient colour), then the half
  table. The pictures are `CanvasImage`s, sized by the canvas and never by their bitmap, so a 16-bit bitmap is plain
  screen pixels drawn 1:1 and a new one never moves the layout (2026-10-23: a resolution set to make the bitmap measure
  the canvas came back from WPF in single precision, the reuse test failed on every frame, and the pane flickered).
  **Preview dithering** (View menu, user setting, off by default until the user has seen it; only with 16-bit samples):
  the colours at 16 bits per channel (`DeepHalfTable`) to 8 through a Bayer matrix of 2, 4, 8 or 16 cells a side for
  10, 12, 14 or 16 bits, fixed to the picture's pixels (on the screen's, by the window's layout rounding); preview only, never in an export (a video crushes it; an export
  above 8 bits per channel is a possible future goal). A better algorithm than Bayer may come later.
- **Colour schemes** are stop lists in `.wdscheme` XML, shipped ones embedded, user ones in
  `%LOCALAPPDATA%\WaveDudeNext\ColorSchemes`, project ones in the `.wdproj`; an in-house picker edits stops;
  renaming a project scheme repoints its layers. **The scene image** is embedded and follows its file on disk, the
  path being where it came from (2026-10-26, Jeroen's report: the scene form shows the image rows for an image scene
  only, says "(not found)" beside a path whose file is gone, which changes nothing, and has three buttons beside the name,
  2026-10-29, the user's design: "…" picks a file, a save glyph writes the embedded bytes to a file, an export that
  changes nothing in the project, and the X drops the name, the path and the bytes and leaves a blank canvas of the
  image's size);
  the "glowing edges" outline overlays the pictures. **Import** of Jeroen's `configuration.json` is key for key; a source whose position was
  never picked (`has_position` false) stays enabled at its x/y, since his program simulated it there (Example 11's
  lattice came in disabled and ran black until 2026-10-16).
  **Languages**: four string tables, live switch; converter output needs the language as an extra binding input.
- **Export**: field, intensity or both, a still of the shown frame, from the current run when fresh and full,
  else a run is made first. **The Save dialog starts where the session's last export of its kind went** (2026-10-28,
  Jeroen's report, the user's rule: the first export after a project is opened starts in the project's folder under the
  project's name as before, every one after it in the last one's folder; a run's export proposes the last name with the
  layer suffix swapped, a still and probe data their own names; per kind and per project file, kept in the user settings
  as `ExportHint` entries, 20 projects, restored when the project is opened, the user's choice over the project file: a
  path belongs to the machine, not to a shared file, and an export must not dirty the project).
  **Export last** (Ctrl+E, File menu below Export, 2026-10-28, the user's request): the session's last run export again,
  the same layers, format and path, no dialog, an existing file or sequence folder replaced without a question; a still
  does not count; disabled until the first run export since the project was opened. Shortcuts (the user's choice, GIMP's
  pair): Ctrl+Shift+E, I, B the field, the intensity, both; Ctrl+Shift+F, G the two frames. In the dialog's order (2026-10-19) H.264 MP4 via Media Foundation (the only
  Windows-bound library piece besides WIC JPEG, which has an in-house fallback), MPEG-1, OpenDML AVI, QuickTime
  MOV with the Animation codec or the PNG codec (2026-10-19, each sample a PNG, for a lossless hand-over to Resolve,
  Premiere and Final Cut; both share `.mov`, so the dialog's filter decides the format and the extension follows it,
  while a scripted path goes by its extension, `.mov` meaning Animation), PNG/JPEG sequences (the files numbered by timeline frame, a range export from its first
  frame, and a still padded the same way, `name_0655.png`; never a step number, 2026-10-19), and
  **probe data as CSV** (2026-10-18: a probe's context menu in the tree and on its graph box; semicolons, decimal
  point, a header row; a point probe one row per step, a line probe one row per frame; the full run like the
  pictures, started first when needed),
  **JPEG is 4:4:4 in both encoders** (2026-10-18: WIC called natively for its subsampling option, which WPF's
  wrapper never set; 4:2:0 averaged the colour, where a wave picture's information is, into blocky fringes even
  at quality 100; 4:4:4 at 90 beat it in every channel at half the size), and
  **OpenEXR sequences or stills** (2026-10-18, the user's choice for data export to NumPy, MATLAB, Octave add-ons
  and viewers such as DJV): the stored halves themselves, one uncompressed "Y" channel per file, no colours,
  brightness, outline or boxes; a sink that `WantsHalves` gets the plane from the job unrendered. Two entries in the
  dialog (2026-10-23): the intensity as I (a run storing I is made first; a scripted `.exr`) or as √I (the preview's
  run as it is); the field the same in both. **Source markers**
  (2026-10-21, Display, off by default, the user's choices): the preview's marker, a composite source's dots and the
  name, enabled sources only, scaled like the probe labels, under the probe markers and boxes. Harness hooks:
  `WAVEDUDE_EXPORT_PATH`, `WAVEDUDE_DISCARD_EDITS=1`.
- **UI zoom** (View > UI zoom, 0.5x to 3x, `Settings.UiZoom`, default 1, live; 2026-11-01/02, the user's request, first
  as a start-up switch for figures at a higher pixel density without a DPI change, then as this setting; `--zoom=<factor>`,
  0.5 to 8, sets the setting at start): every window's content gets a layout scale (`WindowZoom`, from the theme's Window
  style, at Initialized and at each change of the setting) and Ideal text formatting (Display formatting rasterises on the
  100 % grid and WPF scales those glyph bitmaps, soft; seen pixel by pixel), window sizes are multiplied by the change
  (the main window's bound size carries the zoom from `ZoomConverter` when read and is set like the others after a change,
  never re-read: WPF writes both sizes back from the handle after the first resize), then the window is kept inside its
  monitor's work area (`KeepOnScreen`, at SourceInitialized and after a change; the user's rule, a zoomed window had
  wandered off). Whole pixels at any factor (2026-11-02, the user's rule: a line is there or not, 1 or 2 px, never cut; at
  1.5 the About window's bottom outline had been cut): WPF rounds layout to the DPI its tree believes in, so each tree's
  root is told the real DPI times the zoom through `VisualTreeHelper.SetRootDpi` (a system-DPI-aware process never resets
  it; `HwndTarget` does only when per-monitor aware), the root and its chrome outside the transform round to nothing, the
  content rounds. **The process is DPI aware per monitor** (`app.manifest`, PerMonitorV2; 2026-11-02, the user's choice
  after the About buttons' outlines at 1.5 had stayed on the monitor's grid): only in that mode does WPF give a new
  visual its parent's DPI (its `AddVisualChild`), which the templates' inner parts, created at a tree's first layout,
  need, and a Border builds its edge pens once, at its first drawing. WPF then also puts the monitor's DPI on a tree
  when a window's handle takes it as its root and when the window lands on a monitor of another scale, so the zoomed
  DPI is put back at the window's source change and in its DpiChanged event, by the handle's real DPI, never the
  event's (telling a tree its DPI raises DpiChanged itself; taking the event's value fed back without end, a silent
  crash). A DPI change also makes the preview pane tell the presenter its pictures' device sizes again (their layout
  size stays). Users with monitors of different scales get each window rendered at its monitor's scale, title bar
  included, instead of a stretched bitmap, and a window dragged across such a boundary resizes in pixels; untested
  here (one monitor), the user tests by hand. Popups are trees of their own and WPF raises no Loaded for their
  content: the theme's popups (menus, drop-downs, the colour button) carry `theming:WindowZoom.Popup="True"`, a
  tooltip or context menu `theming:WindowZoom.Standalone="True"` in its style, and each content is watched for the
  source it gets at an opening (`PresentationSource.AddSourceChangedHandler`, before any layout), where its whole tree
  is also invalidated: a popup opened before a zoom change keeps its entries' measure under the unchanged constraint,
  and each moved onto the new grid only when a hover invalidated it (the user's report, 1 px shifts); a tooltip or context
  menu sits in a parentless popup of WPF's own, which takes no transform (its `UpdateTransform`), so its style binds
  the scale and the formatting to the setting (`ZoomTransformConverter`, `ZoomTextFormattingConverter`). The log's
  "Display scale" line after the window shows names the monitor's scale and the zoom. Code
  working in device pixels (the 16-bit picture size, text metrics, the number box's drag) asks `DevicePixels.PerDip`: the
  composition target's real DPI (not `GetDpi`, which is the raised one) times the transforms to the root, so the 16-bit
  pictures are rendered at the picture's device size and drawn 1:1 at any zoom (the 8-bit ones are WPF-scaled bitmaps as
  always). The title bar stays at the monitor's scale. The app's own hairlines, the timeline's and the graph boxes',
  are whole at any scale (2026-11-02, the user's rule): a pen of `DevicePixels.Hairline`, the whole number of device
  pixels nearest to the scale, and a `GuidelineSet` at every line's and fill's edges, which WPF moves onto the device
  grid under the full transform; at 100 % the arithmetic is the old one, pixel for pixel. The handle glyphs (1.5 px
  strokes over a halo), the dashed probe lines and the marquee are antialiased by design and stay so. The window must
  fit the screen for a capture: the Desktop Window Manager composes nothing off it, so PrintWindow returns white there. `PreviewQualityCheck` and `LanguageCheck` count
  the View menu's entries past the new one.
- **Optical parts** (2026-10-18; **`OpticalParts.md` is the reference**: the kinds, the painting rules, the measurements
  and how to add a kind): subclasses of `OpticalPart` in `Project.Parts`, one class and one XML element per kind, the
  class being the kind (no enum, 2026-10-22): a lens or mirror, the shapes under `ShapePart` (polygon, rectangle, ellipse,
  triangle, outline), a fibre on a Bézier path, a Bragg reflector, a GRIN lens, the XOR, AND and OR gates under
  `LogicGatePart` and a zone plate (both Jeroen's, 2026-10-21/22). Painted into the scene pixels, red the index (red 0 a
  mirror), blue for absorbers, green never; a part edit is a simulation edit. The menus of kinds share `PartMenuEntries`
  (`ProjectTree.xaml`), added by `ProjectTree.MenuEntries`, not as an `ItemsSource`. **Each kind has a description**
  (2026-10-29, the user's choice): the `Kind.<Type>` string in the four tables, the entry's tooltip in the menus and the
  text at the top of the part's form (`PartCommonView`, through `KindDescriptionConverter` with the language as an input),
  under a header whose chevron turns while it is open; open or closed is one user setting, `PartDescriptionExpanded`. **Coatings** (2026-10-29, the user's
  request, so Jeroen's drawn scenes become parts): a lens, a shape, the GRIN lens and the fibre are `CoatedPart`s, the AR
  band a `Layer` at √n or `Graded`, a raised cosine from the pixel's index to the body (measured against linear, smooth-step
  and quintic in `OpticalParts.md`), on the faces `FrontCoated`, `BackCoated`, `SidesCoated` choose, classed by the outward
  normal in the part frame (within 45° of −x' front, of +x' back, else a side; a fibre's start face, end face and tube); a
  cavity's inner faces can stay bare. A fibre's cladding can absorb (`Absorption`, `AbsorptionRamp`: a hump in its outer
  part, a mode stripper, Jeroen's strips along his fibres).
- **The project file carries its DTD** (2026-10-31, the user's request, so a text editor completes element and attribute
  names and enumeration values): `ProjectSchema` builds the DOCTYPE's internal subset once per process by reflection from
  the serializer attributes (one `ELEMENT` per name with its children in the written order, an `ATTLIST` line per attribute,
  bools and enums enumerated, the rest `CDATA`, no DTD defaults, `#REQUIRED` only where the serializer always writes), with a
  comment giving the setting's description, default and range before each declaration when the `ProjectSchema.Comments`
  constant is true, false since 2026-10-31, the user's choice after seeing them (56 KB a file with them, 15 KB without); `Save`
  writes it, `Load` skips it (`DtdProcessing.Ignore`; the reader's default refuses
  any DOCTYPE, so builds before this refuse new files); history snapshots and clipboard fragments carry none; `library_check`
  validates a one-of-every-kind project and every example as saved against it with .NET's DTD validator and proves a
  misspelt attribute, a wrong value and a misplaced element are reported. The examples and the harnesses' test projects are
  not re-saved with it yet.
- **Memory warning** (2026-11-03, the user's design): Simulation > Warn when a run does not fit in RAM
  (`Settings.WarnWhenRamInsufficient`, default on). A preview run whose frames exceed the budget asks first, F5, Play and
  the automatic re-run alike (the user's choice, so no spill is silent; an export's run is full quality by rule and never
  asks): `MemoryWarningWindow`, in the message window's look, names the need and the budget, offers four checkboxes: the
  RAM cache size to Auto (the user's request the same day; greyed when Auto gains less than a frame) and three that each
  lower the preview quality a notch (half the resolution, half the frames, 8-bit samples; a lever at the View menu's
  lowest is greyed), pre-ticked with the fewest ticks that fit and the mildest among as many (the largest need that
  fits, so the cache comes before any loss of quality), a live line with the ticks' result, and "Do not warn me again",
  which clears the setting. Run puts the choice into the Simulation and View menu settings
  (persisted, so the scratch frames match the run and later runs fit without asking; the settings' change schedules a
  scratch frame and a re-run as the View menu's does, and the run started right after leaves nothing stale for it);
  Cancel keeps the stale run and says so at Info. Asked every time, no memory of an answer. Play asks before the pane's
  clock starts and a cancelled warning turns playback off again (2026-11-03, the user's report: the modal window's message
  loop had ticked the playhead through the stale frames). The arithmetic is
  `RunSpec.StoreBytesAt`, proven by `library_check`.
- File types `.wdproj` / `.wdscheme` are registered per user at start-up; unsaved edits ask on close.
- **Examples** (2026-10-29, the user's request): File > Examples lists the project files of the repository's `Examples`
  folder, embedded in the app (`EmbeddedResource` in the WPF project, `Examples.cs`), in file order with the two-digit
  number leading each name; one opens as an unsaved project named after its file in the title (`SuggestedFileName`, which
  Save As proposes; an import is named the same way). The set is 24 files
  curated from Jeroen's examples (the Classic ones through the importer, open edges with a rim, his drawn scenes rebuilt
  from parts: the ball lens, the spherical mirror, the test glass, the fibres); the UI harnesses count the File menu's
  entries, so a new entry moves their Down presses.
- **Manual** (2026-10-30, the user's request and choices): Help > Manual (F1, below About) opens MANUAL.md in a window
  owned by the main window (the user's choice, after a day unowned: not modal, but attached, so it is minimised and
  restored with the main window and stays above it; Windows does not maximise an owned window with its owner); F1 again
  brings it forward, Esc closes it, it closes with the main window. A contents tree of the headings by level beside the text, every branch open; the title (the one `#`) is no
  row, its chapters are the roots; the window opens with the text at its top and the first chapter's row taken, and a
  row taken goes to its heading. **MANUAL.md is written for its reader, never for us** (2026-10-30, the user's rule,
  said three times): what a control does for the user and how to use it, no internal names, no history, no engine
  talk; the chapters mirror the app (the main window and its panes, the menus, the project settings, sources, optical
  parts, probes, colour schemes), each figure's numbered list is its badges in the form `N. **Label**: text`, and
  settings that several forms share (a part's first six, the glass and mirror material) are described once, the other
  forms' lists starting at their own first number (`StartIndex`). A figure section regenerated by the harness is
  spliced in, keeping the wording. The build converts the Markdown like the license (the license's XAML stayed byte for
  byte the same) and compiles it to BAML (the Page `Help/Manual.xaml`): headings tagged `h{level}#{anchor}` with GitHub's
  anchors, so `[x](#anchor)` links work in the app; tight lists tight, as on GitHub. The figures' SVGs are drawn at build
  time: the embedded PNG becomes a WPF resource (`Manual/<name>.png`), each badge a round Border with its number and its
  `<title>` as the tooltip; an SVG element or attribute the converter does not draw stops the build. A screenshot that
  fits the column is drawn 1:1 at 100 % (whole-pixel line heights, whole-pixel scrolling; proven by a pixel search, only
  the badges' discs differing); a wider one is scaled down. Opening takes about 2 s the first time, 1 s after (the
  layout of the whole document; BAML and the light badges took 0.3 s off the first).

## Numbers (workstation: 10-core Xeon without AVX2, RTX 2080, 64 GB)

GPU step at 1080p 0.21 ms (9-point) to 0.38 ms (4th order, three sub-steps; 0.25–0.47 before the compact maps and the
deferred intensity blend of 2026-11-03), CPU 6–14 ms; OpenCL build 0.3 s; a full-HD frame's capture (both planes as halves, 8.3 MB)
0.9 ms on top of its two steps since the page-locked snapshots (2026-11-03; 2.1 ms before, the stepping thread blocked in
the driver's copy; `bench 1920 1080`: 1.50 → 0.65 ms a step with captures every 2 steps at 4th order, the DMA on its own
queue), the DMA itself 0.63 ms at 13 GB/s. In the app the default project's run (2500 steps, 1250 frames, 16-bit, both
planes, 9.7 GiB) takes 2.7 s (2026-11-03; 3.2 before the transfer queue, about 4 before the page-locked snapshots): the card
needs about 1.3 ms a frame, the writer 1.6 ms for its copy of a frame into the store on one core (6.8 GB/s) and is the
clock, the stepping thread parked 1.8 ms a frame for a free snapshot. Measured on the way and settled: the capture's reads
are flushed at once (NVIDIA holds a small batch back until a blocking call; the writer's wait had been starting the DMA,
0.8 ms a frame); the store's producer touches a fresh block's pages on four cores (Windows zeroes pages at 5 GB/s for one
thread, 1.7 ms a full-HD block, which had been the writer's clock before the copy was; 0.6 on four, eight gain nothing);
every faster copy lost in a run though 0.35 ms on four cores in isolation (torn out, three variants measured in the app):
chunks on the thread pool queued behind the presenter's rendering rows; two or four dedicated threads, at normal or
above-normal priority, copied faster but the writer's wait for the card rose by more, the memory bus being shared with
the DMA, the producer's page zeroing and the presenter — the frame's way into memory is bandwidth-bound on this DDR3
Xeon, and the plain copy at 6.8 GB/s leaves the DMA its share. Renderer per 2880×1080 layer on ten cores (2026-10-23, a
table lookup per pixel): 16-bit 0.5 ms,
8-bit 0.6; a half table built in 0.4 ms; the screen path from 1920×1080 to 914×514 1.4 ms, dithered 1.5. Presenter for
Example 7 (2880×1080 in a 458×172 picture, both layers, playing): 16-bit 7.3–8.5 ms a frame, dithered 7.7–9.1, 8-bit
10–11 (its bitmaps are canvas-sized). Presenter about 7 ms a frame quiet, 20 during a run. JPEG 1080p at 4:4:4: WIC 26 ms a frame
single-threaded (9 ms a frame in a sequence job on half the cores), in-house 9.8 ms on ten cores (15 in a job);
MP4 job 16 ms a frame. Part painter on a Full HD canvas (ten cores): a 400 × 330 outline 30 ms, a 400 px lens 5, a hexagon 8, an S-bent fibre across the canvas 51 (156 chords on the cell grid; 1.3 s without it);
the glowing edges 22 ms; the preview's scene pictures about 100 ms more, so the scene rebuild is coalesced at background priority.
**The composition runs on a worker since 2026-11-03** (the user's choice, after the log of a drag in Example 16: every
coalesced update repainted all four rectangles, one the whole canvas, "composed in 115–270 ms" on the UI thread, then
20–30 ms of pictures, and the handle jumped): `MainWindow.ComposeScene` clones the parts (`OpticalPart.Clone` copies the
point and node lists) and paints on the thread pool, one composition in flight, the newest request after it, the UI
thread taking only the finished pixels (20–24 ms a time, measured on a burst of ten nudges: three compositions of 395,
212 and 128 ms on the worker); `LoadSceneImage` (a run or scratch request) joins an in-flight composition of the same
inputs rather than painting again. **The painter's per-pixel path is out of JIT tiering** (2026-11-03, the user's choice):
alone it took 31 ms for those four parts, in the app 115–460 ms, because the runtime left its methods at tier-0 code in
the busy process (pinned there, the probe took 212 ms); `AggressiveOptimization` on the shape methods a pixel reaches, the
painter's row lambdas and the glowing edges' makes a composition 26–35 ms a background and 10–17 ms a box in the app,
74 ms for the four during a burst of nudges (was 395–460), the pictures 17–32 ms on the UI thread. Each part logs its
paint time, and a composition logs the thread pool's state when it starts. **A pixel is sampled 16 times only where an
edge is within reach** — the outline, the coating band's, or an inner edge the shape reports (`InnerEdgeDistance`: a
fibre's core boundary, a Bragg stack's layer boundaries; the logic gate still says 0, every inside pixel sampled) — where
every pixel of a fibre had been (2026-11-03, after the user's drag of a fibre node: Example 23's three fibres 138/163/398 →
43/50/96 ms, its composition 731 → 218 ms; a graded band's pixels are the centre sample now, as every other coated shape's,
within 0.01 of index where the band curves inside a pixel). A fibre pixel still runs four chord loops (depth, body index,
face, inner edge), sharable for about another factor of two. Left for later, if wanted: a Lipschitz skip
in the rows (the depth is a lower bound on the distance to the outline, so a pixel at depth *d* settles the next
⌈|*d*| − 0.6⌉ pixels: ~20 evaluations a row instead of 600 for a box, the arithmetic itself then a few milliseconds),
and the pictures' glowing edges, now the larger share of the UI thread's time during a drag; not an incremental
painter, since a coating's band rises from the index beneath it. Laptop (2 cores, HD 520, HDD): iGPU 4.3 ms a step against CPU 14.7; the ball
lens run 105 s full quality (disk-bound), 61 s at half resolution with every second frame, in RAM. Settled by these
numbers: no GPU JPEG, no OpenCL colouring at presentation, no `Vector256`, no Direct3D display path.

## Harnesses

`library_check` (100+ checks: engines, store, renderer, half tables, screen path, schemes, probes, scratch, reduced preview, importer, export, parts, group move, open edges, work groups on a prime grid, ranges, XOR gate, zone plate, gate port follower, history, clipboard, the project file's DTD, the first start's tiers, the engine's page-locked snapshots, scene replacement, the memory warning's store bytes).
UI, all on `Common.ps1`, with their check counts: `PreviewCheck` 43, `RunCheck` 71, `LanguageCheck` 11,
`ProbeGraphCheck` 39, `ImportCheck` 10, `ExportCheck` 44, `PreviewQualityCheck` 35, `SceneImageCheck` 8,
`ColorPickerCheck` 13, `KeyboardCheck` 13, `AboutCheck` 18, `SelectionCheck` 10. Test projects `tiny*.wdproj` sit beside them. The
Wave Dude 2D projects the import checks read (`library_check`, `ImportCheck`, `SceneImageCheck`) sit in
`_tools/legacy_examples` (2026-10-31, the user's wish: the root folder `Wave_Dude_Examples` with Jeroen's original
examples is gone from the repository, in history up to 02d0e2d; `library_check` finds the repository root by the
fixtures folder). A harness
sets the persisted settings it expects (split orientation, preview quality). Known flake: a second app start in one
script sometimes comes up active; only the foreground guard fails then. UI Automation files an owned dialog (the message
window, the memory warning) under its owner, not among the root's children: find its content below the main window and
walk up to the Window element for its handle (2026-11-03, half an hour lost to a root search).
**Manual figures** (2026-10-27): `Screenshots.ps1` on `Common.ps1` writes `Assets/*.svg` (the PNG inlined, numbered
badges at UI Automation rectangles), `Assets/Legend.md` (what each number marks) and `Assets/main-window.png` (the
window without badges, the README's picture, 2026-10-30): the window, the menus, the tree,
the preview and the timeline from `Examples/Example16_Wave_in_2_boxes.wdproj` (a relative path, 2026-11-02) at a scratch frame, the
properties form of every object kind from `figures.wdproj` beside the harnesses (one of each kind, made with the
library's adders), plus two crops of the rectangle's form for the manual's sections on the settings several forms share
(`Properties-Figure` takes a row range, a section heading and the first badge's number, so the crops keep the whole
form's numbers; 2026-10-30, the user's request); dark theme, English, **UI zoom 1.25** (2026-11-02, the user's choice,
so the figures are sharp on denser screens; the window 1642 × 1408 px on the monitor at 100 %), the user's 1314 × 1126
arrangement, his settings put back afterwards. A pointer resting on the window is parked just outside it for a capture and put back at
the end (2026-10-30: it lay on a probe's graph box, whose hover border changed the main window's picture between two
runs); the harnesses' one mouse action, clicking and activating nothing. A figure must come out byte for byte the same
from an unchanged app. `-Zoom <factor>` (2026-11-01; 1.25 is the default and the figures' zoom, 1 for figures at the
monitor's own pixels) puts the UI zoom
setting into the figures' settings, scales the script's pixel constants, takes 16-bit samples (the 8-bit picture is a
bitmap WPF scales) and writes the SVG with its size in CSS pixels over a view box in the figure's pixels, which the
manual's converter (`SvgReader`, a uniform view-box scale) and browsers draw at the 100 % size; the PNGs declare their
density. The window must fit the monitor (nothing off it is composed): at 2 the 1314 × 1126 window would be 2252 px
tall, more than the 4K monitor; 1.25 and 1.5 fit, the properties session's 1400 too.

## People, name, license

Alexander Gräf (the user), author of WaveDude Next: his name has the umlaut in documentation shown to the public (the
README; 2026-10-31, his rule), while the ASCII "Graef" stays in the license headers and the git author, his choice to
avoid Unicode there. Jeroen Vleggaar, Huygens Optics, author of Wave Dude 2D,
Copyright (C) 2026. 0x23 (Diffraction Limited), author of WaveSimulator2D, which Jeroen's numerics followed; named by his handle only, at his wish (2026-10-19).
Private addresses stay out of files. The name was agreed with Jeroen (he proposed "Pro"); the GitHub repository
`graealex/WaveDude` still has the old name. The header text is the editorconfig template, identical in every file,
never reworded per file.

## Open items

README and a documentation file (he writes them); an integer
upscale in exports; HEVC;
dropping the 9-point Laplacian (undecided);
**optical parts**: the open points at the end of `OpticalParts.md` (a `PartCheck.ps1` harness, example projects,
Bragg thicknesses in pixels) wait for the user.

**Unsolved: clicks on the intensity picture selected nothing** (2026-10-22, seen once, not reproduced). In
Jeroen's `logical_elements_example.wdproj` (git history up to 02d0e2d; Example 18 is its curated form), after several
undo and redo cycles (gates moved, undone, redone),
a click on Probe 1's or Rectangle 1 copy's handle in the intensity picture selected nothing; nothing lay over them, the
user checked. Reopening the project (a fresh start) cured it. The session is in the Release build's log
(`WaveDudeNext.WPF\bin\Release\net10.0-windows\WaveDudeNext.WPF.log`, 21:58:52 to 22:00:42): no warning or error, and
the log records no clicks or selection changes. Looked at without a finding: `SceneOverlay` and `ProbeGraphOverlay`
rebuild their handles on every list change (an undo keeps the list objects, refills them), the selection bindings
overlay > pane > window are two-way, the marquee releases its mouse capture, the busy dimming is not hit-testable, the
graph boxes lay far from both handles. A keyboard-driven rerun (handles moved, undone, redone) found every handle once,
in place. Not yet looked at: the click path itself (`MarkerThumb` drag start, mouse capture after an undo during or
right after a drag, focus) with logging of what a press hits.
