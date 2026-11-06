// SPDX-License-Identifier: GPL-3.0-or-later

//! Allocation-free numerical kernel with prepared inputs and caller-owned storage.

#[cfg(all(target_arch = "wasm32", not(target_feature = "simd128")))]
compile_error!("The browser kernel requires -C target-feature=+simd128");

pub mod layout;
pub mod observations;
pub mod prepared;
pub mod solver;
mod sources;
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
    InvalidState = 9,
}

/// Numeric kernel ABI version; independent of the WDPK package/layout versions.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_abi_version() -> u32 {
    1
}

/// Capability bits: planning, initialisation, stepping, probe observations and half frames.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_capabilities() -> u32 {
    1 | 2 | 4 | 8 | 16
}

#[cfg(test)]
mod tests;
