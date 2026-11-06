# Rust simulation kernel

Install Rust and its browser target (`rustup target add wasm32-unknown-unknown`),
plus nightly with Rust sources (`rustup toolchain install nightly --component rust-src`).
Then run `just build-rust-kernel` from the repository, or `node build.mjs` here.
The build uses the workspace lockfile and explicitly enables `simd128` for this
crate and its dependencies, including `wide`.
The wrapper clears inherited `RUSTFLAGS` and `CARGO_ENCODED_RUSTFLAGS` so host CPU
settings cannot override the browser target configuration.

The build generates two ignored assets: `web/src/assets/rust-kernel.wasm` with
instance-owned memory and `rust-kernel-shared.wasm` with imported shared memory.
The latter rebuilds `std` with atomics on nightly; each variant has its own build
cache. The frontend imports their URLs through Vite and loads the appropriate
module when Run starts. Kernel loading and execution errors use the existing
simulation error reporting.
`kernel_abi_version()` still returns 1. The module now validates prepared input,
plans working storage and initialises it. Numerical exports now advance the
simulation, sample probes and capture field/sqrt-intensity half frames.
See [INTERFACE.md](INTERFACE.md) for the exports, state layout and JS helper.

Run calls C# `Prepare` through `web/src/Wasm/PreparedSimulation.ts` and retains a
validated `PreparedSimulation` in the simulation worker. The model holds run/grid
metadata, resolved emitters, probe layouts and read-only map/index views over the
owned package. Its packet-relative offsets allow a copy into Rust memory;
cell indices remain unchanged. `prepareKernel(exports, prepared)` from
`interface.mjs` now accepts that model directly and initialises separate mutable
state. Its `advance`, `observe`, `capture` and `reset` methods expose the ported
numerics. Full immutable-package validation runs once, during `kernel_plan`.
Initialisation, reset and all numerical operations then use `kernel_execute_prepared`,
which keeps memory/layout/state checks without rescanning the maps or probe tables.
Standalone numerical exports retain full validation for independent callers.
`web/src/Wasm/RustSimulation.ts` drives these methods for Run and packs
point samples and line profiles into the existing OPFS recording format. C# still
normalises projects and prepares maps, sources and probes; the frontend does not
call its solver exports. Those exports remain available for reference checks.
See `../WaveDudeNext.Wasm/PREPARE.md` for the wire format and ownership.

Cargo configuration is local to this directory: run Cargo here so it discovers
`.cargo/config.toml`, or use the build wrapper from any directory.

On cross-origin-isolated pages, Run starts up to four persistent compute workers,
leaving one reported hardware thread for the UI/coordinator. Each owns every Nth
16-row stripe of the simulated grid, including the damping rim, where N is the
worker count. Tiny grids use shorter stripes so each worker owns some rows.
Their debugger names are `Simulation worker 1` through `Simulation worker 4`; the
coordinator is named separately so profiler tracks can be matched to the counters.
`parallel.mjs` coordinates them with a command generation and one completion/status
slot per worker, spaced 64 bytes apart. There is no row queue or per-row tracking.
The static stripe profile (worker index, worker count, stripe height) is set up
once. `kernel_step_stripes` processes the complete assignment in one Wasm call per
worker/substep, checking the package/layout once and borrowing each mutable stripe
separately. Neighbour reads use the shared current field across stripe boundaries;
there is no halo copying or per-stripe notification. Workers read the same current
field/maps and write disjoint destination/filter stripes. After every substep the
coordinator applies open boundaries and swaps fields;
it samples probes after every complete step. Sources retain their original order.

The coordinator owns the module's original stack; each compute instance gets a
separate 64 KiB stack via its exported `__stack_pointer`. The shared linker initialiser
initialises static data once. All memory growth, package validation and state reset
finish before workers start. Map values are not rescanned on each band/phase call.
Generation checks prevent missed wakeups. Startup and substep waits have 30-second
deadlines; failures terminate the pool. Closing the run terminates workers, and the
UI's shared cancellation flag lets idle workers exit within a 100 ms wait even if
their coordinator was terminated. Cancellation during a band finishes that band
before the worker exits. The pool is recreated for each run.

The status bar shows average completed steps/s and summed solver-call time divided
by elapsed run time. Timing starts after preparation/worker startup and includes
frame capture and disc writes in elapsed time. Open the status figures to read/copy
coordinator work, capture/observation/copy/packing totals, write-queue back-pressure,
and each compute worker's cumulative work and estimated idle percentage. Worker
timings exclude waits; elapsed call time can include browser/OS descheduling, so
the multiplier estimates parallelism rather than measured serial speedup. Timings
update per stored frame, remain visible after stopping/completion, and reset for
each run. Each worker publishes cumulative work and the last band's start/end f64
timestamps beside its completion word, before the atomic completion store. Worker
timestamps are translated to the coordinator's `performance.timeOrigin`. After
each barrier the coordinator updates counts, mean/maximum call and launch times,
last-finisher counts, early-finish waits and waits between dispatches. Call timings
cover the worker's complete stripe batch per substep, not individual stripes. It also records
dispatch-to-resume barrier time and delay after the last kernel return. Counters
use bounded storage; there is no per-band message or retained event history.

Launch delay includes worker wake-up/scheduling; kernel elapsed time includes any
descheduling during the call. Early-finish wait is contained in the gap before the
next dispatch, which also includes coordinator/capture/storage time. Storage write
durations can overlap computation and one another; only queue back-pressure measures
time the producer pauses for capacity. These totals must not be added as disjoint
parts of run time. The details panel closes with Escape and keeps text selectable.

`npm --prefix web run dev` and `npm --prefix web run build` rebuild the kernel
automatically. Vite emits the Wasm as a separate asset, including for snapshot
base paths. Development and preview responses set COOP `same-origin` and COEP
`require-corp`; production hosting must send the same headers (Vite's build does
not configure the production server), serve Wasm as `application/wasm`, and use
HTTPS or localhost for a secure context.

Without cross-origin isolation, or when only one compute thread is selected, Run
uses the ordinary single-worker SIMD module. Shared memory is limited to 2 GiB;
the ordinary module retains its existing wasm32 memory behaviour. Passing the same
module URL to independent workers does not share memory.
See the [shared-memory security requirements](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/SharedArrayBuffer#security_requirements)
and [Vite worker loading](https://vite.dev/guide/features.html#web-workers).

Check the port against the published C# Wasm solver with
`node rust-kernel/checks/check-numerics.mjs` from the repository root. The check
uses identical prepared inputs and explicit float tolerances, covering stencils,
boundaries, source timing, intensity, probes and half frames. Native row-band and
half-conversion tests run with
`cargo test --manifest-path rust-kernel/Cargo.toml --locked --target <host-triple>`.
Run `just check-rust-simulation` to build both Wasm modules and verify the frontend
adapter's frames, probe packets, storage budgets and seeking against C#.
After building, `npm --prefix web run check-rust-simulation` runs that check alone.
Run `just check-rust-parallel` to compare one, two and four shared-memory workers
against the ordinary solver, including both stencils, substeps, rims, open/reflective
edges, filters, probes, reset, cancellation and worker failure/timeout cleanup.
The check compares contiguous assignments and 1/4/16/32-row stripes on seeded odd
grids. `createParallelKernel` accepts `rowsPerStripe` for comparisons: default 16,
zero for contiguous bands. Work counters include each worker's owned row/stripe
counts. The older single-band export remains available for direct range callers.

The stencils and intensity filters use one `wide::f32x4` implementation. Short
vectors handle row tails and boundary cells; there is no separate scalar solver.
`wide`'s Wasm backend maps these operations to SIMD128, enabled by the crate's
Cargo configuration. Source clocks/transcendentals and integrated probe totals
retain the reference's double precision. Numerical equivalence allows floating-point
rounding differences; bitwise identity is not required.
