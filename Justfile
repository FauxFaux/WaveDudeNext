# Use the native command interpreter on Windows; recipes need no Unix shell there.
set windows-shell := ["cmd.exe", "/d", "/s", "/c"]

# List the available commands.
[private]
default:
    @just --list

# Restore the browser project and install locked frontend dependencies.
restore: restore-wasm restore-web

# Restore the Wasm project and its shared-library NuGet dependencies without WPF.
restore-wasm:
    docker build --file _tools/build/Dockerfile --target restore --output type=cacheonly .

# Restore only the existing shared library.
restore-library:
    dotnet restore WaveDudeNext/WaveDudeNext.csproj -warnaserror

# Install frontend dependencies from the lockfile.
restore-web:
    npm --prefix web ci

# Publish AOT Wasm assets, then check TypeScript and build the static website.
build: build-web

# Publish the C# preparation, scene and reference solver exports for Vite to serve and copy.
build-wasm:
    node _tools/scripts/just-wasm-build.mjs

# Build the SIMD Rust solver into the frontend assets directory.
build-rust-kernel:
    node rust-kernel/build.mjs

# Build the existing shared library without the Wasm toolchain or WPF.
build-library:
    dotnet build WaveDudeNext/WaveDudeNext.csproj -c Release -warnaserror

# Build Wasm before Vite copies public/ into dist/.
build-web: build-wasm
    npm --prefix web run build

# Build and upload a Git-named snapshot, then point the site root at it.
deploy-snapshot: build-wasm
    node _tools/scripts/just-deploy-snapshot.mjs

# Build Wasm and start Vite; rerun build-wasm after changing C#.
dev-web: build-wasm
    npm --prefix web run dev

# Serve the previously built static site.
preview-web:
    npm --prefix web run preview

# Check the browser probe graph calculations.
check-probe-graph:
    npm --prefix web run check-probe-graph

# Verify the published AOT runtime executes the shared solver through JavaScript.
check-wasm: build-wasm
    npm --prefix web run check-wasm

# Verify the frontend Rust solver and recordings against the preserved C# reference.
check-rust-simulation: build-wasm build-rust-kernel
    npm --prefix web run check-rust-simulation

# Verify shared-memory fixed bands and compute worker lifecycle against the serial solver.
check-rust-parallel: build-wasm build-rust-kernel
    npm --prefix web run check-rust-parallel
