# Building the browser runtime

`WaveDudeNext.Wasm` is an additional .NET 10 browser project referencing `WaveDudeNext.csproj`
and its ordinary NuGet dependencies. The existing library, WPF project and desktop solution
retain their own build commands. Neither ASP.NET Core nor Blazor is used for the browser target.

## Prerequisites

Use Docker with BuildKit, Node.js 24/npm and Just. Browser builds do not need .NET or a Wasm
workload installed on the host. Native library and desktop builds still use the SDK selected
by `global.json`.

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
`just build-web` publishes Wasm first, then runs TypeScript and Vite; the deployable site is
`web/dist/`. Vite serves `public/` directly in development and copies it unchanged in production.
Keep the complete `wasm/` directory together: .NET's loader expects its framework modules,
manifest, assemblies and Wasm files at their relative URLs.

`just dev-web` publishes the runtime before starting Vite. After C# changes, run `just build-wasm`
again and reload the page. Frontend changes use Vite's normal development loop. `just preview-web`
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
const peak = runtime.SmokeTest()
```

The loader uses Vite's `BASE_URL`, including a deployment prefix. It dynamically imports the
published `wasm/main.mjs` without bundling or rewriting .NET's internal modules. The JavaScript
host creates one runtime per JS context and obtains the `[JSExport]` methods from the assembly.
`SmokeTest()` executes 16 steps of the real CPU solver and returns a finite, positive peak field
amplitude. It is a build/interop check, not the planned frontend simulation API.

Run `just check-wasm` to publish and exercise this export under Node. After an existing build,
`npm --prefix web run check-wasm` checks the generated bundle without publishing again.
`just check-probe-graph` runs the retained graph calculation checks.

The UI edits one Project in root Preact state and opens/saves JSON text without a server.
Simulation is not connected to these exports yet; its controls and examples remain disabled.
See [PLAN.md](PLAN.md) for the worker and frontend migration steps.

References: [Microsoft's browser interop guidance](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0)
and [Vite public assets](https://vite.dev/guide/assets.html#the-public-directory).
