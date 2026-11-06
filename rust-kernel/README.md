# Rust simulation kernel

Install Rust and its browser target (`rustup target add wasm32-unknown-unknown`),
then run `just build-rust-kernel` from the repository, or `node build.mjs` here.
The build uses the workspace lockfile and explicitly enables `simd128` for this
crate and its dependencies, including `wide`.
The wrapper clears inherited `RUSTFLAGS` and `CARGO_ENCODED_RUSTFLAGS` so host CPU
settings cannot override the browser target configuration.

The generated, ignored `web/src/assets/rust-kernel.wasm` is a plain Wasm module.
The frontend imports it with `?url` for Vite asset handling and loads it with
`WebAssembly.instantiateStreaming` in two workers concurrently on startup.
The console reports `Rust kernel worker check: [1, 1]` on success; failures are
reported there too, and both workers are terminated after the check.
`kernel_abi_version()` still returns 1. The module now validates prepared input,
plans working storage and initialises it. Numerical exports now advance the
simulation, sample probes and capture field/sqrt-intensity half frames.
See [INTERFACE.md](INTERFACE.md) for the exports, state layout and JS helper.

Run calls C# `Prepare` through `web/src/Wasm/PreparedSimulation.ts` and retains a
validated `PreparedSimulation` in the simulation worker. The model holds run/grid
metadata, resolved emitters, probe layouts and read-only map/index views over the
owned package. Its packet-relative offsets allow a later copy into Rust memory;
cell indices remain unchanged. `prepareKernel(exports, prepared)` from
`interface.mjs` now accepts that model directly and initialises separate mutable
state. Its `advance`, `observe`, `capture` and `reset` methods expose the ported
numerics. It is ready for worker integration; it is not yet wired into Run. Frames
continue through the C# comparison solver, whose metadata must match preparation.
See `../WaveDudeNext.Wasm/PREPARE.md` for the wire format and ownership.

Cargo configuration is local to this directory: run Cargo here so it discovers
`.cargo/config.toml`, or use the build wrapper from any directory. Shared memory,
private worker stacks and the worker protocol described in `../RUST.md` remain
future work; this kernel uses the default instance-owned memory.

`npm --prefix web run dev` and `npm --prefix web run build` rebuild the kernel
automatically. Vite emits the Wasm as a separate asset, including for snapshot
base paths. Development and preview responses set COOP `same-origin` and COEP
`require-corp`; production hosting must send the same headers (Vite's build does
not configure the production server), serve Wasm as `application/wasm`, and use
HTTPS or localhost for a secure context.

Independent worker calls do not require shared memory or cross-origin isolation.
Sharing the simulation grid later requires `crossOriginIsolated`, imported
`WebAssembly.Memory` created with `shared: true` and a maximum page count, matching
Rust/Wasm linker and atomics settings, private stacks/scratch for each instance,
safe initialisation, and an Atomics coordination protocol as described in
`../RUST.md`. Passing the same module URL to two workers does not share memory.
See the [shared-memory security requirements](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/SharedArrayBuffer#security_requirements)
and [Vite worker loading](https://vite.dev/guide/features.html#web-workers).

Check the port against the published C# Wasm solver with
`node rust-kernel/checks/check-numerics.mjs` from the repository root. The check
uses identical prepared inputs and explicit float tolerances, covering stencils,
boundaries, source timing, intensity, probes and half frames. Native row-band and
half-conversion tests run with
`cargo test --manifest-path rust-kernel/Cargo.toml --locked --target <host-triple>`.

The stencils and intensity filters use one `wide::f32x4` implementation. Short
vectors handle row tails and boundary cells; there is no separate scalar solver.
`wide`'s Wasm backend maps these operations to SIMD128, enabled by the crate's
Cargo configuration. Source clocks/transcendentals and integrated probe totals
retain the reference's double precision. Numerical equivalence allows floating-point
rounding differences; bitwise identity is not required.
