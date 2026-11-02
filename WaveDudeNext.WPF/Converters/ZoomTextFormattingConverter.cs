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
/// The text formatting for the UI zoom (<see cref="Settings.UiZoom"/>): the theme's Display formatting at 1, which
/// rasterises glyphs on the pixel grid, Ideal otherwise, since WPF scales Display glyph bitmaps through a transform,
/// soft (<see cref="Theming.WindowZoom"/>); for the tooltip and context menu styles, bound live.
/// </summary>
public sealed class ZoomTextFormattingConverter : IValueConverter
{
	/// <inheritdoc/>
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		value is double zoom && zoom != 1 ? TextFormattingMode.Ideal : TextFormattingMode.Display;

	/// <inheritdoc/>
	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
