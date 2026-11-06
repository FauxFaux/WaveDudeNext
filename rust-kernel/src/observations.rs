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
#[cfg(target_arch = "wasm32")]
use core::arch::wasm32 as simd;
#[cfg(not(target_arch = "wasm32"))]
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
    let (field_output, intensity_output) = output[..bytes].split_at_mut(bytes / 2);
    for y in 0..height {
        let row = (y + h.rim as usize + 1) * h.stride as usize + h.rim as usize + 1;
        let field = &field[row..row + width];
        let intensity = &intensity[row..row + width];
        let field_output = &mut field_output[y * width * 2..(y + 1) * width * 2];
        let intensity_output = &mut intensity_output[y * width * 2..(y + 1) * width * 2];
        let mut x = 0;
        while x + 4 <= width {
            #[cfg(target_arch = "wasm32")]
            // SAFETY: the checked slices cover each complete four-float load and
            // eight-byte store. Wasm SIMD loads/stores accept unaligned pointers.
            unsafe {
                let values = simd::v128_load(field[x..x + 4].as_ptr().cast());
                let roots = simd::f32x4_sqrt(simd::v128_load(intensity[x..x + 4].as_ptr().cast()));
                simd::v128_store64_lane::<0>(
                    pack_half(values),
                    field_output[x * 2..x * 2 + 8].as_mut_ptr().cast(),
                );
                simd::v128_store64_lane::<0>(
                    pack_half(roots),
                    intensity_output[x * 2..x * 2 + 8].as_mut_ptr().cast(),
                );
            }
            #[cfg(not(target_arch = "wasm32"))]
            {
                let roots = f32x4::from(&intensity[x..x + 4]).sqrt().to_array();
                for (lane, root) in roots.into_iter().enumerate() {
                    let at = (x + lane) * 2;
                    field_output[at..at + 2]
                        .copy_from_slice(&to_half(field[x + lane]).to_le_bytes());
                    intensity_output[at..at + 2].copy_from_slice(&to_half(root).to_le_bytes());
                }
            }
            x += 4;
        }
        for x in x..width {
            field_output[x * 2..x * 2 + 2].copy_from_slice(&to_half(field[x]).to_le_bytes());
            intensity_output[x * 2..x * 2 + 2]
                .copy_from_slice(&to_half(intensity[x].sqrt()).to_le_bytes());
        }
    }
    Ok(())
}

// Convert four binary32 lanes to binary16, packed into the low eight bytes.
// Keep rounding and exceptional values equivalent to the scalar row-tail path.
#[cfg(target_arch = "wasm32")]
#[inline]
fn pack_half(values: simd::v128) -> simd::v128 {
    let sign = simd::v128_and(simd::u32x4_shr(values, 16), simd::i32x4_splat(0x8000));
    let magnitude = simd::v128_and(values, simd::i32x4_splat(0x7fffffff));
    let rounding = simd::v128_and(simd::u32x4_shr(magnitude, 13), simd::i32x4_splat(1));
    let normal = simd::u32x4_shr(
        simd::i32x4_sub(
            simd::i32x4_add(
                simd::i32x4_add(magnitude, simd::i32x4_splat(0xfff)),
                rounding,
            ),
            simd::i32x4_splat(0x38000000),
        ),
        13,
    );
    // Below 2^-14, adding 0.5 rounds to multiples of 2^-24 with ties to even.
    let subnormal = simd::i32x4_sub(
        simd::f32x4_add(magnitude, simd::i32x4_splat(0x3f000000)),
        simd::i32x4_splat(0x3f000000),
    );
    let finite = simd::v128_bitselect(
        subnormal,
        normal,
        simd::u32x4_lt(magnitude, simd::i32x4_splat(0x38800000)),
    );
    let saturated = simd::v128_bitselect(
        simd::i32x4_splat(0x7c00),
        finite,
        simd::u32x4_ge(magnitude, simd::i32x4_splat(0x47800000)),
    );
    // Preserve the sign and high payload bits while quieting every NaN.
    let nan = simd::v128_or(
        simd::i32x4_splat(0x7e00),
        simd::u32x4_shr(simd::v128_and(magnitude, simd::i32x4_splat(0x7fffff)), 13),
    );
    let converted = simd::v128_bitselect(
        nan,
        saturated,
        simd::u32x4_gt(magnitude, simd::i32x4_splat(0x7f800000)),
    );
    let halves = simd::v128_or(sign, converted);
    simd::u16x8_narrow_i32x4(halves, halves)
}

/// IEEE binary32 to binary16, ties to even, preserving signed zero and overflow.
/// Used for row tails and native builds; Wasm packs full groups with SIMD128.
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
