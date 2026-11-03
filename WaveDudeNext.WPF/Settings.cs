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

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Xml;
using System.Xml.Serialization;
using NLog;
using WaveDudeNext.Engine;
using WaveDudeNext.Model;
using WaveDudeNext.Runs;
using WaveDudeNext.WPF.Docking;
using WaveDudeNext.WPF.Theming;

namespace WaveDudeNext.WPF;

/// <summary>
/// The user settings: one XML file per user, <c>%LocalAppData%\WaveDudeNext\Settings.xml</c>, beside the colour
/// schemes and the frame cache, written by <see cref="XmlSerializer"/> sparsely (a value at its default is left out).
/// Replaces <c>ApplicationSettingsBase</c> (2026-10-26, the user's choice): that kept a <c>user.config</c> per exe
/// path and per version under a folder named after the assembly's company, the author's name by default, so a build
/// and a publish never shared their settings and the upgrade step had nothing to carry over. Every member is for the
/// UI thread; a change raises <see cref="PropertyChanged"/>, and nothing is written until <see cref="Save"/>.
/// </summary>
[XmlRoot("Settings")]
public sealed class Settings : INotifyPropertyChanged
{
	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
	private static readonly XmlSerializer Serializer = new(typeof(Settings));

	/// <summary>The settings file.</summary>
	public static string FilePath { get; } = Path.Combine(App.LocalAppDataDirectory, "Settings.xml");

	/// <summary>
	/// Whether this start found no settings file: the one start that chooses the preview quality for the machine
	/// (<see cref="App.OnStartup"/>, <see cref="PreviewDefaults"/>); an unreadable file is not a first start. Set by <see cref="Load"/>.
	/// </summary>
	public static bool IsFirstStart { get; private set; }

	/// <summary>This user's settings, read from <see cref="FilePath"/> at first use; the defaults when the file is missing or unreadable.</summary>
	public static Settings Default { get; } = Load();

	/// <inheritdoc/>
	public event PropertyChangedEventHandler? PropertyChanged;

	private double _windowWidth = 1280;

	/// <summary>Width of the main window in device-independent pixels.</summary>
	[XmlAttribute, DefaultValue(1280.0)]
	public double WindowWidth
	{
		get => _windowWidth;
		set => Set(ref _windowWidth, value);
	}

	private double _windowHeight = 800;

	/// <summary>Height of the main window in device-independent pixels.</summary>
	[XmlAttribute, DefaultValue(800.0)]
	public double WindowHeight
	{
		get => _windowHeight;
		set => Set(ref _windowHeight, value);
	}

	private double _windowLeft = double.NaN;

	/// <summary>Left edge of the main window in device-independent pixels; NaN lets Windows place it.</summary>
	[XmlAttribute, DefaultValue(double.NaN)]
	public double WindowLeft
	{
		get => _windowLeft;
		set => Set(ref _windowLeft, value);
	}

	private double _windowTop = double.NaN;

	/// <summary>Top edge of the main window in device-independent pixels; NaN lets Windows place it.</summary>
	[XmlAttribute, DefaultValue(double.NaN)]
	public double WindowTop
	{
		get => _windowTop;
		set => Set(ref _windowTop, value);
	}

	private WindowState _windowState = WindowState.Normal;

	/// <summary>State of the main window; never saved as minimized.</summary>
	[XmlAttribute, DefaultValue(WindowState.Normal)]
	public WindowState WindowState
	{
		get => _windowState;
		set => Set(ref _windowState, value);
	}

	private DockLayout? _layout = null;

	/// <summary>The dock panes' arrangement at the last close; null for a first start.</summary>
	[XmlElement("Layout")]
	public DockLayout? Layout
	{
		get => _layout;
		set => Set(ref _layout, value);
	}

	private AppTheme _theme = AppTheme.System;

	/// <summary>The colour theme.</summary>
	[XmlAttribute, DefaultValue(AppTheme.System)]
	public AppTheme Theme
	{
		get => _theme;
		set => Set(ref _theme, value);
	}

	private string _language = "";

	/// <summary>The string table's language code; empty for the system's.</summary>
	[XmlAttribute, DefaultValue("")]
	public string Language
	{
		get => _language;
		set => Set(ref _language, value);
	}

	private List<string> _recentFiles = [];

	/// <summary>The recently opened project files, newest first; a fresh list on every change, so the bound menu sees it.</summary>
	[XmlElement("RecentFile")]
	public List<string> RecentFiles
	{
		get => _recentFiles;
		set => Set(ref _recentFiles, value);
	}

	private Orientation _previewOrientation = Orientation.Vertical;

	/// <summary>Whether the preview's pictures sit side by side or one above the other.</summary>
	[XmlAttribute, DefaultValue(Orientation.Vertical)]
	public Orientation PreviewOrientation
	{
		get => _previewOrientation;
		set => Set(ref _previewOrientation, value);
	}

	private bool _autoRerun = true;

	/// <summary>Whether a simulation edit re-runs the simulation after a moment of quiet.</summary>
	[XmlAttribute, DefaultValue(true)]
	public bool AutoRerun
	{
		get => _autoRerun;
		set => Set(ref _autoRerun, value);
	}

	private bool _warnWhenRamInsufficient = true;

	/// <summary>
	/// Whether a run whose frames would not all fit in the RAM cache asks first (Simulation > Warn when a run does not fit
	/// in RAM, 2026-11-03, the user's design): the memory warning offers a lower preview quality for it; off, the frames
	/// beyond the budget go to the cache file with only the log saying so. The warning's "Do not warn me again" clears it.
	/// </summary>
	[XmlAttribute, DefaultValue(true)]
	public bool WarnWhenRamInsufficient
	{
		get => _warnWhenRamInsufficient;
		set => Set(ref _warnWhenRamInsufficient, value);
	}

	private bool _useGpu = true;

	/// <summary>Whether runs use the OpenCL engine, falling back to the CPU.</summary>
	[XmlAttribute, DefaultValue(true)]
	public bool UseGpu
	{
		get => _useGpu;
		set => Set(ref _useGpu, value);
	}

	private int _frameMemoryPercent = 0;

	/// <summary>The RAM cache's bound as a percentage of the physical memory; 0 or less for automatic.</summary>
	[XmlAttribute, DefaultValue(0)]
	public int FrameMemoryPercent
	{
		get => _frameMemoryPercent;
		set => Set(ref _frameMemoryPercent, value);
	}

	private int _previewResolution = 1;

	/// <summary>The preview's resolution divisor: 1, 2 or 4.</summary>
	[XmlAttribute, DefaultValue(1)]
	public int PreviewResolution
	{
		get => _previewResolution;
		set => Set(ref _previewResolution, value);
	}

	private int _previewFrames = 1;

	/// <summary>The preview's frame divisor: every 1st, 2nd or 4th frame.</summary>
	[XmlAttribute, DefaultValue(1)]
	public int PreviewFrames
	{
		get => _previewFrames;
		set => Set(ref _previewFrames, value);
	}

	private int _previewSampleBytes = 1;

	/// <summary>Bytes per preview sample: 1 for palette indices, 2 for halves.</summary>
	[XmlAttribute, DefaultValue(1)]
	public int PreviewSampleBytes
	{
		get => _previewSampleBytes;
		set => Set(ref _previewSampleBytes, value);
	}

	private int _previewDitherBits = 0;

	/// <summary>Preview dithering: 0 for off, else the bits per channel the colours are reduced from (10, 12, 14 or 16).</summary>
	[XmlAttribute, DefaultValue(0)]
	public int PreviewDitherBits
	{
		get => _previewDitherBits;
		set => Set(ref _previewDitherBits, value);
	}

	private double _uiZoom = 1;

	/// <summary>
	/// The zoom of every window's content (View > UI zoom, 2026-11-02, the user's request): 1 for the monitor's scale,
	/// 0.5 to 3 from the menu, up to 8 from the start-up switch; applied live by <see cref="Theming.WindowZoom"/>.
	/// </summary>
	[XmlAttribute, DefaultValue(1.0)]
	public double UiZoom
	{
		get => _uiZoom;
		set => Set(ref _uiZoom, Math.Clamp(value, 0.5, 8));
	}

	private string _lastExportFolder = "";

	/// <summary>The folder of the last export; empty for none.</summary>
	[XmlAttribute, DefaultValue("")]
	public string LastExportFolder
	{
		get => _lastExportFolder;
		set => Set(ref _lastExportFolder, value);
	}

	private int _exportFilterIndex = 0;

	/// <summary>The index of the export format chosen last in the dialog's filter list.</summary>
	[XmlAttribute, DefaultValue(0)]
	public int ExportFilterIndex
	{
		get => _exportFilterIndex;
		set => Set(ref _exportFilterIndex, value);
	}

	private bool _partDescriptionExpanded = true;

	/// <summary>Whether the description at the top of a part's form is open (2026-10-29, the user's choice: one state for every kind, kept between sessions).</summary>
	[XmlAttribute, DefaultValue(true)]
	public bool PartDescriptionExpanded
	{
		get => _partDescriptionExpanded;
		set => Set(ref _partDescriptionExpanded, value);
	}

	private List<ExportHint> _exportHints = [];

	/// <summary>
	/// Where each project's last exports went, newest project first (2026-10-28, the user's choice: a path belongs to the
	/// machine, so it lives here and not in a project file that may be shared): the export dialogs of a project start there
	/// when it is opened again. A fresh list on every change.
	/// </summary>
	[XmlElement("ExportHint")]
	public List<ExportHint> ExportHints
	{
		get => _exportHints;
		set => Set(ref _exportHints, value);
	}

	/// <summary>Sets the three preview quality settings as one (the memory warning's choice, 2026-11-03); each raises its change.</summary>
	public void SetPreviewQuality(PreviewQuality quality)
	{
		PreviewResolution = quality.ResolutionDivisor;
		PreviewFrames = quality.FrameDivisor;
		PreviewSampleBytes = quality.Format == FrameFormat.Half ? 2 : 1;
	}

	/// <summary>Sets the three preview quality settings and the dithering as one: a first start's choice for the machine (<see cref="PreviewDefaults"/>).</summary>
	public void SetPreviewQuality(PreviewQuality quality, int ditherBits)
	{
		SetPreviewQuality(quality);
		PreviewDitherBits = ditherBits;
	}

	/// <summary>Writes the settings to <see cref="FilePath"/>, creating the folder; file errors are the caller's.</summary>
	public void Save()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
		using var writer = XmlWriter.Create(FilePath, new XmlWriterSettings
		{
			Indent = true,
			IndentChars = "\t",
			Encoding = new UTF8Encoding(false),
		});
		Serializer.Serialize(writer, this, new XmlSerializerNamespaces([XmlQualifiedName.Empty]));
	}

	private static Settings Load()
	{
		if (!File.Exists(FilePath))
		{
			Logger.Debug("No settings file at '{Path}': a first start, the defaults and the machine's preview quality", FilePath);
			IsFirstStart = true;
			return new Settings();
		}
		try
		{
			using var reader = XmlReader.Create(FilePath);
			if (Serializer.Deserialize(reader) is Settings settings)
			{
				return settings;
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException or XmlException or IOException or UnauthorizedAccessException)
		{
			// the file's text goes into the log under the error (2026-11-03, the user's rule): the next save overwrites the file
			Logger.Error(new UnreadableFileException(FilePath, ex), "The settings file '{Path}' is unreadable, using the defaults", FilePath);
		}
		return new Settings();
	}

	private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (!EqualityComparer<T>.Default.Equals(field, value))
		{
			field = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		}
	}
}

/// <summary>Where a project's last exports went: an entry of <see cref="Settings.ExportHints"/>, an attribute each, the empty ones left out.</summary>
public sealed class ExportHint
{
	/// <summary>The project file's full path, the key.</summary>
	[XmlAttribute, DefaultValue("")]
	public string Project { get; set; } = "";

	/// <summary>The last export of the run's pictures, folder and file name; empty for none.</summary>
	[XmlAttribute, DefaultValue("")]
	public string Run { get; set; } = "";

	/// <summary>The last still; empty for none.</summary>
	[XmlAttribute, DefaultValue("")]
	public string Still { get; set; } = "";

	/// <summary>The last probe data file; empty for none.</summary>
	[XmlAttribute, DefaultValue("")]
	public string Probe { get; set; } = "";
}
