# Repository Guidelines

## Project Structure & Module Organization

- `WaveDudeNext/`: shared .NET 10 library; model, CPU/OpenCL engines, optical parts, rendering, probes, frame storage and exports.
- `WaveDudeNext.WPF/`: existing Windows desktop application, including controls, docking, localisation and document generation.
- `WaveDudeNext.Wasm/`: additive browser build target referencing the shared library, with JavaScript exports and a runtime loader. No ASP.NET Core host or Blazor UI.
- `web/`: npm-managed Preact/TypeScript/Vite frontend. `src/` contains components; `public/` contains static assets, including the generated, Git-ignored Wasm bundle in `public/wasm/`.
- `_tools/build/`: Dockerfile and its build context ignore rules for the browser runtime.
- `_tools/scripts/`: dependency-free Node.js scripts used by Just; no `web/node_modules` required to run the Wasm build wrapper.
- `_tools/library_check/` and `_tools/ui_check/`: existing Windows verification harnesses from upstream. The browser build does not use these harnesses.
- `Examples/`, `Assets/`, `MANUAL.md`: upstream example projects, documentation images and user documentation.
- `PLAN.md`: browser-only direction and incremental frontend migration. `WASM.md`: build prerequisites, asset delivery and verification.

## Build, Test, and Development Commands

Use Docker with BuildKit, Node.js 24/npm and Just for browser builds. `_tools/build/Dockerfile` pins the .NET 10 SDK and installs its Wasm workload inside a cached image layer; no host Wasm workload is required. Native library and desktop builds use the SDK selected by `global.json`. Read `WASM.md` for container paths, asset export and toolchain maintenance.

[Justfile](Justfile) is the entry point; run `just` to list commands. Recipes run from the repository root, including when invoked from a subdirectory.

- `just restore`: restore the browser project and its shared-library NuGet dependencies inside Docker, then install locked frontend dependencies on the host with `npm ci`.
- `just build`: publish Release AOT/SIMD Wasm into `web/public/wasm/`, check TypeScript and build the production frontend into `web/dist/`.
- `just build-wasm` / `just restore-wasm`: publish or restore only the browser .NET target inside Docker, mounting local sources read-only and exporting generated assets to the host.
- `just build-library` / `just restore-library`: build or restore the original library independently, without WPF or a Wasm workload.
- `just build-web`: publish Wasm, then build the frontend so copied assets are current.
- `just dev-web`: publish Wasm and start Vite. Rerun `just build-wasm` after changing C#.
- `just preview-web`: serve the previously built frontend.
- `just check-probe-graph`: run the existing browser probe graph calculation checks.
- `just check-wasm`: publish Wasm and verify the shared solver through its JavaScript export under Node.

The UI owns one in-memory Project in root Preact state, with local editing, reset and JSON text open/save. There is no server, workspace API or Vite API proxy. Simulation and examples remain disabled pending their Wasm/static integration.

The desktop solution remains independent:

- `dotnet build WaveDudeNext.sln`: build the Windows desktop solution; require zero warnings.
- `dotnet run --project WaveDudeNext.WPF`: launch the desktop application on Windows.
- `dotnet run --project _tools/library_check -c Release`: verify library behaviour on Windows; append `-- export` for export checks.
- `powershell -ExecutionPolicy Bypass -File _tools/ui_check/KeyboardCheck.ps1`: run a targeted Windows UI check; choose the script matching the change.

## Coding Style & Naming Conventions

Follow `.editorconfig`: C# uses tabs and Allman braces; project XML uses two spaces. Preserve the frontend's two-space indentation and existing TSX style.

Use PascalCase for C# types/public members and Preact components. Prefer concise expression-bodied accessors without `this.`. C# source files need the prescribed license header; document public APIs with `///` comments. Build-time Roslyn diagnostics enforce these rules.

Use WPF commands and bindings, with `DynamicResource` for theme brushes and strings. Keep controls keyboard-accessible. Use British UI spelling, except `license`.

## Testing Guidelines

Require zero build warnings. For browser build changes, verify the published runtime and shared solver execute through JavaScript, and that Vite serves and copies all runtime assets. Regenerate the ignored `web/public/wasm/` bundle after runtime changes; do not track generated runtime assets. The web server handles response compression; do not add `.gz` or `.br` sidecars. Do not commit other generated binaries, data or benchmark results. Keep scratch checks and logs outside the repository.

Run library checks for shared-library changes and targeted UI checks when desktop workflows change. Preserve failing output; avoid repeated harness runs without new evidence. No frontend lint script or coverage threshold is configured.

## Commit & Pull Request Guidelines

Keep changes focused. PRs should explain the problem, resulting behaviour and validation, link relevant issues, and include screenshots for visual changes.

Read `CLAUDE.md` for established decisions. Agents must obtain explicit authorisation for commits and other Git mutations, keep scratch files outside the repository, and avoid unsolicited design changes. Do not alter the desktop build to accommodate the browser unless necessary and authorised.
