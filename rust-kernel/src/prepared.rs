// SPDX-License-Identifier: GPL-3.0-or-later
// Derived from WaveDudeNext.Wasm/Prepare.cs; see PREPARE.md for the wire contract.

//! Borrowed, validated WDPK v1 input. Parsing does not allocate or modify the bytes.

use crate::Status;

pub const HEADER_BYTES: usize = 160;
pub const EMITTER_BYTES: usize = 64;
pub const PROBE_BYTES: usize = 32;

#[derive(Clone, Copy, Debug)]
pub struct Header {
    pub canvas_width: u32,
    pub canvas_height: u32,
    pub grid_width: u32,
    pub grid_height: u32,
    pub rim: u32,
    pub stride: u32,
    pub cells: u32,
    pub flags: u32,
    pub substeps: u32,
    pub requested_steps: u32,
    pub interval: u32,
    pub frames: u32,
    pub last_step: u32,
    pub emitter_count: u32,
    pub probe_count: u32,
    pub pixel_count: u32,
    pub c2_offset: u32,
    pub damping_offset: u32,
    pub emitters_offset: u32,
    pub probes_offset: u32,
    pub pixels_offset: u32,
}

/// This value borrows its packet. The caller must keep it immutable while in use.
#[derive(Debug)]
pub struct Prepared<'a> {
    pub header: Header,
    bytes: &'a [u8],
}

impl<'a> Prepared<'a> {
    pub fn parse(bytes: &'a [u8]) -> Result<Self, Status> {
        if bytes.len() < HEADER_BYTES || bytes.len() > i32::MAX as usize || &bytes[..4] != b"WDPK" {
            return Err(Status::InvalidPackage);
        }
        if u32_at(bytes, 4) != 1 {
            return Err(Status::UnsupportedVersion);
        }
        if u32_at(bytes, 8) as usize != bytes.len()
            || u32_at(bytes, 12) != HEADER_BYTES as u32
            || bytes.len() % 16 != 0
            || u32_at(bytes, 144) != EMITTER_BYTES as u32
            || u32_at(bytes, 148) != PROBE_BYTES as u32
            || u32_at(bytes, 152) != 30
            || u32_at(bytes, 100) != 0
            || u32_at(bytes, 156) != 0
        {
            return Err(Status::InvalidLayout);
        }
        let h = Header {
            canvas_width: u32_at(bytes, 16),
            canvas_height: u32_at(bytes, 20),
            grid_width: u32_at(bytes, 24),
            grid_height: u32_at(bytes, 28),
            rim: u32_at(bytes, 32),
            stride: u32_at(bytes, 36),
            cells: u32_at(bytes, 40),
            flags: u32_at(bytes, 44),
            substeps: u32_at(bytes, 48),
            requested_steps: u32_at(bytes, 52),
            interval: u32_at(bytes, 56),
            frames: u32_at(bytes, 60),
            last_step: u32_at(bytes, 64),
            emitter_count: u32_at(bytes, 68),
            probe_count: u32_at(bytes, 72),
            pixel_count: u32_at(bytes, 76),
            c2_offset: u32_at(bytes, 80),
            damping_offset: u32_at(bytes, 84),
            emitters_offset: u32_at(bytes, 88),
            probes_offset: u32_at(bytes, 92),
            pixels_offset: u32_at(bytes, 96),
        };
        if h.canvas_width == 0
            || h.canvas_height == 0
            || h.flags & !7 != 0
            || h.flags & 4 == 0
            || h.substeps == 0
            || h.requested_steps == 0
            || h.interval == 0
            || u64::from(h.canvas_width) + 2 * u64::from(h.rim) != u64::from(h.grid_width)
            || u64::from(h.canvas_height) + 2 * u64::from(h.rim) != u64::from(h.grid_height)
            || u64::from(h.grid_width) + 2 != u64::from(h.stride)
            || u64::from(h.stride) * (u64::from(h.grid_height) + 2) != u64::from(h.cells)
        {
            return Err(Status::InvalidLayout);
        }
        let frames = (h.requested_steps - 1) / h.interval + 1;
        if h.frames != frames
            || u64::from(h.last_step) != u64::from(frames - 1) * u64::from(h.interval) + 1
        {
            return Err(Status::InvalidLayout);
        }
        let mut end = HEADER_BYTES;
        for (offset, count, size) in [
            (h.c2_offset, h.cells, 4),
            (h.damping_offset, h.cells, 4),
            (h.emitters_offset, h.emitter_count, EMITTER_BYTES),
            (h.probes_offset, h.probe_count, PROBE_BYTES),
            (h.pixels_offset, h.pixel_count, 4),
        ] {
            let offset = offset as usize;
            // Use fixed-width arithmetic so native checks and wasm32 return the same status.
            let length = u64::from(count) * size as u64;
            if length > i32::MAX as u64 {
                return Err(Status::TooLarge);
            }
            let next = offset as u64 + length;
            if offset % 16 != 0
                || offset < end
                || next > bytes.len() as u64
                || bytes[end..offset].iter().any(|&v| v != 0)
            {
                return Err(Status::InvalidLayout);
            }
            end = next as usize;
        }
        if bytes[end..].iter().any(|&v| v != 0) {
            return Err(Status::InvalidLayout);
        }
        let result = Self { header: h, bytes };
        result.validate_values()?;
        Ok(result)
    }

    pub fn bytes(&self) -> &'a [u8] {
        self.bytes
    }

    /// Record bytes preserve their original source/element order, including overlaps.
    pub fn emitters(&self) -> impl Iterator<Item = &'a [u8]> {
        let start = self.header.emitters_offset as usize;
        self.bytes[start..start + self.header.emitter_count as usize * EMITTER_BYTES]
            .chunks_exact(EMITTER_BYTES)
    }

    fn canvas_index(&self, index: u32) -> bool {
        let h = self.header;
        let x = index % h.stride;
        let y = index / h.stride;
        x > h.rim && x <= h.rim + h.canvas_width && y > h.rim && y <= h.rim + h.canvas_height
    }

    fn validate_values(&self) -> Result<(), Status> {
        let b = self.bytes;
        let h = self.header;
        for at in [104, 108, 112, 116, 120, 124] {
            let value = f32_at(b, at);
            if !value.is_finite() || value < 0.0 {
                return Err(Status::InvalidValue);
            }
        }
        if f32_at(b, 104) <= 0.0
            || f32_at(b, 104) > 1.0
            || f32_at(b, 108) > 1.0
            || f32_at(b, 112) <= 0.0
            || f32_at(b, 116) > 1.0
            || f32_at(b, 120) > 1.0
            || f32_at(b, 124) <= 0.0
            || !f64_at(b, 128).is_finite()
            || f64_at(b, 128) < 0.0
            || !f64_at(b, 136).is_finite()
            || f64_at(b, 136) < 1.0
        {
            return Err(Status::InvalidValue);
        }
        for start in [h.c2_offset, h.damping_offset] {
            for i in 0..h.cells {
                let value = f32_at(b, start as usize + i as usize * 4);
                let x = i % h.stride;
                let y = i / h.stride;
                let ring = x == 0 || x == h.stride - 1 || y == 0 || y == h.grid_height + 1;
                if !value.is_finite() || !(0.0..=1.0).contains(&value) || (ring && value != 0.0) {
                    return Err(Status::InvalidValue);
                }
            }
        }
        for e in self.emitters() {
            if !self.canvas_index(u32_at(e, 0))
                || u32_at(e, 4) > 1
                || u32_at(e, 8) > 1
                || u32_at(e, 12) == 0
                || u32_at(e, 36) != 0
                || i32_at(e, 32) < -1
                || [16, 20, 24].iter().any(|&at| i32_at(e, at) < 1)
                || i32_at(e, 28) < 0
                || !f64_at(e, 40).is_finite()
                || f64_at(e, 40) < 0.0
                || !f64_at(e, 48).is_finite()
                || !(0.0..=1.0).contains(&f64_at(e, 48))
                || !f64_at(e, 56).is_finite()
            {
                return Err(Status::InvalidValue);
            }
        }
        let mut pixel_end = 0u64;
        let mut previous_project_index = None;
        for i in 0..h.probe_count as usize {
            let at = h.probes_offset as usize + i * PROBE_BYTES;
            let count = u32_at(b, at + 4);
            let flags = u32_at(b, at + 8);
            let line = flags & 1 != 0;
            let project_index = u32_at(b, at + 24);
            if u64::from(u32_at(b, at)) != pixel_end
                || count == 0
                || flags & !31 != 0
                || (!line && count != 1)
                || (flags & 4 != 0 && (line || flags & 2 == 0))
                || (flags & 8 != 0 && (!line || flags & 2 == 0))
                || i32_at(b, at + 12) < 0
                || (flags & 16 != 0) != (i32_at(b, at + 12) > 0)
                || !f64_at(b, at + 16).is_finite()
                || f64_at(b, at + 16) < 0.0
                || (!line && f64_at(b, at + 16) != 0.0)
                || u32_at(b, at + 28) != 0
                || previous_project_index.is_some_and(|previous| project_index <= previous)
            {
                return Err(Status::InvalidValue);
            }
            previous_project_index = Some(project_index);
            pixel_end += u64::from(count);
            if pixel_end > u64::from(h.pixel_count) {
                return Err(Status::InvalidLayout);
            }
        }
        if pixel_end != u64::from(h.pixel_count) {
            return Err(Status::InvalidLayout);
        }
        for i in 0..h.pixel_count as usize {
            if !self.canvas_index(u32_at(b, h.pixels_offset as usize + i * 4)) {
                return Err(Status::InvalidValue);
            }
        }
        Ok(())
    }
}

pub(crate) fn u32_at(b: &[u8], at: usize) -> u32 {
    u32::from_le_bytes(b[at..at + 4].try_into().unwrap())
}
pub(crate) fn i32_at(b: &[u8], at: usize) -> i32 {
    i32::from_le_bytes(b[at..at + 4].try_into().unwrap())
}
fn f32_at(b: &[u8], at: usize) -> f32 {
    f32::from_bits(u32_at(b, at))
}
fn f64_at(b: &[u8], at: usize) -> f64 {
    f64::from_le_bytes(b[at..at + 8].try_into().unwrap())
}
