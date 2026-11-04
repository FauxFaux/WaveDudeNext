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
/// Transport commands of the preview pane beyond the built-in <see cref="MediaCommands"/>.
/// Bound on the pane, so the keys work from any control inside it (the pane's handlers refuse
/// them while a text box has focus, so typing is never hijacked). Home and End are gestures of
/// the commands; the letter and punctuation keys (, . I O X L F) are <c>KeyBinding</c>s in the
/// pane's XAML, because <see cref="KeyGesture"/> rejects unmodified letters. The in/out commands
/// take a frame number as parameter, or null for the frame shown.
/// </summary>
public static class PreviewCommands
{
	/// <summary>Shows the previous frame.</summary>
	public static readonly RoutedUICommand StepBack = new("Previous frame", nameof(StepBack), typeof(PreviewCommands));

	/// <summary>
	/// Plays or pauses the timeline from anywhere, F7 (2026-11-04, the user's request): the window binds it and hands it to
	/// the preview's <see cref="MediaCommands.TogglePlayPause"/>, whose binding is the pane's; the Simulation menu shows it.
	/// </summary>
	public static readonly RoutedUICommand PlayPause = new("Play or pause", nameof(PlayPause), typeof(PreviewCommands),
		[new KeyGesture(Key.F7)]);

	/// <summary>Shows the next frame.</summary>
	public static readonly RoutedUICommand StepForward = new("Next frame", nameof(StepForward), typeof(PreviewCommands));

	/// <summary>Jumps to the in point (the first frame while no range is set).</summary>
	public static readonly RoutedUICommand GoToStart = new("Go to in point", nameof(GoToStart), typeof(PreviewCommands),
		[new KeyGesture(Key.Home)]);

	/// <summary>Jumps to the out point (the last frame while no range is set).</summary>
	public static readonly RoutedUICommand GoToEnd = new("Go to out point", nameof(GoToEnd), typeof(PreviewCommands),
		[new KeyGesture(Key.End)]);

	/// <summary>Sets the in point of the export/loop range at the parameter frame or the frame shown.</summary>
	public static readonly RoutedUICommand SetInPoint = new("Set in point", nameof(SetInPoint), typeof(PreviewCommands));

	/// <summary>Sets the out point of the export/loop range at the parameter frame or the frame shown.</summary>
	public static readonly RoutedUICommand SetOutPoint = new("Set out point", nameof(SetOutPoint), typeof(PreviewCommands));

	/// <summary>Clears the export/loop range back to the whole run.</summary>
	public static readonly RoutedUICommand ClearRange = new("Clear range", nameof(ClearRange), typeof(PreviewCommands));

	/// <summary>Toggles looping between the in and out points.</summary>
	public static readonly RoutedUICommand ToggleLoop = new("Loop", nameof(ToggleLoop), typeof(PreviewCommands));

	/// <summary>Toggles following the newest frame of a run in progress.</summary>
	public static readonly RoutedUICommand ToggleLive = new("Live", nameof(ToggleLive), typeof(PreviewCommands));
}
