// SPDX-License-Identifier: GPL-3.0-or-later

//! Prepared-input validation and caller-owned working storage for the browser kernel.
//! Numerical stepping and worker scheduling are deliberately separate from this ABI.

#[cfg(all(target_arch = "wasm32", not(target_feature = "simd128")))]
compile_error!("The browser kernel requires -C target-feature=+simd128");

pub mod layout;
pub mod prepared;
#[cfg(target_arch = "wasm32")]
mod wasm;

/// Stable status codes returned by the numeric Wasm interface.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum Status {
    Ok = 0,
    InvalidRange = 1,
    InvalidPackage = 2,
    UnsupportedVersion = 3,
    InvalidLayout = 4,
    InvalidValue = 5,
    InsufficientCapacity = 6,
    Overlap = 7,
    TooLarge = 8,
}

/// Numeric kernel ABI version; independent of the WDPK package/layout versions.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_abi_version() -> u32 {
    1
}

/// Capability bits: 1 = input/layout planning, 2 = state initialisation, 4 = stepping.
/// Stepping is not implemented; consumers must not treat initialised zeros as a frame.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_capabilities() -> u32 {
    1 | 2
}

#[cfg(test)]
mod tests;
