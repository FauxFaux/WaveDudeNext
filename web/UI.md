# Browser UI development guide

This guide covers work on the Preact interface. Read the repository's
[AGENTS.md](../AGENTS.md), [CLAUDE.md](../CLAUDE.md), and [PLAN.md](../PLAN.md)
for repository rules, established product decisions, and the Wasm migration plan.

## Scope and current state

The browser UI is a separate consumer of the shared WaveDude Next backend.
WPF provides the reference for terminology, workflows, controls, and behaviour.
The browser implementation uses Preact, TypeScript, CSS, and browser APIs.

MainWindow owns one Project in Preact state. The project tree and property editors
edit it locally. File resets it or opens/saves validated JSON through a text area;
reload clears it. There is no workspace, HTTP API or SSE connection. Canvas
renderers and the timeline display frames read from an OPFS disc recording of the current canvas project.
The capture interval increases when necessary to fit half the estimated unused storage quota.
Only the selected picture is retained in memory; there is no RAM frame cache.
Run starts a dedicated Wasm worker; Stop or project edits terminate it. Examples load
from static JSON. Probe recordings and image scene delivery remain unconnected.

`src/Wasm/Runtime.ts` lazily loads the new .NET runtime from Vite's static assets.
Simulation uses `src/Wasm/Simulation.worker.ts` and the shared solver exports;
keep numerical work off the UI thread. Use `addEventListener` in the worker and leave
`onmessage` unset so .NET 10 initialises it as an independent runtime.
Keep unavailable operations disabled and preserve empty preview/readout states.

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

MainWindow owns the Project, shared selection, source drafts, modified state and
preview orientation. Functional state updates apply local edits to the current
Project. PropertiesPane dispatches to the selected editor.
Editors report changes through callbacks, while picture-local presentation
settings can stay with the picture until shared state is needed.

The existing `src/Api/Project.generated.ts` is a schema snapshot used by current
frontend types. The old generator depended on server code and was not retained.
Local project and object creation use its default metadata, with contextual
defaults matching the shared model's constructors. Re-establish contract generation
at the Wasm boundary before expanding numerical or normalisation behaviour.

`Views/SourceDraft.ts` keeps number fields as strings so an intermediate edit can
be empty without turning into `NaN` or resetting the input. Keep these drafts
separate from numeric project values. Valid edits enter root state immediately;
incomplete number strings remain in the editor. Reset and Open clear old drafts.

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

## Connecting the Wasm worker

Follow PLAN.md for connecting the root-owned Project to a Wasm worker.
Model validation, wave simulation and numerical results belong to the shared
.NET code. The browser owns transient selection, focus, drag feedback and display
state. Keep the main thread responsive while the worker computes bounded batches.

Serialise model mutations and retain later unsent drafts when applying an
acknowledgement. Identify results by project revision, run and frame; an obsolete
response must not replace a newer project or picture. Transfer frame planes as
binary buffers, keep outstanding requests bounded, and use a bounded frame cache.
The browser owns the playback clock; simulation proceeds independently.

Reuse `src/Rendering/FrameRenderer.ts` for WebGL2 colour mapping and Canvas 2D
fallback. Brightness and palette edits should recolour stored frames. Keep scene
selection, handles and probe overlays as browser interactions when their workflows
are implemented. Consult CLAUDE.md before expanding the chosen UI scope.

Browser recovery storage is separate from explicit project saving. Do not rely on
an unload handler to persist edits, or assume long recordings fit in the Wasm heap.
Enable run, playback, save and export controls alongside their actual operations
and availability conditions.

## Verification and local work

From the repository root, after the Wasm prerequisites in [WASM.md](../WASM.md):

```sh
just restore
just build
just dev-web
just preview-web
```

`just build` publishes Wasm before TypeScript checking and the production Vite
build. `just check-probe-graph` checks the existing graph calculations. No
frontend lint script is configured. Choose browser checks appropriate
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
