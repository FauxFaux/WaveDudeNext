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

using WaveDudeNext.Probes;

namespace WaveDudeNext.Engine;

/// <summary>
/// A wave-field solver (CPU <see cref="WaveSimulator"/> or OpenCL <see cref="GpuWaveSimulator"/>), created by
/// <see cref="WaveEngineFactory"/> and driven by the run on its worker task. The field and intensity buffers
/// are padded by one cell on each side of the grid, and the grid may carry a rim of <see cref="Rim"/> cells around the
/// canvas (<see cref="SceneData.Rim"/>), so canvas row y starts at index (y + 1 + Rim) * Stride + 1 + Rim.
/// Exactly one thread drives an engine at a time and nothing else reads it; the engines have no internal
/// synchronisation and no thread affinity. Implementations that own native resources are <see cref="IDisposable"/>;
/// the interface deliberately is not, the driver disposes through <c>(engine as IDisposable)?.Dispose()</c>.
/// </summary>
public interface IWaveEngine
{
	/// <summary>Where the simulation runs, for the status bar and result: for example "CPU (8 threads)" or "GPU: NVIDIA GeForce MX130 (OpenCL)".</summary>
	string Description { get; }

	/// <summary>Scene width in cells (= canvas pixels), without padding.</summary>
	int Width { get; }

	/// <summary>Scene height in cells (= canvas pixels), without padding.</summary>
	int Height { get; }

	/// <summary>Row pitch of <see cref="FieldData"/> and <see cref="IntensityData"/>: <see cref="Width"/> + 2 + 2 × <see cref="Rim"/>. Canvas pixel (x, y) is at (y + 1 + Rim) * Stride + x + 1 + Rim.</summary>
	int Stride { get; }

	/// <summary>Cells simulated beyond each side of the canvas (a damping edge outside it); 0 when the grid is the canvas.</summary>
	int Rim { get; }

	/// <summary>
	/// The field of the last completed step, <see cref="Stride"/> * (<see cref="Height"/> + 2 + 2 × <see cref="Rim"/>) floats. Valid for
	/// reading after <see cref="PrepareFrame"/>(true, ...); the CPU engine exposes its live buffer (a different
	/// array instance after every sub-step, so re-read the property per frame), the GPU engine a host copy.
	/// Do not change.
	/// </summary>
	float[] FieldData { get; }

	/// <summary>
	/// The averaged intensity, same layout as <see cref="FieldData"/>, valid after <see cref="PrepareFrame"/>(..., true);
	/// null when intensity tracking was not requested. Do not change.
	/// </summary>
	float[]? IntensityData { get; }

	/// <summary>One time step: apply the sources, update the field (internally several sub-steps with the 4th-order Laplacian), update the intensity.</summary>
	void AdvanceOneStep();

	/// <summary>
	/// Makes the requested buffers reflect the current state so the renderer can read them: a no-op on the CPU;
	/// on the GPU the data is copied from the graphics memory, so only ask for what is going to be drawn. Call
	/// before touching <see cref="FieldData"/> / <see cref="IntensityData"/>.
	/// </summary>
	void PrepareFrame(bool needField, bool needIntensity);

	/// <summary>
	/// A snapshot for <see cref="CaptureFrame"/> in the memory this engine fills fastest. The GPU engine's planes are
	/// page-locked host memory (<c>CL_MEM_ALLOC_HOST_PTR</c> buffers, kept mapped) the card writes by DMA while the
	/// stepping thread goes on; into an ordinary snapshot's arrays, pageable to the driver, its "non-blocking" read
	/// drains the queue and copies on the calling thread (2.96 against 1.91 ms a full-HD frame, both planes as halves,
	/// RTX 2080, 2026-11-03). The CPU engine returns an ordinary snapshot. Engine-owned planes are released with the
	/// engine and throw afterwards, so a snapshot that outlives its engine, the scratch runner's result, is made with
	/// the constructor instead.
	/// </summary>
	/// <param name="shape">The capture's geometry and format; its canvas must be this engine's.</param>
	/// <param name="withIntensity">Whether the snapshot gets an intensity plane.</param>
	FrameSnapshot CreateSnapshot(FrameShape shape, bool withIntensity);

	/// <summary>
	/// Copies the newest field (and the intensity when asked and tracked) into <paramref name="snapshot"/> as
	/// planes of its <see cref="FrameShape"/>: the padding stripped, the canvas shrunk by the shape's divisor,
	/// the samples as halves or palette indices (<see cref="FrameConversion"/> is the reference). The CPU engine
	/// copies the floats now and converts in <see cref="AwaitFrame"/>; the GPU engine converts on the card and
	/// enqueues the reads without waiting, so the driver can keep stepping while they land into a snapshot of
	/// <see cref="CreateSnapshot"/> (an ordinary snapshot's read stalls the stepping thread for the copy). The snapshot
	/// must not be touched until <see cref="AwaitFrame"/> returned, and a capture must be awaited before the engine is disposed.
	/// </summary>
	/// <param name="snapshot">A snapshot whose shape has this engine's canvas; its planes receive the data.</param>
	/// <param name="needIntensity">Also copy the intensity (needs tracking and a snapshot with an intensity plane).</param>
	void CaptureFrame(FrameSnapshot snapshot, bool needIntensity);

	/// <summary>
	/// Blocks until a <see cref="CaptureFrame"/> into <paramref name="snapshot"/> has completed. May be called
	/// from a thread other than the driver's (a writer pipeline); a no-op for a snapshot not in flight.
	/// </summary>
	void AwaitFrame(FrameSnapshot snapshot);

	/// <summary>
	/// Back to step 0: the field, the previous field, the intensity averages, the emitters' cycle state and
	/// the elapsed time are cleared; the scene, the emitters and the options stay. The probes are removed
	/// (install them again when needed). Any capture in flight must have been awaited.
	/// </summary>
	void Reset();

	/// <summary>
	/// Swaps in another scene of the same grid (size, rim, edges) with the same number of image sources: the per-cell
	/// maps and the image sources are replaced, the sub-steps re-derived, and the engine is <see cref="Reset"/> (the
	/// emitters and options kept; the probes removed as by Reset). The GPU engine keeps its compiled program and buffers,
	/// which saves the 180–350 ms build a scene edit cost the preview (2026-11-03). False, and nothing changed, when the
	/// scene differs in a way that needs a new engine. Any capture in flight must have been awaited.
	/// </summary>
	/// <param name="scene">The new scene.</param>
	bool TryReplaceScene(SceneData scene);

	/// <summary>
	/// Replaces the object-source emitters, keeping the scene and the image sources; emitters outside the
	/// canvas are dropped as at construction. Takes effect from the next step; the emitters' cycle state
	/// starts fresh, so call it before <see cref="Reset"/> or at step 0. The intensity averaging constant
	/// follows the new lowest source level.
	/// </summary>
	void SetEmitters(IReadOnlyList<Emitter>? emitters);

	/// <summary>
	/// Installs the probe definitions for this run (the GPU engine compiles its probe kernel here).
	/// A null or empty sampler installs nothing.
	/// </summary>
	/// <exception cref="InvalidOperationException">A probe needs the intensity but it is not tracked, or the GPU probe kernel cannot be built.</exception>
	void SetProbes(ProbeSampler? sampler);

	/// <summary>
	/// Measures the probes in the state after the last <see cref="AdvanceOneStep"/>: one value per step for every
	/// point probe, and the running totals of the line probes that integrate the intensity.
	/// </summary>
	void SampleProbes();

	/// <summary>
	/// Returns and clears the values measured for the point probes since the last call, one array per probe (empty for line probes).
	/// </summary>
	/// <returns>Empty outer array when no probes are installed.</returns>
	float[][] TakeProbeSamples();

	/// <summary>
	/// The values along the line of every line probe in the current state: the field, the intensity, or the
	/// intensity totals of the steps so far. On the GPU this waits for the card, so do not call it every step.
	/// </summary>
	/// <returns>Null entries for point probes; empty outer array when no probes are installed.</returns>
	float[]?[] ReadProbeProfiles();
}
