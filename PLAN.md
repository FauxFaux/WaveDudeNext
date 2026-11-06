# Browser frontend with the Rust solver in Wasm

The web application runs the Rust SIMD solver locally through WebAssembly, using
the shared .NET model to prepare its inputs and paint scene previews.
Deployment is a static Vite site. This branch starts from `origin/master` and retains the
Preact frontend from the HTTP experiment, with an additive Wasm project and build procedure.
The shared library and Windows desktop solution remain unchanged.

## Current boundary

- `WaveDudeNext.Wasm/` references the real shared library and publishes a .NET browser runtime
  using Release AOT and SIMD. There is no copied solver, benchmark project or Windows harness dependency.
- `just build` publishes the runtime to `web/public/wasm/` before Vite copies it to `web/dist/wasm/`.
  Docker mounts the local C# sources read-only and exports the runtime bundle. Runtime modules and
  resources retain their relative URLs. The generated bundle, intermediate build output and
  `web/dist/` are ignored. Response compression belongs to the web server.
- `web/src/Wasm/Runtime.ts` provides a lazy, base-path-aware C# runtime loader for
  preparation and scene painting. C# solver exports remain available for reference checks.
  The Rust kernel is rebuilt by npm before development and production builds and
  emitted as a separate Vite asset, loaded only by the simulation worker.
- The Preact root owns one in-memory Project. Project/property panes edit it locally; File
  resets the project or opens/saves validated JSON through a text area. Reload clears the project.
  Workspace creation, HTTP/SSE and server mutation queues have been removed.
- Run submits the current canvas project to a dedicated worker. The shared model normalises it,
  paints optical parts and prepares maps, sources and probes. The Rust kernel advances
  the simulation, integrates probes and captures frames. The worker stores field/intensity
  pairs in OPFS,
  increasing the capture interval to fit half the estimated unused storage quota. The UI reads
  selected frames from disc into the existing renderer and timeline, without a RAM frame cache.
  Completion and Stop terminate the worker;
  edits to simulation inputs cancel active work and mark retained frames outdated. Display and
  colour-scheme edits recolour stored samples while the simulation continues. Examples load from static JSON.
  Image scenes remain unconnected.
- Active probes use the shared prepared layouts and Rust sampling: point histories include every solver step,
  line profiles belong to each captured frame, and integrated intensity honours the start step.
  Point histories are retained once per run in the page; incremental samples and line profiles are
  stored alongside each frame in OPFS. Seeking selects the history prefix and that frame's profile.
  Probe measurement edits invalidate results; graph box/axis edits preserve the running simulation.
- Shipped colour schemes are copied into static frontend data in `web/src/assets/color-schemes.ts`.
  The JavaScript renderer uses them directly, with project schemes taking precedence and legacy
  names still resolving. Colour selection does not require Wasm or a server.
- Canvas scene previews run independently in a dedicated Wasm worker using the shared part painter
  and glowing-edge detector. Geometry edits replace the preview; source, display and timeline edits
  reuse it. Both pictures show the scene before a run and apply their configured edge overlays
  to compatible retained frames. Scene workers terminate after rendering or when superseded.
- The generated Zod project schema is retained as a snapshot for existing frontend types.
  Its old server-dependent generator and contract verification are not part of this branch.

No server, server tests, experimental probe sources, benchmark data or new reference images are
carried across. Existing examples and Windows harnesses already present in `origin/master` stay intact.

## Next iterations

1. Replace the current per-preview worker with a long-lived worker when longer runs are needed.
   Re-establish schema generation independently of any server using the shared model's defaults,
   ranges and normalisation.
   Replace the schema-snapshot defaults used by local editing with the shared model contract,
   and consider computing a preview after a source edit.
2. Add bounded batches of simulation steps. Process stop and edit messages between batches so
   cancellation remains responsive. Do not assume desktop task/thread scheduling works unchanged
   in a single-threaded Wasm runtime. Measure edit-to-picture latency and worker responsiveness.
3. Connect playback to worker results, extending the existing run control and
   timeline seeking. Keep transfer queues bounded, identify results by project revision and
   run/frame identity, and discard stale presentation results after an edit or seek.
4. Extend the first OPFS recording path with lifecycle/recovery support if needed. Long recordings
   cannot all live in the Wasm heap. Checkpoint/replay remains a deferred design in
   [FRAME-REGENERATION.md](docs/FRAME-REGENERATION.md). Evaluate IndexedDB for project recovery. Explicit Save
   downloads a project; recovery storage must remain separate from saving the user's file.
5. Migrate image import, colour-scheme import/export and supported exports incrementally.
   Preserve project compatibility and compare calculations against native .NET as features arrive.

The browser owns selection, focus, drag feedback, picture layout and playback timing. Shared .NET
code owns model normalisation and preparation; Rust owns numerical simulation. Reuse the existing
WebGL2 colour mapper and Canvas 2D fallback: half-float frame planes and indexed palettes already have browser renderers.
A brightness or colour-scheme change should recolour the selected frame without another simulation.

Keep visible keyboard focus and the desktop's established terminology and behaviour. Introduce
working operations alongside their availability conditions; do not fill empty panes with synthetic
results. Read `web/UI.md` for the retained layout and control guidance.

## Constraints and validation

The initial runtime is single-threaded with SIMD; this does not promise desktop multicore throughput.
OpenCL and Windows Media Foundation do not provide browser GPU simulation or video export.
Browser threading, WebGPU and large-recording storage are separate decisions for later evidence.
Invariant globalisation currently keeps the numerical build small; revisit it before localised
model parsing or formatting is introduced.

Verify the asset pipeline in Vite development and production, including a deployment below a URL
prefix. Exercise the JavaScript export against actual Wasm, not just a successful C# compile.
Future worker checks should cover cancellation, stale results, bounded memory and transfer queues,
main-thread responsiveness, and independent projects in separate tabs.

See [WASM.md](WASM.md) for prerequisites and the build/runtime boundary. Numerical experiments and
benchmark evidence remain on `wasm-experiment`; they are not dependencies of this branch.
