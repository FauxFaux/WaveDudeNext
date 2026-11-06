// SPDX-License-Identifier: GPL-3.0-or-later

//! Versioned working storage, independent of the immutable preparation package.

use crate::{
    Status,
    prepared::{Prepared, i32_at, u32_at},
};

pub const DESCRIPTOR_BYTES: usize = 64;
pub const ALIGNMENT: u32 = 16;
pub const STATE_HEADER_BYTES: u32 = 16;
pub const EMITTER_STATE_BYTES: u32 = 16;

/// All offsets are bytes relative to the working-state base, never package pointers.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Layout {
    pub state_bytes: u32,
    pub field0: u32,
    pub field1: u32,
    pub intensity_stage1: u32,
    pub intensity_stage2: u32,
    pub intensity_output: u32,
    pub emitter_state: u32,
    pub probe_totals: u32,
    pub cells: u32,
    pub emitter_count: u32,
    pub pixel_count: u32,
}

impl Layout {
    pub fn plan(input: &Prepared<'_>) -> Result<Self, Status> {
        let h = input.header;
        let mut end = STATE_HEADER_BYTES;
        let mut reserve = |count: u32, size: u32| -> Result<u32, Status> {
            let start = end;
            end = end
                .checked_add(count.checked_mul(size).ok_or(Status::TooLarge)?)
                .and_then(|n| n.checked_add(15))
                .map(|n| n & !15)
                .ok_or(Status::TooLarge)?;
            // Rust slices must fit isize; the C# package also uses signed lengths.
            if end > i32::MAX as u32 {
                return Err(Status::TooLarge);
            }
            Ok(start)
        };
        let field0 = reserve(h.cells, 4)?;
        let field1 = reserve(h.cells, 4)?;
        let intensity_stage1 = reserve(h.cells, 4)?;
        let intensity_stage2 = reserve(h.cells, 4)?;
        let intensity_output = reserve(h.cells, 4)?;
        let emitter_state = reserve(h.emitter_count, EMITTER_STATE_BYTES)?;
        let probe_totals = reserve(h.pixel_count, 8)?;
        Ok(Self {
            state_bytes: end,
            field0,
            field1,
            intensity_stage1,
            intensity_stage2,
            intensity_output,
            emitter_state,
            probe_totals,
            cells: h.cells,
            emitter_count: h.emitter_count,
            pixel_count: h.pixel_count,
        })
    }

    pub fn write_descriptor(&self, output: &mut [u8]) -> Result<(), Status> {
        let output = output
            .get_mut(..DESCRIPTOR_BYTES)
            .ok_or(Status::InsufficientCapacity)?;
        let words = [
            u32::from_le_bytes(*b"KLAY"),
            1,
            DESCRIPTOR_BYTES as u32,
            self.state_bytes,
            ALIGNMENT,
            self.field0,
            self.field1,
            self.intensity_stage1,
            self.intensity_stage2,
            self.intensity_output,
            self.emitter_state,
            self.probe_totals,
            self.cells,
            self.emitter_count,
            self.pixel_count,
            0,
        ];
        for (word, target) in words.into_iter().zip(output.chunks_exact_mut(4)) {
            target.copy_from_slice(&word.to_le_bytes());
        }
        Ok(())
    }

    /// Initialise/reset all working data, including per-emitter mutable pulse state.
    /// The package must be the same input used to obtain this layout.
    pub fn initialise(&self, input: &Prepared<'_>, output: &mut [u8]) -> Result<(), Status> {
        if Self::plan(input)? != *self {
            return Err(Status::InvalidLayout);
        }
        let output = output
            .get_mut(..self.state_bytes as usize)
            .ok_or(Status::InsufficientCapacity)?;
        output.fill(0);
        for (i, emitter) in input.emitters().enumerate() {
            let at = self.emitter_state as usize + i * EMITTER_STATE_BYTES as usize;
            output[at + 8..at + 12].copy_from_slice(&i32_at(emitter, 20).to_le_bytes());
            output[at + 12..at + 16].copy_from_slice(&u32_at(emitter, 12).to_le_bytes());
        }
        Ok(())
    }
}
