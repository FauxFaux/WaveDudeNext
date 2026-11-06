# Building the browser runtime

`WaveDudeNext.Wasm` is an additional .NET 10 browser project referencing `WaveDudeNext.csproj`
and its ordinary NuGet dependencies. The existing library, WPF project and desktop solution
retain their own build commands. Neither ASP.NET Core nor Blazor is used for the browser target.

## Prerequisites

Use Docker with BuildKit, Node.js 24/npm, Just and Rust with the
`wasm32-unknown-unknown` target (`rustup target add wasm32-unknown-unknown`) and
nightly sources (`rustup toolchain install nightly --component rust-src`) for the
shared-memory kernel's standard library. Browser
builds do not need .NET or a Wasm workload installed on the host. Native library
and desktop builds still use the SDK selected by `global.json`.

Just uses `cmd.exe` on Windows, so these recipes do not require Git Bash or Cygwin.
The scripts under `_tools/scripts/` use only Node built-ins and can run before `npm ci`.
For example, `node _tools/scripts/just-wasm-build.mjs` builds the runtime without
`web/node_modules`. The scripts resolve the checkout from their own location. Frontend builds
still need `just restore-web`; snapshot deployment also requires Git, rsync and SSH access.

`_tools/build/Dockerfile` pins Microsoft's .NET SDK 10.0.100 Ubuntu Noble image by digest, matching
the feature band in `global.json`. Its cached toolchain layer installs Python, libatomic and
`wasm-tools` using the manifests shipped with that SDK (`--skip-manifest-update`). Workload
installation happens only inside the image. The first build downloads a substantial native
toolchain; later builds reuse it. Update the SDK image version/digest and check `global.json`
together when upgrading the browser toolchain.

The local checkout is the build context; there is no clone. `_tools/build/Dockerfile.dockerignore` admits only
`WaveDudeNext/`, `WaveDudeNext.Wasm/`, `global.json`, `Directory.Build.props` and `.editorconfig`,
excluding host `bin/` and `obj/` directories. The Dockerfile explicitly mounts those inputs
read-only under `/src/`. Restore and publish write intermediates under `/build/artifacts/`
and the complete runtime bundle under `/out/`. NuGet packages use a BuildKit cache. Source
edits invalidate the publish layer while retaining the toolchain and project restore layers.

## Build and development

From the repository root:

```sh
just restore
just build
just dev-web
```

`just build-wasm` publishes Release AOT code with Wasm SIMD. The runtime is single-threaded and
uses invariant globalisation. Docker's `wasm` stage contains only the SDK's published
`wwwroot/` bundle. BuildKit exports it to a temporary host directory; after a successful build,
the recipe replaces `web/public/wasm/` with that output. A failed build keeps the existing
bundle, and replacing the directory removes stale fingerprinted files. Temporary output is
cleaned up on exit. No writable source mount or root-owned host build output is needed.
`just build-web` publishes C# Wasm first, then rebuilds the Rust SIMD solver through
npm's `prebuild` hook and runs TypeScript and Vite; the deployable site is `web/dist/`.
Vite emits `web/src/assets/rust-kernel.wasm` and `rust-kernel-shared.wasm` as
fingerprinted assets and includes their base-path-aware URLs in the simulation
worker. Both generated kernels are ignored by Git. `just build-rust-kernel`
rebuilds them independently. Cross-origin-isolated pages use persistent compute
workers with static round-robin 16-row stripes over shared memory, processed in
one Wasm call per worker/substep; other pages use the
single-worker SIMD module. See [the kernel guide](rust-kernel/README.md).
Vite serves `public/` directly in development and copies it unchanged in production.
Keep the complete `wasm/` directory together: .NET's loader expects its framework modules,
manifest, assemblies and Wasm files at their relative URLs.

`just dev-web` publishes C# Wasm and rebuilds the Rust kernel before starting Vite.
After C# changes, run `just build-wasm` again and reload the page. After Rust changes,
run `just build-rust-kernel` and reload. Frontend changes use Vite's normal development loop. `just preview-web`
serves an existing production build. `just build-library` builds only the shared library and
does not require the browser workload. For a frontend-only rebuild after publishing Wasm, use
`npm --prefix web run build`.

The complete generated `web/public/wasm/` bundle is ignored by Git, including its JavaScript
loaders. Generate it on a fresh checkout and regenerate it after C# changes with
`just build-wasm`. `just restore-wasm` warms the container's dependency layers without
publishing; `just restore` also runs `npm ci` on the host. `CompressionEnabled=false` disables
`.gz` and `.br` sidecars; the deployment web server handles response compression. `web/dist/`,
`bin/` and `obj/` remain ignored. Build sources belong in `WaveDudeNext.Wasm/`, outside `_tools/`.

To export the assets directly to another location, run from the repository root:

```sh
docker build --file _tools/build/Dockerfile --target wasm --output type=local,dest=/absolute/path/to/wasm-output .
```

The context `.` is the local repository; the destination is a host path. The direct Docker
command merges files into its destination, so use an empty directory to avoid stale runtime
files. `just build-wasm` handles replacement automatically.

## Runtime boundary

Import `loadWaveDude` from `web/src/Wasm/Runtime.ts` to initialise the runtime lazily:

```ts
const runtime = await loadWaveDude()
const preparedBytes = runtime.Prepare(projectJson, maxFrames)
```

The loader uses Vite's `BASE_URL`, including a deployment prefix. It dynamically imports the
published `wasm/main.mjs` without bundling or rewriting .NET's internal modules. The JavaScript
host creates one runtime per JS context and obtains the `[JSExport]` methods from the assembly.
The C# solver exports remain available for independent consumers and reference checks.
`SmokeTest()` executes 16 steps of the real CPU solver and returns a finite, positive peak field
amplitude. `StartSimulation(projectJson, maxFrames)` reads and normalises a canvas project using the shared
model, paints its optical parts and creates the CPU solver. It returns width, height, bounded
step count, frame interval and frame count. The export increases the interval to fit the caller's
frame budget without changing the input project. The run ends at its last sampled step.
`NextFrame()` advances to the next capture
and returns two binary half-float planes: field, then square-root intensity. `EndSimulation()`
releases the engine and snapshot. Image scenes report an error until image delivery is connected.

`Prepare(projectJson, maxFrames)` returns the independent binary input package documented
in [PREPARE.md](WaveDudeNext.Wasm/PREPARE.md), without constructing an engine. Run decodes it
through `prepareRustSimulation` into a typed model retained in the simulation worker for Rust
kernel hand-off. `prepareKernel` copies the packet into an independent Rust Wasm instance
and initialises its mutable state. `RustSimulation.ts` advances to completed step
`index * visualizeEvery + 1`, captures the two half-float planes and packs Rust's
per-step point samples and current line profiles for recording. The frontend calls
only C# preparation and scene painting; all solver stepping, intensity filtering,
probe integration and frame capture run in Rust. Maps and probe indices share the
retained packet's buffer and are treated as read-only. See
[the Rust interface](rust-kernel/INTERFACE.md) for the numerical ABI and memory ownership.

The preserved C# solver installs active probes through the shared `ProbeSampler`, samples after every solver step,
and integrates using the shared `ProbeCollector` and engine. `GetProbeFrame()` returns the latest
capture's binary probe packet: little-endian int32 probe count and steps completed; per active probe
in project order, int32 point sample count, int32 profile count (-1 for point probes), float64 line
length in canvas pixels, then float32 point samples and profile values. Point samples cover only
steps since the previous capture; line profiles belong to the current capture. Reading the packet
does not drain it. The Rust adapter emits the same packet format; the worker stores
it after the two half-float planes in the same closed frame file.
Integrated intensity excludes values before the probe's start step.

`RenderScene(projectJson)` independently normalises a canvas project and calls the shared
`PartPainter.Paint` and `GlowingEdges.Compute`. It accepts either a complete project or just
`scene` and `parts`. The result starts with two little-endian 32-bit integers (width, height),
followed by two tightly packed RGBA planes: scene image, then glowing edges. An unpainted canvas
returns only the eight-byte header. Image scenes fail explicitly. The browser does not need
the server experiment's PNG encoding/decoding for these local pixels.

`useScene` caches the current geometry independently of simulation results, ignoring source,
display, timeline, object name and identity edits. A dedicated scene worker computes and transfers
both planes without blocking the UI, then terminates to release its Wasm heap. Geometry changes
terminate pending workers and immediately hide obsolete scene layers. The pictures show the scene
when there is no wave frame; each layer's overlay switch and gain control the translucent glowing
edges, including over retained frames of matching dimensions.

Run `just check-wasm` to publish and exercise this export under Node. After an existing build,
`npm --prefix web run check-wasm` checks the generated bundle without publishing again, including
project submission, source edits, deterministic frames and the storage-driven frame interval.
The same check compares scene and edge pixels against native .NET output for the server commit's
scene fixture and checks geometry edits, empty canvases and independent simulation lifetimes.
It also checks point/line measurements, integrated start gates, sampling between captures and exact
probe data when seeking stored frames.
It also exercises the frontend preparation decoder, including filtered and overlapping sources,
probe flags, relocated packets, empty sections and rejection of malformed binary inputs.
`just check-probe-graph` runs the retained graph calculation checks.
`just check-rust-simulation` builds both Wasm modules and checks the frontend Rust
adapter against the preserved C# solver, including probe packets, capture intervals,
storage budgets, deterministic runs, file storage and seeking. After building,
`npm --prefix web run check-rust-simulation` checks the existing assets. The wider
kernel numerical comparison is `node rust-kernel/checks/check-numerics.mjs`.
`npm --prefix web run check-frame-writes` checks the two-frame write bound, overlap,
ordered notifications, final drain and write/producer failures without a Wasm build.

The UI edits one Project in root Preact state and opens/saves JSON text without a server.
Run sends the current project to a dedicated worker. It uses half the estimated unused origin
storage, allowing 4096 bytes of file overhead per frame, and increases the capture interval if
needed. Simulation overlaps with at most two outstanding OPFS frame writes, making
room before producing the next owned frame buffer. Files are announced in capture
order after closing, even if writes finish out of order. All writes settle before
completion or a reported error. Work timings are snapshotted at capture so an older
frame's notification cannot read a newer in-progress advance's mutable state.
The UI reads the selected frame from disc into the existing previews and timeline; it has no
RAM frame cache. Point probe histories are retained once per run as growable float32 arrays in
the page, exposing only the prefix through the selected frame. Line profiles stay on disc with their
frames. Rust retains only the current probe batch and integration totals. The storage budget includes
the point samples and a conservative maximum size for every line profile.
Missing estimates, insufficient space and write/read errors fail explicitly.
Completion, Stop, project replacement and unmount terminate the worker and release its Wasm heap.
Edits outside display settings, colour schemes and probe graph box/axis settings cancel active work and mark retained results
outdated. Display and palette edits recolour stored samples without cancelling the worker or
invalidating its frames, including during startup. Probe measurement edits cancel the run and hide outdated recordings.
New runs, project replacement and unmount delete the previous recording. Reloads/crashes can
leave orphaned files; recovery and automatic orphan cleanup are deferred. See
[FRAME-REGENERATION.md](docs/FRAME-REGENERATION.md) for the storage calculations and deferred
checkpoint/replay design. Storage quota estimates do not guarantee physical free disc space.
The worker uses `addEventListener`, leaving `onmessage` unset to avoid .NET 10 misidentifying it
as a managed runtime thread ([dotnet/runtime#114918](https://github.com/dotnet/runtime/issues/114918)).
The runtime URL is resolved in the page before passing it to the worker, preserving deployment
prefixes. See [PLAN.md](PLAN.md) for the remaining migration steps.

References: [Microsoft's browser interop guidance](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0)
and [Vite public assets](https://vite.dev/guide/assets.html#the-public-directory).
