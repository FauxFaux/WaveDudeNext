// SPDX-License-Identifier: GPL-3.0-or-later

use crate::{Status, layout::Layout, prepared::Prepared};

fn word(bytes: &mut [u8], at: usize, value: u32) {
    bytes[at..at + 4].copy_from_slice(&value.to_le_bytes());
}
fn float(bytes: &mut [u8], at: usize, value: f32) {
    word(bytes, at, value.to_bits());
}
fn double(bytes: &mut [u8], at: usize, value: f64) {
    bytes[at..at + 8].copy_from_slice(&value.to_le_bytes());
}

fn packet() -> Vec<u8> {
    // An 8x8 canvas with no rim: padded planes have 100 floats. One overlapping
    // source pair and an integrated point probe exercise record order/state seeding.
    let mut b = vec![0; 1120];
    b[..4].copy_from_slice(b"WDPK");
    for (at, value) in [
        (4, 1),
        (8, 1120),
        (12, 160),
        (16, 8),
        (20, 8),
        (24, 8),
        (28, 8),
        (36, 10),
        (40, 100),
        (44, 4),
        (48, 1),
        (52, 9),
        (56, 2),
        (60, 5),
        (64, 9),
        (68, 2),
        (72, 1),
        (76, 1),
        (80, 160),
        (84, 560),
        (88, 960),
        (92, 1088),
        (96, 1120 - 16),
        (144, 64),
        (148, 32),
        (152, 30),
    ] {
        word(&mut b, at, value);
    }
    for (at, value) in [
        (104, 1.0),
        (108, 0.9),
        (112, 1.0),
        (116, 0.9),
        (120, 0.5),
        (124, 0.562_138_8),
    ] {
        float(&mut b, at, value);
    }
    double(&mut b, 128, 2.0);
    double(&mut b, 136, 10.0);
    for y in 1..=8 {
        for x in 1..=8 {
            float(&mut b, 160 + (y * 10 + x) * 4, 1.0);
            float(&mut b, 560 + (y * 10 + x) * 4, 1.0);
        }
    }
    for (i, off) in [7, 11].into_iter().enumerate() {
        let at = 960 + i * 64;
        for (offset, value) in [
            (0, 44),
            (4, 1),
            (8, 1),
            (12, 100 + i as u32),
            (16, 3),
            (20, off),
            (24, 5),
            (28, 2),
            (32, u32::MAX),
        ] {
            word(&mut b, at + offset, value);
        }
        double(&mut b, at + 40, 0.4);
        double(&mut b, at + 48, 0.75);
        double(&mut b, at + 56, i as f64);
    }
    // Probe record occupies 1088..1120; move the pixel section beyond it.
    b.resize(1136, 0);
    word(&mut b, 8, 1136);
    word(&mut b, 96, 1120);
    word(&mut b, 1088 + 4, 1);
    word(&mut b, 1088 + 8, 2 | 4 | 16);
    word(&mut b, 1088 + 12, 5);
    word(&mut b, 1088 + 24, 3);
    word(&mut b, 1120, 44);
    b
}

#[test]
fn initialise_preserves_inputs_and_seeds_each_overlapping_emitter() {
    let b = packet();
    let before = b.clone();
    let input = Prepared::parse(&b).unwrap();
    let layout = Layout::plan(&input).unwrap();
    let mut state = vec![0xff; layout.state_bytes as usize + 16];
    layout.initialise(&input, &mut state).unwrap();
    assert_eq!(b, before);
    assert!(
        state[..layout.emitter_state as usize]
            .iter()
            .all(|&v| v == 0)
    );
    for (i, off) in [7i32, 11].into_iter().enumerate() {
        let at = layout.emitter_state as usize + i * 16;
        assert_eq!(&state[at..at + 8], &[0; 8]);
        assert_eq!(&state[at + 8..at + 12], &off.to_le_bytes());
        assert_eq!(&state[at + 12..at + 16], &(100 + i as u32).to_le_bytes());
    }
    assert!(
        state[layout.probe_totals as usize..layout.state_bytes as usize]
            .iter()
            .all(|&v| v == 0)
    );
    assert_eq!(&state[layout.state_bytes as usize..], &[0xff; 16]);
    state[0] = 55;
    state[layout.field0 as usize] = 99;
    layout.initialise(&input, &mut state).unwrap();
    assert_eq!(state[0], 0);
    assert_eq!(state[layout.field0 as usize], 0);
    let mut descriptor = [0xcc; 80];
    layout.write_descriptor(&mut descriptor).unwrap();
    assert_eq!(&descriptor[..4], b"KLAY");
    assert_eq!(&descriptor[64..], &[0xcc; 16]);
    let offsets = [
        layout.field0,
        layout.field1,
        layout.intensity_stage1,
        layout.intensity_stage2,
        layout.intensity_output,
        layout.emitter_state,
        layout.probe_totals,
    ];
    assert!(offsets.windows(2).all(|w| w[0] < w[1]));
    assert!(offsets.iter().all(|offset| offset % 16 == 0));
}

#[test]
fn invalid_packets_are_rejected_before_initialisation() {
    assert_eq!(Prepared::parse(&[]).unwrap_err(), Status::InvalidPackage);
    for (at, value, expected) in [
        (4, 2, Status::UnsupportedVersion),
        (80, 161, Status::InvalidLayout),
        (84, 160, Status::InvalidLayout),
        (40, u32::MAX, Status::InvalidLayout),
        (68, u32::MAX, Status::TooLarge),
        (96, u32::MAX, Status::InvalidLayout),
        (44, 12, Status::InvalidLayout),
        (56, 0, Status::InvalidLayout),
        (960, 0, Status::InvalidValue),
        (960 + 12, 0, Status::InvalidValue),
        (1088 + 4, 2, Status::InvalidValue),
        (1120, 0, Status::InvalidValue),
    ] {
        let mut b = packet();
        word(&mut b, at, value);
        assert_eq!(Prepared::parse(&b).unwrap_err(), expected, "offset {at}");
    }
    for (at, value) in [(160, 1.0), (108, f32::NAN), (160 + 44 * 4, f32::INFINITY)] {
        let mut b = packet();
        float(&mut b, at, value);
        assert_eq!(Prepared::parse(&b).unwrap_err(), Status::InvalidValue);
    }
    let b = packet();
    for len in [1, 159, 160, b.len() - 1] {
        assert!(Prepared::parse(&b[..len]).is_err());
    }
}

#[test]
fn insufficient_output_is_unchanged() {
    let b = packet();
    let input = Prepared::parse(&b).unwrap();
    let layout = Layout::plan(&input).unwrap();
    let mut output = vec![0xab; layout.state_bytes as usize - 1];
    assert_eq!(
        layout.initialise(&input, &mut output),
        Err(Status::InsufficientCapacity)
    );
    assert!(output.iter().all(|&v| v == 0xab));
    let mut descriptor = [0xab; 63];
    assert_eq!(
        layout.write_descriptor(&mut descriptor),
        Err(Status::InsufficientCapacity)
    );
    assert_eq!(descriptor, [0xab; 63]);
}

fn half_value(bits: u16) -> f32 {
    let sign = if bits & 0x8000 == 0 { 1.0 } else { -1.0 };
    let exponent = (bits >> 10) & 31;
    let fraction = bits & 1023;
    if exponent == 0 {
        sign * f32::from(fraction) * 2.0_f32.powi(-24)
    } else {
        sign * (1.0 + f32::from(fraction) / 1024.0) * 2.0_f32.powi(i32::from(exponent) - 15)
    }
}

#[test]
fn half_conversion_covers_every_finite_half_and_rounding_midpoint() {
    use crate::observations::to_half;
    for bits in 0..=u16::MAX {
        if bits & 0x7c00 != 0x7c00 {
            assert_eq!(to_half(half_value(bits)), bits, "half {bits:x}");
        }
    }
    for bits in 0..0x7bff_u16 {
        let midpoint = (half_value(bits) + half_value(bits + 1)) * 0.5;
        let even = bits + (bits & 1);
        assert_eq!(to_half(midpoint), even);
        assert_eq!(to_half(-midpoint), even | 0x8000);
        assert_eq!(to_half(midpoint.next_down()), bits);
        assert_eq!(to_half(midpoint.next_up()), bits + 1);
    }
    assert_eq!(to_half(65519.0), 0x7bff);
    assert_eq!(to_half(65520.0), 0x7c00);
    assert_eq!(to_half(f32::INFINITY), 0x7c00);
    assert_eq!(to_half(f32::NEG_INFINITY), 0xfc00);
    assert_eq!(to_half(f32::NAN) & 0x7e00, 0x7e00);
}

#[test]
fn row_bands_match_whole_grid_with_tails_tiny_grids_and_both_stencils() {
    use crate::solver::{Band, Grid, Parameters, step_rows};
    for width in 1..=13 {
        for height in 1..=7 {
            for fourth_order in [false, true] {
                for track in [false, true] {
                    let stride = width + 2;
                    let cells = stride * (height + 2);
                    let p = Parameters {
                        width,
                        height,
                        stride,
                        fourth_order,
                        multiplier: 1.0 / 9.0,
                        alpha: 0.93,
                        scale: 1.7,
                        blend: 0.5,
                        edge_speed: 0.562_138_8,
                    };
                    let current: Vec<f32> =
                        (0..cells).map(|i| (i as f32 * 0.21).sin() * 0.1).collect();
                    let c2: Vec<f32> = (0..cells).map(|i| 0.1 + (i % 9) as f32 * 0.09).collect();
                    let damping = vec![0.97; cells];
                    let initial = [
                        vec![0.003; cells],
                        vec![0.02; cells],
                        vec![0.01; cells],
                        vec![0.025; cells],
                    ];
                    let mut whole = initial.clone();
                    let [prev, s1, s2, output] = &mut whole;
                    let rows = stride..(height + 1) * stride;
                    step_rows(
                        p,
                        Grid {
                            current: &current,
                            c2: &c2,
                            damping: &damping,
                        },
                        Band {
                            previous: &mut prev[rows.clone()],
                            stage1: &mut s1[rows.clone()],
                            stage2: &mut s2[rows.clone()],
                            intensity: &mut output[rows.clone()],
                        },
                        0..height,
                        track,
                    );
                    for band_height in [1, 2, 3] {
                        let mut split = initial.clone();
                        let [prev, s1, s2, output] = &mut split;
                        for start in (0..height).step_by(band_height) {
                            let end = (start + band_height).min(height);
                            let rows = (start + 1) * stride..(end + 1) * stride;
                            step_rows(
                                p,
                                Grid {
                                    current: &current,
                                    c2: &c2,
                                    damping: &damping,
                                },
                                Band {
                                    previous: &mut prev[rows.clone()],
                                    stage1: &mut s1[rows.clone()],
                                    stage2: &mut s2[rows.clone()],
                                    intensity: &mut output[rows.clone()],
                                },
                                start..end,
                                track,
                            );
                        }
                        assert_eq!(
                            whole, split,
                            "{width}x{height} fourth={fourth_order} track={track} band={band_height}"
                        );
                    }
                    for plane in 0..4 {
                        assert_eq!(&whole[plane][..stride], &initial[plane][..stride]);
                        assert_eq!(
                            &whole[plane][(height + 1) * stride..],
                            &initial[plane][(height + 1) * stride..]
                        );
                        for y in 1..=height {
                            assert_eq!(whole[plane][y * stride], initial[plane][y * stride]);
                            assert_eq!(
                                whole[plane][y * stride + width + 1],
                                initial[plane][y * stride + width + 1]
                            );
                        }
                    }
                    if !track {
                        assert_eq!(&whole[1..], &initial[1..]);
                    }
                }
            }
        }
    }
}

#[test]
fn complete_steps_preserve_batching_and_reject_invalid_calls_without_writes() {
    use crate::{observations, solver};
    let b = packet();
    let input = Prepared::parse(&b).unwrap();
    let layout = Layout::plan(&input).unwrap();
    let mut whole = vec![0; layout.state_bytes as usize];
    layout.initialise(&input, &mut whole).unwrap();
    let mut split = whole.clone();
    let mut expected_samples = vec![0.0; 9];
    solver::advance(&input, &mut whole, 9, &mut expected_samples).unwrap();
    let mut samples = Vec::new();
    for steps in [1, 3, 5] {
        let mut batch = vec![0.0; steps as usize];
        solver::advance(&input, &mut split, steps, &mut batch).unwrap();
        samples.extend(batch);
    }
    assert_eq!(samples, expected_samples);
    assert_eq!(split, whole);
    let before = whole.clone();
    assert_eq!(
        solver::advance(&input, &mut whole, 1, &mut [99.0]),
        Err(Status::InvalidState)
    );
    assert_eq!(whole, before);
    layout.initialise(&input, &mut whole).unwrap();
    let before = whole.clone();
    assert_eq!(
        solver::advance(&input, &mut whole, 2, &mut [99.0]),
        Err(Status::InsufficientCapacity)
    );
    assert_eq!(whole, before);
    let mut frame = [0xab; 3];
    assert_eq!(
        observations::capture(&input, &whole, &mut frame),
        Err(Status::InsufficientCapacity)
    );
    assert_eq!(frame, [0xab; 3]);
    whole[4] = 2;
    let before = whole.clone();
    assert_eq!(
        solver::advance(&input, &mut whole, 1, &mut [99.0]),
        Err(Status::InvalidState)
    );
    assert_eq!(whole, before);
}
