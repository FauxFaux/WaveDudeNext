# Prepared simulation package, version 1

`Program.Prepare(projectJson, maxFrames)` returns a `Uint8Array` through the existing
.NET JavaScript exports. It normalises the canvas project, paints its optical
elements, expands and filters its emitters, and prepares active probes. It does not
construct an engine, start/end a run or modify a running simulation. Each result
is independent; there is no retained preparation handle or release call.

The input JSON and unsupported-image-scene behaviour match `StartSimulation`.
`maxFrames` must be positive. Frame-budget arithmetic also matches that export:
`interval = max(visualizeEvery, ceil(requestedSteps / maxFrames))`,
`frameCount = ceil(requestedSteps / interval)`. Capture k is after completed step
`k * interval + 1`; the final captured step can precede the requested step count.

The package is immutable **input**, independent of the Rust kernel ABI. It contains
no field history, intensity state, stacks or allocator metadata. JS allocates those
separately and can either copy each input section into a kernel's chosen layout or
copy the complete package to an aligned allocation in Rust linear memory. The C#
and Rust Wasm modules have separate memories; a JS copy is required once per run.

## Binary layout

All integers and IEEE floats are little-endian. Offsets and counts are unsigned
32-bit unless marked `i32`; current packages fit signed 32-bit lengths. Offsets are
**bytes from the beginning of this package**, never pointers into the .NET heap.
The header, each section and total length are aligned to 16 bytes. Reserved bytes
and alignment gaps are zero. Empty sections still have a valid aligned offset.
Reject unsupported versions, invalid lengths/counts/offsets and unknown flag bits
before constructing views or accepting input in Rust.

| Header byte | Type | Meaning |
| ---: | --- | --- |
| 0 | 4 bytes | ASCII `WDPK` |
| 4 | u32 | Format version: 1 |
| 8 | u32 | Total package bytes |
| 12 | u32 | Header bytes: 160 |
| 16, 20 | u32 | Canvas width, height |
| 24, 28 | u32 | Simulated grid width, height, including rim but excluding outer ring |
| 32, 36, 40 | u32 | Rim, padded stride, padded cell count |
| 44 | u32 | Flags: bit 0 open edges, bit 1 fourth-order stencil, bit 2 track intensity (always set in v1) |
| 48 | u32 | Substeps per simulation step |
| 52, 56, 60, 64 | u32 | Requested steps, effective capture interval, frame count, final completed step |
| 68, 72, 76 | u32 | Emitter count, active probe count, total probe pixel-index count |
| 80, 84 | u32 | Offsets of padded speed-squared and damping f32 maps |
| 88, 92, 96 | u32 | Offsets of emitter records, probe records, probe pixel-index u32 table |
| 100 | u32 | Reserved |
| 104 | f32 | Speed-squared multiplier: `1 / substeps²` (maps are not multiplied by it) |
| 108, 112 | f32 | Intensity retention alpha, intensity scale |
| 116, 120, 124 | f32 | Source opacity (0.9), intensity blend (0.5), open-edge speed factor (0.56213877) |
| 128, 136 | f64 | Source frequency scale and intensity time-decay setting, for provenance; kernel receives resolved omega/alpha |
| 144, 148, 152 | u32 | Emitter record bytes (64), probe record bytes (32), detach fade length (30 steps) |
| 156 | u32 | Reserved |

Both maps contain `cellCount = stride * (gridHeight + 2)` f32 values. Their outer
one-cell ring is zero. `stride = gridWidth + 2`; canvas pixel `(x,y)` maps to
`(y + rim + 1) * stride + x + rim + 1`. The damping map is already corrected to
`pow(originalDamping, 1 / substeps)`; do not apply the root again. Damping is
velocity retention: 1 means no damping. C# has already applied the inside border
or extended the scene into its outside rim and applied the rim absorption.

### Emitter record (64 bytes)

| Record byte | Type | Meaning |
| ---: | --- | --- |
| 0 | u32 | Padded cell index |
| 4 | u32 | Mode: 0 continuous, 1 pulsed |
| 8 | u32 | Pulse shape: 0 Gaussian, 1 smoothed square |
| 12 | u32 | Nonzero initial xorshift32 state |
| 16, 20, 24, 28 | i32 | On-width, initial off-width, off-width base, random off-width amount, all in cycles |
| 32 | i32 | Detach-after step; -1 means never |
| 36 | u32 | Reserved |
| 40, 48, 56 | f64 | Angular frequency in radians/step, amplitude (0..1), phase in radians |

Emitters are in original source/element order; disabled and outside-canvas emitters
are omitted. Order matters for overlapping sources. Omega has the same float
rounding then double promotion as `WaveSimulator.LevelFrequency`; do not convert
it from source levels again. Intensity alpha uses the lowest positive source level
**after** filtering, including when source frequency scale is zero. Initialise
each mutable cycle anchor to zero, off-width to the initial value and RNG to its
seed. Preserve the original pulse startup, redrawing and detach behaviour from
`WaveSimulator.cs:567–765`; only the definitions are packaged. Image-pixel sources
are absent because the current browser boundary only supports canvas scenes.

### Probe record (32 bytes)

| Record byte | Type | Meaning |
| ---: | --- | --- |
| 0, 4 | u32 | First element and element count in the probe pixel-index table (not byte offsets) |
| 8 | u32 | Flags: bit 0 line profile, bit 1 use intensity, bit 2 cumulative point, bit 3 integrated line, bit 4 use start-after |
| 12 | i32 | Start-after step |
| 16 | f64 | Line length in canvas pixels (0 for points) |
| 24 | u32 | Index in the original project's complete probe array, including disabled probes |
| 28 | u32 | Reserved |

Only active probes are present, in project order. Table elements are padded cell
indices; points have one index and lines include both endpoints in A-to-B order.
Measurement determines field/intensity selection, as in `ProbeSampler.Build`.
Use double accumulation for integrated measurements, sample points at every
completed step, and return unrooted intensity before half conversion. Start gating
applies to integrated totals; ordinary point values are still recorded before the
gate. The original project retains names/IDs and display settings on the JS side.

## JS to Rust

The frontend runtime interface exposes `Prepare`. `prepareRustSimulation` in
`web/src/Wasm/PreparedSimulation.ts` submits the project and frame budget, validates
the result and returns a typed `PreparedSimulation`: numerical metadata, ordered
emitter definitions, active probe layouts and typed map/index views into the packet.
Treat those views as read-only. Aligned packets are retained without copying;
unaligned input views are copied once before creating typed views.

Run keeps this model in `Simulation.worker.ts`, using its dimensions and capture
timing. The worker still starts the C# comparison solver and checks its metadata
against the model before recording frames. This currently prepares the scene twice;
replacing that comparison path with Rust stepping remains separate work. No Rust
mutable buffers, memory allocation ABI or scheduler are imposed by the model.

For direct access to the binary sections:

```js
const runtime = await loadWaveDude()
const packet = runtime.Prepare(JSON.stringify(project), maxFrames)
const header = new DataView(packet.buffer, packet.byteOffset, packet.byteLength)
// Validate magic/version, layout and ranges first (see checks/check-prepare.mjs).
const cells = header.getUint32(40, true)
const c2Offset = header.getUint32(80, true)
const c2 = new Float32Array(packet.buffer, packet.byteOffset + c2Offset, cells)

// base is a 16-byte-aligned allocation reserved by the JS/kernel memory layout.
// Acquire the current memory.buffer after any growth. No allocator ABI is imposed.
new Uint8Array(rustMemory.buffer, base, packet.byteLength).set(packet)
const rustC2Address = base + c2Offset
// Emitter/probe cell indices remain indices: do not add base to their values.
```

Do not infer the Rust state layout from this package or write mutable buffers over
its maps. With shared Rust memory, compute workers may read these sections directly;
their private stacks and mutable output bands belong elsewhere. No C# calls are
needed during stepping. Scalar/one-worker and shared-memory consumers use the same
preparation format.

## Verification and example handoff

After `just build-wasm`, run from the repository root:

```sh
node WaveDudeNext.Wasm/checks/check-prepare.mjs /tmp/wavedude-prepare
```

This runs checks through the published JavaScript export and exports the unmodified
`web/src/assets/examples/Example01_Source_Mirror_Probe.json` with `maxFrames=2500`.
The output directory contains its `.wdprep` package, source JSON, manifest and C#
reference frame/probe packets for captures 0, 1 and 4 (completed steps 1, 3 and 9).
The manifest records hashes, metadata and output formats. Capture 0 is initially
zero because the source phase is zero; captures 1 and 4 exercise propagation.
Frame bytes contain the tightly packed canvas field f16 plane followed by the
sqrt-intensity f16 plane. Probe packets begin with i32 probe count and completed
steps, then for each probe: i32 sample count, i32 profile count (-1 for points),
f64 line length, f32 samples, and f32 profile values. Each packet is the original
capture batch, not an aggregation over the selected exported captures.
Keep these generated files outside the checkout. The check script also validates
empty/filtered inputs, frame budgets, map preparation, composite source expansion,
probe layouts and independence from a running C# simulation.
