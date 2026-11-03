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

using System.Collections.Immutable;
using NLog;
using WaveDudeNext.Engine;
using WaveDudeNext.Model;
using WaveDudeNext.Probes;

namespace WaveDudeNext.Runs;

/// <summary>A project cannot be run as it stands; the message is meant for the user.</summary>
public sealed class RunSpecException(string message) : Exception(message);

/// <summary>
/// Everything a run needs, snapshotted from the <see cref="Project"/> on the UI thread by <see cref="Create"/>
/// so the worker never touches the (UI-thread-only) model and later edits do not affect a run in progress.
/// Immutable; shared read-only by the worker and the UI.
/// </summary>
/// <remarks>
/// Frames and steps: the run records a frame after every <see cref="VisualizeEvery"/>-th step, starting with
/// the first, so frame k shows the state after 0-based step k × <see cref="VisualizeEvery"/> (the step index
/// the preview readout prints) and the run yields <see cref="FrameCount"/> frames in total.
/// </remarks>
public sealed class RunSpec
{
	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	private RunSpec(Project project, ScenePixels? image, int width, int height, bool trackIntensity, bool preferGpu, PreviewQuality quality)
	{
		Seed = project.Seed;
		Steps = project.Simulation.Steps;
		Quality = quality.Validate();
		VisualizeEvery = Math.Max(1, project.Simulation.VisualizeEvery);
		Shape = new FrameShape(width, height, quality.ResolutionDivisor, quality.Format, (float)project.Display.Field.Brightness, (float)project.Display.Intensity.Brightness, quality.IntensityRoot);
		PreferGpu = preferGpu;
		Engine = EngineOptions.From(project.Simulation, project.Scene, trackIntensity);
		Image = image;
		Width = width;
		Height = height;
		DampingEdge = project.Scene.DampingEdge;
		Edges = project.Scene.Edges;
		RefractiveIndex = project.Scene.RefractiveIndex;
		// a canvas scene with painted parts has pixels too, but no green to emit from (the parts never touch green)
		ImageSources = project.Scene.Mode == SceneMode.Image && image != null && project.Scene.ImageSources;
		Emitters = [.. Emitter.FromProject(project)];
		Probes = [.. project.Probes.Where(probe => probe.Active).Select(ProbeSpec.From)];
	}

	/// <summary>The project's random seed, for the record (the emitters already carry their derived seeds).</summary>
	public int Seed { get; }

	/// <summary>Number of simulation steps to run.</summary>
	public int Steps { get; }

	/// <summary>A frame is due every this many steps (at least 1): the project's interval, the timeline's unit whatever the <see cref="Quality"/>.</summary>
	public int VisualizeEvery { get; }

	/// <summary>Number of frames of the run on the timeline: ⌈<see cref="Steps"/> / <see cref="VisualizeEvery"/>⌉, the same for every quality.</summary>
	public int FrameCount => (Steps + VisualizeEvery - 1) / VisualizeEvery;

	/// <summary>How much of the run is kept: the application's preview quality, or <see cref="PreviewQuality.Full"/> for an export's run.</summary>
	public PreviewQuality Quality { get; }

	/// <summary>
	/// Only every this many-th frame is captured and stored (the quality's frame divisor, 1 for all of them); a
	/// stored frame stands in for the frames up to the next one, so the timeline does not change (the user's rule,
	/// 2026-10-15: the setting silently leaves frames out to save memory and time, nothing else).
	/// </summary>
	public int FrameStride => Quality.FrameDivisor;

	/// <summary>Steps between two stored frames: <see cref="VisualizeEvery"/> × <see cref="FrameStride"/>.</summary>
	public int CaptureEvery => VisualizeEvery * FrameStride;

	/// <summary>Number of frames actually stored: ⌈<see cref="FrameCount"/> / <see cref="FrameStride"/>⌉ (the store's capacity).</summary>
	public int StoredFrameCount => (FrameCount + FrameStride - 1) / FrameStride;

	/// <summary>Index in the store of the frame that shows timeline frame <paramref name="frame"/>: the last stored one at or before it.</summary>
	public int StoredIndexOf(int frame) => frame / FrameStride;

	/// <summary>The frames' geometry and format: the canvas shrunk by the quality's resolution divisor, in its sample format, with the display brightness of the moment for index frames.</summary>
	public FrameShape Shape { get; }

	/// <summary>Try the OpenCL engine first; an application setting, not part of the project.</summary>
	public bool PreferGpu { get; }

	/// <summary>The physics options the engine is created with.</summary>
	public EngineOptions Engine { get; }

	/// <summary>Whether the engine tracks the intensity, i.e. the frame store has an intensity plane.</summary>
	public bool TrackIntensity => Engine.TrackIntensity;

	/// <summary>The planes every frame of the store holds.</summary>
	public FrameLayers Layers => FrameLayers.Field | (TrackIntensity ? FrameLayers.Intensity : FrameLayers.None);

	/// <summary>The scene pixels (the decoded image, or a canvas with painted optical parts), or null for a blank canvas of <see cref="Width"/> × <see cref="Height"/>.</summary>
	public ScenePixels? Image { get; }

	/// <summary>Canvas width in pixels.</summary>
	public int Width { get; }

	/// <summary>Canvas height in pixels.</summary>
	public int Height { get; }

	/// <summary>Thickness of the absorbing border in pixels; 0 for none.</summary>
	public int DampingEdge { get; }

	/// <summary>What the grid's edge does with a wave (<see cref="Scene.Edges"/>).</summary>
	public EdgeMode Edges { get; }

	/// <summary>Cells the engine simulates beyond each side of the canvas: a damping edge outside the canvas (a negative <see cref="DampingEdge"/>), else 0.</summary>
	public int Rim => Math.Max(0, -DampingEdge);

	/// <summary>Background refractive index of a blank canvas.</summary>
	public double RefractiveIndex { get; }

	/// <summary>Whether the green channel of the scene image emits.</summary>
	public bool ImageSources { get; }

	/// <summary>The emitters of the enabled object sources (empty when the scene does not use them).</summary>
	public ImmutableArray<Emitter> Emitters { get; }

	/// <summary>The active probes, in project order.</summary>
	public ImmutableArray<ProbeSpec> Probes { get; }

	/// <summary>
	/// Snapshots <paramref name="project"/> for a run. Regenerates the source emitters first (the model only
	/// does that on load, reseed and add, not on edits). UI thread.
	/// </summary>
	/// <param name="project">The project to run; not modified beyond the emitter regeneration.</param>
	/// <param name="image">The scene pixels: the decoded image when <see cref="Scene.Mode"/> is <see cref="SceneMode.Image"/>, with the optical parts painted in; for a canvas scene, the parts painted on a blank canvas of the scene's size, or null without parts (a canvas of another size is ignored).</param>
	/// <param name="preferGpu">Try the OpenCL engine first (the application's choice; the CPU is the fallback either way).</param>
	/// <param name="trackIntensity">Store the intensity plane even when nothing in the project needs it (an export of the intensity asks for it).</param>
	/// <param name="quality">How much of the run to keep (the application's preview setting); null is <see cref="PreviewQuality.Full"/>.</param>
	/// <exception cref="RunSpecException">The project cannot run: no image for an image scene, or no steps.</exception>
	public static RunSpec Create(Project project, ScenePixels? image, bool preferGpu = true, bool trackIntensity = false, PreviewQuality? quality = null)
	{
		project.Normalize();
		if (project.Simulation.Steps < 1)
		{
			throw new RunSpecException("The simulation has no steps to run.");
		}
		int width;
		int height;
		if (project.Scene.Mode == SceneMode.Image)
		{
			if (image == null)
			{
				throw new RunSpecException("The scene uses an image, but no image is loaded.");
			}
			width = image.Width;
			height = image.Height;
			if (project.Scene.ImageSources && SceneData.CountSources(image.Bgra, width, height, image.StrideBytes) == 0)
			{
				Logger.Warn("Image sources are enabled, but the scene image has no green pixels.");
			}
		}
		else
		{
			width = project.Scene.Width;
			height = project.Scene.Height;
			if (image != null && (image.Width != width || image.Height != height))
			{
				image = null;
			}
		}
		trackIntensity |= project.Display.Intensity.Preview
			|| project.Probes.Any(probe => probe.Active && probe.Measurement != ProbeMeasurement.Amplitude);
		return new RunSpec(project, image, width, height, trackIntensity, preferGpu, quality ?? PreviewQuality.Full);
	}

	/// <summary>
	/// Whether an engine built for <paramref name="other"/> serves this spec too: the same canvas (size, image,
	/// damping, refractive index, image sources), the same solver options and engine choice. The emitters may differ.
	/// </summary>
	public bool SameSceneAs(RunSpec other) =>
		Width == other.Width && Height == other.Height && ReferenceEquals(Image, other.Image) && DampingEdge == other.DampingEdge && Edges == other.Edges
		&& RefractiveIndex == other.RefractiveIndex && ImageSources == other.ImageSources && Engine == other.Engine && PreferGpu == other.PreferGpu;

	/// <summary>Whether the object sources are the same emitters, value for value.</summary>
	public bool SameEmittersAs(RunSpec other) => Emitters.AsSpan().SequenceEqual(other.Emitters.AsSpan());

	/// <summary>Whether the engine built for <paramref name="other"/> can take this spec's scene through <see cref="Engine.IWaveEngine.TryReplaceScene"/>: the same grid, edges, options and engine choice, the scene's pixels or background aside.</summary>
	public bool SameGridAs(RunSpec other) =>
		Width == other.Width && Height == other.Height && DampingEdge == other.DampingEdge && Edges == other.Edges && Engine == other.Engine && PreferGpu == other.PreferGpu;

	/// <summary>Builds the engine's scene input from the snapshot. Worker; allocates the per-pixel maps.</summary>
	internal SceneData BuildScene() => Image is { } image
		? SceneData.FromBgra(image.Bgra, image.Width, image.Height, image.StrideBytes, ImageSources, DampingEdge, Edges == EdgeMode.Open)
		: SceneData.Empty(Width, Height, DampingEdge, RefractiveIndex, Edges == EdgeMode.Open);
}
