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

using System.Runtime.CompilerServices;

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
		for (int i = 3; i < result.Length; i += 4)
		{
			result[i] = 255;
		}
		if (width < 3 || height < 3)
		{
			return result;
		}
		// rows are independent: a Full HD canvas took 60 ms on one core (2026-10-19)
		Parallel.For(1, height - 1, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int y) =>
		{
			int rowUp = (y - 1) * strideBytes;
			int rowMid = y * strideBytes;
			int rowDown = (y + 1) * strideBytes;
			int dstRow = y * width * 4;
			for (int x = 1; x < width - 1; x++)
			{
				int cUp = rowUp + (x - 1) * 4;
				int cMid = rowMid + (x - 1) * 4;
				int cDown = rowDown + (x - 1) * 4;
				int dst = dstRow + x * 4;
				for (int channel = 0; channel < 3; channel++)
				{
					int p00 = bgra[cUp + channel];
					int p01 = bgra[cUp + 4 + channel];
					int p02 = bgra[cUp + 8 + channel];
					int p10 = bgra[cMid + channel];
					int p12 = bgra[cMid + 8 + channel];
					int p20 = bgra[cDown + channel];
					int p21 = bgra[cDown + 4 + channel];
					int p22 = bgra[cDown + 8 + channel];
					int gx = (p02 + 2 * p12 + p22) - (p00 + 2 * p10 + p20);
					int gy = (p20 + 2 * p21 + p22) - (p00 + 2 * p01 + p02);
					double magnitude = Math.Sqrt((double)gx * gx + (double)gy * gy) * Scale;
					result[dst + channel] = (byte)Math.Min(255.0, magnitude);
				}
			}
		});
		return result;
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
