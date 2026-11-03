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

using System.Diagnostics;
using NLog;
using WaveDudeNext.Model;
using WaveDudeNext.Runs;

namespace WaveDudeNext.Parts;

/// <summary>
/// Paints the enabled <see cref="OpticalPart"/>s of a project into the red channel of the scene pixels, the
/// channel the engines read as the refractive index (red = index × 100; red 0 is a mirror, see
/// <see cref="Engine.SceneData.MirrorRefractiveIndex"/>), and an absorbing part's absorption into the blue channel
/// (damping, see <see cref="PaintAbsorption"/>). Green (image sources) is never touched. Parts are painted in list
/// order, a later one's index over an earlier one's. Refractive parts are supersampled
/// 4 × 4 per pixel and the sample indices averaged, a sample outside the part counting the pixel's existing index
/// (red 0 as 1), which is also the medium a graded coating band rises from (<see cref="CoatedPart"/>); a mirror part
/// tests the pixel centre alone and writes red 0, since an averaged mirror edge would
/// clamp to glass. Only pixels near an edge (the outline, the coating band's inner edge, a Bragg layer) are
/// supersampled: the shape's depth at the pixel centre is an exact distance, so a pixel farther than a sample's
/// reach from every edge takes its centre's index in one go (2026-10-19, a drag of a part was sluggish). And where the
/// shape's depths are distances (<see cref="PartShape.DepthIsDistance"/>), one pixel centre settles a run of the pixels
/// after it along the row (<see cref="Settled"/>): a centre k pixels on is at most k nearer to every edge, so the pixels
/// within the nearest edge's distance less the reach are outside too, or body too, and only the body index is asked of
/// them (2026-11-04: a box of 600 pixels a row takes a handful of depth evaluations instead of 600; proven byte for byte
/// against the pixel-by-pixel painting by <c>library_check</c>).
/// Pure geometry, no UI; any thread. The per-kind tests are in <see cref="PartShape"/> so the
/// harness can check them.
/// </summary>
public static class PartPainter
{
	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	/// <summary>Samples per pixel edge for refractive parts.</summary>
	public const int Supersample = 4;

	/// <summary>How far a sample can lie from the pixel centre, with a margin: beyond this depth from an edge, every sample falls on the same side.</summary>
	private const double SampleReach = 0.6;

	/// <summary>Taken off a run's reach for the rounding of the depths' arithmetic, in pixels.</summary>
	private const double Epsilon = 1e-6;

	/// <summary>
	/// How many pixels after one along a row its depths settle: with the nearest edge <paramref name="edge"/> pixels from
	/// the centre and the shape's depths distances, the next floor(edge − <paramref name="reach"/> − allowance − ε) centres
	/// are still farther than <paramref name="reach"/> from every edge, on the same side; 0 for a shape whose depths are
	/// not distances, which is sampled pixel by pixel.
	/// </summary>
	/// <param name="shape">The shape.</param>
	/// <param name="edge">The distance from this pixel's centre to the nearest edge, positive.</param>
	/// <param name="reach">The samples' reach to stay clear of, <see cref="SampleReach"/> for the refractive painting, 0 for a mirror's centre test.</param>
	private static int Settled(PartShape shape, double edge, double reach)
	{
		if (!shape.DepthIsDistance)
		{
			return 0;
		}
		double pixels = edge - reach - shape.SkipAllowance - Epsilon;
		return pixels >= 1.0 ? (int)pixels : 0;
	}

	/// <summary>
	/// The scene pixels with the enabled parts painted in: <paramref name="image"/> itself (the same reference, so
	/// callers comparing by reference see no change) when no part is enabled, else a copy of the image, or a blank
	/// canvas of <paramref name="width"/> × <paramref name="height"/> with red = round(<paramref name="backgroundIndex"/> × 100),
	/// green 0, blue 0, alpha 255 when there is no image, with each enabled part painted in list order.
	/// </summary>
	/// <param name="image">The decoded scene image, or null for a blank canvas; never modified.</param>
	/// <param name="width">Canvas width in pixels (the image's when there is one).</param>
	/// <param name="height">Canvas height in pixels.</param>
	/// <param name="backgroundIndex">Refractive index of a blank canvas (<see cref="Scene.RefractiveIndex"/>).</param>
	/// <param name="parts">The project's parts; disabled ones are skipped.</param>
	public static ScenePixels? Paint(ScenePixels? image, int width, int height, double backgroundIndex, IReadOnlyList<OpticalPart> parts)
	{
		if (!parts.Any(part => part.Enabled))
		{
			return image;
		}
		if (image != null)
		{
			width = image.Width;
			height = image.Height;
		}
		int stride = width * 4;
		byte[] bgra = new byte[stride * height];
		if (image != null)
		{
			for (int y = 0; y < height; y++)
			{
				Buffer.BlockCopy(image.Bgra, y * image.StrideBytes, bgra, y * stride, stride);
			}
		}
		else
		{
			byte red = (byte)Math.Clamp((int)Math.Round(backgroundIndex * 100.0), 0, 255);
			for (int i = 0; i < bgra.Length; i += 4)
			{
				bgra[i + 2] = red;
				bgra[i + 3] = 255;
			}
		}
		foreach (OpticalPart part in parts)
		{
			if (part.Enabled)
			{
				Paint(bgra, width, height, stride, part);
			}
		}
		return new ScenePixels(width, height, stride, bgra);
	}

	/// <summary>Paints one part into the red channel of <paramref name="bgra"/> (rows <paramref name="stride"/> bytes apart), and its absorption into the blue channel.</summary>
	public static void Paint(byte[] bgra, int width, int height, int stride, OpticalPart part)
	{
		PartShape? shape = PartShape.For(part);
		if (shape == null)
		{
			Logger.Debug("Part '{Name}' ({Type}) has no shape to paint (too few points)", part.Name, part.GetType().Name);
			return;
		}
		(int x0, int y0, int x1, int y1) = shape.Bounds(width, height);
		if (x0 > x1 || y0 > y1)
		{
			Logger.Debug("Part '{Name}' ({Type}) lies outside the canvas", part.Name, part.GetType().Name);
			return;
		}
		long started = Stopwatch.GetTimestamp();
		if (shape.Absorption > 0)
		{
			PaintAbsorption(bgra, stride, shape, x0, y0, x1, y1);
		}
		if (shape.PaintsIndex)
		{
			PaintIndex(bgra, stride, shape, x0, y0, x1, y1);
		}
		// what a report needs: which part took how long (2026-11-03, when the app's compositions ran 4-10 times slower than the painter alone)
		Logger.Debug("Painted part '{Name}' ({Type}) at ({X}, {Y}), {Angle}°, rows {Y0}..{Y1} in {Ms:0.0} ms", part.Name, part.GetType().Name, part.X, part.Y, part.Angle, y0, y1, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
	}

	/// <summary>Paints the part's refractive index (red; 0 for a mirror) over the pixel rectangle, supersampling the pixels an edge crosses.</summary>
	private static void PaintIndex(byte[] bgra, int stride, PartShape shape, int x0, int y0, int x1, int y1)
	{
		if (shape.IsMirror)
		{
			Parallel.For(y0, y1 + 1, (int y) =>
			{
				int row = y * stride;
				double cy = y + 0.5;
				for (int x = x0; x <= x1;)
				{
					// the centre test: inside at a depth of 0 and more; the run its depth settles is on the same side
					double depth = shape.DepthAt(x + 0.5, cy);
					int run;
					if (depth >= 0.0)
					{
						run = Math.Min(Settled(shape, depth, 0.0), x1 - x);
						for (int k = 0; k <= run; k++)
						{
							bgra[row + (x + k) * 4 + 2] = 0;
						}
					}
					else
					{
						run = Math.Min(Settled(shape, -depth, 0.0), x1 - x);
					}
					x += run + 1;
				}
			});
			return;
		}
		const double step = 1.0 / Supersample;
		const double half = step / 2.0;
		int coating = shape.CoatingWidth;
		Parallel.For(y0, y1 + 1, (int y) =>
		{
			int row = y * stride;
			double cy = y + 0.5;
			for (int x = x0; x <= x1;)
			{
				int offset = row + x * 4 + 2;
				(double depth, double inner) = shape.DepthsAt(x + 0.5, cy);
				if (depth < -SampleReach)
				{
					// every sample outside: this pixel and the run its depth settles
					x += Math.Min(Settled(shape, -depth, SampleReach), x1 - x) + 1;
					continue;
				}
				int red = bgra[offset];
				// red 0 is a mirror the engine reads as index 1000; averaging it in as glass of index 1 is the user's rule,
				// and a graded band rises from the index the pixel had
				double existing = red == 0 ? 1.0 : red / 100.0;
				if (depth > SampleReach && inner > SampleReach && (coating == 0 || Math.Abs(depth - coating) > SampleReach))
				{
					// every sample inside and no edge within reach, neither the outline, an inner edge (a fibre's core, a
					// stack's layer) nor the coating band's: the centre's index stands for the pixel
					if (shape.IndexAt(x + 0.5, cy, existing) is { } whole)
					{
						bgra[offset] = (byte)Math.Clamp((int)Math.Round(whole * 100.0), 1, 255);
					}
					// deeper than the band, or without one: the run the depths settle is body too, clear of the outline, the
					// band's inner edge and every inner edge, and the body index alone is its pixels' index
					double body = coating == 0 ? depth : depth - coating;
					int run = body > SampleReach ? Math.Min(Settled(shape, Math.Min(body, inner), SampleReach), x1 - x) : 0;
					for (int k = 1; k <= run; k++)
					{
						bgra[offset + k * 4] = (byte)Math.Clamp((int)Math.Round(shape.BodyIndexAt(x + k + 0.5, cy) * 100.0), 1, 255);
					}
					x += run + 1;
					continue;
				}
				double sum = 0.0;
				int inside = 0;
				for (int sy = 0; sy < Supersample; sy++)
				{
					for (int sx = 0; sx < Supersample; sx++)
					{
						double? index = shape.IndexAt(x + half + sx * step, y + half + sy * step, existing);
						if (index is { } n)
						{
							sum += n;
							inside++;
						}
						else
						{
							sum += existing;
						}
					}
				}
				if (inside > 0)
				{
					bgra[offset] = (byte)Math.Clamp((int)Math.Round(sum / (Supersample * Supersample) * 100.0), 1, 255);
				}
				x++;
			}
		});
	}

	/// <summary>
	/// Paints a part's absorption into the blue channel: at a pixel whose centre lies at depth δ inside the outline,
	/// round(<see cref="PartShape.Absorption"/> × <see cref="PartShape.AbsorptionProfile"/>(δ / ramp)) (or the shape's own
	/// <see cref="PartShape.AbsorptionShareAt"/>), the ramp being
	/// the part's, shortened to the deepest pixel centre when the part is thinner than twice it (so a thin part still
	/// reaches its full absorption in its middle). The larger of that and the blue already there is kept, so a part
	/// never lowers the damping of the image or of an earlier part. The profile is continuous and 0 at the outline, so
	/// the pixel centre alone stands for the pixel; no supersampling.
	/// </summary>
	private static void PaintAbsorption(byte[] bgra, int stride, PartShape shape, int x0, int y0, int x1, int y1)
	{
		int columns = x1 - x0 + 1;
		float[] depths = new float[columns * (y1 - y0 + 1)];
		double deepest = 0.0;
		var gate = new Lock();
		Parallel.For(y0, y1 + 1, () => 0.0, (int y, ParallelLoopState _, double local) =>
		{
			int row = (y - y0) * columns;
			double cy = y + 0.5;
			for (int x = x0; x <= x1;)
			{
				double depth = shape.DepthAt(x + 0.5, cy);
				depths[row + x - x0] = (float)depth;
				local = Math.Max(local, depth);
				// outside: the run its depth settles is outside too, and only its sign is read
				int run = depth < 0.0 ? Math.Min(Settled(shape, -depth, 0.0), x1 - x) : 0;
				for (int k = 1; k <= run; k++)
				{
					depths[row + x + k - x0] = -1f;
				}
				x += run + 1;
			}
			return local;
		}, local =>
		{
			lock (gate)
			{
				deepest = Math.Max(deepest, local);
			}
		});
		double ramp = Math.Min(shape.AbsorptionRamp, deepest);
		int full = shape.Absorption;
		Logger.Debug("Absorption {Absorption} over a ramp of {Ramp:0.#} px (the part reaches {Deepest:0.#} px deep)", full, ramp, deepest);
		Parallel.For(y0, y1 + 1, (int y) =>
		{
			int row = (y - y0) * columns;
			for (int x = x0; x <= x1; x++)
			{
				float depth = depths[row + x - x0];
				if (depth >= 0f)
				{
					int offset = y * stride + x * 4;
					int blue = (int)Math.Round(full * shape.AbsorptionShareAt(x + 0.5, y + 0.5, depth, ramp));
					bgra[offset] = (byte)Math.Max(bgra[offset], blue);
				}
			}
		});
	}
}
