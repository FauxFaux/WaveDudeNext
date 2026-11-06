# Browser frontend with the shared solver in Wasm

The web application will run the existing .NET CPU solver locally through WebAssembly.
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
- `web/src/Wasm/Runtime.ts` provides a lazy, base-path-aware runtime loader. The initial `SmokeTest`
  export executes a small deterministic scene with the shared CPU solver.
- The Preact root owns one in-memory Project. Project/property panes edit it locally; File
  resets the project or opens/saves validated JSON through a text area. Reload clears the project.
  Workspace creation, HTTP/SSE and server mutation queues have been removed.
- Run submits the current canvas project to a dedicated worker. The shared model normalises it,
  paints optical parts and runs the CPU solver. The worker stores field/intensity pairs in OPFS,
  increasing the capture interval to fit half the estimated unused storage quota. The UI reads
  selected frames from disc into the existing renderer and timeline, without a RAM frame cache.
  Completion and Stop terminate the worker;
  edits cancel active work and mark retained frames outdated. Examples load from static JSON.
  Image scenes, probe recordings and shared colour-scheme loading remain unconnected.
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
3. Connect probe graphs and playback to worker results, extending the existing run control and
   timeline seeking. Keep transfer queues bounded, identify results by project revision and
   run/frame identity, and discard stale presentation results after an edit or seek.
4. Extend the first OPFS recording path with lifecycle/recovery support if needed. Long recordings
   cannot all live in the Wasm heap. Checkpoint/replay remains a deferred design in
   [FRAME-REGENERATION.md](docs/FRAME-REGENERATION.md). Evaluate IndexedDB for project recovery. Explicit Save
   downloads a project; recovery storage must remain separate from saving the user's file.
5. Migrate image import, colour schemes and supported exports incrementally.
   Preserve project compatibility and compare calculations against native .NET as features arrive.

The browser owns selection, focus, drag feedback, picture layout and playback timing. Shared .NET
code owns model normalisation and numerical simulation. Reuse the existing WebGL2 colour mapper
and Canvas 2D fallback: half-float frame planes and indexed palettes already have browser renderers.
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
