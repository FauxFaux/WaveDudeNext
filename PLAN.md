# Browser frontend and Linux server

Status: workspace creation and event subscriptions are implemented. Project support, simulation,
persistence and the remaining features below are planned.

## Implemented workspace slice

`WaveDudeNext.Server/` is a Linux-runnable ASP.NET Core 10 host referencing the shared library. It owns
isolated, empty workspaces in memory; creation does not construct a `Project` or start any jobs.

| Method and route | Behaviour |
| --- | --- |
| `POST /api/workspaces` | Return `201 Created` with `{ workspaceId, createdAt, statusSequence }` and the event URL in `Location` |
| `GET /api/workspaces/{workspaceId}/events` | Subscribe to `workspace-status` SSE snapshots; unknown IDs return `404` Problem Details |

Each connection immediately receives the current status, including reconnects. Status sequences start
at one and increase within a workspace; subscribers retain only the newest pending snapshot. Idle
connections receive a `keepalive` event every 15 seconds. `Last-Event-ID` does not request replay.
Disconnecting removes that subscription; workspaces remain available until server shutdown, which
ends their streams and releases the registry. Idle eviction and explicit release are future work.

On each page load the browser creates a fresh workspace, stores its ID in `sessionStorage` under
`wavedude.workspaceId`, and opens one native `EventSource` subscription. EventSource reconnects to the
same workspace after transient connection failures. Storage restrictions do not prevent the live page
from connecting. Reload reattachment, restart recovery and project copying between tabs are deferred;
even a tab with copied session storage creates an independent empty workspace in this first slice.
The existing browser project preview remains local and is not attached to the server workspace.
There are no project creation, editing or usage APIs.

Run `just dev-server` and `just dev-web` in separate terminals, then open the Vite URL. The API defaults
to `http://localhost:5080`; Vite proxies `/api`, including SSE, to that origin. The host does not yet serve
the built frontend. Run `just test-server` for the workspace service and HTTP integration tests.

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
WaveDudeNext.Server/    ASP.NET Core host, runnable on Linux
          |
          | HTTP JSON API; SSE status; binary frame and file transfers
          v
web/                   npm / Preact browser application
```

The server uses modern .NET and ASP.NET Core. It does not need Mono or a running WPF application.
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

The Justfile provides the portable server solution, workspace tests and frontend commands today.
Portable library checks and the remaining API tests will be added with their implementations.

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

The existing `WaveDudeNext.sln` includes WPF and remains the Windows build entry point.
`WaveDudeNext.Server.slnx` contains the shared library, server and workspace tests, with no WPF project
references. Add the library harness when it is portable.

Available Justfile commands:

| Command | Work |
| --- | --- |
| `just restore` | Restore the portable server solution; run `npm ci` in `web/` |
| `just restore-server` / `just restore-library` / `just restore-web` | Restore dependencies for one component |
| `just build` | Build the server solution in Release with warnings as errors; build the frontend |
| `just build-server` / `just build-library` / `just build-web` | Build one component |
| `just test-server` | Run the workspace service and HTTP API tests |
| `just dev-server` | Run the API with `dotnet watch` on loopback port 5080 |
| `just dev-web` | Run Vite for development |
| `just preview-web` | Inspect the built frontend with Vite |

The current build verifies C# compilation, TypeScript and frontend bundling. It does not run runtime
tests. Add the following commands as the server and portable harness become available:

| Command | Work |
| --- | --- |
| `just test` | Run the portable library checks, export checks and server API tests |
| `just check` | Restore, build and test, failing on any unsuccessful command |
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
stale revisions, start and cancel a run, seek stored frames, and release its cache. Verify SSE initial
snapshots and reconnects, completion during subscription, coalescing for a slow subscriber, and that
disconnecting a status or picture request does not cancel a run. Cover workspace isolation, reload
reattachment, duplicate-tab isolation, recovery after a server restart, failed autosaves, and the
separation of recovery copies from explicit project downloads. Add browser workflow
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

## Server and browser transport

Use ASP.NET Core 10 with Kestrel and Minimal APIs. Keep endpoints in cohesive route groups and put
project ownership, validation, run scheduling and result lifetimes in application services. Minimal APIs
are the HTTP boundary, not a reason to put the application in `Program.cs`. Use explicit DTOs,
`System.Text.Json`, typed results and Problem Details for request failures.

The transport choices follow the UI's different kinds of work:

| UI work | Transport | Reason |
| --- | --- | --- |
| Project tree, properties, examples and capabilities | JSON GET through `fetch` | Read authoritative state on demand |
| Committed project edits | Whole-project JSON PUT through `fetch` | One editing contract for properties, removal and reordering |
| Object creation, undo/redo, run control, scratch requests and export creation | JSON POST through `fetch` | Reuse backend operations and return state or a job identity |
| Run state, steps done, available frame extent, preview readiness and export state | One SSE connection per application tab | Small server-to-browser updates while background work proceeds |
| Selected field/intensity pictures and probe ranges | Binary GET for pictures; JSON GET for modest probe data | Fetch only what the current view needs |
| Project/image imports and project/export downloads | Binary or multipart HTTP transfers | Keep file contents out of JSON and the event stream |
| Selection, focus, drag feedback, layout and playback clock | Browser state | Immediate interaction without a server round trip |

JSON is a payload format; SSE and WebSockets are communication mechanisms. JSON commands and SSE
notifications complement each other. Use GET for reads, PUT for the complete editable project document,
and POST for application operations. Do not introduce per-field endpoints or a generic path-based editor.

### Technology choices

| Technology | Assessment for this application |
| --- | --- |
| ASP.NET Core Minimal APIs on Kestrel | Selected: Linux host, JSON and binary responses, static frontend assets and native .NET 10 SSE support in one application |
| ASP.NET Core controllers | Viable alternative if controller conventions become useful; no different transport or inherent latency benefit |
| SSE with native browser `EventSource` | Selected for notifications: one-way updates with browser reconnection and no frontend client package |
| SignalR | Useful if bidirectional subscriptions, hub calls or multiple cooperating clients become requirements; adds a client package and hub protocol |
| Raw WebSockets | Reserve for a measured need for sustained binary streaming; requires application framing, reconnection and flow control |
| gRPC / gRPC-Web | Browser adaptation and generated clients add machinery without a current need; ordinary browser `fetch` cannot call native gRPC |
| Blazor / Razor UI | No benefit for the chosen Preact interface; keep rendering and interaction in the browser |

SSE is built into ASP.NET Core 10 through `TypedResults.ServerSentEvents` with an async event sequence;
it does not need SignalR or a third-party SSE package. If SignalR becomes appropriate, configure and test
its reconnection policy explicitly: automatic reconnect is not enabled by default, and does not retry
the initial connection attempt.

Serve the published frontend and API from the same loopback origin. During development, proxy `/api`
from Vite to Kestrel, including the SSE response without buffering. Preact can use native `fetch`,
`EventSource`, `AbortController`, canvas and `requestAnimationFrame`; no Preact-specific transport is
needed. Open the page through the server, rather than from `file://` or an unrelated website.

Support ordinary HTTP/1.1 on localhost initially. Chrome does not negotiate cleartext HTTP/2 with Kestrel
as it would HTTPS, so do not assume multiplexing from the .NET server's HTTP/2 support alone. Keep a
single SSE stream for the tab, shared by the panes; HTTP/1.1 browser connection limits make separate
streams for every pane, layer or probe inappropriate. Multiple tabs share that connection budget.

References: [Kestrel](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-10.0),
[Minimal APIs and controllers](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/apis?view=aspnetcore-10.0),
[.NET 10 SSE support](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0?view=aspnetcore-10.0#support-for-server-sent-events-sse),
[browser SSE behaviour](https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events/Using_server-sent_events),
[Kestrel HTTP/2](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/http2?view=aspnetcore-10.0),
[SignalR JavaScript client](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client?view=aspnetcore-10.0),
[gRPC in browsers](https://learn.microsoft.com/en-us/aspnet/core/grpc/browser?view=aspnetcore-10.0).

## Workspaces and persistence

A workspace is the server-side equivalent of a desktop application window. The current implementation
is an empty workspace with identity and status subscriptions only. With project support, it will own
one open project, initially a new untitled project, plus its revision, undo/redo history, run, scratch
runner and export jobs. A browser tab attaches to one workspace. The following persistence and
attachment rules describe that later project-enabled design: the server owns authoritative project
state in memory and keeps a recovery copy on disc; the browser holds drafts and presentation state.

| State | Owner and storage | Recovery |
| --- | --- | --- |
| Project model and revision | Server workspace in memory; recovery copy on disc | Restore the last committed revision after restart |
| Undo/redo history | Server workspace in memory | Survives tab reload while the workspace remains loaded; not persisted initially |
| Runs, scratch results, probe recordings and exports in progress | Server workspace and its frame/cache files | Transient; do not promise resumption after server restart |
| Selection, unfinished inputs, drag feedback and playback position | Browser tab | Local interaction state; not part of project autosave |
| Workspace attachment | Browser `sessionStorage` holds the workspace ID | Reattach on page reload |
| Layout, theme and similar browser preferences | Browser storage | Independent of project files and server recovery copies |

### Identity and tab attachment

Issue an opaque `workspaceId` when creating a workspace. Scope project operations, status streams,
frames and jobs to that ID, for example `/api/workspaces/{workspaceId}/...`. Names and browser tab titles
are display labels, never lookup keys or storage paths. Include the expected revision in model edits,
and never reuse a project revision within a workspace, including after New, Open or Undo. Replacing the
project must not allow an old request to apply to the replacement.

Use an application service to own workspaces; do not put live project objects in ASP.NET cookie session
state or request-scoped services. A cookie is shared between tabs and cannot identify an editing tab.
Store the workspace ID in `sessionStorage` for reload attachment. Newly opened tabs get independent
workspaces. Duplicating a tab must also produce an independent workspace containing a copy of the
project, rather than creating two concurrent editors of one mutable project. Session storage can be
copied when a tab opens, so enforce this through an attachment protocol that distinguishes reload from
a second live editor; storing an ID alone is insufficient. Define that protocol with the first host.

### Autosave and explicit Save

Persist a recovery copy after every accepted project-edit transaction, including Undo and Redo. Group
related property changes and drag gestures into meaningful commits. Do not rewrite the project for
selection changes, playback ticks, status notifications, picture requests or run-control commands.

Serialise each edit transaction within the workspace: validate the expected revision and input, prepare
the normalised next state, persist it with the new revision, then publish that state and acknowledge
success. Write to a temporary file and replace the previous recovery copy so an interrupted write does
not leave a partially written project. A persistence failure must leave the previous committed revision
authoritative and return an error; do not report the edit as saved. Keep the browser draft available for
correction or retry. Run invalidation and other edit side effects follow the committed change.

Use the existing project serialisation for the recovery model, with workspace metadata beside it as
needed. Retain imported assets required to reopen it; a browser upload name is not a usable server file
path. Recovery storage belongs in the server's application-data directory, separate from disposable
frame caches. A database is not required initially. Measure the cost of rewriting projects with embedded
images before considering separate asset storage or debounced checkpoints; changing acknowledgement
to precede persistence would change the recovery guarantee.

Autosave protects the workspace working copy. File > Save / Save As produces a `.wdproj` download of a
specific committed revision in the existing format. It does not overwrite an uploaded source file.
Track recovery persistence separately from the revision last explicitly saved/downloaded; autosave must
not silently redefine the desktop's modified-state indicator. Ordinary browser downloads do not confirm
that the user completed a filesystem save. Direct saving through a user-granted browser file handle is
a separate future integration.

### Reload, close and server restart

Reloading reattaches to the workspace and fetches current project and operation state. An SSE disconnect
or disappearing tab detaches the view without discarding the project or cancelling its jobs. Do not
depend on a final browser unload request for saving or cleanup. Keep recoverable workspace metadata on
disc so a later visit can find its working copy even when the original tab's session storage is gone.

An explicit workspace release can cancel and join owned jobs and release engines and frame caches;
discarding the recovery copy is a separate deliberate action. Idle eviction may release runtime state
while retaining the recovery copy, and must not silently terminate active work. Choose timeout and
cache-retention values with the first server implementation. After a server restart, restore the model
and revision on attachment; report that transient results are unavailable rather than implying a run
or export resumed. The WPF application's existing Save and discard behaviour remains its own workflow.

References: [browser session storage](https://developer.mozilla.org/en-US/docs/Web/API/Window/sessionStorage),
[ASP.NET Core session state](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/app-state?view=aspnetcore-10.0),
[browser unload behaviour](https://developer.mozilla.org/en-US/docs/Web/API/Window/beforeunload_event),
[browser file saving](https://developer.mozilla.org/en-US/docs/Web/API/Window/showSaveFilePicker).

## JSON API and operation lifetimes

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
Serialise edits per project and validate an expected project revision atomically with applying the edit.
Return the authoritative normalised values and new revision. Keep incomplete number-field drafts local;
commit related property changes together, and coalesce drag updates rather than sending every pointer event.

### Whole-project editing

Optimise the editing API for maintainability in the local application. Send the complete editable JSON
project document for a committed edit. Do not use JSONPath updates, JSON Patch or Merge Patch, generate
CRUD routes for model properties, or require a separate request DTO for every property.

Measured on 2026-11-05: the 24 shipped examples are 692–3,193 bytes as sparse XML. A temporary .NET probe
loaded them through `Project.Load` and projected their persisted model members to compact JSON, with
defaults included, string enums, camel-case names and a part-kind discriminator. That projection was
1,719–5,544 bytes, median 2,570.5 bytes. This measures a candidate representation, not a finished API
response; workspace metadata and object IDs will add some bytes. Image bytes, generated emitters,
computed properties, simulation frames and probe recordings are excluded. Example 7's 861 generated
emitters are represented by one source and its array parameters; its projected document is 2,075 bytes.
The project has no fixed maximum size, but the existing examples give no reason for delta updates.

Use this small set of project routes, under `/api/workspaces/{workspaceId}`:

| Method and route | Behaviour |
| --- | --- |
| `GET /project` | Return the complete editable project document and revision |
| `PUT /project` | Accept `{ expectedRevision, project }`; validate, normalise, persist and return `{ revision, project }` |
| `POST /project/objects` | Create a source, part, probe or colour scheme through the existing backend add methods; return the complete document and revision |
| `POST /project/undo` and `POST /project/redo` | Move through workspace history, persist the resulting state and return the complete document and revision |

Property changes, removal and reordering use the same PUT. An object-creation request identifies a
closed object kind, the part kind where applicable, and an optional canvas position, plus the expected
revision. Reuse `Project.AddSource`, `AddPart`, `AddProbe` and `AddColorScheme` for defaults, naming and
initial geometry. A single explicit handler dispatches these known kinds; it does not expose arbitrary
.NET types or methods. Keep import/load, run control, scratch, frame and export operations as ordinary
hand-written endpoints. Generate the shared data contract, not the endpoints or an RPC framework.

Give editable objects stable workspace IDs so a full round trip preserves identity across renames and
reordering. Keep these IDs and asset references in the JSON contract without changing the existing
`.wdproj` format. Normal project responses contain only editable model state and those references;
derived values and job status are separate responses. The complete project document is also the edit
input, so do not mix read-only computed results into it.

Reject a stale expected revision with `409 Conflict`, without partial changes. Serialise mutations in
the browser as well as the workspace: at most one model mutation is in flight. Keep later input as
drafts, then build the next complete document from the acknowledged state and those drafts; an earlier
response must not erase unsent edits. Each committed edit is one undo step, with related changes grouped.

Reconcile the previous and incoming project centrally on the server. Whole-project delivery must still
distinguish simulation, probe and display changes, preserve scheme-rename and gate-follower behaviour,
and avoid restarting a simulation for an unrelated display edit. Reuse the library's rules and test
these cases through the same commit path. This is application logic, independent of HTTP granularity.

### Generated Zod contract

Generate Zod source for the browser from the C# JSON contract and infer TypeScript types with
`z.infer<typeof ProjectSchema>`. Zod is the preferred frontend schema asset; do not maintain a parallel
hand-written TypeScript project hierarchy or make JSON Schema the frontend contract. Keep handwritten
editor-specific refinements in a wrapper around the generated schemas, never inside generated files.

Define the JSON member selection, naming, enum representation, nullability and part-kind discriminators
once, and use that same contract for server serialisation and generation. Reuse the model's types and
existing range/default/description metadata rather than duplicating those constraints in DTOs and UI
code. XML-only annotations do not automatically configure `System.Text.Json`; computed members and
image bytes need an explicit exclusion policy, with asset references supplied by the workspace layer.

Use a small .NET generator under `_tools/contract_gen/` to emit
`web/src/Api/Project.generated.ts` directly. Scope it to the contract's actual types: objects, arrays,
strings, booleans, bounded numbers, string enums, nullable values and discriminated part variants. Fail
generation on unsupported contract shapes rather than emitting `any` or silently weakening validation.
The generated module exports individual object schemas as well as the complete project schema, so
editors can validate their fields without defining the same rules again.

For example, the existing source frequency metadata should generate
`z.number().min(0.01).max(255).meta({ default: 100 })`. Defaults are descriptive metadata, not automatic
replacement for missing required fields. New objects come from the backend creation operation; the
generator must not bake a random project seed or other context-dependent defaults into static code.
Form drafts remain strings until commit, separately from the numeric project contract. The backend
still validates and normalises every update and owns rules that depend on other model values.

Add generation and a check for stale generated output to the normal build procedure when introducing
the first contract. Verify real serialised example projects against the generated Zod schema, including
every part kind, nullability and range boundaries. No server process should be needed for generation.
Zod's schema composition leaves room for browser-specific validation as the interface grows, while
generated declarations stay reproducible.

References: [Zod schemas](https://zod.dev/api), [inferred TypeScript types](https://zod.dev/basics#inferring-types),
[schema metadata](https://zod.dev/metadata).

### Background operation lifetimes

Starting a run, a scratch computation or an export must return an operation identity promptly (normally
`202 Accepted` with a status URL), rather than keeping a request open for the computation. The server
application layer owns the work through completion or explicit cancellation, with host shutdown and
resource cleanup integrated into its lifetime. A request ending, a frame fetch being aborted or an SSE
connection closing must not itself cancel a stored run or export. Workspace release and cache retention
follow the lifecycle above. After an ambiguous network failure, reconcile operation state
before retrying a command that could create duplicate work.

Preserve the established live-edit rules in CLAUDE.md: simulation edits invalidate and stop the old run,
scratch work serves the newest requested position, and automatic re-runs follow the configured delay.
Probe-only changes and display changes have different invalidation rules. Centralise those decisions in
the server application layer rather than making each browser control reproduce them.

Use JSON for commands, metadata and numerical results of suitable size. Images, large frame buffers,
uploads and downloads should use appropriate binary transfers rather than base64 inside JSON.
Polling can verify the first host before the UI connects, but SSE is the intended status mechanism when
run and playback controls are enabled.

### Status events and reconnection

Publish small, revisioned status snapshots or invalidations. Include the relevant project revision,
run/operation identity, a monotonically increasing status sequence and available frame extent. Coalesce
progress at a bounded rate; do not emit an event for every simulated step or stored frame. Keep pictures,
full probe histories and file contents out of SSE. Probe notifications identify available ranges that
the browser can request in batches.

Subscribe before taking the initial status snapshot, with ordering controlled by the application layer,
so a completion between subscription and snapshot cannot be lost. Send a current snapshot on every SSE
connection, including reconnects. The browser reconciles it with command responses using identities,
revisions and status sequences; arrival order across HTTP responses and SSE is not a state version.
Reattaching to a retained workspace can recover current operation state through ordinary GET requests too.

Treat events as recoverable notifications, not a durable log or an exactly-once delivery mechanism.
Native EventSource reconnects automatically, but replay based on `Last-Event-ID` would require a separate
retained history; the initial design resynchronises from current state instead. Bound subscriber queues
and coalesce pending status to the newest snapshot, including terminal state and errors. A slow or
disconnected browser must not block simulation, grow an unbounded event backlog or lose the ability to
discover completion. Flush event records promptly and use periodic keepalives while otherwise idle.

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

The browser owns playback timing, speed, loop and the selected position. Simulation runs as fast as the
engine and store allow; it is not paced by the browser or its network connection. SSE reports how far
the store has progressed. Playback requests frames at its own pace and holds at the newest available
frame when it catches up. Seeking stored results is a read; asking for a scratch frame of an edited
project is a separate compute operation.

Start with a simple image response and measure encoding cost, bandwidth and display latency on
representative scenes. Full-resolution raw colour frames can be large: two 1920 × 1080 RGBA pictures at
30 frames per second would be about 500 MB/s before protocol overhead.

If necessary, evaluate indexed or numerical frame transfers with colour mapping in the browser. The
library's indexed-frame representation provides a possible starting point. The frame representation
and compression choices remain open until measured. Existing 8-bit indexed frames have
brightness applied at capture, so they cannot provide arbitrary later brightness adjustment like the
16-bit samples. Preserve the current brightness and Auto behaviour when evaluating browser colour mapping.

Request pictures at the actual display size, accounting for device pixel ratio and preserving the scene's
aspect ratio. Start with a lossless image response as the correctness baseline. Measure store read,
rendering, encoding, transfer, browser decode and canvas presentation separately, including simultaneous
field and intensity display, scrubbing during a run, and brightness changes. Localhost removes external
network latency but not these costs. At 30 fps the complete display path has about 33 ms per frame;
WebSockets cannot remove pixel bandwidth or encoding cost.

Keep one active display request per layer and only the newest pending target; use a small bounded cache
for playback rather than fetching the entire run. Use `AbortController` to cancel obsolete transfers and
propagate cancellation to disposable rendering work where possible. Aborting a fetch alone does not
guarantee that server computation has stopped. Also check the selected position, project revision,
run/scratch identity and display parameters after decode, before presenting either picture or its probe
overlay. Coordinate both layers so a late response cannot mix different positions. Superseded scratch
requests must replace pending work on the server as well. Retain the previous picture until its
replacement is ready. Discarding obsolete presentation work must not discard simulation frames from
the server's store.

Consider binary WebSocket delivery only if measurements show repeated HTTP delivery is a material
bottleneck after payload and rendering choices are addressed. Such a stream still needs a bounded
queue, request identities and explicit demand or acknowledgements; the browser WebSocket API provides
no automatic receive backpressure.

References: [fetch cancellation](https://developer.mozilla.org/en-US/docs/Web/API/Fetch_API/Using_Fetch#canceling_a_request),
[WebSocket backpressure](https://developer.mozilla.org/en-US/docs/Web/API/WebSocket).

## Delivery sequence

1. Add a Linux-runnable server host referencing the shared library and owning isolated workspaces.
   Load an example, compute a frame and return state as JSON and the picture as a binary response.
2. Generate the shared Zod project contract and connect the existing Preact project with workspace
   attachment, whole-project PUT editing and recovery persistence. Show field/intensity canvases,
   edit a few properties and request an updated preview. Verify reload and duplicate-tab behaviour,
   and measure the complete edit-to-picture latency including autosave.
3. Add run control, one SSE status stream, timeline seeking and probes. Implement workspace release
   and cache retention; verify reconnect resynchronisation, bounded delivery to a slow browser, and
   that stale HTTP responses or SSE events cannot replace newer state.
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
