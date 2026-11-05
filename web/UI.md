# Browser UI development guide

This guide covers work on the Preact interface. Read the repository's
[AGENTS.md](../AGENTS.md), [CLAUDE.md](../CLAUDE.md), and [PLAN.md](../PLAN.md)
for repository rules, established product decisions, and the server architecture.

## Scope and current state

The browser UI is a separate consumer of the shared WaveDude Next backend.
WPF provides the reference for terminology, workflows, controls, and behaviour.
The browser implementation uses Preact, TypeScript, CSS, and browser APIs.

The current implementation is a structural shell. It has a project hierarchy,
a local source editor, two empty preview canvases, a timeline, and transport
controls. There is no server connection, computed preview, project persistence,
or working run/playback/export operation yet. Source edits survive selection
changes within the page but are lost on reload. Other property editors are
placeholders. Brightness is local UI state; it does not render an image yet.

Keep unavailable operations visibly disabled. Empty previews and frame/step
readouts must reflect the absence of results. The timeline's current scale is
placeholder geometry, not a claim about available frames. Replace it with the
planned frame extent when run metadata exists.

## Layout and visual conventions

The window has three rows: menu, application workspace, and status. The workspace
has a left column containing Project above Properties, and a larger Preview pane
on the right. Preview owns the two pictures, timeline, and transport bar.
Field and Intensity are stacked by default; the split buttons and View menu can
switch them to side by side.

Use the compact dark desktop-tool appearance established by the shell:

- Neutral charcoal surfaces, slightly lighter pane headers, thin separators,
  and a restrained blue accent for selection and focus.
- Small system text, compact inputs and toolbars, modest corner rounding, and
  minimal decoration. Preserve space for the pictures.
- No large marketing headings, onboarding cards, gradients, or ornamental chrome.
- Put the object hierarchy in Project and editors for the selection in Properties.
  Keep playback and simulation transport together below the pictures.
- Use British UI spelling, including “colour” and “visualise”; use “license”.
  Preserve existing code identifiers such as `ColorScheme` when mirroring them.

`src/index.css` defines global defaults and shared colour variables.
`src/MainWindow.css` currently owns the shell and component styles. Extend the
existing classes and tokens before creating another styling system. If the
stylesheet becomes difficult to navigate, split styles with their owning panes
or controls using the same component names.

Use `minmax(0, 1fr)`, `min-width: 0`, and `min-height: 0` where nested grid/flex
children must shrink. Project and Properties scroll internally. Keep menu,
status, timeline, and transport visible as the window height changes; wrap
transport controls when needed. The current desktop shell has a 600 px minimum
width and 420 px minimum height. A phone-specific layout is not implemented.

Each picture retains the 16:9 placeholder canvas aspect ratio and fits inside its
viewport. The viewport is a CSS size container; `cqw`/`cqh` constrain the picture
in both dimensions. The canvas is absolutely positioned inside that frame so
its intrinsic 1920 × 1080 dimensions cannot force the grid larger. When scene
sizes become dynamic, derive the aspect ratio and canvas backing size from
actual frame metadata rather than retaining the placeholder dimensions.

## Component names and paths

Use names and directory roles from WPF where the concepts correspond. File and
import casing matters on Linux. Prefer the established product name over a
new synonym for the same pane or control.

| Browser file | WPF reference | Responsibility |
| --- | --- | --- |
| `src/MainWindow.tsx` | `MainWindow.xaml` and `MainWindow.*.cs` | Shell composition, menus, shared selection and draft state |
| `src/Panes/ProjectTree.tsx` | `Panes/ProjectTree.xaml` and `.xaml.cs` | Project hierarchy and selection |
| `src/Panes/PropertiesPane.tsx` | `Panes/PropertiesPane.xaml` and `.xaml.cs` | Select the editor for the selected object |
| `src/Panes/PreviewPane.tsx` | `Panes/PreviewPane.xaml` and `.xaml.cs` | Picture arrangement, timeline, transport and readouts |
| `src/Views/SourceView.tsx` | `Views/SourceView.xaml` and `.xaml.cs` | Source form, grouped fields and conditional availability |
| `src/Controls/PictureFrame.tsx` | `Controls/PictureFrame.cs` and PreviewPane's picture-cell template | Browser picture surface and its presentation chrome |
| `src/Controls/TimelineBar.tsx` | `Controls/TimelineBar.cs` | Time axis and seeking control |
| `src/Controls/IconView.tsx` | `Controls/IconView.cs` | Shared icon rendering |

WPF paths in the table are relative to `WaveDudeNext.WPF/`. These are conceptual
counterparts, not identical implementations: for example, the browser's
`PictureFrame` currently also contains its layer header and brightness controls.

Put future object editors beside SourceView, following WPF's `SceneView`,
`SimulationView`, `DisplayView`, `ProbeView`, and individual part view names.
Keep reusable controls under Controls and pane composition under Panes. Do not
recreate WPF's dependency-property, docking, or command infrastructure simply to
match its implementation. Use ordinary props, callbacks, and browser semantics.

## How to investigate the WPF reference

Start with the smallest relevant view, then follow its bindings to the model and
commands. Screenshots show an arrangement and example values, but do not explain
units, defaults, availability rules, or keyboard behaviour.

1. **Find the owner.** `MainWindow.xaml` defines menus, status, default docking,
   and pane bindings. `Panes/PreviewPane.xaml` contains picture arrangement,
   brightness controls, transport, and timeline bindings. `Panes/ProjectTree.xaml`
   defines hierarchy, row controls, and add-object menus.
2. **Find the editor.** `Panes/PropertiesPane.xaml` maps model types to views.
   Read the corresponding `Views/*View.xaml` for section order, field order,
   tooltips, ranges, and conditions such as pulse-only controls. Read the
   code-behind when behaviour is not expressed by bindings.
3. **Resolve terminology.** `Strings/Strings.EN.xaml` contains visible English
   labels and hints. `Markup/ModelMemberExtension.cs` explains how model member
   references become `Label.*` and `Hint.*` resource keys. Other language tables
   are alongside the English table. Do not expose resource keys as UI text.
4. **Verify the domain.** `WaveDudeNext/Model/Source.cs`, `Scene.cs`,
   `Simulation.cs`, `Display.cs`, `Probe.cs`, and the relevant part class define
   model properties, enums, defaults, ranges, normalisation, and units. Read the
   property comments as well as the editor. Frequency, for example, is an editor
   level, amplitude is a percentage, and phase is degrees; do not invent Hz labels.
5. **Trace actions.** `Commands/*Commands.cs` identifies the product's actions;
   search their command bindings and handlers in MainWindow's partial files or
   PreviewPane's code-behind for eligibility and resulting behaviour. Running a
   simulation and playing stored frames are distinct operations.
6. **Check custom controls.** Read `Controls/TimelineBar.cs`, `NumberBox.cs`,
   `SceneOverlay.cs`, or `ProbeGraphOverlay.cs` for interaction details before
   reproducing those interactions. Theme dictionaries under `Themes/` explain
   the desktop's visual roles; the browser keeps its own CSS palette.

Useful searches from the repository root:

```sh
rg --files WaveDudeNext.WPF/Views WaveDudeNext.WPF/Panes WaveDudeNext.WPF/Controls
rg -n 'SourceEnabled|SourceX|SectionWave|PulseShape' WaveDudeNext.WPF
rg -n 'PreviewCommands|SimulationCommands|CommandBinding' WaveDudeNext.WPF/Panes/PreviewPane.*
rg -n 'Frequency|Amplitude|Phase|Normalize|Range' WaveDudeNext/Model/Source.cs
rg -n 'Label.Source|Hint.Source|SectionTiming' WaveDudeNext.WPF/Strings/Strings.EN.xaml
```

Follow one complete example before implementing a new editor: SourceView's
Frequency field leads through its binding, model property, range and normaliser,
label/hint resources, and the preview update path. Preserve the meaning while
choosing browser-native controls. Derived values such as wavelength belong to
the backend's calculation rules; do not substitute a convenient approximation.

Consult MANUAL.md and the targeted scripts in `_tools/ui_check/` for documented
user workflows. Those PowerShell harnesses exercise the Windows application;
they are references, not browser tests or Linux-runnable verification.

## State, controls and accessibility

MainWindow owns state needed by multiple panes: selection, the source draft,
modified state, and preview orientation. PropertiesPane dispatches to the
selected editor. Editors report changes through callbacks. Picture-local
presentation settings can stay with the picture until shared state is needed.

SourceView keeps number fields as strings so an intermediate edit can be empty
without turning into `NaN` or unexpectedly resetting the input. Native min/max
attributes communicate the intended ranges; they are not complete validation.
When API editing is added, parse and validate at the commit boundary, handle
validation errors, and use the server's authoritative returned state. The local
SourceDraft is temporary UI state, not an agreed API contract.

Prefer native buttons, labelled inputs, selects, checkboxes, range inputs, and
`details`/`summary` disclosures. Keep every enabled action keyboard reachable and
show a visible focus outline. Use `aria-label` for icon-only buttons and
`aria-pressed` for toggle state. Decorative SVGs are hidden from accessibility
APIs. A title tooltip supplements a control's accessible name.

ProjectTree currently uses nested native disclosures and ordinary selection
buttons. Do not add ARIA tree/menu roles without also implementing their keyboard
contracts. Escape dismisses an open main-menu popup and returns focus to its
summary; outside clicks dismiss it too. Native disclosures remain reachable
through Tab and activate with the keyboard.

Preserve the WPF distinction between frame index and simulation step when real
readouts are introduced. Keep common command names and useful automation names
where appropriate, as the initial `PreviewRun` and `PreviewTimeline` IDs do.
When adding shortcuts, consult WPF's bindings, avoid intercepting input editing,
and ensure the same action remains available through a visible control.

## Connecting the planned server

Follow PLAN.md; routes and transport details are still open. Model validation,
wave simulation, frame storage, and computed values belong to the shared backend.
The browser owns transient selection, focus, drag feedback, and display state.

Introduce explicit request/response types alongside the first working flow.
Use JSON for commands and metadata, binary transfers for pictures and files.
Keep outstanding preview requests bounded and reject stale results using
project/run revisions. Scrubbing requests server-held frames rather than
loading an entire run into browser memory. Enabling the Run, playback, save,
or export controls should accompany implementation of their actual operation
and availability conditions.

Canvas is the preview target. Add scene selection, handles and probe overlays
as browser interactions when their workflows are implemented. Avoid adding
sample numerical results, extra timeline tracks, or speculative dashboard
features merely to fill an empty pane. Check CLAUDE.md's established decisions
before expanding the controls.

## Verification and local work

From `web/`:

```sh
npm ci
npm run dev
npm run build
npm run preview
```

`npm run build` runs TypeScript checking and the production Vite build. No
frontend test or lint script is configured. Choose browser checks appropriate
to the changed behaviour instead of adding tests that mirror component markup.

For layout changes, inspect at a desktop size such as 1440 × 1000 and a smaller
window such as 800 × 600. Check both preview orientations, canvas proportions,
internal scrolling, and the visibility of status, timeline, and transport.
For controls, check selection, local edits, disclosure state, disabled states,
keyboard focus and activation, menu dismissal, and browser console errors.
Include a screenshot with a visual PR, following repository guidance.

Keep one-off scripts, screenshots, browser profiles, and other scratch files
outside the repository, under a task-specific temporary path; clean them up
when finished. Do not add browser tooling as an application dependency solely
for an ad hoc check. Preserve failing output and investigate the failure before
running a check again. Git mutations still require explicit authorisation.
