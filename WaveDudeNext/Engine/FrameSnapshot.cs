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

using System.Buffers;
using System.Runtime.InteropServices;

namespace WaveDudeNext.Engine;

/// <summary>
/// A host-side copy of one frame in the frame store's format: the field and, optionally, the intensity as
/// planes of a <see cref="FrameShape"/> (<see cref="Width"/> × <see cref="Height"/> samples row-major, the
/// engine's padding stripped, the canvas shrunk by the shape's divisor, each sample a <see cref="Half"/> or a
/// palette index byte). Filled by <see cref="IWaveEngine.CaptureFrame"/> and complete after
/// <see cref="IWaveEngine.AwaitFrame"/>. The planes never move: an ordinary snapshot's (the constructors) are arrays
/// on the pinned object heap; one made by <see cref="IWaveEngine.CreateSnapshot"/> may lie in memory the engine owns
/// (the GPU engine's page-locked host memory, which the card fills by DMA while the stepping thread goes on,
/// 2026-11-03), released with the engine, after which the planes throw. Reusable: a pool of two or three lets a run
/// convert and store one frame while the engine computes the next. Not thread-safe; one owner at a time.
/// </summary>
public sealed class FrameSnapshot
{
	private readonly Memory<byte> _field;
	private readonly Memory<byte> _intensity;
	private readonly IntPtr _fieldAddress;
	private readonly IntPtr _intensityAddress;
	private bool _released;

	/// <summary>An ordinary snapshot: its planes are pinned arrays of this process, alive as long as the snapshot.</summary>
	/// <param name="shape">What to capture: the canvas, the shrinking, the format and the brightness baked into an index frame.</param>
	/// <param name="withIntensity">Whether to allocate the intensity plane.</param>
	public FrameSnapshot(FrameShape shape, bool withIntensity)
	{
		Shape = shape.Validate();
		int bytes = checked((int)shape.PlaneBytes);
		byte[] field = GC.AllocateArray<byte>(bytes, pinned: true);
		_field = field;
		_fieldAddress = Marshal.UnsafeAddrOfPinnedArrayElement(field, 0);
		if (withIntensity)
		{
			byte[] intensity = GC.AllocateArray<byte>(bytes, pinned: true);
			_intensity = intensity;
			_intensityAddress = Marshal.UnsafeAddrOfPinnedArrayElement(intensity, 0);
		}
	}

	/// <summary>A full-quality snapshot of a canvas: every cell as a <see cref="Half"/>.</summary>
	/// <param name="width">Canvas width in pixels.</param>
	/// <param name="height">Canvas height in pixels.</param>
	/// <param name="withIntensity">Whether to allocate the intensity plane.</param>
	public FrameSnapshot(int width, int height, bool withIntensity)
		: this(FrameShape.Full(width, height), withIntensity)
	{
	}

	/// <summary>
	/// A snapshot over planes an engine owns (<see cref="IWaveEngine.CreateSnapshot"/>): <paramref name="field"/> and
	/// <paramref name="intensity"/> (<see cref="IntPtr.Zero"/> for none) each address <see cref="FrameShape.PlaneBytes"/>
	/// bytes that stay put until the engine calls <see cref="Release"/>.
	/// </summary>
	internal FrameSnapshot(FrameShape shape, IntPtr field, IntPtr intensity)
	{
		Shape = shape.Validate();
		int bytes = checked((int)shape.PlaneBytes);
		_fieldAddress = field;
		_field = new PlaneMemoryManager(field, bytes).Memory;
		_intensityAddress = intensity;
		if (intensity != IntPtr.Zero)
		{
			_intensity = new PlaneMemoryManager(intensity, bytes).Memory;
		}
	}

	/// <summary>The frame's geometry and format; the engine captures accordingly.</summary>
	public FrameShape Shape { get; }

	/// <summary>Frame width in samples (<see cref="FrameShape.Width"/>).</summary>
	public int Width => Shape.Width;

	/// <summary>Frame height in samples (<see cref="FrameShape.Height"/>).</summary>
	public int Height => Shape.Height;

	/// <summary>The field plane's bytes (<see cref="FrameShape.PlaneBytes"/>); valid after <see cref="IWaveEngine.AwaitFrame"/>.</summary>
	/// <exception cref="ObjectDisposedException">The engine that owned the planes has been disposed.</exception>
	public Span<byte> FieldBytes => FieldMemory.Span;

	/// <summary>The intensity plane's bytes, or empty when the snapshot was created without one (<see cref="HasIntensityPlane"/>).</summary>
	/// <exception cref="ObjectDisposedException">The engine that owned the planes has been disposed.</exception>
	public Span<byte> IntensityBytes => IntensityMemory.Span;

	/// <summary>Whether the snapshot has an intensity plane at all; <see cref="HasIntensity"/> says whether the last capture filled it.</summary>
	public bool HasIntensityPlane => _intensityAddress != IntPtr.Zero;

	/// <summary>The field plane as <see cref="Half"/> values; only for a <see cref="FrameFormat.Half"/> shape.</summary>
	/// <exception cref="InvalidOperationException">The shape's samples are not halves.</exception>
	public Span<Half> Field => Halves(FieldBytes);

	/// <summary>The intensity plane as <see cref="Half"/> values; only for a <see cref="FrameFormat.Half"/> shape with an intensity plane.</summary>
	/// <exception cref="InvalidOperationException">The shape's samples are not halves, or there is no intensity plane.</exception>
	public Span<Half> Intensity => Halves(HasIntensityPlane ? IntensityBytes : throw new InvalidOperationException("The snapshot has no intensity plane."));

	/// <summary>Whether the last capture filled <see cref="IntensityBytes"/> (the engine may have been asked for the field only).</summary>
	public bool HasIntensity { get; internal set; }

	/// <summary>The frame index the owner captured into this snapshot; a tag for the pipeline, not used by the engines.</summary>
	public int Frame { get; set; }

	/// <summary>The simulation step the snapshot shows (0-based), set by the owner.</summary>
	public int Step { get; set; }

	/// <summary>OpenCL events of the reads in flight (GPU engine), released by <see cref="IWaveEngine.AwaitFrame"/>.</summary>
	internal IntPtr[] Events { get; } = new IntPtr[2];

	/// <summary>Number of valid entries in <see cref="Events"/>.</summary>
	internal int EventCount { get; set; }

	/// <summary>The CPU engine's staging: the canvas planes as floats, copied at capture time and converted in <see cref="IWaveEngine.AwaitFrame"/> (on the awaiting thread, off the stepping thread).</summary>
	internal float[]? FieldFloats { get; set; }

	/// <summary>Intensity staging of the CPU engine; see <see cref="FieldFloats"/>.</summary>
	internal float[]? IntensityFloats { get; set; }

	/// <summary>Whether <see cref="FieldFloats"/> (and <see cref="IntensityFloats"/> when <see cref="HasIntensity"/>) hold a capture not yet converted.</summary>
	internal bool PendingConversion { get; set; }

	/// <summary>The field plane as <see cref="Memory{T}"/>, for the CPU engine's row-parallel conversion (<see cref="FrameConversion.Convert"/>).</summary>
	internal Memory<byte> FieldMemory => Alive(_field);

	/// <summary>The intensity plane as <see cref="Memory{T}"/> (empty without one); see <see cref="FieldMemory"/>.</summary>
	internal Memory<byte> IntensityMemory => Alive(_intensity);

	/// <summary>Address of the field plane's first byte, stable (pinned or page-locked), for the GPU engine's DMA reads.</summary>
	internal IntPtr FieldAddress => Alive(_fieldAddress);

	/// <summary>Address of the intensity plane's first byte, or <see cref="IntPtr.Zero"/> without one; see <see cref="FieldAddress"/>.</summary>
	internal IntPtr IntensityAddress => Alive(_intensityAddress);

	/// <summary>Bytes per plane (<see cref="FrameShape.PlaneBytes"/>).</summary>
	internal int PlaneBytes => (int)Shape.PlaneBytes;

	/// <summary>Called by the engine that owns the planes when it releases them: every later access throws.</summary>
	internal void Release() => _released = true;

	private T Alive<T>(T value) => _released ? throw new ObjectDisposedException(nameof(FrameSnapshot), "The engine that owned the snapshot's planes has been disposed.") : value;

	private Span<Half> Halves(Span<byte> plane)
	{
		if (Shape.Format != FrameFormat.Half)
		{
			throw new InvalidOperationException("The snapshot's samples are palette indices, not halves.");
		}
		return MemoryMarshal.Cast<byte, Half>(plane);
	}

	/// <summary>A <see cref="Memory{T}"/> over an engine-owned plane; the memory is the engine's, so disposing frees nothing.</summary>
	private sealed unsafe class PlaneMemoryManager(IntPtr address, int length) : MemoryManager<byte>
	{
		/// <inheritdoc/>
		public override Span<byte> GetSpan() => new((void*)address, length);

		/// <inheritdoc/>
		public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)address + elementIndex);

		/// <inheritdoc/>
		public override void Unpin()
		{
		}

		/// <inheritdoc/>
		protected override void Dispose(bool disposing)
		{
		}
	}
}
