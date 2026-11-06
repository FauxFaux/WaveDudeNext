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

//! Per-step unrooted probes and full-resolution field/sqrt-intensity half frames.

use crate::{
    Status,
    layout::Layout,
    prepared::{PROBE_BYTES, Prepared, u32_at},
    solver::{plane, validate_state},
};
use wide::f32x4;

pub(crate) fn sample(
    input: &Prepared<'_>,
    field: &[f32],
    intensity: &[f32],
    totals: &mut [f64],
    completed: u32,
    samples: &mut [f32],
) {
    let h = input.header;
    let b = input.bytes();
    for (i, output) in samples.iter_mut().enumerate() {
        let at = h.probes_offset as usize + i * PROBE_BYTES;
        let start = u32_at(b, at) as usize;
        let count = u32_at(b, at + 4) as usize;
        let flags = u32_at(b, at + 8);
        let data = if flags & 2 != 0 { intensity } else { field };
        let gated = flags & 16 != 0 && completed < u32_at(b, at + 12);
        *output = 0.0;
        if flags & 1 != 0 {
            if flags & 8 != 0 && !gated {
                for (k, total) in totals.iter_mut().enumerate().skip(start).take(count) {
                    let index = u32_at(b, h.pixels_offset as usize + k * 4) as usize;
                    *total += f64::from(data[index]);
                }
            }
        } else {
            let index = u32_at(b, h.pixels_offset as usize + start * 4) as usize;
            *output = data[index];
            if flags & 4 != 0 {
                if gated {
                    totals[start] = 0.0;
                } else {
                    totals[start] += f64::from(*output);
                }
                *output = totals[start] as f32;
            }
        }
    }
}

fn fields<'a>(
    input: &Prepared<'_>,
    layout: Layout,
    state: &'a [u8],
) -> Result<(&'a [f32], &'a [f32]), Status> {
    validate_state(input, layout, state)?;
    let offset = if u32_at(state, 4) == 0 {
        layout.field0
    } else {
        layout.field1
    };
    Ok((
        plane(state, offset, layout.cells)?,
        plane(state, layout.intensity_output, layout.cells)?,
    ))
}

/// Read every prepared pixel in table order. Integrated values come from f64
/// totals converted once to f32; other values are full-precision live field/I.
/// This does not accumulate, clear totals or retain a history.
pub fn observe(input: &Prepared<'_>, state: &[u8], output: &mut [f32]) -> Result<(), Status> {
    let h = input.header;
    if output.len() < h.pixel_count as usize {
        return Err(Status::InsufficientCapacity);
    }
    let layout = Layout::plan(input)?;
    let (field, intensity) = fields(input, layout, state)?;
    let totals = plane::<f64>(state, layout.probe_totals, h.pixel_count)?;
    let b = input.bytes();
    for i in 0..h.probe_count as usize {
        let at = h.probes_offset as usize + i * PROBE_BYTES;
        let start = u32_at(b, at) as usize;
        let count = u32_at(b, at + 4) as usize;
        let flags = u32_at(b, at + 8);
        let data = if flags & 2 != 0 { intensity } else { field };
        for k in start..start + count {
            let index = u32_at(b, h.pixels_offset as usize + k * 4) as usize;
            output[k] = if flags & (4 | 8) != 0 {
                totals[k] as f32
            } else {
                data[index]
            };
        }
    }
    Ok(())
}

/// Byte count of tightly packed f16 field followed by f16 sqrt(I).
pub fn frame_bytes(input: &Prepared<'_>) -> Result<u32, Status> {
    let size = u64::from(input.header.canvas_width) * u64::from(input.header.canvas_height) * 4;
    if size > i32::MAX as u64 {
        return Err(Status::TooLarge);
    }
    Ok(size as u32)
}

/// Copy the canvas only (excluding rim/ring), without changing any solver state.
/// Field samples are signed; intensity is rooted before round-to-nearest-even f16.
/// Probes always read the original unrooted f32 intensity.
pub fn capture(input: &Prepared<'_>, state: &[u8], output: &mut [u8]) -> Result<(), Status> {
    let bytes = frame_bytes(input)? as usize;
    if output.len() < bytes {
        return Err(Status::InsufficientCapacity);
    }
    let layout = Layout::plan(input)?;
    let (field, intensity) = fields(input, layout, state)?;
    let h = input.header;
    let width = h.canvas_width as usize;
    let height = h.canvas_height as usize;
    for y in 0..height {
        let row = (y + h.rim as usize + 1) * h.stride as usize + h.rim as usize + 1;
        for x in (0..width).step_by(4) {
            let count = (width - x).min(4);
            let root = f32x4::from(&intensity[row + x..row + x + count])
                .sqrt()
                .to_array();
            for (lane, root_value) in root.into_iter().enumerate().take(count) {
                let at = (y * width + x + lane) * 2;
                output[at..at + 2].copy_from_slice(&to_half(field[row + x + lane]).to_le_bytes());
                let at = bytes / 2 + at;
                output[at..at + 2].copy_from_slice(&to_half(root_value).to_le_bytes());
            }
        }
    }
    Ok(())
}

/// IEEE binary32 to binary16, ties to even, preserving signed zero and overflow.
/// SIMD128 has no f16 packing instruction; use integer rounding after vector sqrt.
pub(crate) fn to_half(value: f32) -> u16 {
    let bits = value.to_bits();
    let sign = ((bits >> 16) & 0x8000) as u16;
    let exponent = ((bits >> 23) & 255) as i32;
    let fraction = bits & 0x7fffff;
    if exponent == 255 {
        return sign
            | 0x7c00
            | if fraction == 0 {
                0
            } else {
                0x200 | (fraction >> 13) as u16
            };
    }
    let e = exponent - 127 + 15;
    if e >= 31 {
        return sign | 0x7c00;
    }
    if e <= 0 {
        if e < -10 {
            return sign;
        }
        let mantissa = fraction | 0x800000;
        let shift = (14 - e) as u32;
        let truncated = mantissa >> shift;
        let remainder = mantissa & ((1 << shift) - 1);
        let midpoint = 1 << (shift - 1);
        let rounded = truncated
            + u32::from(remainder > midpoint || (remainder == midpoint && truncated & 1 != 0));
        return sign | rounded as u16;
    }
    let rounded = fraction + 0xfff + ((fraction >> 13) & 1);
    sign | (((e as u32) << 10) + (rounded >> 13)) as u16
}
