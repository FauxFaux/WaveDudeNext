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

using System.Windows.Input;

namespace WaveDudeNext.WPF.Commands;

/// <summary>
/// Commands of the View menu's preview quality entries (2026-10-15), bound on the main window (handlers in
/// <c>MainWindow.xaml.cs</c>): each sets one persisted user setting from its parameter, a number as text.
/// The settings describe the machine, not the project: the simulation is the same, only what the preview
/// captures and keeps changes, and an export always uses a full run.
/// </summary>
public static class ViewCommands
{
	/// <summary>Sets the preview resolution: the parameter is the divisor of the canvas along each axis (1, 2 or 4).</summary>
	public static readonly RoutedUICommand SetPreviewResolution = new("Preview resolution", nameof(SetPreviewResolution), typeof(ViewCommands));

	/// <summary>Sets the preview's frame share: the parameter multiplies the project's visualisation interval (1, 2 or 4).</summary>
	public static readonly RoutedUICommand SetPreviewFrames = new("Preview frames", nameof(SetPreviewFrames), typeof(ViewCommands));

	/// <summary>Sets the preview's sample size: the parameter is the bytes per sample (2 for halves, 1 for palette indices).</summary>
	public static readonly RoutedUICommand SetPreviewSamples = new("Preview samples", nameof(SetPreviewSamples), typeof(ViewCommands));

	/// <summary>Sets the dithering of the 16-bit pictures (2026-10-23): the parameter is the bits per channel (0 for none, 10, 12, 14 or 16).</summary>
	public static readonly RoutedUICommand SetPreviewDither = new("Preview dithering", nameof(SetPreviewDither), typeof(ViewCommands));

	/// <summary>Sets the UI zoom (2026-11-02): the parameter is the factor of every window's content as text, 0.5 to 3, with a decimal point.</summary>
	public static readonly RoutedUICommand SetUiZoom = new("UI zoom", nameof(SetUiZoom), typeof(ViewCommands));
}
