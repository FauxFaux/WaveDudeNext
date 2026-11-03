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

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NLog;
using WaveDudeNext.Model;
using WaveDudeNext.Rendering;
using WaveDudeNext.WPF.Localization;
using WaveDudeNext.WPF.Rendering;

namespace WaveDudeNext.WPF.Controls;

/// <summary>
/// One row of a <see cref="ColorSchemePicker"/>: a scheme with its gradient, the name a layer refers to
/// that no scheme has (greyed), or one of the two actions at the end of the list.
/// </summary>
/// <param name="Scheme">The scheme, or null for a missing name or an action.</param>
/// <param name="Label">Text shown.</param>
/// <param name="Tag">Small grey text on the right ("project" for the project's own schemes), or "".</param>
/// <param name="Action">What selecting the row does; <see cref="PickerAction.None"/> for a scheme.</param>
public sealed record PickerEntry(ColorScheme? Scheme, string Label, string Tag, PickerAction Action)
{
	/// <summary>Whether the row shows a gradient.</summary>
	public bool HasScheme => Scheme != null;

	/// <summary>Actions are italic, so they read as commands rather than names.</summary>
	public FontStyle FontStyle => Action == PickerAction.None ? FontStyles.Normal : FontStyles.Italic;

	/// <summary>A missing name is greyed.</summary>
	public bool IsMissing => Scheme == null && Action == PickerAction.None;
}

/// <summary>What a picker row does when chosen.</summary>
public enum PickerAction
{
	/// <summary>Selects the scheme.</summary>
	None,
	/// <summary>Loads a scheme file into the project and selects it.</summary>
	Browse,
	/// <summary>Copies the selected scheme into the project as 9 editable stops, selects the copy and opens it in the tree.</summary>
	EditCopy,
}

/// <summary>
/// The colour scheme drop-down of a display layer (2026-10-13): every choice with a gradient preview,
/// the project's own schemes first (tagged "project"), then the shared ones of the layer's
/// <see cref="Kind"/> (<see cref="ColorSchemeCatalog.Choices"/>), and two actions at the end: load a
/// scheme file into the project, or copy the current scheme into the project to edit it. The value is
/// the scheme's name (<see cref="SelectedName"/>, bound to <c>DisplayLayer.ColorScheme</c>), so a
/// layer keeps referring to a scheme by name as before. UI thread.
/// </summary>
public sealed class ColorSchemePicker : ComboBox
{
	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
	/// <summary>More stops than this on a loaded file means a sampled table, which is slimmed to its bends.</summary>
	private const int SampledTableStops = 32;
	/// <summary>The tolerance (largest channel difference, 0 to 255) the shipped colormaps were slimmed with.</summary>
	private const double SimplifyTolerance = 3;

	/// <summary>Identifies the <see cref="Project"/> property.</summary>
	public static readonly DependencyProperty ProjectProperty = DependencyProperty.Register(
		nameof(Project), typeof(Project), typeof(ColorSchemePicker), new PropertyMetadata(null, OnProjectChanged));

	/// <summary>Identifies the <see cref="Kind"/> property.</summary>
	public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
		nameof(Kind), typeof(ColorSchemeKind), typeof(ColorSchemePicker), new PropertyMetadata(ColorSchemeKind.Unspecified, (d, _) => ((ColorSchemePicker)d).Rebuild()));

	/// <summary>Identifies the <see cref="SelectedName"/> property.</summary>
	public static readonly DependencyProperty SelectedNameProperty = DependencyProperty.Register(
		nameof(SelectedName), typeof(string), typeof(ColorSchemePicker),
		new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorSchemePicker)d).Rebuild()));

	private readonly ObservableCollection<PickerEntry> _entries = [];
	private bool _rebuilding;

	public ColorSchemePicker()
	{
		// one list for the control's lifetime, updated in place: replacing ItemsSource discards every item
		// container, and a discarded ComboBoxItem logs two binding errors as it loses its ancestor
		ItemsSource = _entries;
		// the action rows and the "project" tag are texts of the current language
		PropertyChangedEventManager.AddHandler(LanguageController.Instance, (_, _) => Rebuild(), nameof(LanguageController.Current));
	}

	/// <summary>The project whose own schemes come first and receive loaded files and edit copies.</summary>
	public Project? Project
	{
		get => (Project?)GetValue(ProjectProperty);
		set => SetValue(ProjectProperty, value);
	}

	/// <summary>The output the layer renders; shared schemes of the other kind are left out.</summary>
	public ColorSchemeKind Kind
	{
		get => (ColorSchemeKind)GetValue(KindProperty);
		set => SetValue(KindProperty, value);
	}

	/// <summary>The chosen scheme's name, the layer's setting.</summary>
	public string SelectedName
	{
		get => (string)GetValue(SelectedNameProperty);
		set => SetValue(SelectedNameProperty, value);
	}

	private static void OnProjectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var picker = (ColorSchemePicker)d;
		if (e.OldValue is Project old)
		{
			old.ColorSchemes.CollectionChanged -= picker.OnSchemesChanged;
		}
		if (e.NewValue is Project project)
		{
			project.ColorSchemes.CollectionChanged += picker.OnSchemesChanged;
		}
		picker.Rebuild();
	}

	private void OnSchemesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

	/// <summary>Fills the list for the current project, kind and name, and selects the row of <see cref="SelectedName"/>.</summary>
	private void Rebuild()
	{
		if (_rebuilding)
		{
			return;
		}
		_rebuilding = true;
		try
		{
			var entries = new List<PickerEntry>();
			string projectTag = TryFindResource("PickerProjectTag") as string ?? "project";
			PickerEntry? selected = null;
			foreach (ColorScheme scheme in ColorSchemeCatalog.Instance.Choices(Project, Kind))
			{
				bool own = Project != null && Project.ColorSchemes.Contains(scheme);
				var entry = new PickerEntry(scheme, scheme.Name, own ? projectTag : "", PickerAction.None);
				entries.Add(entry);
				if (selected == null && string.Equals(scheme.Name, SelectedName, StringComparison.OrdinalIgnoreCase))
				{
					selected = entry;
				}
			}
			if (selected == null && SelectedName.Length > 0)
			{
				string format = TryFindResource("PickerMissingFormat") as string ?? "{0} (not found)";
				selected = new PickerEntry(null, string.Format(format, SelectedName), "", PickerAction.None);
				entries.Insert(0, selected);
			}
			entries.Add(new PickerEntry(null, TryFindResource("PickerBrowse") as string ?? "Load from file…", "", PickerAction.Browse));
			entries.Add(new PickerEntry(null, TryFindResource("PickerEditCopy") as string ?? "Edit a copy in the project…", "", PickerAction.EditCopy));
			// bring the live list to the wanted one with the fewest changes: drop rows that went, insert rows that came
			for (int i = _entries.Count - 1; i >= 0; i--)
			{
				if (!entries.Any(wanted => Same(wanted, _entries[i])))
				{
					_entries.RemoveAt(i);
				}
			}
			for (int i = 0; i < entries.Count; i++)
			{
				int at = IndexOf(entries[i]);
				if (at < 0)
				{
					_entries.Insert(i, entries[i]);
				}
				else if (at != i)
				{
					_entries.Move(at, i);
				}
				if (_entries[i].Label != entries[i].Label || _entries[i].Tag != entries[i].Tag)
				{
					_entries[i] = entries[i]; // relabelled (a language change)
				}
			}
			SelectedItem = selected == null ? null : _entries[IndexOf(selected)];
		}
		finally
		{
			_rebuilding = false;
		}
	}

	private int IndexOf(PickerEntry wanted)
	{
		for (int i = 0; i < _entries.Count; i++)
		{
			if (Same(wanted, _entries[i]))
			{
				return i;
			}
		}
		return -1;
	}

	/// <summary>Whether two rows stand for the same thing (the same scheme object, or the same action or missing name).</summary>
	private static bool Same(PickerEntry a, PickerEntry b) =>
		ReferenceEquals(a.Scheme, b.Scheme) && a.Action == b.Action && (a.Action != PickerAction.None || a.Label == b.Label) && a.Tag == b.Tag;

	/// <inheritdoc/>
	protected override void OnSelectionChanged(SelectionChangedEventArgs e)
	{
		base.OnSelectionChanged(e);
		if (_rebuilding || SelectedItem is not PickerEntry entry)
		{
			return;
		}
		switch (entry.Action)
		{
			case PickerAction.None when entry.Scheme != null:
				SelectedName = entry.Scheme.Name;
				break;
			case PickerAction.Browse:
				Browse();
				break;
			case PickerAction.EditCopy:
				EditCopy();
				break;
		}
		// an action row never stays selected: the list shows the scheme the layer uses
		if (entry.Action != PickerAction.None)
		{
			Dispatcher.InvokeAsync(Rebuild);
		}
	}

	/// <summary>Loads a JSON or XML scheme file into the project's own schemes and selects it.</summary>
	private void Browse()
	{
		if (Project is not { } project)
		{
			return;
		}
		var dialog = new OpenFileDialog
		{
			Filter = TryFindResource("ColorSchemeFilter") as string ?? "Colour schemes (*.wdscheme;*.json)|*.wdscheme;*.json|All files (*.*)|*.*",
			Title = TryFindResource("PickerBrowse") as string ?? "Load from file",
		};
		if (dialog.ShowDialog(Window.GetWindow(this)) != true)
		{
			return;
		}
		try
		{
			ColorScheme scheme = ColorSchemeStore.LoadFile(dialog.FileName);
			scheme.FilePath = ""; // it lives in the project from now on
			// one of the author's sampled 255-colour tables keeps only its bends (the shipped ones were slimmed the same
			// way, tolerance 3 of 255): nobody edits 255 rows, and the look does not change
			if (scheme.Stops.Count > SampledTableStops)
			{
				int before = scheme.Stops.Count;
				scheme.Simplify(SimplifyTolerance);
				Logger.Info("Colour scheme '{Name}': {Before} sampled colours reduced to {After} stops", scheme.Name, before, scheme.Stops.Count);
			}
			project.AddColorScheme(scheme);
			SelectedName = scheme.Name;
			Logger.Info("Colour scheme '{Name}' loaded from '{Path}' into the project", scheme.Name, dialog.FileName);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
		{
			// the file's text goes into the log under the error (2026-11-03, the user's rule)
			Logger.Error(new UnreadableFileException(dialog.FileName, ex), "The colour scheme could not be loaded: {Message}", ex.Message);
		}
	}

	/// <summary>Copies the scheme in use into the project as it is, selects the copy and opens it in the Project tree.</summary>
	private void EditCopy()
	{
		if (Project is not { } project || ColorSchemeCatalog.Instance.Resolve(project, SelectedName) is not { } current)
		{
			return;
		}
		ColorScheme copy = current.Clone(current.Name + " copy");
		if (project.ColorSchemes.Contains(current))
		{
			copy.Description = "copy of project scheme '" + current.Name + "'";
		}
		project.AddColorScheme(copy);
		SelectedName = copy.Name;
		Logger.Info("Colour scheme '{Name}' copied into the project for editing", copy.Name);
		if (Application.Current.MainWindow is MainWindow main)
		{
			main.SelectedItem = copy;
		}
	}
}
