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

using System.Runtime.InteropServices;
using WaveDudeNext.Engine;
using WaveDudeNext.Rendering;

namespace WaveDudeNext.Runs;

/// <summary>
/// Something a frame can be read from for display: the <see cref="FrameStore"/> of a run, or a single
/// captured <see cref="FrameSnapshot"/>. Frames are planes of <see cref="Width"/> × <see cref="Height"/>
/// samples in <see cref="Format"/>, shrunk from a canvas of <see cref="CanvasWidth"/> × <see cref="CanvasHeight"/>
/// (the coordinate system of sources and probes). Readers call <see cref="CopyLayer(int, FrameLayer, Span{byte})"/>
/// from any thread.
/// </summary>
public interface IFrameSource
{
	/// <summary>The frames' geometry and format; the properties below read it.</summary>
	FrameShape Shape { get; }

	/// <summary>Frame width in samples.</summary>
	int Width => Shape.Width;

	/// <summary>Frame height in samples.</summary>
	int Height => Shape.Height;

	/// <summary>Width of the simulated canvas the frames were shrunk from, in cells; equals <see cref="Width"/> at full resolution.</summary>
	int CanvasWidth => Shape.CanvasWidth;

	/// <summary>Height of the simulated canvas, in cells.</summary>
	int CanvasHeight => Shape.CanvasHeight;

	/// <summary>The sample format of every plane.</summary>
	FrameFormat Format => Shape.Format;

	/// <summary>Whether a <see cref="FrameFormat.Half"/> intensity plane holds the intensity's square root, the preview's, rather than the intensity (<see cref="FrameShape.IntensityRoot"/>).</summary>
	bool IntensityRoot => Shape.IntensityRoot;

	/// <summary>Frames available: indices 0..Count-1 may be read.</summary>
	int Count { get; }

	/// <summary>Whether the frames hold the given plane.</summary>
	bool Has(FrameLayer layer);

	/// <summary>Reads one plane of a frame as bytes (<see cref="Width"/> × <see cref="Height"/> samples of <see cref="Format"/>) into <paramref name="destination"/>; false when the source is gone.</summary>
	bool CopyLayer(int frame, FrameLayer layer, Span<byte> destination);

	/// <summary>Reads one plane of a frame as <see cref="Half"/> values into <paramref name="destination"/>; false when the source is gone.</summary>
	/// <exception cref="InvalidOperationException">The samples are not halves (<see cref="Format"/>).</exception>
	bool CopyLayer(int frame, FrameLayer layer, Span<Half> destination);
}

/// <summary>What the planes of a frame source hold, for the renderer and the brightness fit.</summary>
public static class FrameSources
{
	/// <summary>The kind of the 16-bit samples of <paramref name="layer"/>: the field, the intensity, or its square root.</summary>
	public static SampleKind KindOf(this IFrameSource source, FrameLayer layer) =>
		layer == FrameLayer.Field ? SampleKind.Field : source.IntensityRoot ? SampleKind.IntensityRoot : SampleKind.Intensity;
}

/// <summary>A single captured frame as a one-frame <see cref="IFrameSource"/>; every frame index reads the same snapshot.</summary>
public sealed class SnapshotFrameSource(FrameSnapshot snapshot) : IFrameSource
{
	/// <summary>The snapshot read.</summary>
	public FrameSnapshot Snapshot { get; } = snapshot;

	/// <inheritdoc/>
	public FrameShape Shape => Snapshot.Shape;

	/// <inheritdoc/>
	public int Count => 1;

	/// <inheritdoc/>
	public bool Has(FrameLayer layer) => layer == FrameLayer.Field || Snapshot.HasIntensity;

	/// <inheritdoc/>
	public bool CopyLayer(int frame, FrameLayer layer, Span<byte> destination)
	{
		if (layer != FrameLayer.Field && !Snapshot.HasIntensity)
		{
			return false;
		}
		(layer == FrameLayer.Field ? Snapshot.FieldBytes : Snapshot.IntensityBytes).CopyTo(destination);
		return true;
	}

	/// <inheritdoc/>
	public bool CopyLayer(int frame, FrameLayer layer, Span<Half> destination)
	{
		if (Shape.Format != FrameFormat.Half)
		{
			throw new InvalidOperationException("The snapshot's samples are palette indices, not halves.");
		}
		return CopyLayer(frame, layer, MemoryMarshal.AsBytes(destination));
	}
}
