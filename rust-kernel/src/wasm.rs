// SPDX-License-Identifier: GPL-3.0-or-later

//! Checked linear-memory entry points. JS reserves memory; Rust never allocates it.

use crate::{
    Status,
    layout::{DESCRIPTOR_BYTES, Layout},
    prepared::Prepared,
};
use core::ops::Range;
use wide::bytemuck::try_cast_slice_mut;

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

enum Operation {
    Advance(u32),
    Observe,
    Capture,
}

// Validate every region before creating references. This coordinator entry point
// requires exclusive state/output ownership; row workers use solver::step_rows
// with slices restricted to their own bands instead of borrowing this whole state.
fn numerical_call(
    package: u32,
    package_bytes: u32,
    state: u32,
    state_capacity: u32,
    output: u32,
    output_capacity: u32,
    operation: Operation,
) -> Result<(), Status> {
    let input_range = region(package, package_bytes)?;
    let state_range = region(state, state_capacity)?;
    let output_range = region(output, output_capacity)?;
    if overlaps(&input_range, &state_range)
        || overlaps(&input_range, &output_range)
        || overlaps(&state_range, &output_range)
    {
        return Err(Status::Overlap);
    }
    // SAFETY: all regions are in memory, length-bounded, aligned, above the
    // module's stack/static data and disjoint. JS cannot enter these calls while
    // workers use the arena, nor mutate the package during a call.
    let input =
        unsafe { core::slice::from_raw_parts(input_range.start as *const u8, input_range.len()) };
    let parsed = Prepared::parse(input)?;
    let required = match operation {
        Operation::Advance(steps) => u64::from(steps) * u64::from(parsed.header.probe_count) * 4,
        Operation::Observe => u64::from(parsed.header.pixel_count) * 4,
        Operation::Capture => u64::from(crate::observations::frame_bytes(&parsed)?),
    };
    if required > i32::MAX as u64 {
        return Err(Status::TooLarge);
    }
    if required > u64::from(output_capacity) {
        return Err(Status::InsufficientCapacity);
    }
    let state =
        unsafe { core::slice::from_raw_parts_mut(state_range.start as *mut u8, state_range.len()) };
    let output = unsafe {
        core::slice::from_raw_parts_mut(output_range.start as *mut u8, required as usize)
    };
    match operation {
        Operation::Advance(steps) => crate::solver::advance(
            &parsed,
            state,
            steps,
            try_cast_slice_mut(output).map_err(|_| Status::InvalidRange)?,
        ),
        Operation::Observe => crate::observations::observe(
            &parsed,
            state,
            try_cast_slice_mut(output).map_err(|_| Status::InvalidRange)?,
        ),
        Operation::Capture => crate::observations::capture(&parsed, state, output),
    }
}

/// Complete steps, including ordered sources, Mur boundaries and per-step probes.
/// Output is step-major f32[steps * probe_count], with zero slots for line probes.
/// The caller owns all storage; this function neither allocates nor grows memory.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_advance(
    package: u32,
    package_bytes: u32,
    state: u32,
    state_capacity: u32,
    steps: u32,
    samples: u32,
    samples_capacity: u32,
) -> u32 {
    numerical_call(
        package,
        package_bytes,
        state,
        state_capacity,
        samples,
        samples_capacity,
        Operation::Advance(steps),
    )
    .err()
    .unwrap_or(Status::Ok) as u32
}

/// Current f32 observations in flattened probe-pixel order, without accumulating.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_observe(
    package: u32,
    package_bytes: u32,
    state: u32,
    state_capacity: u32,
    output: u32,
    capacity: u32,
) -> u32 {
    numerical_call(
        package,
        package_bytes,
        state,
        state_capacity,
        output,
        capacity,
        Operation::Observe,
    )
    .err()
    .unwrap_or(Status::Ok) as u32
}

/// Full canvas f16 field plane followed by f16 sqrt-intensity plane, little-endian.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_capture(
    package: u32,
    package_bytes: u32,
    state: u32,
    state_capacity: u32,
    output: u32,
    capacity: u32,
) -> u32 {
    numerical_call(
        package,
        package_bytes,
        state,
        state_capacity,
        output,
        capacity,
        Operation::Capture,
    )
    .err()
    .unwrap_or(Status::Ok) as u32
}
