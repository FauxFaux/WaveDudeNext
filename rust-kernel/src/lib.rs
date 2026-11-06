// SPDX-License-Identifier: GPL-3.0-or-later

//! Browser kernel scaffold. Numerical routines will be added separately.

#[cfg(all(target_arch = "wasm32", not(target_feature = "simd128")))]
compile_error!("The browser kernel requires -C target-feature=+simd128");

/// Identifies the numeric ABI of this placeholder module.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_abi_version() -> u32 {
    1
}
