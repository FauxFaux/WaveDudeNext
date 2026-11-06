// SPDX-License-Identifier: GPL-3.0-or-later

//! Checked linear-memory entry points. JS reserves memory; Rust never allocates it.

use crate::{
    Status,
    layout::{DESCRIPTOR_BYTES, Layout},
    prepared::{Prepared, u32_at},
    solver::{Band, Grid, Parameters, plane, step_rows},
};
use core::ops::Range;
use wide::bytemuck::try_cast_slice_mut;

unsafe extern "C" {
    static __heap_base: u8;
}

enum RowAssignment {
    Band(u32, u32),
    Stripes {
        worker: u32,
        workers: u32,
        rows: u32,
    },
}

// Parse/check once per worker/substep, then borrow each owned destination stripe
// separately. No mutable reference spans another worker's rows between stripes.
fn step_assignment(
    package: u32,
    package_bytes: u32,
    state: u32,
    state_capacity: u32,
    assignment: RowAssignment,
) -> Result<(), Status> {
    let input_range = region(package, package_bytes)?;
    let state_range = region(state, state_capacity)?;
    if overlaps(&input_range, &state_range) {
        return Err(Status::Overlap);
    }
    // SAFETY: only immutable package/control/field reads and band-restricted
    // mutable references are formed. The host enforces the barrier contract.
    let input =
        unsafe { core::slice::from_raw_parts(input_range.start as *const u8, input_range.len()) };
    let parsed = Prepared::parse_layout(input)?;
    let h = parsed.header;
    let layout = Layout::plan(&parsed)?;
    if state_capacity < layout.state_bytes {
        return Err(Status::InsufficientCapacity);
    }
    let (first, limit, rows, period) = match assignment {
        RowAssignment::Band(start, end) => {
            if start > end || end > h.grid_height {
                return Err(Status::InvalidRange);
            }
            (start, end, end - start, end - start)
        }
        RowAssignment::Stripes {
            worker,
            workers,
            rows,
        } => {
            if workers == 0 || workers > h.grid_height || worker >= workers || rows > h.grid_height
            {
                return Err(Status::InvalidValue);
            }
            if rows == 0 {
                let start =
                    (u64::from(worker) * u64::from(h.grid_height) / u64::from(workers)) as u32;
                let end =
                    (u64::from(worker + 1) * u64::from(h.grid_height) / u64::from(workers)) as u32;
                (start, end, end - start, end - start)
            } else {
                let first = worker.checked_mul(rows).ok_or(Status::TooLarge)?;
                let period = workers.checked_mul(rows).ok_or(Status::TooLarge)?;
                (first, h.grid_height, rows, period)
            }
        }
    };
    let control = unsafe { core::slice::from_raw_parts(state as *const u8, 16) };
    let slot = u32_at(control, 4);
    let sub = u32_at(control, 8);
    if u32_at(control, 0) >= h.last_step
        || slot > 1
        || sub >= h.substeps
        || u32_at(control, 12) != 1
    {
        return Err(Status::InvalidState);
    }
    let (current, previous) = if slot == 0 {
        (layout.field0, layout.field1)
    } else {
        (layout.field1, layout.field0)
    };
    if first >= limit {
        return Ok(());
    }
    let current =
        unsafe { core::slice::from_raw_parts((state + current) as *const f32, h.cells as usize) };
    let p = Parameters::from_input(&parsed);
    let grid = Grid {
        current,
        c2: plane(input, h.c2_offset, h.cells)?,
        damping: plane(input, h.damping_offset, h.cells)?,
    };
    for row_start in (first..limit).step_by(period as usize) {
        let row_end = row_start + rows.min(limit - row_start);
        let start = (row_start as usize + 1) * h.stride as usize;
        let count = (row_end - row_start) as usize * h.stride as usize;
        let band = |offset: u32| unsafe {
            core::slice::from_raw_parts_mut(
                (state as usize + offset as usize + start * 4) as *mut f32,
                count,
            )
        };
        step_rows(
            p,
            grid,
            Band {
                previous: band(previous),
                stage1: band(layout.intensity_stage1),
                stage2: band(layout.intensity_stage2),
                intensity: band(layout.intensity_output),
            },
            row_start as usize..row_end as usize,
            sub + 1 == h.substeps,
        );
    }
    Ok(())
}

/// Update one fixed row band in an active coordinated substep. The package must
/// have passed full preparation validation and remain immutable. Each caller owns
/// disjoint destination/filter rows and a private stack. Shared field/maps/control
/// stay read-only until the barrier. Never borrow the complete state mutably here.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_step_band(
    package: u32,
    package_bytes: u32,
    state: u32,
    state_capacity: u32,
    row_start: u32,
    row_end: u32,
) -> u32 {
    step_assignment(
        package,
        package_bytes,
        state,
        state_capacity,
        RowAssignment::Band(row_start, row_end),
    )
    .err()
    .unwrap_or(Status::Ok) as u32
}

/// Update a worker's complete static stripe assignment in one call. Worker i owns
/// [i*rows + k*workers*rows, min(start+rows, height)) for each k. A zero stripe height
/// selects contiguous equal bands. The same ownership/barrier contract as
/// kernel_step_band applies; the host supplies one consistent profile to all workers.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_step_stripes(
    package: u32,
    package_bytes: u32,
    state: u32,
    state_capacity: u32,
    worker: u32,
    workers: u32,
    rows: u32,
) -> u32 {
    step_assignment(
        package,
        package_bytes,
        state,
        state_capacity,
        RowAssignment::Stripes {
            worker,
            workers,
            rows,
        },
    )
    .err()
    .unwrap_or(Status::Ok) as u32
}

/// Coordinator-only phase: 0 applies sources and starts a step; 1 finishes a
/// substep after its barrier. Only the final substep writes probe_count samples.
/// The package must already be validated/immutable; state/output are exclusive.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_phase(
    package: u32,
    package_bytes: u32,
    state: u32,
    state_capacity: u32,
    phase: u32,
    output: u32,
    capacity: u32,
) -> u32 {
    fn run(
        package: u32,
        package_bytes: u32,
        state: u32,
        state_capacity: u32,
        phase: u32,
        output: u32,
        capacity: u32,
    ) -> Result<(), Status> {
        if phase > 1 {
            return Err(Status::InvalidValue);
        }
        let input_range = region(package, package_bytes)?;
        let state_range = region(state, state_capacity)?;
        let output_range = region(output, capacity)?;
        if overlaps(&input_range, &state_range)
            || overlaps(&input_range, &output_range)
            || overlaps(&state_range, &output_range)
        {
            return Err(Status::Overlap);
        }
        // SAFETY: the coordinator holds exclusive state/output access between
        // generations. The validated package stays immutable for the entire run.
        let input = unsafe {
            core::slice::from_raw_parts(input_range.start as *const u8, input_range.len())
        };
        let parsed = Prepared::parse_layout(input)?;
        let state = unsafe {
            core::slice::from_raw_parts_mut(state_range.start as *mut u8, state_range.len())
        };
        if phase == 0 {
            return crate::solver::begin_step(&parsed, state);
        }
        let output = unsafe {
            core::slice::from_raw_parts_mut(output_range.start as *mut u8, output_range.len())
        };
        let float_bytes = output.len() & !3;
        crate::solver::finish_substep(
            &parsed,
            state,
            try_cast_slice_mut(&mut output[..float_bytes]).map_err(|_| Status::InvalidRange)?,
        )
    }
    run(
        package,
        package_bytes,
        state,
        state_capacity,
        phase,
        output,
        capacity,
    )
    .err()
    .unwrap_or(Status::Ok) as u32
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
    Initialise,
    Advance(u32),
    Observe,
    Capture,
}

enum PackageValidation {
    Full,
    AlreadyValidated,
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
    validation: PackageValidation,
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
    let parsed = match validation {
        PackageValidation::Full => Prepared::parse(input)?,
        PackageValidation::AlreadyValidated => Prepared::parse_layout(input)?,
    };
    let required = match operation {
        Operation::Initialise => 0,
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
        Operation::Initialise => Layout::plan(&parsed)?.initialise(&parsed, state),
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

/// Execute against a package already validated by kernel_plan or kernel_initialise.
/// Operations: 0 initialise/reset, 1 advance steps, 2 observe, 3 capture. Steps must
/// be zero except for advance. Package bytes must remain immutable for the run.
/// Memory ranges, disjointness, layout, state and output capacity are still checked;
/// immutable map/emitter/probe values are not rescanned. All state/output access is
/// exclusive: this coordinator call must never overlap active compute bands.
#[unsafe(no_mangle)]
pub extern "C" fn kernel_execute_prepared(
    package: u32,
    package_bytes: u32,
    state: u32,
    state_capacity: u32,
    operation: u32,
    steps: u32,
    output: u32,
    capacity: u32,
) -> u32 {
    let operation = match (operation, steps) {
        (0, 0) => Operation::Initialise,
        (1, steps) => Operation::Advance(steps),
        (2, 0) => Operation::Observe,
        (3, 0) => Operation::Capture,
        _ => return Status::InvalidValue as u32,
    };
    numerical_call(
        package,
        package_bytes,
        state,
        state_capacity,
        output,
        capacity,
        operation,
        PackageValidation::AlreadyValidated,
    )
    .err()
    .unwrap_or(Status::Ok) as u32
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
        PackageValidation::Full,
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
        PackageValidation::Full,
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
        PackageValidation::Full,
    )
    .err()
    .unwrap_or(Status::Ok) as u32
}
