# Browser frontend and Linux server

Status: planned. This document describes the intended direction; the server and API are not implemented yet.

The plan is to make the existing WaveDude Next backend usable through a server that runs on Linux, and to
build a browser frontend in the existing `web/` npm project. The frontend uses Preact, TypeScript and Vite.
It will recreate a useful subset of the Windows application's interface and behaviour, probably without
reusing its WPF UI code.

The WPF application remains a consumer of the shared backend. The server adds another way to use that
backend, with simulation and frame storage running on the server rather than in the browser.

## Intended structure

```text
WaveDudeNext.WPF/       Windows desktop application
          |
          v
WaveDudeNext/           Shared model, simulation, rendering and export library
          ^
          |
WaveDudeNext.Server/    Proposed ASP.NET Core host, runnable on Linux
          |
          | HTTP JSON API; frame and file transfers
          v
web/                   npm / Preact browser application
```

The proposed server uses modern .NET and ASP.NET Core. It does not need Mono or a running WPF application.
The server project must build and run on Linux independently of the Windows desktop project.

## Proposed Linux build and test procedure

Use the .NET 10 SDK and its `dotnet` CLI. The SDK already includes MSBuild, NuGet and the .NET and ASP.NET
Core runtimes. No Mono, Visual Studio, Windows desktop workload or separate build framework is needed.
Use ordinary JIT compilation, retaining the settings in `Directory.Build.props`. Native AOT, trimming and
ReadyToRun are separate deployment choices and are not prerequisites for Linux support.

The command runner is [Justfile](Justfile), wrapping `dotnet` and npm. Keep dependency resolution,
incremental C# builds and publishing in MSBuild; do not reproduce them in Just or Make.

| Entry point | Benefit | Cost |
| --- | --- | --- |
| `dotnet` and npm directly | Standard tools; sufficient for every individual build and check | Several commands for a complete backend/frontend check |
| Justfile (recommended) | Short, discoverable commands for those combined workflows | Contributors need Just for the shortcuts |
| Makefile | Familiar and widely installed on Linux | File targets and `.PHONY` add machinery without helping the existing build systems |

The Justfile provides the portable library and frontend commands today. The server solution, portable
test recipes and server tests will be added with their implementations.

### Machine setup

On Ubuntu 26.04, install the SDK from Ubuntu's own repositories:

```sh
sudo apt update
sudo apt install dotnet-sdk-10.0
dotnet --info
```

For GPU diagnosis, also install `clinfo` and run `clinfo -l`. The OpenCL loader and a driver providing an
OpenCL GPU device are separate requirements. CPU builds and tests must work without either. Do not
install a driver merely to get the first server build running.

The development machine checked on 2026-11-05 has Ubuntu 26.04 x64, SDK 10.0.114, .NET and ASP.NET Core
runtimes 10.0.14, Just 1.43.0, Node 24.20.0, npm 11.19.0, curl and ffmpeg. No further tool installation is
needed for CPU development. `clinfo` is installed and GPU detection has since been fixed. GPU verification
is a separate optional step, not a prerequisite for the initial build/test procedure.
ffmpeg is useful as an independent decoder in the existing export checks; it is not a server
runtime requirement or a solution to the Windows-only MP4 writer.

When GPU verification is introduced, check loading of the versioned OpenCL runtime library on Linux;
a deployed server should not require an OpenCL development package for its unversioned symlink.

`global.json` selects stable .NET 10 SDKs: version `10.0.100`, `rollForward: latestFeature`,
`allowPrerelease: false`. This admits Ubuntu's serviced SDK
without allowing an accidental change to .NET 11.

### Build scope and commands

For a fresh checkout, run these commands from the repository root:

```sh
just restore
just build
```

Run `just` to list recipes. `just build-library` runs
`dotnet build WaveDudeNext/WaveDudeNext.csproj -c Release -warnaserror`; `just build-web` runs
`npm run build` in `web/`. Both can be used separately after restoring dependencies.

The existing `WaveDudeNext.sln` includes WPF and remains the Windows build entry point. When introducing
the host, add `WaveDudeNext.Server.slnx` containing the shared library, server, portable library harness
and server tests. It must contain no WPF project references. Until then there is no server to build or test.

Available Justfile commands:

| Command | Work |
| --- | --- |
| `just restore` | Restore the portable library; run `npm ci` in `web/` |
| `just restore-library` / `just restore-web` | Restore dependencies for one component |
| `just build` | Build the library in Release with warnings as errors; build the frontend |
| `just build-library` / `just build-web` | Build one component |
| `just dev-web` | Run Vite for development |
| `just preview-web` | Inspect the built frontend with Vite |

The current build verifies C# compilation, TypeScript and frontend bundling. It does not run runtime
tests. Add the following commands as the server and portable harness become available:

| Command | Work |
| --- | --- |
| `just restore` / `just build` | Extend the existing commands to cover the server solution |
| `just test` | Run the portable library checks, export checks and server API tests |
| `just check` | Restore, build and test, failing on any unsuccessful command |
| `just dev-server` | Run the server with `dotnet watch`, bound to loopback |
| `just test-gpu` | Optional, separate from `just check`: require an actual OpenCL GPU; fail if unavailable |
| `just publish` | Build the frontend, include its output in the server and publish for Linux |

The Justfile uses npm's `--prefix web` so its dependencies and output stay there. For the future combined
check, use `--no-restore` after the explicit restore and `--no-build` for checks following a successful
build. A standalone check must build first.
Keep the underlying commands documented so Just remains optional. Use the same procedure on Linux CI.

### Verification

First make `_tools/library_check` runnable as `net10.0` without WPF. Its current project enables WPF,
although its C# checks now call the library's native WIC wrapper and guard Windows export paths with
`OperatingSystem.IsWindows()`. Verify that removing the WPF dependency preserves the Windows checks.
Reuse these checks rather than rewriting the numerical verification in a new framework.

A Linux build probe with WPF disabled and both restore and build targeting `net10.0` found four CA1416
platform-analysis errors in `ExportChecks.cs`: the WIC benchmark callback, the MP4 benchmark callback,
and two accesses to `Mp4Writer.Throttle`. Make those call sites visibly Windows-only to the analyser;
do not suppress the diagnostics. The shared library itself builds cleanly. No harness run was attempted
after this failed build.

After that adaptation, run both commands in Release:

```sh
dotnet run --project _tools/library_check -c Release
dotnet run --project _tools/library_check -c Release -- export
```

The mandatory Linux checks must exercise the CPU explicitly, including operation with OpenCL absent.
Cover example/project loading and round trips, painting and numerical results, previews and probes,
run cancellation, frame-store disk spill and cleanup, and every advertised portable export. Keep fixtures
available and report skipped checks. Run export verification with ffmpeg on CI so decoder checks execute.
Add a separate GPU-required mode for CPU/GPU comparisons and GPU capture checks; CPU fallback must not
make that mode pass. A CPU-only OpenCL implementation does not exercise this engine, which selects GPU
devices. Keep Windows-only codecs in Windows verification and assert their unavailability on Linux.

For the new API, use a `net10.0` test project with Microsoft's MSTest and
`Microsoft.AspNetCore.Mvc.Testing`, run through `dotnet test`. Exercise the real application and CPU
backend: load a small example, request state and a binary picture, edit and validate properties, reject
stale revisions, start and cancel a run, seek stored frames, and release its cache. Add browser workflow
checks when the frontend connects; `npm run build` currently checks TypeScript and production bundling
but does not test browser behaviour.

Finally start the published server on loopback and verify an API response, a computed picture and the
built frontend's static assets. This catches packaging failures that an in-process API test cannot.
Tests should use isolated temporary cache/configuration folders, clean up in `finally`, exit nonzero
on failure and retain failing output. Keep performance benchmarks separate from correctness checks.

### Publishing

Start with a framework-dependent publish, using the installed ASP.NET Core 10 runtime. Once the server
and frontend packaging exist, the server publish step is:

```sh
dotnet publish WaveDudeNext.Server/WaveDudeNext.Server.csproj -c Release -r linux-x64 --self-contained false
```

The frontend build must be copied into the server's static-file output before publishing. Use the normal
ignored `bin/` publish folder. A runtime-only destination needs `aspnetcore-runtime-10.0`; Node and the SDK
are build-machine requirements. Self-contained publishing is an alternative for destinations without
.NET, at the cost of larger output and republishing to service the bundled runtime. It still needs any
OpenCL loader and driver used by the GPU engine.

References: [Ubuntu SDK installation](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install),
[SDK selection](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json),
[native library loading](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading),
[ASP.NET Core integration tests](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0),
[publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/).

## Backend reuse

Reuse the existing project model, validation, optical-part painting, CPU solver, OpenCL solver, probes,
frame storage and portable rendering/export code. Keep numerical behaviour and the existing project file
format consistent between the desktop and browser applications.

The library already separates much of this work from WPF. `SimulationRun`, `ScratchRunner` and `ExportJob`
accept a null synchronisation context for headless use, and projects can be loaded and saved through
streams. These are useful starting points, but Linux behaviour still needs verification.

Add a server application layer to own projects, serialise edits, create immutable run specifications,
manage runs and expose results. A request must not mutate a project while another operation is reading
it to construct a run. Simulation work must run independently of HTTP request lifetimes.

Audit platform assumptions as the server is introduced:

- OpenCL uses a portable import name, but the Linux host still needs a suitable loader and GPU driver.
  Retain the CPU fallback.
- Memory and storage detection already have non-Windows paths; verify their behaviour on Linux.
- JPEG has an in-house fallback. MP4 currently requires Windows Media Foundation, so Linux must report
  it as unavailable until an alternative is chosen.
- Scene image decoding currently belongs to the WPF application. Provide a browser/server path that
  produces the same pixel input for the library.
- Settings, cache locations and logging must work without Windows folders or registry access.

## JSON API

Expose application operations and state through an HTTP JSON API. Define explicit request and response
types rather than exposing arbitrary .NET objects, methods or WPF concepts.

The API should cover the frontend's selected workflows:

- Create, inspect and edit a project, including sources, optical parts, probes and simulation settings.
- Load examples, import existing project files and save projects in the existing format.
- Start, pause, resume and cancel runs; read progress, errors and available frames.
- Request preview frames and probe data for a selected frame or step.
- Request supported exports and download their results.

The backend remains authoritative for validation and computed results. Include project/run revisions in
state and preview responses so the frontend can identify results belonging to an earlier edit or run.
Agree the first API contracts alongside the first working frontend flow; exact routes are still open.

Use JSON for commands, metadata and numerical results of suitable size. Images, large frame buffers,
uploads and downloads should use appropriate binary transfers rather than base64 inside JSON.
Polling is sufficient for an initial implementation; choose WebSocket or SignalR updates if the measured
interaction requires them.

## Browser UI

Use `web/` as a separate frontend with ordinary Preact components and browser APIs. Recreate the selected
Windows UI workflows, rather than attempting to execute WPF controls or translate XAML automatically.
Assume the controls, layout, event handling and bindings need new implementations. Reuse backend rules
and data where useful, without making WPF UI reuse a dependency of the browser work.

The initial interface should include a project tree, property editors, run/playback controls, a timeline
and field/intensity pictures. Display the pictures on canvas; implement selection, handles and probe
overlays in browser code. Preserve keyboard access and visible focus as controls are introduced.

Keep transient interaction state, such as selection and a drag in progress, in the frontend. Send model
edits to the server and apply the validated state it returns. Keep network round trips out of purely
visual pointer feedback.

## Frame delivery and storage

Keep the complete frame store on the server, including its existing disk spill behaviour. The browser
requests frames for display and scrubbing instead of downloading an entire run into browser memory.

Start with a simple image response and measure encoding cost, bandwidth and display latency on
representative scenes. Full-resolution raw colour frames can be large: two 1920 × 1080 RGBA pictures at
30 frames per second would be about 500 MB/s before protocol overhead.

If necessary, evaluate indexed or numerical frame transfers with colour mapping in the browser. The
library's indexed-frame representation provides a possible starting point. The transport and compression
choices remain open until measured. Bound outstanding display requests so an old preview cannot build
up a delivery queue; this must not discard simulation frames from the server's store.

## Delivery sequence

1. Add a Linux-runnable server host referencing the shared library. Load an example, compute a frame and
   return state as JSON and the picture as a binary response.
2. Connect the existing Preact project. Show field/intensity canvases, edit a few properties and request
   an updated preview. Measure the complete edit-to-picture latency.
3. Add run control, progress, timeline seeking and probes. Define project ownership and disconnect
   behaviour, and verify that stale responses cannot replace newer state.
4. Expand the chosen UI subset with sources, parts, direct manipulation, project import/save and
   supported exports. Decide the order from the workflows needed first.
5. Package the server and built frontend for Linux. Use a Vite development proxy during development
   and serve the built frontend with the API under one origin for deployment.

Validate reused calculations against the existing backend, check project file compatibility, and exercise
the browser's main editing and playback workflows. Linux checks must cover CPU operation, OpenCL where
available, frame-cache cleanup and supported exports. The existing `library_check` project currently
targets Windows/WPF, so it needs adaptation or a portable verification entry point for Linux checks.

Initial deployment can be a local server opened at `localhost`. Remote hosting, authentication, concurrent
users and per-user resource limits need explicit decisions before expanding to a shared service.
