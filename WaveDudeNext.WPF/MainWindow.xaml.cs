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
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Shell;
using Microsoft.Win32;
using NLog;
using WaveDudeNext.Model;
using WaveDudeNext.Runs;
using WaveDudeNext.Rendering;
using WaveDudeNext.WPF.Dialogs;
using WaveDudeNext.WPF.Docking;
using WaveDudeNext.WPF.Localization;
using WaveDudeNext.WPF.Theming;

namespace WaveDudeNext.WPF;

/// <summary>
/// Shell window: menu, status bar and the <see cref="DockHost"/> with the Project and
/// Properties panes around the preview. The window is its own data context: it owns the open
/// <see cref="Project"/>, its <see cref="Filename"/>, the tree's <see cref="SelectedItem"/> and the
/// current <see cref="Run"/> (see <c>MainWindow.Simulation.cs</c>). Window placement is bound two-way
/// to <see cref="Settings"/> in the XAML; the pane arrangement is stored there on closing.
/// </summary>
public partial class MainWindow : Window
{
	/// <summary>Extension of project files written by Save.</summary>
	public const string ProjectExtension = ".wdproj";
	private const int RecentFileCount = 10;

	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

	/// <summary>Identifies the <see cref="Project"/> property.</summary>
	public static readonly DependencyProperty ProjectProperty = DependencyProperty.Register(
		nameof(Project), typeof(Project), typeof(MainWindow), new PropertyMetadata(null, (d, e) => ((MainWindow)d).OnProjectReplaced((Project?)e.NewValue)));

	/// <summary>Identifies the <see cref="Filename"/> property.</summary>
	public static readonly DependencyProperty FilenameProperty = DependencyProperty.Register(
		nameof(Filename), typeof(string), typeof(MainWindow),
		new PropertyMetadata(null, (_, _) => CommandManager.InvalidateRequerySuggested()));

	/// <summary>Identifies the <see cref="IsDirty"/> property.</summary>
	public static readonly DependencyProperty IsDirtyProperty = DependencyProperty.Register(
		nameof(IsDirty), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

	/// <summary>Identifies the <see cref="SelectedItem"/> property.</summary>
	public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
		nameof(SelectedItem), typeof(object), typeof(MainWindow),
		new PropertyMetadata(null, (d, e) => ((MainWindow)d).OnSelectedItemChanged(e.NewValue)));

	public MainWindow()
	{
		Project = new Project();
		InitializeComponent();

		if (!TryLoadLayout() && (dockHost.DefaultLayout == null || !dockHost.LoadLayout(dockHost.DefaultLayout)))
		{
			// Messages stays hidden until asked for (View > Panes, status bar double-click)
			dockHost.Show(projectDock);
			dockHost.Show(propertiesDock);
		}

		SourceInitialized += (_, _) => ThemeController.Instance.ApplyTitleBar(this);
		// texts taken once at construction follow a language change here: the dock titles (plain properties) and the
		// theme entries (converted headers); everything else is a DynamicResource (2026-10-14)
		LanguageController.Instance.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(LanguageController.Current))
			{
				ApplyLanguageTexts();
			}
		};
		// the engine choice is only ever written through the Simulation menu; say so in the log with a time
		// stamp, so a run on the CPU can be traced back to the click that caused it (2026-10-10)
		Settings.Default.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(Settings.UseGpu))
			{
				Logger.Info(Settings.Default.UseGpu ? "Use the GPU switched on (Simulation menu)" : "Use the GPU switched off (Simulation menu): runs use the CPU from now on");
			}
			else if (args.PropertyName == nameof(Settings.WarnWhenRamInsufficient))
			{
				Logger.Info(Settings.Default.WarnWhenRamInsufficient ? "Memory warning switched on (Simulation menu)" : "Memory warning switched off: a run whose frames do not fit in the RAM cache goes to the cache file without asking");
			}
			else if (args.PropertyName == nameof(Settings.FrameMemoryPercent))
			{
				int percent = Settings.Default.FrameMemoryPercent;
				double budget = FrameMemory.ToGigabytes(FrameMemory.Budget(percent));
				if (percent <= FrameMemory.Automatic)
				{
					Logger.Info("RAM cache size: automatic (what is free minus {Margin:0.0} GB), {Budget:0.0} GB right now (Simulation menu or the memory warning)", FrameMemory.ToGigabytes(FrameMemory.Margin), budget);
				}
				else
				{
					Logger.Info("RAM cache size: at most {Percent} % of the RAM, {Budget:0.0} GB right now (Simulation menu)", percent, budget);
				}
			}
			else if (args.PropertyName is nameof(Settings.PreviewResolution) or nameof(Settings.PreviewFrames) or nameof(Settings.PreviewSampleBytes))
			{
				Logger.Info("Preview quality: {Quality} (View menu or the memory warning)", PreviewQualitySetting);
				OnPreviewQualityChanged();
			}
			else if (args.PropertyName == nameof(Settings.PreviewDitherBits))
			{
				Logger.Info("Preview dithering {State} (View menu)", Settings.Default.PreviewDitherBits == 0 ? "off" : "from " + Settings.Default.PreviewDitherBits + " bits per channel");
			}
			else if (args.PropertyName == nameof(Settings.UiZoom))
			{
				Logger.Info("UI zoom {Zoom} (View menu)", Settings.Default.UiZoom);
			}
		};
		Closing += (_, e) =>
		{
			if (!ConfirmDiscard())
			{
				e.Cancel = true;
				return;
			}
			Settings.Default.Layout = dockHost.SaveLayout();
			ShutdownExport();
			ShutdownRun();
			ShutdownScratch();
		};
	}

	/// <summary>The open project. Never null: an empty project while nothing is loaded.</summary>
	public Project Project
	{
		get => (Project)GetValue(ProjectProperty);
		set => SetValue(ProjectProperty, value);
	}

	/// <summary>Full path the project was loaded from or saved to; null for an unsaved project.</summary>
	public string? Filename
	{
		get => (string?)GetValue(FilenameProperty);
		set => SetValue(FilenameProperty, value);
	}

	/// <summary>
	/// Whether the project was edited since it was opened, created or last saved (2026-10-14): the title
	/// carries an asterisk and New, Open, Close and Exit ask before discarding. Set by the watcher on any
	/// change, model-wide; cleared when the project is replaced or saved, and when Undo or Redo reach the saved step
	/// (<c>MainWindow.History.cs</c>).
	/// </summary>
	public bool IsDirty
	{
		get => (bool)GetValue(IsDirtyProperty);
		set => SetValue(IsDirtyProperty, value);
	}

	/// <summary>The model object selected in the project tree (or on the preview's pictures), or null: the current item, always one of <see cref="SelectedItems"/>.</summary>
	public object? SelectedItem
	{
		get => GetValue(SelectedItemProperty);
		set => SetValue(SelectedItemProperty, value);
	}

	/// <summary>
	/// The selected sources, parts and probes (2026-10-19, the user's request): one shared list the tree, the preview's
	/// overlays and the commands all edit and watch. <see cref="SelectedItem"/> is the current one among them, the one
	/// the properties pane shows; setting a current item that is not in the list makes the list that item alone, and
	/// null empties it. The tree and the overlays add and remove the rest (Ctrl and Shift, the marquee).
	/// </summary>
	public ObservableCollection<object> SelectedItems { get; } = [];

	/// <summary>
	/// A selection made elsewhere (a marker or graph box on the preview) is mirrored in the tree, which then
	/// raises its own change with the same item. The tree node does not take the focus: the handle keeps it,
	/// so Tab can move on to the next handle (the tree stole it and broke the Tab cycle, 2026-10-14).
	/// </summary>
	private void OnSelectedItemChanged(object? item)
	{
		CommandManager.InvalidateRequerySuggested();
		if (item == null)
		{
			SelectedItems.Clear();
			// deselected on the preview (a click on empty space): the tree lets go too
			projectTree.ClearSelection();
		}
		else if (!SelectedItems.Contains(item))
		{
			// a plain selection: the item alone (the tree and the overlays put an item in first when they mean to extend)
			SelectedItems.Clear();
			SelectedItems.Add(item);
		}
		if (item != null && !ReferenceEquals(projectTree.SelectedItem, item))
		{
			projectTree.Select(item, focus: false);
		}
	}

	/// <summary>
	/// Loads the project file at <paramref name="fileName"/> and makes it the open project.
	/// Failures are logged and shown to the user; the previous project stays open then.
	/// </summary>
	internal void OpenFile(string fileName)
	{
		if (string.Equals(Path.GetExtension(fileName), ColorSchemeStore.XmlExtension, StringComparison.OrdinalIgnoreCase))
		{
			ImportScheme(fileName);
			return;
		}
		if (LegacyProject.IsLegacyFile(fileName))
		{
			ImportLegacy(fileName);
			return;
		}
		try
		{
			fileName = Path.GetFullPath(fileName);
			Project loaded = Project.Load(fileName);
			DiscardRun();
			Project = loaded;
			Filename = fileName;
			SuggestedFileName = null;
			RestoreExportPaths();
			Logger.Info("Opened '{Path}'", fileName);
			RememberRecent(fileName);
			projectTree.SelectRoot();
			// the embedded image against its file: a changed file is taken (the project is then dirty) and watched from here on
			SyncSceneImage("changed since the project was saved");
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error while opening '{Path}'", fileName);
			ShowError(string.Format((string)FindResource("OpenError"), fileName), ex);
		}
	}

	/// <summary>
	/// A project of the author's program (its <c>configuration.json</c>) becomes a new, unsaved project
	/// (2026-10-14): the scene image is embedded, what could not be carried over exactly is logged as a
	/// warning, and Save As proposes a <c>.wdproj</c> named after the project beside the original.
	/// </summary>
	private void ImportLegacy(string fileName)
	{
		try
		{
			var notes = new List<string>();
			Project imported = LegacyProject.Import(fileName, notes, out string projectName);
			DiscardRun();
			Project = imported;
			Filename = null;
			SyncSceneImage("imported");
			SuggestedFileName = LegacyProject.SuggestedFileName(fileName, projectName);
			foreach (string note in notes)
			{
				Logger.Warn(note);
			}
			Logger.Info("Imported the legacy project '{Name}'; save it as a WaveDude Next project (File > Save As)", projectName.Length > 0 ? projectName : Path.GetFileName(fileName));
			projectTree.SelectRoot();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error while importing '{Path}'", fileName);
			ShowError(string.Format((string)FindResource("OpenError"), fileName), ex);
		}
	}

	public static readonly DependencyProperty SuggestedFileNameProperty = DependencyProperty.Register(
		nameof(SuggestedFileName), typeof(string), typeof(MainWindow), new PropertyMetadata(null));

	/// <summary>
	/// The file name of an unsaved project that came from somewhere, an import or a shipped example: the title shows it
	/// (2026-10-29, the user's request: an opened example read as nothing, then "Untitled*") and Save As proposes it; null
	/// for a new project.
	/// </summary>
	public string? SuggestedFileName
	{
		get => (string?)GetValue(SuggestedFileNameProperty);
		set => SetValue(SuggestedFileNameProperty, value);
	}

	/// <summary>A scheme file (double-clicked, or given on the command line) is added to the open project's own schemes and selected.</summary>
	private void ImportScheme(string fileName)
	{
		try
		{
			ColorScheme scheme = ColorSchemeStore.LoadFile(fileName);
			scheme.FilePath = "";
			Project.AddColorScheme(scheme);
			projectTree.Select(scheme);
			Logger.Info("Colour scheme '{Name}' loaded from '{Path}' into the project", scheme.Name, fileName);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error while opening '{Path}'", fileName);
			ShowError(string.Format((string)FindResource("OpenError"), fileName), ex);
		}
	}

	/// <summary>Writes the open project to <paramref name="fileName"/>; false when that failed.</summary>
	private bool SaveFile(string fileName)
	{
		try
		{
			fileName = Path.GetFullPath(fileName);
			// an image file inside the project's folder is stored relative to it, so the project moves with its image
			Project.Scene.RelativizeImagePath(Path.GetDirectoryName(fileName));
			Project.Save(fileName);
			Filename = fileName;
			MarkHistorySaved();
			IsDirty = false;
			ArmSceneImageWatch();
			Logger.Info("Saved '{Path}'", fileName);
			RememberRecent(fileName);
			return true;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error while saving '{Path}'", fileName);
			ShowError(string.Format((string)FindResource("SaveError"), fileName), ex);
			return false;
		}
	}

	/// <summary>Puts the file at the top of the recent list and of the taskbar jump list.</summary>
	private static void RememberRecent(string fileName)
	{
		JumpList.AddToRecentCategory(fileName);

		// a fresh list so the bound menu sees the change
		var recent = new List<string> { fileName };
		foreach (string entry in Settings.Default.RecentFiles)
		{
			if (!string.Equals(entry, fileName, StringComparison.OrdinalIgnoreCase) && recent.Count < RecentFileCount)
			{
				recent.Add(entry);
			}
		}
		Settings.Default.RecentFiles = recent;
		SaveSettings();
	}

	private void ShowError(string message, Exception ex) =>
		MessageWindow.Show(this, $"{message}\n{ex.Message}", MessageKind.Error, MessageButtons.Ok);

	/// <summary>
	/// Asks whether to save an edited project before it is discarded: Save saves (through Save As when it has
	/// no file yet), Don't save discards, Cancel keeps it. True when the caller may go on.
	/// </summary>
	private bool ConfirmDiscard()
	{
		if (!IsDirty)
		{
			return true;
		}
		if (Environment.GetEnvironmentVariable("WAVEDUDE_DISCARD_EDITS") == "1")
		{
			// a UI harness cannot answer the question by posted keys: it discards
			Logger.Debug("Edits discarded without asking (WAVEDUDE_DISCARD_EDITS)");
			return true;
		}
		string name = Filename != null ? Path.GetFileName(Filename) : (string)FindResource("Untitled");
		MessageAnswer answer = MessageWindow.Show(this, string.Format((string)FindResource("SaveChangesFormat"), name),
			MessageKind.Question, MessageButtons.SaveDiscardCancel);
		return answer switch
		{
			MessageAnswer.Save => SaveInteractively(),
			MessageAnswer.Discard => true,
			_ => false,
		};
	}

	/// <summary>Save, or Save As for a project without a file; false when the user cancelled or the save failed.</summary>
	private bool SaveInteractively()
	{
		if (Filename != null)
		{
			return SaveFile(Filename);
		}
		var dialog = new SaveFileDialog
		{
			Filter = (string)FindResource("SaveFileDialogFilter"),
			DefaultExt = ProjectExtension,
			AddExtension = true,
			FileName = SuggestedFileName ?? "",
		};
		return dialog.ShowDialog(this) == true && SaveFile(dialog.FileName);
	}

	/// <summary>Simulation > RAM cache size: the chosen share of the RAM, applied to the next run.</summary>
	private void OnSetFrameMemory(object sender, ExecutedRoutedEventArgs e) => SetIntSetting(e, percent => Settings.Default.FrameMemoryPercent = percent);

	/// <summary>View > Preview resolution: the canvas divisor (1, 2, 4), applied to the next scratch frame and run.</summary>
	private void OnSetPreviewResolution(object sender, ExecutedRoutedEventArgs e) => SetIntSetting(e, divisor => Settings.Default.PreviewResolution = divisor);

	/// <summary>View > Preview frames: the multiplier of the project's visualisation interval (1, 2, 4).</summary>
	private void OnSetPreviewFrames(object sender, ExecutedRoutedEventArgs e) => SetIntSetting(e, divisor => Settings.Default.PreviewFrames = divisor);

	/// <summary>View > Preview samples: the bytes per sample (2 halves, 1 palette indices).</summary>
	private void OnSetPreviewSamples(object sender, ExecutedRoutedEventArgs e) => SetIntSetting(e, bytes => Settings.Default.PreviewSampleBytes = bytes);

	/// <summary>View > Preview dithering: the bits per channel the 16-bit pictures are dithered from (0 none, 10, 12, 14, 16); the pictures redraw.</summary>
	private void OnSetPreviewDither(object sender, ExecutedRoutedEventArgs e) => SetIntSetting(e, bits => Settings.Default.PreviewDitherBits = bits);

	/// <summary>View > UI zoom: the factor of every window's content, from the parameter as text with a decimal point (0.5 to 3); applied live by WindowZoom.</summary>
	private void OnSetUiZoom(object sender, ExecutedRoutedEventArgs e)
	{
		if (e.Parameter is string text && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double factor))
		{
			Settings.Default.UiZoom = factor;
			SaveSettings();
		}
	}

	/// <summary>A menu choice carried as a number in the command parameter (an int, or digits as text): stores it and saves the settings.</summary>
	private static void SetIntSetting(ExecutedRoutedEventArgs e, Action<int> apply)
	{
		int value;
		if (e.Parameter is int number)
		{
			value = number;
		}
		else if (e.Parameter is not string text || !int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
		{
			return;
		}
		apply(value);
		SaveSettings();
	}

	private void OnFileNew(object sender, ExecutedRoutedEventArgs e)
	{
		if (!ConfirmDiscard())
		{
			return;
		}
		DiscardRun();
		Project = new Project();
		Filename = null;
		SuggestedFileName = null;
		projectTree.SelectRoot();
	}

	private void OnFileOpen(object sender, ExecutedRoutedEventArgs e)
	{
		if (!ConfirmDiscard())
		{
			return;
		}
		if (e.Parameter is string recent)
		{
			OpenFile(recent);
			return;
		}
		var dialog = new OpenFileDialog
		{
			Filter = (string)FindResource("OpenFileDialogFilter"),
			DefaultExt = ProjectExtension,
		};
		if (dialog.ShowDialog(this) == true)
		{
			OpenFile(dialog.FileName);
		}
	}

	/// <summary>
	/// File > Examples > one of the shipped examples (2026-10-29): the embedded project becomes the open project, unsaved
	/// and without a file, its own file name suggested when it is saved; a failure leaves the previous project open.
	/// </summary>
	private void OnFileOpenExample(object sender, ExecutedRoutedEventArgs e)
	{
		if (e.Parameter is not ExampleEntry example || !ConfirmDiscard())
		{
			return;
		}
		try
		{
			Project loaded;
			using (Stream stream = Examples.Open(example))
			{
				loaded = Project.Load(stream);
			}
			DiscardRun();
			Project = loaded;
			Filename = null;
			SuggestedFileName = example.FileName;
			Logger.Info("Opened the example '{Title}' ({Resource})", example.Title, example.Resource);
			projectTree.SelectRoot();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "Error while opening the example '{Resource}'", example.Resource);
			ShowError(string.Format((string)FindResource("OpenError"), example.FileName), ex);
		}
	}

	private void OnFileSave(object sender, ExecutedRoutedEventArgs e)
	{
		if (Filename == null)
		{
			OnFileSaveAs(sender, e);
		}
		else
		{
			SaveFile(Filename);
		}
	}

	private void OnFileSaveAs(object sender, ExecutedRoutedEventArgs e)
	{
		var dialog = new SaveFileDialog
		{
			Filter = (string)FindResource("SaveFileDialogFilter"),
			DefaultExt = ProjectExtension,
			AddExtension = true,
			FileName = Filename ?? SuggestedFileName ?? "",
		};
		if (dialog.ShowDialog(this) == true)
		{
			SaveFile(dialog.FileName);
		}
	}

	/// <summary>The folder of the project file, or null for an unsaved project.</summary>
	private string? ProjectFolder => Filename != null ? Path.GetDirectoryName(Filename) : null;

	private void CanFileClose(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Filename != null;

	private void OnFileClose(object sender, ExecutedRoutedEventArgs e)
	{
		if (!ConfirmDiscard())
		{
			return;
		}
		DiscardRun();
		Project = new Project();
		Filename = null;
		SuggestedFileName = null;
		projectTree.SelectRoot();
	}

	private void OnFileExit(object sender, ExecutedRoutedEventArgs e) => Close();

	/// <summary>Shows the Messages pane if hidden, brings it to the front of its group, and gives it keyboard focus.</summary>
	private void OnShowMessages(object sender, ExecutedRoutedEventArgs e)
	{
		dockHost.Show(messagesDock);
		messagesPane.FocusList();
	}

	/// <summary>
	/// The window takes files dropped on it (a project, a scheme, Jeroen's configuration file); anything else is refused
	/// where nothing inside took it (the tree takes project objects, the text boxes text), so the cursor shows what a drop
	/// does. Without this a dragged tree object showed as allowed over the pictures and was then ignored (2026-10-23).
	/// </summary>
	private void OnDragOver(object sender, DragEventArgs e)
	{
		e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private void OnDrop(object sender, DragEventArgs e)
	{
		if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && ConfirmDiscard())
		{
			OpenFile(files[0]);
		}
	}

	private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => SelectedItem = e.NewValue;

	/// <summary>Re-reads the texts that are not DynamicResources: the dock pane titles and the theme menu's entries.</summary>
	private void ApplyLanguageTexts()
	{
		projectDock.Title = (string)FindResource("PaneProject");
		propertiesDock.Title = (string)FindResource("PaneProperties");
		messagesDock.Title = (string)FindResource("PaneMessages");
		themeMenu.Items.Refresh();
		// the title's "Untitled" is looked up by the converter; the busy text is built in code
		BindingOperations.GetMultiBindingExpression(this, TitleProperty)?.UpdateTarget();
		UpdateBusy();
	}

	/// <summary>
	/// A plain F10 focuses the menu bar (its first header; Left/Right move along it, Down opens a menu,
	/// Escape leaves). WPF enters menu mode itself for a real F10 or Alt, which arrive as system keys and
	/// need the real keyboard state; a harness posting messages has neither, so this is its way into the
	/// menus (2026-10-14), and it does no harm for a person.
	/// </summary>
	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		base.OnPreviewKeyDown(e);
		if (!e.Handled && e.Key == Key.F10 && !mainMenu.IsKeyboardFocusWithin
			&& mainMenu.ItemContainerGenerator.ContainerFromIndex(0) is MenuItem first)
		{
			first.Focus();
			e.Handled = true;
		}
	}

	/// <summary>Escape on a focused menu header (not inside an open menu) returns the focus to the preview.</summary>
	private void OnMenuPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape && e.OriginalSource is MenuItem { Role: MenuItemRole.TopLevelHeader, IsSubmenuOpen: false })
		{
			previewPane.Focus();
			e.Handled = true;
		}
	}

	/// <inheritdoc/>
	protected override void OnClosed(EventArgs e)
	{
		base.OnClosed(e);
		// the manual is not owned by this window, and the application ends with its last window
		_manual?.Close();
		if (Settings.Default.WindowState == WindowState.Minimized)
		{
			// never start minimized
			Settings.Default.WindowState = WindowState.Normal;
		}
		SaveSettings();
	}

	private static void SaveSettings()
	{
		try
		{
			Settings.Default.Save();
			Logger.Debug("User settings saved");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Losing window placement or the recent list is not worth interrupting the user. The file errors: two
			// instances closing at once write the same file, and the second one finds it open (2026-10-19)
			Logger.Warn(ex, "Could not save the user settings");
		}
	}

	private bool TryLoadLayout()
	{
		DockLayout? layout = Settings.Default.Layout;
		if (layout == null)
		{
			Logger.Debug("No saved layout, using the default arrangement");
			return false;
		}
		bool applied = dockHost.LoadLayout(layout);
		Logger.Debug("Saved layout {Result}", applied ? "restored" : "rejected, using the default arrangement");
		return applied;
	}
}
