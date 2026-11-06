# Kernel integration interface

The kernel accepts the `PreparedSimulation` introduced by commit `a60d6bf` through
its original `.packet`. Its decoded JS properties remain useful to the coordinator;
Rust reads the same WDPK v1 bytes, without another serializer or JSON parser.
See [the preparation contract](../WaveDudeNext.Wasm/PREPARE.md) for immutable inputs.

The kernel validates inputs, plans/reset storage, advances complete simulation
steps, samples probes and captures half frames. `kernel_capabilities()` returns 63.
C# supplies preparation and the comparison solver; frontend stepping runs in Rust.

## JS entry point

`interface.mjs` exports `prepareKernel(exports, prepared, base?)`; its accompanying
`interface.d.mts` supplies TypeScript types without importing frontend code. Pass
the existing `PreparedSimulation` model directly, or its `Uint8Array` packet.

```ts
// After loading the kernel instance and obtaining the existing JS model:
const run = prepareKernel(instance.exports as KernelExports, prepared)
const { field0, field1, emitterState } = run.views()
// Inputs reside at run.packageAddress + prepared.offsets.<section>.
// Mutable data resides at run.stateAddress + run.layout.<buffer>.
// Capture 0 follows the first completed step; subsequent captures advance interval steps.
const samples = run.advance(1) // f32[step * activeProbeCount + probe], zero for lines
const frame = run.capture() // f16 field followed by f16 sqrt(I), owned JS bytes
const observations = run.observe() // f32 values in flattened probe-pixel order
run.reset()
```

The helper copies the package once, validates all immutable values with `kernel_plan`,
reads the Rust-produced layout, grows memory as needed and initialises state through
`kernel_execute_prepared`. It does not reinterpret the model's JS arrays as
Rust pointers. Package cell indices are unchanged by relocation. Source definitions
and maps stay immutable; emitter cycle state and integrated totals live separately.

JS owns the arena above `kernel_heap_base()`. The optional base must be aligned to
16 bytes. Reusing a base invalidates the previous run in that arena; reserve disjoint
arenas if multiple inputs must coexist, including scratch space after each state.
The helper uses `run.outputAddress` immediately after state for samples, observations
and frames. It grows memory before each call and copies results into owned JS arrays;
reserve at least the largest output used by that run before placing another arena. No Rust heap allocation, engine handle,
retained global run state, release call or worker scheduler is involved. Reset by
calling `kernel_execute_prepared` operation 0 with the same package and state addresses.
Initialisation, reset, stepping, observation and capture through this helper do not
repeat full validation. The uploaded package must remain immutable until the run
is discarded; a replacement package must go through preparation again. Memory
growth preserves the validated bytes and does not require another map scan.

Acquire `run.views()` again after any memory growth: ordinary Wasm memory views
detach. Never grow/reuse/reset an arena while compute workers are using it. Calls
must have exclusive access to their state/output regions, except for the band
export described below. Packages remain immutable throughout a run. The ordinary
build owns its memory; the shared build imports `env.memory` and exports a mutable
`__stack_pointer`. The coordinator supplies the imported memory as `exports.memory`
to `prepareKernel`. The relative layouts are identical in both builds.

## Numeric exports (kernel ABI 1)

The ordinary linker exports `memory`; the shared build imports it. All addresses, lengths, capacities, versions and status
codes below are u32. Addresses are absolute bytes in this instance's memory; inputs
and outputs must start at 16-byte-aligned addresses at or above `kernel_heap_base`.
The module never grows memory. JS must grow it before a call.

| Export | Contract |
| --- | --- |
| `kernel_abi_version()` | Returns 1, preserving the existing worker startup check. |
| `kernel_capabilities()` | Bits: 1 input/layout planning, 2 initialisation, 4 stepping, 8 observations, 16 half frames, 32 prepared execution; currently 63. |
| `kernel_heap_base()` | First aligned byte above the module's static data and stack available for the JS arena. |
| `kernel_plan(package, packageBytes, descriptor, capacity)` | Validate package and write the 64-byte KLAY descriptor. Does not allocate or initialise state. |
| `kernel_initialise(package, packageBytes, state, capacity)` | Validate package, recalculate its layout, clear working storage and seed emitter state. Also serves as reset. |
| `kernel_advance(package, packageBytes, state, stateCapacity, steps, samples, capacity)` | Complete `steps` full steps and write step-major f32 probe slots. Capacity is bytes, at least `steps * probeCount * 4`. |
| `kernel_observe(package, packageBytes, state, stateCapacity, output, capacity)` | Read f32 values in flattened probe-pixel order. Capacity is at least `pixelCount * 4` bytes. |
| `kernel_capture(package, packageBytes, state, stateCapacity, output, capacity)` | Capture the canvas as f16 field then f16 sqrt(I), both little-endian. Capacity is at least `canvasWidth * canvasHeight * 4` bytes. |
| `kernel_execute_prepared(package, packageBytes, state, stateCapacity, operation, steps, output, capacity)` | Execute against a package already validated by `kernel_plan` or `kernel_initialise`. Operation 0 initialises/resets state, 1 advances `steps`, 2 observes, 3 captures. `steps` must be zero for other operations. Initialisation/reset needs zero output bytes. |
| `kernel_phase(package, packageBytes, state, stateCapacity, phase, output, capacity)` | Coordinator only: phase 0 applies sources and starts a step; phase 1 finishes one substep after all bands complete. The final substep writes `probeCount * 4` sample bytes and completes the step. Other phases return invalid value. |
| `kernel_step_band(package, packageBytes, state, stateCapacity, rowStart, rowEnd)` | Update `[rowStart, rowEnd)` in an active substep. Each worker owns disjoint mutable bands and a private stack. Reads the current slot/substep from the stable state header. |

The standalone `kernel_initialise`, `kernel_advance`, `kernel_observe` and
`kernel_capture` exports retain full package validation on every call. Prepared
execution, phase and band calls validate structure/ranges without rescanning
immutable map, emitter and probe values. Their caller must establish full
validation first and keep the uploaded package immutable. State, output capacity,
alignment, disjointness and memory bounds remain checked on every call. No global
validation cache, retained Rust pointer or allocation is involved.

All mutating/query exports return 0 on success. Nonzero statuses:
1 invalid memory range/alignment, 2 invalid package/magic, 3 unsupported WDPK
version, 4 inconsistent package layout, 5 invalid numerical/record values,
6 insufficient capacity, 7 overlapping regions, 8 size overflow, 9 invalid mutable
state or advancement beyond the final captured step. Zero-step batches are valid.
Rejected calls leave working state and output unchanged. Extra output capacity is untouched. Sizes
are limited to signed 32-bit lengths, matching preparation and Rust slice limits.

The parser checks section ranges, grid/capture dimensions, reserved bytes, maps,
emitter definitions and probe layouts before exposing them. It does not depend on
JS having validated the packet. Input and output regions must not overlap; two
separate calls may use the same immutable package with disjoint working buffers.
The whole arena remains reserved to JS: future kernel code must preserve the
allocation-free contract instead of introducing a competing Rust allocator.

## KLAY descriptor, version 1 (64 bytes)

All fields are little-endian u32. Buffer offsets are **relative to the state base**,
distinct from the package-relative WDPK offsets. Every buffer begins at a 16-byte
boundary; section padding is zeroed during initialisation.

| Descriptor byte | Meaning |
| ---: | --- |
| 0, 4, 8 | ASCII `KLAY`, version 1, descriptor bytes (64) |
| 12, 16 | Required state bytes, required alignment (16) |
| 20, 24 | Offsets of field buffers 0 and 1, each f32[cellCount] |
| 28, 32, 36 | Offsets of first intensity stage, second stage, blended output, each f32[cellCount] |
| 40 | Offset of mutable emitter records, 16 bytes each |
| 44 | Offset of f64[probePixelCount] integration totals |
| 48, 52, 56 | Cell count, emitter count, probe pixel-index count |
| 60 | Reserved zero |

The state starts with 16 control bytes: u32 completed steps at byte 0, u32 current
field slot at byte 4 (0 selects field0, 1 selects field1), substep index at byte 8
and active flag at byte 12. The latter two words are zero between complete steps,
including throughout ordinary `kernel_advance` calls. During a coordinated step,
the active flag is 1 and the substep index advances after each barrier.
Initialisation selects field0 and zero steps. Both fields, all intensity
buffers and all probe totals start at zero.

Mutable emitter record: f64 cycle anchor at byte 0 (initially zero), i32 current
off-width at byte 8 (copied from initial off-width), u32 RNG state at byte 12
(copied from the seed). Definitions keep original order, including overlaps.
Probe totals follow the flattened WDPK pixel table: a point uses its one entry,
an integrated line uses its whole range. Entries for nonintegrated probes remain
available but unused. This avoids building a second probe-index mapping.

Output buffers are separate caller-owned regions; input, state and output must
all be disjoint, even when extra capacity is provided. Extra capacity is untouched.
The module never retains output pointers or point histories. Worker commands and
shared-memory initialisation remain the JS scheduler's responsibility.

## Numerical behaviour

`kernel_advance` injects sources into the current field in original order at the
pre-step time. Every stability substep updates all interior rows, applies Mur's
open ring when enabled, then swaps field roles. Intensity's two stages and blended
output update only on the final substep. Completed steps advance once per full step;
the current field slot is toggled once per substep. The maps are already prepared:
do not root damping or multiply speed-squared a second time.

The nine-point weights are 0.066 (corners) and 0.184 (edge neighbours), with centre
weight -1. The fourth-order weights are -1/12 (far), 4/3 (near), and -5 (centre);
outer interior rows/columns still use the nine-point stencil. The single `wide::f32x4`
implementation processes four columns at a time. Short vectors handle tails and
boundary cells, without reading or writing past them. SIMD128 remains required.

The [wide 1.7.1 API](https://docs.rs/wide/1.7.1/wide/struct.f32x4.html)
corresponds to the existing C# SIMD operations as follows:

| C# operation | Kernel equivalent |
| --- | --- |
| `new Vector<float>(array, index)` | `f32x4::from(&array[index..index + count])`, with an exact 1–4 element slice; short slices zero-fill unused lanes. |
| Vector `+`, `-`, `*` and scalar weights | The same `wide` operators; scalar operands broadcast to all four lanes. No shuffle/gather is needed. |
| `CopyTo(array, index)` | `to_array()` and a fixed-size slice copy; this does not allocate. |
| `MathF.Sqrt` / tensor sqrt | `f32::sqrt` for the Mur ring and `f32x4::sqrt` for capture. The Wasm backend uses the corresponding square-root instruction. |
| `Math.Sin`, `Exp`, `Atan` on doubles | Scalar f64 functions; `wide`'s f32 transcendental functions are not equivalent to the reference's double calculations. |
| Float-to-`Half` conversion | Integer rounding to binary16, ties to even; SIMD128 has no baseline f16 packing instruction. |

Unlike .NET's machine-dependent `Vector<float>.Count`, this port fixes four lanes.
The crate chooses its backend at compile time. `mul_add` may fuse on native FMA/
NEON targets and uses multiply plus add on baseline Wasm; such rounding differences
are allowed. No guarantee is made about NaN payload bits.

Sources use scalar f64 sine, exponential and atan, with f64 cycle anchors and the
reference's xorshift32 draws; overlapping sources are never reordered.

Points are sampled after every completed step, including steps between captures.
The `samples` output contains one slot per active probe per step, zero for line
probes. Cumulative points return the f64 running total rounded to f32; ordinary
points return live field/unrooted intensity even before their start gate. Integrated
lines accumulate one f64 total per prepared pixel. Both integrated kinds start
inclusively at `completedStep >= startAfter`. `kernel_observe` returns live or
integrated values in the WDPK pixel-table order, including point slots. It never
changes totals. The coordinator can assemble the existing probe packet from the
point batches, current profiles and prepared line lengths.

Captures exclude the rim and padding ring, root intensity in f32 and then round
to f16 with ties to even, including subnormals, signed zero and overflow. The field
keeps its sign. Probe data remains unrooted and is never rounded to f16.

`solver::step_rows` accepts shared full-grid reads and mutable slices limited to
one band. Its global row coordinates preserve fourth-order eligibility across band
boundaries. Coordinator exports require exclusive access to the full state and
must not run while bands are active. Band/phase calls check package structure but
require values already validated by `kernel_plan` or `kernel_initialise`; the host must keep that
package immutable. They do not rescan maps per call. Missing/duplicate bands are a
host scheduling error; the Rust ABI cannot establish whether the barrier was met.

`parallel.mjs` exports `createParallelKernel(module, prepared, options)` and
`runComputeWorker(setup, postMessage)`. The former returns the ordinary prepared-run
interface with asynchronous `advance(steps)`, `workerCount` and `close()`. Options
provide `workerCount`, `createWorker`, an optional shared `cancellation` Int32Array,
and an optional wait deadline `timeoutMs` (default 30000). It reserves all memory
before launching workers, rejects concurrent advances/reset/observe/capture during
stepping, and terminates the pool on error. `advance` returns owned JS samples;
capture/profile buffers are copied before the next generation. Shared memory has a
32768-page maximum, and each compute instance gets a private 64 KiB stack above
`kernel_heap_base`, before the package/state arena. See [README.md](README.md) for
the worker lifecycle and fallback selection.

Functional equivalence is the requirement; FMA and transcendental rounding
changes are acceptable within stated tolerances. The C# comparison checks use
`abs(error) <= 2e-7 + 3e-4 * abs(reference)` for f32 probes and
`abs(error) <= 1e-6 + 0.002 * abs(reference)` for decoded f16 samples, and reject
nonfinite output. Bitwise floating-point identity is not an API guarantee.

## Checks

```sh
node rust-kernel/build.mjs
node rust-kernel/checks/check-interface.mjs
node rust-kernel/checks/check-numerics.mjs
```

The check reads `/tmp/wavedude-prepare/Example01_Source_Mirror_Probe.wdprep` by default,
or a package path given as its first argument. Regenerate the fixture with
`node WaveDudeNext.Wasm/checks/check-prepare.mjs /tmp/wavedude-prepare` after publishing
C# Wasm if needed. The Rust check uses the actual JS decoder/model, uploads to a
nonzero base and verifies maps, working storage, seeds, reset, memory growth,
independent instances, invalid ranges/overlaps and malformed packages.

Native parser/layout tests can be run with
`cargo test --manifest-path rust-kernel/Cargo.toml --locked --target <host-triple>`;
specify the host because this crate defaults to wasm32. Native tests cover every
finite f16 encoding and rounding midpoint, reset/batch invariance, rejected calls,
and whole-grid versus split-band updates on tiny and odd grids with both stencils.
The numerical JS check executes both real Wasm modules using the C# preparation
packet. It covers both stencils, tails, open/reflective edges, rims/inside damping,
pulses, random gaps, detach, overlapping emitters, gated points and lines, sparse
captures and the original Example 1. It needs the published C# bundle but no saved
numerical fixtures. These checks establish correctness, not a performance claim.
