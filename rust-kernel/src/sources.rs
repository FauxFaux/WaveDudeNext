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

//! Ordered soft source injection and double-precision pulse clocks.

use crate::prepared::{Prepared, f64_at, i32_at, u32_at};

pub(crate) fn apply(input: &Prepared<'_>, field: &mut [f32], states: &mut [u8], time: u32) {
    let opacity = crate::prepared::f32_at(input.bytes(), 116);
    for (emitter, state) in input
        .emitters()
        .zip(states.as_chunks_mut::<16>().0.iter_mut())
    {
        let detach = i32_at(emitter, 32);
        let mut fade = 1.0_f32;
        if detach >= 0 {
            let since = f64::from(time) - f64::from(detach);
            if since >= 30.0 {
                continue;
            }
            if since > 0.0 {
                fade = (1.0 - since / 30.0) as f32;
            }
        }
        let omega = f64_at(emitter, 40);
        let envelope = if u32_at(emitter, 4) == 0 {
            1.0
        } else {
            let (position, off, first) = pulse_position(emitter, state, omega, time);
            pulse_envelope(emitter, position, off, first)
        };
        // The cast precedes the f32 detach multiply, as in the CPU engine.
        let value = ((f64_at(emitter, 56) + omega * f64::from(time)).sin()
            * f64_at(emitter, 48)
            * envelope) as f32
            * fade;
        let index = u32_at(emitter, 0) as usize;
        field[index] = field[index] * opacity + value * (1.0 - opacity);
    }
}

fn pulse_position(e: &[u8], state: &mut [u8], omega: f64, time: u32) -> (f64, i32, bool) {
    let clock = omega * f64::from(time) / (2.0 * core::f64::consts::PI);
    let on = i32_at(e, 16);
    let amount = i32_at(e, 28);
    if amount == 0 {
        let off = i32_at(e, 20);
        let period = on.wrapping_add(off);
        if period <= 0 {
            return (0.0, off, true);
        }
        let period = f64::from(period);
        let adjusted = clock + f64::from(off);
        let cycle = (adjusted / period).floor();
        return (adjusted - period * cycle, off, cycle == 0.0);
    }
    let mut anchor = f64_at(state, 0);
    let mut off = i32_at(state, 8);
    let mut rng = u32_at(state, 12);
    let mut since = clock - anchor;
    let mut period = on.wrapping_add(off);
    if period <= 0 {
        return (0.0, off, true);
    }
    // Match the reference's guard and the number/order of draws even after large jumps.
    for _ in 0..1000 {
        if since < f64::from(period) {
            break;
        }
        anchor += f64::from(period);
        since -= f64::from(period);
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        // C# uses unchecked i32 arithmetic here, including its rare overflow cases.
        let range = amount.wrapping_mul(2).wrapping_add(1) as u32;
        off = i32_at(e, 24)
            .wrapping_add((rng % range) as i32)
            .wrapping_sub(amount)
            .max(1);
        period = on.wrapping_add(off);
    }
    state[..8].copy_from_slice(&anchor.to_le_bytes());
    state[8..12].copy_from_slice(&off.to_le_bytes());
    state[12..16].copy_from_slice(&rng.to_le_bytes());
    (since.max(0.0), off, anchor == 0.0)
}

fn pulse_envelope(e: &[u8], position: f64, off: i32, first: bool) -> f64 {
    let on = f64::from(i32_at(e, 16));
    let period = i32_at(e, 16).wrapping_add(off);
    if period <= 0 {
        return 1.0;
    }
    let period = f64::from(period);
    let mut on_position = position - f64::from(off);
    if u32_at(e, 8) == 0 {
        if on_position < 0.0 {
            return 0.0;
        }
        let z = (on_position - on / 2.0) / (on / 6.0).max(1e-6);
        return (-0.5 * z * z).exp();
    }
    if on_position < 0.0 && !first {
        on_position += period;
    }
    let fall_argument = on_position - on;
    let mut rise_argument = on_position;
    if rise_argument > period / 2.0 {
        rise_argument -= period;
    }
    let rise = 0.5 + (rise_argument / 0.35).atan() / core::f64::consts::PI;
    let fall = 0.5 - (fall_argument / 0.35).atan() / core::f64::consts::PI;
    rise * fall
}
