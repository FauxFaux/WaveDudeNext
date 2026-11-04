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
/// Commands that edit the project structure. Bound once on the main window; the parameter is
/// the tree node to act on, or null to act on the current selection. Exposed through the
/// Project pane toolbar, the tree's row buttons, its context menu and its key bindings.
/// </summary>
public static class ProjectCommands
{
	/// <summary>Appends a new source.</summary>
	public static readonly RoutedUICommand AddSource = new("Add source", nameof(AddSource), typeof(ProjectCommands));

	/// <summary>Appends a new optical part of the <see cref="WaveDudeNext.Model.OpticalPart"/> subclass given as parameter; without one, opens the menu of kinds on the invoking element.</summary>
	public static readonly RoutedUICommand AddPart = new("Add part", nameof(AddPart), typeof(ProjectCommands));

	/// <summary>Appends a new probe.</summary>
	public static readonly RoutedUICommand AddProbe = new("Add probe", nameof(AddProbe), typeof(ProjectCommands));

	/// <summary>Appends a new item to the folder node given as parameter.</summary>
	public static readonly RoutedUICommand Add = new("Add", nameof(Add), typeof(ProjectCommands));

	/// <summary>Inserts a copy of a source, part or probe after the original.</summary>
	public static readonly RoutedUICommand Duplicate = new("Duplicate", nameof(Duplicate), typeof(ProjectCommands));

	/// <summary>Removes a source, part or probe.</summary>
	public static readonly RoutedUICommand Remove = new("Remove", nameof(Remove), typeof(ProjectCommands));

	/// <summary>Swaps a source or probe with its predecessor.</summary>
	public static readonly RoutedUICommand MoveUp = new("Move up", nameof(MoveUp), typeof(ProjectCommands));

	/// <summary>Swaps a source or probe with its successor.</summary>
	public static readonly RoutedUICommand MoveDown = new("Move down", nameof(MoveDown), typeof(ProjectCommands));

	/// <summary>Selects every source, part and probe (Ctrl+A in the tree and on the preview; the tree's context menu).</summary>
	public static readonly RoutedUICommand SelectAll = new("Select all", nameof(SelectAll), typeof(ProjectCommands),
		// the gesture on the command, so the Edit menu shows it and it works wherever the window's binding is reached, the
		// preview included; a text box keeps its own Ctrl+A, handled before the window sees the key (2026-11-04)
		[new KeyGesture(Key.A, ModifierKeys.Control)]);

	/// <summary>Adds the item to the selection or takes it out again (Insert or Ctrl+Space on the tree row; the tree's context menu).</summary>
	public static readonly RoutedUICommand ToggleSelected = new("Select or deselect", nameof(ToggleSelected), typeof(ProjectCommands));

	/// <summary>Flips a source's or part's Enabled or a probe's Active (the tree's check boxes; Space on the selected row).</summary>
	public static readonly RoutedUICommand ToggleEnabled = new("Enable or disable", nameof(ToggleEnabled), typeof(ProjectCommands));

	/// <summary>Picks an image file and embeds it as the scene image.</summary>
	public static readonly RoutedUICommand BrowseSceneImage = new("Browse for a scene image", nameof(BrowseSceneImage), typeof(ProjectCommands));

	/// <summary>Writes the embedded scene image to a file of the user's choice, the bytes as they were embedded (2026-10-29, the user's design); the project is not changed.</summary>
	public static readonly RoutedUICommand SaveSceneImage = new("Save the scene image to a file", nameof(SaveSceneImage), typeof(ProjectCommands));

	/// <summary>Drops the scene image: a blank canvas of the image's size remains (2026-10-26, Jeroen's report: there was no way).</summary>
	public static readonly RoutedUICommand RemoveSceneImage = new("Remove the scene image", nameof(RemoveSceneImage), typeof(ProjectCommands));
}
