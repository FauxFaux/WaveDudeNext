# Rust/Wasm simulation kernel

Port the browser's numerical kernel to Rust, using `wide` for SIMD and JavaScript
for worker scheduling and state lifetime. Keep loading C# for project input, optical
elements, scene painting and preview. This is a small replacement of the stepping
path, not a rewrite of the application or an attempt to remove .NET.

Functional equivalence matters; bitwise floating-point equivalence does not. Preserve
the numerical scheme, source timing, boundaries, intensity and probe semantics.
Use the C# implementation as the reference, not `native/`: that proposal has a
different stencil, source injection, damping and intensity calculation.

## Reference and current boundary

Line numbers below refer to commit `6b37215fa2fc6ecde01ba452b9fc4f99e5f457f0`.
Recover an original file with `git show <commit>:<path>` if it has since changed.
The reference is GPL-3.0-or-later: retain the applicable notices and credit Alexander
Graef, Jeroen Vleggaar / Huygens Optics and 0x23 / Diffraction Limited as recorded in
the C# headers and repository license. This is a derived implementation.

| Reference | Context to preserve |
| --- | --- |
| `WaveDudeNext.Wasm/Program.cs:47` | `StartSimulation`: project preparation, CPU-engine construction, active probes, frame budget and interval. |
| `WaveDudeNext.Wasm/Program.cs:73` | `NextFrame`: advance and sample every step, then capture. Frame k follows completed step `k * interval + 1`; the run stops at the last sampled step. |
| `WaveDudeNext.Wasm/ScenePreview.cs:36` | Keep `RenderScene` and its glowing-edge output in C#. |
| `WaveDudeNext.Wasm/ScenePreview.cs:54` | Existing JSON input and normalisation stay on the C# side; image scenes remain unsupported. |
| `WaveDudeNext/Engine/SceneData.cs:91` | Painted BGRA to speed-squared and damping maps; canvas runs pass `useSources: false`. |
| `WaveDudeNext/Engine/SceneData.cs:128` | Rim growth, edge extension and absorbing band. `Empty` at line 177 handles an unpainted canvas. |
| `WaveDudeNext/Engine/Emitter.cs:103` | Expanded emitter records, units and seeds; `FromProject` at line 139 honours object-source enablement. Composite expansion remains in C#. |
| `WaveDudeNext/Engine/WaveSimulator.cs:224` | Padded maps, stability substeps, damping root and speed multiplier. |
| `WaveDudeNext/Engine/WaveSimulator.cs:496` | Substep calculation; lowest source level at line 527 and intensity retention at line 555. |
| `WaveDudeNext/Engine/WaveSimulator.cs:567` | Soft source injection and detach fade; pulse clock/random gaps at line 646, envelopes at line 718. |
| `WaveDudeNext/Engine/WaveSimulator.cs:784` | Row-band update; scalar stencils at lines 857/867, leapfrog and intensity at line 882, vector versions at lines 897–935. |
| `WaveDudeNext/Engine/WaveSimulator.cs:952` | Mur open boundaries, applied after every substep; constants start at line 975. |
| `WaveDudeNext/Probes/ProbeSampler.cs:77` | Prepared pixel indices, measurement flags, lengths and start gates. Keep layout preparation in C#. |
| `WaveDudeNext/Engine/WaveSimulator.cs:402` | Per-step point sampling and integrated line totals; live profiles at line 457. |
| `WaveDudeNext/Probes/ProbeCollector.cs:57` | Cumulative point totals and start-after gating. Reproduce the calculation without its trace/collection classes. |
| `WaveDudeNext/Engine/FrameShape.cs:120` | Full-size half conversion; intensity is square-rooted for frame storage, but probes read unrooted full-precision intensity. |
| `WaveDudeNext.Wasm/ProbeFrames.cs:46` | Existing probe packet format and per-frame point deltas; histories are retained once in JS. |

The CPU solver is about 660 nonblank, noncomment lines; its engine/frame helpers and
probe files bring the relevant whole-file count to about 1,450. Much of the model,
snapshot abstraction and collection machinery need not follow it into Rust.

## Preparation, memory and ownership

Add a preparation export beside the current C# simulation exports. Reuse project
normalisation, `PartPainter`, `SceneData`, `Emitter.FromProject` and
`ProbeSampler.Build`. Return typed arrays plus small numerical metadata to JS;
Rust receives offsets and parameters, with no JSON parser or project model.
Expose prepared maps and derived constants without constructing a full C# engine
just to retrieve them. Keep the existing C# simulation exports as a comparison path.

Use one documented memory layout, allocated before stepping:

- Canvas dimensions, rim, grid dimensions, stride, stencil selection, substeps,
  damping correction, intensity retention/scale and frame interval.
- Two field buffers, speed-squared and damping maps, and two intensity-filter
  stages. Initially retain the blended intensity buffer for a direct translation.
- Expanded emitter definitions and mutable pulse-cycle anchors, off-widths and
  RNG state. Preserve emitter order when multiple sources occupy one cell.
- Probe index lists/flags, point-sample batches, double-precision integration
  totals and output space for frames/profiles.

For canvas width Cw, height Ch and rim R, the simulated interior is
`W = Cw + 2*R`, `H = Ch + 2*R`; its stride is `W + 2`, allocation is
`(W + 2)*(H + 2)` floats per plane, and canvas pixel `(x,y)` lies at
`(y + R + 1)*stride + x + R + 1`. The extra one-cell ring is distinct from
the damping rim. Preserve reflective/open ring behaviour and the fourth-order
stencil's fallback to the original nine-point stencil on the outer interior rows
and columns.

The update is `next = current + (current - previous)*d + laplacian*c2*mult`.
Here `d` is velocity retention (1 is lossless), not an absorption coefficient.
Sources blend into the current field before stepping. Intensity uses the newly
computed field, two cascaded filters and a 50/50 blend of their outputs.

JS owns allocation, buffer offsets, time, reset, cancellation and worker lifetime.
Rust functions update caller-owned memory; source/probe helpers may also be Rust
functions called only by the coordinator. There is no need for a Rust scheduler,
shared allocator, Rayon, pthreads, serde or the desktop `IWaveEngine` interface.
Use allocation-free exports with an explicit numeric ABI and `wide::f32x4`;
`~/code/webpop/libwebpop/src/dsp/enc_wide.rs` is a local example of keeping SIMD
private to numerical functions. Enable Wasm `simd128` explicitly.

## JS worker coordination

Keep a coordinator worker off the UI thread. Persistent compute workers each
instantiate the kernel against the same imported shared `WebAssembly.Memory` and
own a fixed band of rows. The current field and maps are read-only during a
substep; each worker writes only its assigned destination/intensity rows. Neighbour
reads across bands use the shared current field, so no halo copying is necessary.

```text
for each complete simulation step:
    coordinator applies sources to current field, in original order
    for each substep:
        publish buffer offsets/parameters and a new work generation
        workers update their row bands; update intensity only on final substep
        wait for every worker's completion
        coordinator applies open boundaries, then swaps field offsets
    increment completed-step count; sample probes, including between captures
    capture when due; finish copying output before stepping resumes
```

Use JS `Atomics` on integer command/completion words to publish work and wait for
completion. Floating-point cells need no atomic operations. Use generation checks
around waits, and define cancellation/error handling so a failed worker cannot
leave the coordinator waiting forever. Use `postMessage` for setup, batch commands,
progress and completed frames, not a message round trip per substep. Barriers remain
necessary at every substep, even when the UI requests many steps at once.

Shared memory still requires COOP/COEP headers in development and deployment.
Avoiding native thread libraries does not remove the Wasm shared-memory linker
configuration: prove it with a tiny two-worker kernel early. Give each instance
private stack/scratch space and ensure initialisation cannot overwrite live shared
data. Avoid concurrent allocation and memory growth during a run. Rust mutable
slices must cover only each worker's owned regions; never form overlapping mutable
references over the whole shared grid. Keep a one-worker SIMD path for comparison
and environments without shared memory.

Relevant API context: [shared Wasm memory](https://developer.mozilla.org/en-US/docs/WebAssembly/Reference/JavaScript_interface/Memory#creating_a_shared_memory),
[worker waits](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Atomics/wait),
[wide SIMD build settings](https://docs.rs/crate/wide/latest).

## Short implementation sequence

1. **Extract inputs and prove the boundary.** Add C# preparation and a small Rust
   module/build recipe. Exercise one worker, then two workers sharing memory and
   separate scratch space before building the full worker protocol.
2. **Translate and integrate.** Implement scalar stepping, source dynamics,
   intensity, probes and full-resolution frame conversion; then add `wide` lanes
   with scalar tails. Adapt `web/src/Wasm/Simulation.worker.ts` and its loader;
   preserve `FrameStore.ts`, `ProbeFrames.ts`, rendering and `useSimulation.ts`
   behaviour. Keep `Scene.worker.ts` on C#. Add the JS band scheduler using the
   same kernel exports, without UI changes.
3. **Compare and measure.** Feed identical prepared inputs into C# and Rust. Check
   both stencils, odd/tiny dimensions and band boundaries, open/reflective edges,
   rims, pulses/random gaps/detach, overlapping emitters, both intensity stages,
   gated point/line integration and sparse captures. Compare floats with stated
   tolerances; require finite/stable output and matching timing/counts. Compare
   one-worker and multiworker results and test stop/restart and worker failure.

Use `web/scripts/check-wasm.mjs` for existing browser-boundary expectations and
`_tools/library_check/Program.cs` for numerical cases; port relevant assertions,
not the Windows harness. Measure stepping separately from preparation, capture,
OPFS writes and runtime startup, on a small grid and a representative larger grid.
Verify Vite serves/copies both bundles and the deployed headers enable sharing.
Keep generated Wasm and benchmark output untracked, scratch material outside the
repository, and the desktop build independent. Completion means the existing web
simulation runs through Rust with C# preparation/preview still available.
