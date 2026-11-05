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
