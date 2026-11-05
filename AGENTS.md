# Repository Guidelines

## Project Structure & Module Organization

- `WaveDudeNext/`: shared .NET 10 library; model, CPU/OpenCL engines, optical parts, rendering, probes, frame storage and exports.
- `WaveDudeNext.WPF/`: Windows desktop application, including controls, docking, localisation and document generation.
- `web/`: npm-managed Preact/TypeScript/Vite frontend, currently a starter. `src/` contains components; `public/` contains static assets.
- `_tools/library_check/`: console verification harness, outside the solution. `_tools/ui_check/`: Windows PowerShell UI checks and fixtures.
- `Examples/`, `Assets/`, `MANUAL.md`: example projects, documentation images and user documentation.
- `PLAN.md`: planned Linux server and JSON API architecture; the server is not implemented yet.

## Build, Test, and Development Commands

Use the .NET 10 SDK selected by `global.json`, Node.js 24/npm and Just. [Justfile](Justfile) is the entry point for portable builds; run `just` to list commands. From a fresh checkout, run `just restore`, then `just build`. Recipes run from the repository root, including when invoked from a subdirectory.

- `just restore`: restore the portable library and install locked frontend dependencies with `npm ci`.
- `just build`: build the portable library in Release with zero warnings, then check TypeScript and build the production frontend.
- `just build-library` / `just build-web`: build only the changed component.
- `just restore-library` / `just restore-web`: restore only that component's dependencies.
- `just dev-web` / `just preview-web`: run Vite for development or inspect the built frontend.

The server is not implemented yet. Linux library/API test recipes will be added when the harness is portable and the server exists; `just build` currently verifies compilation and frontend bundling, not runtime behaviour. See `PLAN.md` for the remaining test work. GPU verification is optional and separate from the CPU build.

The WPF application and current verification harnesses require Windows:

- `dotnet build WaveDudeNext.sln`: build the desktop solution; require zero warnings.
- `dotnet run --project WaveDudeNext.WPF`: launch the desktop application.
- `dotnet run --project _tools/library_check -c Release`: verify library behaviour; append `-- export` for export checks.
- `powershell -ExecutionPolicy Bypass -File _tools/ui_check/KeyboardCheck.ps1`: run a targeted UI check; choose the script matching the change.

## Coding Style & Naming Conventions

Follow `.editorconfig`: C# uses tabs and Allman braces; project XML uses two spaces. Preserve the frontend's two-space indentation and existing TSX style.

Use PascalCase for C# types/public members and Preact components. Prefer concise expression-bodied accessors without `this.`. C# source files need the prescribed license header; document public APIs with `///` comments. Build-time Roslyn diagnostics enforce these rules.

Use WPF commands and bindings, with `DynamicResource` for theme brushes and strings. Keep controls keyboard-accessible. Use British UI spelling, except `license`.

## Testing Guidelines

Verification uses custom console and PowerShell harnesses. Name UI scripts `<Feature>Check.ps1`. Run library checks for library changes and targeted UI checks when their workflows change. Preserve failing output; avoid repeated harness runs without new evidence. No frontend test/lint script or coverage threshold is configured.

## Commit & Pull Request Guidelines

History favours descriptive subjects explaining behaviour; release commits use `Version <number>: ...`, while some maintenance commits use `chore:`. Keep changes focused. PRs should explain the problem, resulting behaviour and validation, link relevant issues, and include screenshots for visual changes.

Read `CLAUDE.md` for established decisions. Agents must obtain explicit authorisation for commits and other Git mutations, keep scratch files outside the repository, and avoid unsolicited design changes.
