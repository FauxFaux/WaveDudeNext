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

using System.Numerics;
using System.Runtime.InteropServices;

namespace WaveDudeNext.Rendering;

/// <summary>
/// The author's "glowing edges" picture of a scene image (ported 2026-10-13 from <c>Form1.vb</c>,
/// <c>ComputeGlowingEdges</c> / <c>ComposeGlowOverlay</c>): a same-size picture that is black except
/// along edges, where each channel holds how sharply that channel changes there (a Sobel magnitude
/// times <see cref="Scale"/>, clamped to a byte), so a hard red-to-black edge lights up in red, the way
/// Photoshop's Glowing Edges filter does. The outermost ring of pixels stays black (no full 3×3
/// neighbourhood). The preview lays this over the pictures as a semi-transparent layer; an export bakes
/// it in additively with <see cref="BlendAdditive"/>, as his program does. Pure functions, any thread.
/// </summary>
public static class GlowingEdges
{
	/// <summary>
	/// The author's brightness knob: a full black-to-white edge gives a raw Sobel magnitude of about 1442,
	/// and 0.25 brings that into the byte range without every ordinary edge saturating to white.
	/// </summary>
	public const double Scale = 0.25;

	/// <summary>
	/// The edge picture of a BGRA image as BGRA bytes (alpha 255), <paramref name="width"/> × <paramref name="height"/>,
	/// tightly packed (stride = width × 4). An image smaller than 3 × 3 gives all black.
	/// </summary>
	/// <param name="bgra">The scene image's pixels, BGRA, <paramref name="strideBytes"/> bytes per row.</param>
	/// <param name="width">Width in pixels.</param>
	/// <param name="height">Height in pixels.</param>
	/// <param name="strideBytes">Bytes per source row (at least width × 4).</param>
	public static byte[] Compute(byte[] bgra, int width, int height, int strideBytes)
	{
		var result = new byte[width * height * 4];
		// black with alpha 255: the pixel as a little-endian int, filled in lanes
		MemoryMarshal.Cast<byte, int>(result.AsSpan()).Fill(unchecked((int)0xFF000000));
		if (width < 3 || height < 3)
		{
			return result;
		}
		// rows are independent: a Full HD canvas took 60 ms on one core (2026-10-19). A row goes through the Sobel in
		// lanes over all four channels at once (2026-11-04): first the column sums up + 2 mid + down and the differences
		// down − up as 16-bit integers, then per lane the gradients as ints, their squares summed (at most 2 × 1020²,
		// exact in single precision), the root, the scale and the clamp in double as the scalar loop had them and the
		// floor, so the bytes are the same; the alpha lanes ride along and are put back to 255
		int n = width * 4;
		Parallel.For(1, height - 1, () => new Rows(n), (int y, ParallelLoopState _, Rows rows) =>
		{
			ReadOnlySpan<byte> up = bgra.AsSpan((y - 1) * strideBytes, n);
			ReadOnlySpan<byte> mid = bgra.AsSpan(y * strideBytes, n);
			ReadOnlySpan<byte> down = bgra.AsSpan((y + 1) * strideBytes, n);
			Span<ushort> sum = rows.Sum;
			Span<short> diff = rows.Difference;
			int bytes = Vector<byte>.Count;
			int half = Vector<ushort>.Count;
			int i = 0;
			for (; i + bytes <= n; i += bytes)
			{
				Vector.Widen(new Vector<byte>(up.Slice(i)), out Vector<ushort> u0, out Vector<ushort> u1);
				Vector.Widen(new Vector<byte>(mid.Slice(i)), out Vector<ushort> m0, out Vector<ushort> m1);
				Vector.Widen(new Vector<byte>(down.Slice(i)), out Vector<ushort> d0, out Vector<ushort> d1);
				(u0 + m0 + m0 + d0).CopyTo(sum.Slice(i));
				(u1 + m1 + m1 + d1).CopyTo(sum.Slice(i + half));
				// the wrapped unsigned difference reinterpreted is the signed one
				Vector.AsVectorInt16(d0 - u0).CopyTo(diff.Slice(i));
				Vector.AsVectorInt16(d1 - u1).CopyTo(diff.Slice(i + half));
			}
			for (; i < n; i++)
			{
				sum[i] = (ushort)(up[i] + 2 * mid[i] + down[i]);
				diff[i] = (short)(down[i] - up[i]);
			}
			Span<byte> row = result.AsSpan(y * n, n);
			int end = n - 4;
			int x = 4;
			for (; x + bytes <= end; x += bytes)
			{
				Edges(sum, diff, x).CopyTo(row.Slice(x));
			}
			for (; x < end; x++)
			{
				if ((x & 3) == 3)
				{
					continue;   // alpha stays 255
				}
				int gx = sum[x + 4] - sum[x - 4];
				int gy = diff[x - 4] + 2 * diff[x] + diff[x + 4];
				row[x] = (byte)Math.Min(255.0, Math.Sqrt((double)gx * gx + (double)gy * gy) * Scale);
			}
			return rows;
		}, _ => { });
		return result;
	}

	/// <summary>The alpha lanes of a byte vector of pixels, every fourth from the fourth.</summary>
	private static readonly Vector<byte> AlphaLanes = MakeAlphaLanes();

	private static Vector<byte> MakeAlphaLanes()
	{
		var lanes = new byte[Vector<byte>.Count];
		for (int i = 3; i < lanes.Length; i += 4)
		{
			lanes[i] = 255;
		}
		return new Vector<byte>(lanes);
	}

	/// <summary>One vector of edge bytes for the positions from <paramref name="x"/> on, alpha 255, from the column sums and differences at x − 4, x and x + 4.</summary>
	private static Vector<byte> Edges(ReadOnlySpan<ushort> sum, ReadOnlySpan<short> diff, int x)
	{
		int half = Vector<ushort>.Count;
		Vector<float> f0 = Magnitudes(sum, diff, x, out Vector<float> f1);
		Vector<float> f2 = Magnitudes(sum, diff, x + half, out Vector<float> f3);
		Vector<short> s0 = Vector.Narrow(Vector.ConvertToInt32(f0), Vector.ConvertToInt32(f1));
		Vector<short> s1 = Vector.Narrow(Vector.ConvertToInt32(f2), Vector.ConvertToInt32(f3));
		return Vector.ConditionalSelect(AlphaLanes, AlphaLanes, Vector.AsVectorByte(Vector.Narrow(s0, s1)));
	}

	/// <summary>The whole magnitudes of the positions from <paramref name="p"/> on, a vector of 16-bit sums wide, as two single vectors.</summary>
	private static Vector<float> Magnitudes(ReadOnlySpan<ushort> sum, ReadOnlySpan<short> diff, int p, out Vector<float> second)
	{
		// gx: the sum a pixel to the right less the sum a pixel to the left; gy: the difference a pixel to the left, twice here, a pixel to the right
		Vector.Widen(new Vector<ushort>(sum.Slice(p + 4)), out Vector<uint> r0, out Vector<uint> r1);
		Vector.Widen(new Vector<ushort>(sum.Slice(p - 4)), out Vector<uint> l0, out Vector<uint> l1);
		Vector.Widen(new Vector<short>(diff.Slice(p - 4)), out Vector<int> a0, out Vector<int> a1);
		Vector.Widen(new Vector<short>(diff.Slice(p)), out Vector<int> b0, out Vector<int> b1);
		Vector.Widen(new Vector<short>(diff.Slice(p + 4)), out Vector<int> c0, out Vector<int> c1);
		second = Magnitude(Vector.AsVectorInt32(r1) - Vector.AsVectorInt32(l1), a1 + b1 + b1 + c1);
		return Magnitude(Vector.AsVectorInt32(r0) - Vector.AsVectorInt32(l0), a0 + b0 + b0 + c0);
	}

	/// <summary>floor(min(255, √(gx² + gy²) × <see cref="Scale"/>)) per lane: the squares' sum exact in single precision, the rest in double.</summary>
	private static Vector<float> Magnitude(Vector<int> gx, Vector<int> gy)
	{
		Vector.Widen(Vector.ConvertToSingle(gx * gx + gy * gy), out Vector<double> low, out Vector<double> high);
		return Vector.Narrow(Clamp(low), Clamp(high));
	}

	private static Vector<double> Clamp(Vector<double> squares) => Vector.Floor(Vector.Min(Vector.SquareRoot(squares) * Scale, new Vector<double>(255.0)));

	/// <summary>A worker's scratch rows for <see cref="Compute"/>: the column sums and differences of a row, all four channels.</summary>
	private sealed class Rows
	{
		public Rows(int n)
		{
			Sum = new ushort[n];
			Difference = new short[n];
		}

		public ushort[] Sum { get; }
		public short[] Difference { get; }
	}


	/// <summary>
	/// Bakes the edge picture into a rendered frame the author's way: each channel becomes
	/// min(255, frame + edge × <paramref name="gain"/>), so black edge pixels leave the frame alone at any
	/// gain and the picture never gets lighter where there is no edge. For the export (opt-in per layer,
	/// <c>DisplayLayer.OverlayInExport</c>); the preview composites a translucent layer instead.
	/// </summary>
	/// <param name="frameArgb">The frame's pixels, packed <c>0xAARRGGBB</c>, width × height, modified in place.</param>
	/// <param name="width">Width in pixels of both.</param>
	/// <param name="height">Height in pixels of both.</param>
	/// <param name="edgesBgra">The picture from <see cref="Compute"/> (tightly packed BGRA).</param>
	/// <param name="gain">Multiplier of the edge picture; 0 changes nothing.</param>
	public static void BlendAdditive(Span<int> frameArgb, int width, int height, ReadOnlySpan<byte> edgesBgra, double gain)
	{
		if (gain <= 0.0)
		{
			return;
		}
		int count = width * height;
		for (int i = 0; i < count; i++)
		{
			int argb = frameArgb[i];
			int b = Math.Min(255, (argb & 0xFF) + (int)(edgesBgra[i * 4] * gain));
			int g = Math.Min(255, ((argb >> 8) & 0xFF) + (int)(edgesBgra[i * 4 + 1] * gain));
			int r = Math.Min(255, ((argb >> 16) & 0xFF) + (int)(edgesBgra[i * 4 + 2] * gain));
			frameArgb[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
		}
	}
}
