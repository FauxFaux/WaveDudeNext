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

using System.Windows;
using System.Windows.Media;

namespace WaveDudeNext.WPF;

/// <summary>
/// How many device pixels one device-independent pixel of a visual covers on the screen: the DPI its window really
/// renders at (the composition target's transform to the device; the tree's own DPI is raised by the start-up zoom,
/// see <see cref="Theming.WindowZoom"/>, so <see cref="VisualTreeHelper.GetDpi"/> is not it) times the scale of the
/// transforms between the visual and its window's root, which is how the zoom reaches the code that works in device
/// pixels: a bitmap drawn one pixel to one, text metrics, a drag measured on the screen. The vertical scale, as WPF's
/// PixelsPerDip, the same as the horizontal on Windows. UI thread; a visual that is not in a window gets its DPI alone.
/// </summary>
public static class DevicePixels
{
	/// <summary>Device pixels per device-independent pixel of <paramref name="visual"/>, see the class.</summary>
	public static double PerDip(Visual visual)
	{
		if (PresentationSource.FromVisual(visual) is not { CompositionTarget: { } target, RootVisual: Visual root })
		{
			return VisualTreeHelper.GetDpi(visual).PixelsPerDip;
		}
		double dpi = target.TransformToDevice.M22;
		if (ReferenceEquals(root, visual))
		{
			return dpi;
		}
		// the transforms on the way up scale uniformly (a zoom, never a rotation here): the unit square's height after them
		Rect unit = visual.TransformToAncestor(root).TransformBounds(new Rect(0, 0, 1, 1));
		return dpi * unit.Height;
	}

	/// <summary>
	/// The thickness in device-independent pixels of a one-pixel line drawn at <paramref name="perDip"/> device pixels
	/// per unit: the whole number of device pixels nearest to the scale (1 up to 1.25, 2 at 1.5 and 2, 3 at 2.5 and 3),
	/// so that a line's two edges can both lie on the pixel grid, where a guideline set at those edges puts them
	/// (2026-11-02, the user's rule: a line is there or not, 1 or 2 pixels wide, never cut).
	/// </summary>
	public static double Hairline(double perDip) => Math.Max(1, Math.Round(perDip)) / perDip;
}
