// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alexander Graef <alex@graef.cc>
//
// This file is part of WaveDude Next, a 2D wave simulator.
//
// Based on the work of
//   Jeroen Vleggaar (Huygens Optics), author of Wave Dude 2D, Copyright (C) 2026
//     <https://www.youtube.com/@HuygensOptics>, <https://www.huygensoptics.com>
//   0x23 (Diffraction Limited), author of WaveSimulator2D
//     <https://www.youtube.com/@DiffractionLimited>, <https://github.com/0x23/WaveSimulator2D>
//
// WaveDude Next is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
// <https://www.gnu.org/licenses/>.

//! Four-lane translation of the C# stencils, leapfrog update and intensity filters.
//! The row function borrows only its destination band, for later JS worker scheduling.

use crate::{
    Status,
    layout::Layout,
    prepared::{Prepared, f32_at, f64_at, i32_at, u32_at},
};
use wide::{
    bytemuck::{Pod, try_cast_slice, try_cast_slice_mut},
    f32x4,
};

/// Constants resolved by C# preparation; damping already includes the substep root.
#[derive(Clone, Copy)]
pub struct Parameters {
    pub width: usize,
    pub height: usize,
    pub stride: usize,
    pub fourth_order: bool,
    pub multiplier: f32,
    pub alpha: f32,
    pub scale: f32,
    pub blend: f32,
    pub edge_speed: f32,
}

impl Parameters {
    pub fn from_input(input: &Prepared<'_>) -> Self {
        let h = input.header;
        let b = input.bytes();
        Self {
            width: h.grid_width as usize,
            height: h.grid_height as usize,
            stride: h.stride as usize,
            fourth_order: h.flags & 2 != 0,
            multiplier: f32_at(b, 104),
            alpha: f32_at(b, 108),
            scale: f32_at(b, 112),
            blend: f32_at(b, 120),
            edge_speed: f32_at(b, 124),
        }
    }
}

/// Immutable full-grid reads, shared by all row bands during a substep.
#[derive(Clone, Copy)]
pub struct Grid<'a> {
    pub current: &'a [f32],
    pub c2: &'a [f32],
    pub damping: &'a [f32],
}

/// Mutable planes restricted to the owned rows. The first element is global
/// padded index (row_start + 1) * stride.
/// Ring columns are present in each row but never written by stepping.
pub struct Band<'a> {
    pub previous: &'a mut [f32],
    pub stage1: &'a mut [f32],
    pub stage2: &'a mut [f32],
    pub intensity: &'a mut [f32],
}

#[inline]
fn load(data: &[f32], at: usize, count: usize) -> f32x4 {
    // From<&[f32]> zero-fills short vectors; the full-width case is an unaligned
    // load. Never cast an arbitrary neighbour address to an aligned vector reference.
    f32x4::from(&data[at..at + count])
}

#[inline]
fn store(data: &mut [f32], at: usize, value: f32x4, count: usize) {
    data[at..at + count].copy_from_slice(&value.to_array()[..count]);
}

#[inline]
fn chunk<const FOURTH: bool, const COUNT: usize>(
    p: Parameters,
    grid: Grid<'_>,
    band: &mut Band<'_>,
    at: usize,
    local: usize,
    track: bool,
) {
    let count = COUNT;
    let Grid {
        current,
        c2,
        damping,
    } = grid;
    let s = p.stride;
    let center = load(current, at, count);
    let near = load(current, at - 1, count)
        + load(current, at + 1, count)
        + load(current, at - s, count)
        + load(current, at + s, count);
    let laplacian = if FOURTH {
        let far = load(current, at - 2, count)
            + load(current, at + 2, count)
            + load(current, at - 2 * s, count)
            + load(current, at + 2 * s, count);
        (-1.0_f32 / 12.0) * far + (4.0_f32 / 3.0) * near + -5.0 * center
    } else {
        let corners = load(current, at - s - 1, count)
            + load(current, at - s + 1, count)
            + load(current, at + s - 1, count)
            + load(current, at + s + 1, count);
        0.066 * corners + 0.184 * near - center
    };
    // Start with the reference's grouping; numerical equivalence permits FMA rounding.
    let next = center
        + (center - load(band.previous, local, count)) * load(damping, at, count)
        + laplacian * load(c2, at, count) * p.multiplier;
    store(band.previous, local, next, count);
    if track {
        let s1 = load(band.stage1, local, count) * p.alpha + next * next * (1.0 - p.alpha);
        let s2 = load(band.stage2, local, count) * p.alpha + s1 * (1.0 - p.alpha);
        store(band.stage1, local, s1, count);
        store(band.stage2, local, s2, count);
        store(
            band.intensity,
            local,
            (s1 * (1.0 - p.blend) + s2 * p.blend) * p.scale,
            count,
        );
    }
}

/// Advance [row_start, row_end) in the interior, excluding the padding ring.
/// Current/maps cover the complete padded grid; each mutable slice covers exactly
/// these rows including ring columns. Fourth-order eligibility uses global rows,
/// so worker boundaries never change the stencil. The caller applies open edges
/// and swaps fields only after all row bands have completed.
pub fn step_rows(
    p: Parameters,
    grid: Grid<'_>,
    mut band: Band<'_>,
    rows: core::ops::Range<usize>,
    track: bool,
) {
    let row_start = rows.start;
    let row_end = rows.end;
    assert!(row_start <= row_end && row_end <= p.height);
    assert_eq!(band.previous.len(), (row_end - row_start) * p.stride);
    if track {
        assert_eq!(band.stage1.len(), band.previous.len());
        assert_eq!(band.stage2.len(), band.previous.len());
        assert_eq!(band.intensity.len(), band.previous.len());
    }
    for y in row_start..row_end {
        let row = (y + 1) * p.stride + 1;
        let local = (y - row_start) * p.stride + 1;
        let fourth = p.fourth_order && y > 0 && y + 1 < p.height && p.width > 1;
        let mut x = 0;
        let end = if fourth { p.width - 1 } else { p.width };
        if fourth {
            chunk::<false, 1>(p, grid, &mut band, row, local, track);
            x = 1;
        }
        if fourth {
            row_chunks::<true>(p, grid, &mut band, row, local, x..end, track);
            chunk::<false, 1>(
                p,
                grid,
                &mut band,
                row + p.width - 1,
                local + p.width - 1,
                track,
            );
        } else {
            row_chunks::<false>(p, grid, &mut band, row, local, x..end, track);
        }
    }
}

#[inline]
fn row_chunks<const FOURTH: bool>(
    p: Parameters,
    grid: Grid<'_>,
    band: &mut Band<'_>,
    row: usize,
    local: usize,
    columns: core::ops::Range<usize>,
    track: bool,
) {
    let mut x = columns.start;
    while x + 4 <= columns.end {
        chunk::<FOURTH, 4>(p, grid, band, row + x, local + x, track);
        x += 4;
    }
    // Specialise the load/store sizes even if LLVM keeps chunk out of line.
    match columns.end - x {
        1 => chunk::<FOURTH, 1>(p, grid, band, row + x, local + x, track),
        2 => chunk::<FOURTH, 2>(p, grid, band, row + x, local + x, track),
        3 => chunk::<FOURTH, 3>(p, grid, band, row + x, local + x, track),
        _ => {}
    }
}

/// Mur's first-order ring, including diagonal inner neighbours for the corners.
pub fn open_edges(p: Parameters, old: &[f32], next: &mut [f32], c2: &[f32]) {
    let mut absorb = |ring: usize, inner: usize| {
        let speed = (c2[inner] * p.multiplier).sqrt() * p.edge_speed;
        let k = (speed - 1.0) / (speed + 1.0);
        next[ring] = old[inner] + k * (next[inner] - old[ring]);
    };
    for x in 0..=p.width + 1 {
        let inner_x = x.clamp(1, p.width);
        absorb(x, p.stride + inner_x);
        absorb((p.height + 1) * p.stride + x, p.height * p.stride + inner_x);
    }
    for y in 1..=p.height {
        absorb(y * p.stride, y * p.stride + 1);
        absorb(y * p.stride + p.width + 1, y * p.stride + p.width);
    }
}

pub(crate) fn plane<T: Pod>(bytes: &[u8], offset: u32, count: u32) -> Result<&[T], Status> {
    try_cast_slice(&bytes[offset as usize..offset as usize + count as usize * size_of::<T>()])
        .map_err(|_| Status::InvalidRange)
}

fn take<'a, T: Pod>(bytes: &mut &'a mut [u8], count: u32) -> Result<&'a mut [T], Status> {
    let length = count as usize * size_of::<T>();
    let (section, rest) = core::mem::take(bytes).split_at_mut((length + 15) & !15);
    *bytes = rest;
    try_cast_slice_mut(&mut section[..length]).map_err(|_| Status::InvalidRange)
}

pub(crate) fn validate_state(
    input: &Prepared<'_>,
    layout: Layout,
    state: &[u8],
) -> Result<(), Status> {
    if state.len() < layout.state_bytes as usize {
        return Err(Status::InsufficientCapacity);
    }
    if u32_at(state, 0) > input.header.last_step
        || u32_at(state, 4) > 1
        || state[8..16].iter().any(|&b| b != 0)
    {
        return Err(Status::InvalidState);
    }
    for i in 0..input.header.emitter_count as usize {
        let at = layout.emitter_state as usize + i * 16;
        if !f64_at(state, at).is_finite()
            || f64_at(state, at) < 0.0
            || i32_at(state, at + 8) < 1
            || u32_at(state, at + 12) == 0
        {
            return Err(Status::InvalidState);
        }
    }
    Ok(())
}

/// Advance complete steps on one coordinator, including sources and per-step probes.
/// Samples are step-major f32[steps * probe_count]; line slots contain zero.
/// No allocation, captured frames or retained point histories. The complete state
/// and output must be exclusively owned during this call. Validation errors leave
/// them untouched. A batch must not advance past the package's final captured step.
pub fn advance(
    input: &Prepared<'_>,
    state: &mut [u8],
    steps: u32,
    samples: &mut [f32],
) -> Result<(), Status> {
    let h = input.header;
    let layout = Layout::plan(input)?;
    validate_state(input, layout, state)?;
    let start = u32_at(state, 0);
    if steps > h.last_step - start {
        return Err(Status::InvalidState);
    }
    let sample_count = steps as u64 * u64::from(h.probe_count);
    if sample_count > samples.len() as u64 {
        return Err(Status::InsufficientCapacity);
    }
    let c2 = plane::<f32>(input.bytes(), h.c2_offset, h.cells)?;
    let damping = plane::<f32>(input.bytes(), h.damping_offset, h.cells)?;
    let (control, mut rest) = state[..layout.state_bytes as usize].split_at_mut(16);
    let mut current = take::<f32>(&mut rest, h.cells)?;
    let mut previous = take::<f32>(&mut rest, h.cells)?;
    let stage1 = take::<f32>(&mut rest, h.cells)?;
    let stage2 = take::<f32>(&mut rest, h.cells)?;
    let intensity = take::<f32>(&mut rest, h.cells)?;
    let emitter_state = take::<u8>(&mut rest, h.emitter_count * 16)?;
    let totals = take::<f64>(&mut rest, h.pixel_count)?;
    let mut slot = u32_at(control, 4);
    if slot == 1 {
        core::mem::swap(&mut current, &mut previous);
    }
    let p = Parameters::from_input(input);
    let interior = p.stride..(p.height + 1) * p.stride;
    for n in 0..steps {
        crate::sources::apply(input, current, emitter_state, start + n);
        for sub in 0..h.substeps {
            step_rows(
                p,
                Grid {
                    current,
                    c2,
                    damping,
                },
                Band {
                    previous: &mut previous[interior.clone()],
                    stage1: &mut stage1[interior.clone()],
                    stage2: &mut stage2[interior.clone()],
                    intensity: &mut intensity[interior.clone()],
                },
                0..p.height,
                sub + 1 == h.substeps,
            );
            if h.flags & 1 != 0 {
                open_edges(p, current, previous, c2);
            }
            core::mem::swap(&mut current, &mut previous);
            slot ^= 1;
        }
        crate::observations::sample(
            input,
            current,
            intensity,
            totals,
            start + n + 1,
            &mut samples
                [n as usize * h.probe_count as usize..(n as usize + 1) * h.probe_count as usize],
        );
    }
    control[..4].copy_from_slice(&(start + steps).to_le_bytes());
    control[4..8].copy_from_slice(&slot.to_le_bytes());
    Ok(())
}

/// Begin a coordinated step with exclusive access to state. Sources are applied
/// once, in order, before any band reads the current field. Words 2/3 in the state
/// header hold the substep index and active flag until finish_substep completes.
pub fn begin_step(input: &Prepared<'_>, state: &mut [u8]) -> Result<(), Status> {
    let h = input.header;
    let layout = Layout::plan(input)?;
    validate_state(input, layout, state)?;
    let completed = u32_at(state, 0);
    if completed >= h.last_step {
        return Err(Status::InvalidState);
    }
    let slot = u32_at(state, 4);
    let (control, mut rest) = state[..layout.state_bytes as usize].split_at_mut(16);
    let field0 = take::<f32>(&mut rest, h.cells)?;
    let field1 = take::<f32>(&mut rest, h.cells)?;
    let _ = take::<f32>(&mut rest, h.cells)?;
    let _ = take::<f32>(&mut rest, h.cells)?;
    let _ = take::<f32>(&mut rest, h.cells)?;
    let emitters = take::<u8>(&mut rest, h.emitter_count * 16)?;
    crate::sources::apply(
        input,
        if slot == 0 { field0 } else { field1 },
        emitters,
        completed,
    );
    control[12..16].copy_from_slice(&1u32.to_le_bytes());
    Ok(())
}

/// Finish a substep after every band has completed. Exclusive access is required:
/// apply open edges and swap fields, then sample probes after the final substep.
/// Intermediate substeps accept an empty sample slice. No worker may start again
/// until this function returns and the coordinator publishes the next generation.
pub fn finish_substep(
    input: &Prepared<'_>,
    state: &mut [u8],
    samples: &mut [f32],
) -> Result<(), Status> {
    let h = input.header;
    let layout = Layout::plan(input)?;
    if state.len() < layout.state_bytes as usize {
        return Err(Status::InsufficientCapacity);
    }
    let completed = u32_at(state, 0);
    let slot = u32_at(state, 4);
    let sub = u32_at(state, 8);
    if completed >= h.last_step || slot > 1 || sub >= h.substeps || u32_at(state, 12) != 1 {
        return Err(Status::InvalidState);
    }
    let final_substep = sub + 1 == h.substeps;
    if final_substep && samples.len() < h.probe_count as usize {
        return Err(Status::InsufficientCapacity);
    }
    let c2 = plane::<f32>(input.bytes(), h.c2_offset, h.cells)?;
    let (control, mut rest) = state[..layout.state_bytes as usize].split_at_mut(16);
    let field0 = take::<f32>(&mut rest, h.cells)?;
    let field1 = take::<f32>(&mut rest, h.cells)?;
    let _ = take::<f32>(&mut rest, h.cells)?;
    let _ = take::<f32>(&mut rest, h.cells)?;
    let intensity = take::<f32>(&mut rest, h.cells)?;
    let _ = take::<u8>(&mut rest, h.emitter_count * 16)?;
    let totals = take::<f64>(&mut rest, h.pixel_count)?;
    let (current, next) = if slot == 0 {
        (field0, field1)
    } else {
        (field1, field0)
    };
    if h.flags & 1 != 0 {
        open_edges(Parameters::from_input(input), current, next, c2);
    }
    control[4..8].copy_from_slice(&(slot ^ 1).to_le_bytes());
    if final_substep {
        crate::observations::sample(
            input,
            next,
            intensity,
            totals,
            completed + 1,
            &mut samples[..h.probe_count as usize],
        );
        control[..4].copy_from_slice(&(completed + 1).to_le_bytes());
        control[8..16].fill(0);
    } else {
        control[8..12].copy_from_slice(&(sub + 1).to_le_bytes());
    }
    Ok(())
}
