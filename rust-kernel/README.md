# Rust kernel scaffold

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
reported there too, and both workers are terminated after the check. Its only
function export is
`kernel_abi_version()`, which returns 1; no simulation code has been ported.

Cargo configuration is local to this directory: run Cargo here so it discovers
`.cargo/config.toml`, or use the build wrapper from any directory. Shared memory,
private worker stacks and the worker protocol described in `../RUST.md` remain
future work; this scaffold uses the default instance-owned memory.

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
