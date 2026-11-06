# Kernel integration interface

The kernel accepts the `PreparedSimulation` introduced by commit `a60d6bf` through
its original `.packet`. Its decoded JS properties remain useful to the coordinator;
Rust reads the same WDPK v1 bytes, without another serializer or JSON parser.
See [the preparation contract](../WaveDudeNext.Wasm/PREPARE.md) for immutable inputs.

This interface validates inputs, plans storage and initialises/reset working data.
There are no numerical steps, frames or probe samples yet. C# remains the running
comparison solver. `kernel_capabilities()` returns 3; stepping capability bit 4 is
unset, and no placeholder stepping export exists.

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
// This call has initialised storage only; it has not produced a simulation frame.
```

The helper copies the package once, reads the Rust-produced layout, grows memory
as needed and initialises state. It does not reinterpret the model's JS arrays as
Rust pointers. Package cell indices are unchanged by relocation. Source definitions
and maps stay immutable; emitter cycle state and integrated totals live separately.

JS owns the arena above `kernel_heap_base()`. The optional base must be aligned to
16 bytes. Reusing a base invalidates the previous run in that arena; reserve disjoint
arenas if multiple inputs must coexist. No Rust heap allocation, engine handle,
retained global run state, release call or worker scheduler is involved. Reset by
calling `kernel_initialise` again with the same package and state addresses.

Acquire `run.views()` again after any memory growth: ordinary Wasm memory views
detach. Never grow/reuse/reset an arena while compute workers are using it. Calls
must have exclusive access to their package/output regions. This build still uses
instance-owned memory; imported shared memory and private worker stacks remain a
separate prerequisite for parallel execution. The relative layouts work for either.

## Numeric exports (kernel ABI 1)

The linker exports `memory`. All addresses, lengths, capacities, versions and status
codes below are u32. Addresses are absolute bytes in this instance's memory; inputs
and outputs must start at 16-byte-aligned addresses at or above `kernel_heap_base`.
The module never grows memory. JS must grow it before a call.

| Export | Contract |
| --- | --- |
| `kernel_abi_version()` | Returns 1, preserving the existing worker startup check. |
| `kernel_capabilities()` | Bits: 1 input/layout planning, 2 state initialisation, 4 stepping; currently 3. |
| `kernel_heap_base()` | First aligned byte above the module's static data and stack available for the JS arena. |
| `kernel_plan(package, packageBytes, descriptor, capacity)` | Validate package and write the 64-byte KLAY descriptor. Does not allocate or initialise state. |
| `kernel_initialise(package, packageBytes, state, capacity)` | Validate package, recalculate its layout, clear working storage and seed emitter state. Also serves as reset. |

`kernel_plan` and `kernel_initialise` return 0 on success. Nonzero statuses:
1 invalid memory range/alignment, 2 invalid package/magic, 3 unsupported WDPK
version, 4 inconsistent package layout, 5 invalid numerical/record values,
6 insufficient output capacity, 7 overlapping input/output regions, 8 size overflow.
On error they leave output unchanged. Extra output capacity is untouched. Sizes
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
field slot at byte 4 (0 selects field0, 1 selects field1), then eight reserved zero
bytes. Initialisation selects field0 and zero steps. Both fields, all intensity
buffers and all probe totals start at zero.

Mutable emitter record: f64 cycle anchor at byte 0 (initially zero), i32 current
off-width at byte 8 (copied from initial off-width), u32 RNG state at byte 12
(copied from the seed). Definitions keep original order, including overlaps.
Probe totals follow the flattened WDPK pixel table: a point uses its one entry,
an integrated line uses its whole range. Entries for nonintegrated probes remain
available but unused. This avoids building a second probe-index mapping.

Capture buffers, point-history batches and worker commands are not allocated by
this version. They belong to the numerical/capture port and JS scheduler. Consumers
must negotiate later capabilities/layout versions before relying on those outputs.

## Checks

```sh
node rust-kernel/build.mjs
node rust-kernel/checks/check-interface.mjs
```

The check reads `/tmp/wavedude-prepare/Example01_Source_Mirror_Probe.wdprep` by default,
or a package path given as its first argument. Regenerate the fixture with
`node WaveDudeNext.Wasm/checks/check-prepare.mjs /tmp/wavedude-prepare` after publishing
C# Wasm if needed. The Rust check uses the actual JS decoder/model, uploads to a
nonzero base and verifies maps, working storage, seeds, reset, memory growth,
independent instances, invalid ranges/overlaps and malformed packages.

Native parser/layout tests can be run with
`cargo test --manifest-path rust-kernel/Cargo.toml --locked --target <host-triple>`;
specify the host because this crate defaults to wasm32. No solver benchmark or
numerical equivalence claim is implied by these checks.
