// SPDX-License-Identifier: GPL-3.0-or-later

//! Checked linear-memory entry points. JS reserves memory; Rust never allocates it.

use crate::{
    Status,
    layout::{DESCRIPTOR_BYTES, Layout},
    prepared::Prepared,
};
use core::ops::Range;

unsafe extern "C" {
    static __heap_base: u8;
}

/// First byte available to the JS-owned arena, above this instance's static data/stack.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_heap_base() -> u32 {
    (&raw const __heap_base as usize as u32 + 15) & !15
}

fn region(pointer: u32, length: u32) -> Result<Range<usize>, Status> {
    let start = pointer as usize;
    let end = start
        .checked_add(length as usize)
        .ok_or(Status::InvalidRange)?;
    let memory_bytes = core::arch::wasm32::memory_size(0) as u64 * 65536;
    if pointer < kernel_heap_base()
        || pointer % 16 != 0
        || length > i32::MAX as u32
        || end as u64 > memory_bytes
    {
        return Err(Status::InvalidRange);
    }
    Ok(start..end)
}

fn overlaps(a: &Range<usize>, b: &Range<usize>) -> bool {
    a.start < b.end && b.start < a.end
}

/// Validate WDPK input and write a 64-byte KLAY descriptor. Does not initialise state.
/// Input/output regions must be aligned, disjoint and exclusively owned during this call.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_plan(
    package: u32,
    package_bytes: u32,
    descriptor: u32,
    capacity: u32,
) -> u32 {
    fn plan(
        package: u32,
        package_bytes: u32,
        descriptor: u32,
        capacity: u32,
    ) -> Result<(), Status> {
        if capacity < DESCRIPTOR_BYTES as u32 {
            return Err(Status::InsufficientCapacity);
        }
        let input_range = region(package, package_bytes)?;
        let output_range = region(descriptor, capacity)?;
        if overlaps(&input_range, &output_range) {
            return Err(Status::Overlap);
        }
        // SAFETY: ranges are in the caller-owned arena, within memory, disjoint and length-bounded.
        // JS must not mutate input or enter the module concurrently during planning/initialisation.
        let input = unsafe {
            core::slice::from_raw_parts(input_range.start as *const u8, input_range.len())
        };
        let parsed = Prepared::parse(input)?;
        let layout = Layout::plan(&parsed)?;
        let output = unsafe {
            core::slice::from_raw_parts_mut(output_range.start as *mut u8, output_range.len())
        };
        layout.write_descriptor(output)
    }
    plan(package, package_bytes, descriptor, capacity)
        .err()
        .unwrap_or(Status::Ok) as u32
}

/// Validate input, then initialise/reset working storage. No simulation step is executed.
/// State has the layout returned by kernel_plan for the same package. Input/state must be disjoint.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_initialise(
    package: u32,
    package_bytes: u32,
    state: u32,
    capacity: u32,
) -> u32 {
    fn initialise(
        package: u32,
        package_bytes: u32,
        state: u32,
        capacity: u32,
    ) -> Result<(), Status> {
        let input_range = region(package, package_bytes)?;
        let output_range = region(state, capacity)?;
        if overlaps(&input_range, &output_range) {
            return Err(Status::Overlap);
        }
        // SAFETY: same exclusive-access contract and range checks as kernel_plan.
        let input = unsafe {
            core::slice::from_raw_parts(input_range.start as *const u8, input_range.len())
        };
        let parsed = Prepared::parse(input)?;
        let layout = Layout::plan(&parsed)?;
        if capacity < layout.state_bytes {
            return Err(Status::InsufficientCapacity);
        }
        let output = unsafe {
            core::slice::from_raw_parts_mut(output_range.start as *mut u8, output_range.len())
        };
        layout.initialise(&parsed, output)
    }
    initialise(package, package_bytes, state, capacity)
        .err()
        .unwrap_or(Status::Ok) as u32
}
