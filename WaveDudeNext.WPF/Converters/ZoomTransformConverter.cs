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

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WaveDudeNext.WPF.Converters;

/// <summary>
/// The UI zoom (<see cref="Settings.UiZoom"/>) as a layout transform, the identity at 1: for the theme's tooltip and
/// context menu styles, whose popups sit outside any window's tree and take no transform from their target
/// (<see cref="Theming.WindowZoom"/>), bound live so the first frame is right and a change of the setting follows.
/// </summary>
public sealed class ZoomTransformConverter : IValueConverter
{
	/// <inheritdoc/>
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is not double zoom || zoom == 1)
		{
			return Transform.Identity;
		}
		var scale = new ScaleTransform(zoom, zoom);
		scale.Freeze();
		return scale;
	}

	/// <inheritdoc/>
	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
