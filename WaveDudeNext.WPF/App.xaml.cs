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
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using NLog;
using NLog.Config;
using NLog.Targets;
using WaveDudeNext.WPF.Localization;
using WaveDudeNext.WPF.Logging;
using WaveDudeNext.Rendering;
using WaveDudeNext.Runs;
using WaveDudeNext.WPF.Rendering;
using WaveDudeNext.WPF.Theming;

namespace WaveDudeNext.WPF;

/// <summary>
/// Application entry point. Logging is configured by <see cref="SetupLoggingConfig"/> from
/// <c>Program.Main</c>; <see cref="OnStartup"/> records the environment and applies the theme
/// before the main window is created.
/// </summary>
public partial class App : Application
{
	/// <summary>Logger of the application class; other classes get their own via <see cref="LogManager.GetCurrentClassLogger"/>.</summary>
	public static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

	private const string FileLayout =
		"${date:format=yyyy-MM-dd HH\\:mm\\:ss}\t${threadid}\t${level}\t${logger}\t${message}\t${onexception:EXCEPTION\\: ${exception:format=tostring}}";
	private const string DebugLayout =
		"${date:format=HH\\:mm\\:ss} ${threadid} ${level} ${logger:shortName=true}\t${message}\t${onexception:EXCEPTION\\: ${exception:format=tostring}}";

	private const string LogFileName = "WaveDudeNext.WPF.log";

	/// <summary>
	/// Per-user, per-machine data folder (<c>%LocalAppData%\WaveDudeNext</c>, deliberately not the
	/// roaming profile), used for the log when the executable's folder is read-only.
	/// </summary>
	public static string LocalAppDataDirectory { get; } =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveDudeNext");

	/// <summary>
	/// The folder the executable was started from: <see cref="Environment.ProcessPath"/>'s, not
	/// <see cref="AppContext.BaseDirectory"/>, which for the single-file publish is the bundle's extraction folder under
	/// %Temp% (2026-10-19, the user found the log there).
	/// </summary>
	public static string ExecutableDirectory { get; } =
		Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } folder ? folder : AppContext.BaseDirectory;

	/// <summary>
	/// Path of the current log file. Next to the executable when that folder is writable,
	/// otherwise in <see cref="LocalAppDataDirectory"/>. Set by <see cref="SetupLoggingConfig"/>.
	/// </summary>
	public static string LogFile { get; private set; } = Path.Combine(ExecutableDirectory, LogFileName);


	/// <summary>
	/// Replaces the NLog configuration with the application's targets: a rolling file next to
	/// the executable (everything in Debug builds, Info and up in Release), plus the debugger
	/// and console in Debug builds. Also routes unhandled exceptions of the AppDomain and of
	/// unobserved tasks to the log. Call once, before the application object exists.
	/// </summary>
	public static void SetupLoggingConfig()
	{
		LogFile = Path.Combine(ResolveLogDirectory(), LogFileName);
		var config = new LoggingConfiguration();

		var fileTarget = new FileTarget("logfile")
		{
			FileName = LogFile,
			// one rolling file: it grows across sessions and rolls over at 20 MB, the previous one kept as the single
			// archive, so the last 20 to 40 MB are always there (the user's choice, 2026-10-19; archiving at every start
			// had left each session's log as an archive before anyone looked at it)
			ArchiveFileName = Path.ChangeExtension(LogFile, "archive.log"), // NLog 6 appends _00, _01 itself
			MaxArchiveFiles = 1,
			ArchiveAboveSize = 20 * 1024 * 1024,
			KeepFileOpen = true,
			Layout = FileLayout,
		};
		config.AddTarget(fileTarget);

		// status bar: Info and above, warnings on orange, errors on red; attaches to the window once it exists
		var statusBar = new StatusBarTarget
		{
			Name = "statusbar",
			WindowName = nameof(MainWindow),
			ControlName = "txtStatus",
			Layout = "${message}${onexception: (${exception:format=message})}",
		};
		statusBar.ColoringRules.Add(new StatusBarColoringRule("level >= LogLevel.Error", "StatusBarErrorForeground", "StatusBarErrorBackground"));
		statusBar.ColoringRules.Add(new StatusBarColoringRule("level == LogLevel.Warn", "StatusBarWarnForeground", "StatusBarWarnBackground"));
		config.AddTarget(statusBar);
		config.AddRule(LogLevel.Info, LogLevel.Fatal, statusBar);

		// in-memory buffer behind the Messages pane: everything, in every build; the pane's level toggles hide
		// Trace and Debug by default in Release, so they can be switched on when something needs a look
		// (2026-10-14, the user: a Release build had nothing to show there, the messages never got in)
		var messages = new MessagesTarget { Name = "messages" };
		config.AddTarget(messages);
#if DEBUG
		config.AddRule(LogLevel.Trace, LogLevel.Fatal, messages);
		config.AddRule(LogLevel.Trace, LogLevel.Fatal, fileTarget);
		config.AddRule(LogLevel.Trace, LogLevel.Fatal, new DebuggerTarget("debugger") { Layout = DebugLayout });
		config.AddRule(LogLevel.Trace, LogLevel.Fatal, new ColoredConsoleTarget("console") { Layout = DebugLayout });
#else
		// Trace is a Debug-build affair (2026-10-16, the user's rule): the presenter traces every frame it draws, and a
		// Release build must not pay for entries nobody can see; with no rule for Trace, NLog skips the call outright
		config.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
		// the file carries everything the build logs (the user's rule, 2026-10-16): a bug report comes with the
		// decision lines without anyone opening the Messages pane
		config.AddRule(LogLevel.Debug, LogLevel.Fatal, fileTarget);
#endif
		// every number in every entry with a decimal point, whatever the Windows culture (2026-10-27, the user's rule: a
		// report's reader, or a tool summarising the log, must not trip over "7,6 s"); the message templates and the
		// layouts format through this culture when the call names none
		config.DefaultCultureInfo = CultureInfo.InvariantCulture;
		LogManager.Configuration = config;

#if DEBUG
		// WPF binding errors go to a trace source, not to exceptions; route them into the log
		PresentationTraceSources.Refresh();
		PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
		PresentationTraceSources.DataBindingSource.Listeners.Add(new TraceLogListener { Name = "WPF" });
#endif

		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
			Logger.Fatal(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
		TaskScheduler.UnobservedTaskException += (_, args) =>
			Logger.Fatal(args.Exception, "TaskScheduler.UnobservedTaskException");
	}

	/// <summary>The executable's folder if a file can be created there, else <see cref="LocalAppDataDirectory"/>.</summary>
	private static string ResolveLogDirectory()
	{
		if (IsWritable(ExecutableDirectory))
		{
			return ExecutableDirectory;
		}
		Directory.CreateDirectory(LocalAppDataDirectory);
		return LocalAppDataDirectory;
	}

	private static bool IsWritable(string directory)
	{
		try
		{
			string probe = Path.Combine(directory, Path.GetRandomFileName());
			using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
			{
			}
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <inheritdoc/>
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		// WPF formats and parses bound numbers with the element's Language, which defaults to en-US
		// regardless of the Windows locale; make every element follow the user's culture instead
		// (a combo listing 0.25 next to a text box showing 1,5 otherwise). Must run before any window.
		FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
			new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

		DispatcherUnhandledException += (_, args) =>
		{
			Logger.Fatal(args.Exception, "Application.DispatcherUnhandledException");
			args.Handled = false; // let it crash so the issue is visible
		};

		Logger.Info("Application: '{Path}'", Environment.ProcessPath);
		Logger.Info("Application Version: {Version}", Help.AboutInfo.Version);
		Logger.Info("OS: '{OS}'", Environment.OSVersion.VersionString);
		Logger.Info("Runtime: '{Runtime}'", RuntimeInformation.FrameworkDescription);
		Logger.Info("Log file: '{Path}'", LogFile);

		// double-clicking a project file in Explorer then starts this exe with the path as e.Args[0]
		if (Environment.ProcessPath is string executable)
		{
			FileAssociation.Register(WPF.MainWindow.ProjectExtension, FileAssociation.ProjectProgId, FileAssociation.ProjectDescription, executable);
			FileAssociation.Register(ColorSchemeStore.XmlExtension, FileAssociation.SchemeProgId, FileAssociation.SchemeDescription, executable);
		}

		FrameCache.Sweep();
		// the one start without a settings file chooses the preview quality for the machine (2026-10-31, the user's
		// design); the facts and the choice are told after the "ready" line below, so the status bar shows them
		(MachineFacts Facts, PreviewTier Tier)? firstStart = Settings.IsFirstStart ? ChoosePreviewDefaults() : null;
		ColorSchemeCatalog.Instance.Load();
		ThemeController.Instance.Current = Settings.Default.Theme;
		ThemeController.Instance.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(ThemeController.Current))
			{
				Settings.Default.Theme = ThemeController.Instance.Current;
			}
		};
		ThemeController.Instance.Refresh();

		// the string table: the setting's language, or the system's; persisted like the theme
		LanguageController.Instance.Current = Settings.Default.Language;
		LanguageController.Instance.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(LanguageController.Current))
			{
				Settings.Default.Language = LanguageController.Instance.Current;
			}
		};
		LanguageController.Instance.Refresh();

		string? file = ReadArguments(e.Args);
		WindowZoom.Install();
		MenuClickThrough.Install();
		var window = new MainWindow();
		MainWindow = window;
		window.Show();
		if (PresentationSource.FromVisual(window)?.CompositionTarget is { } target)
		{
			// the facts a report's picture depends on (the process is DPI aware per monitor, 2026-11-02)
			Logger.Info("Display scale {Percent} %, UI zoom {Zoom}", Math.Round(target.TransformToDevice.M11 * 100), Settings.Default.UiZoom);
		}
		// the last start-up line, and so the status bar's first text unless this is a first start or a file is opened below
		Logger.Info("{Title} {Version} ready", FindResource("Title"), Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));
		if (firstStart is { } first)
		{
			Logger.Info("First start on this machine: {Memory:0.0} GB of memory, {Threads} threads, {Gpu}, cache drive {Drive} {DriveKind}: the preview is set to {Quality} (View menu)",
				first.Facts.PhysicalBytes / (double)(1L << 30), first.Facts.Threads, first.Facts.Gpu.Length == 0 ? "no OpenCL GPU" : first.Facts.Gpu, first.Facts.CacheDrive,
				first.Facts.CacheDriveSeekPenalty switch { true => "with a seek penalty (a hard disk)", false => "without a seek penalty", null => "of unknown kind" },
				PreviewDefaults.Describe(first.Tier));
		}
		if (file != null)
		{
			// launched with a project file (double-click, "open with", command line); after
			// Show so that an error box has the main window behind it
			window.OpenFile(file);
		}
	}

	/// <summary>
	/// Reads the start-up arguments: the switches are applied (<c>--zoom=&lt;factor&gt;</c> sets the UI zoom setting,
	/// <see cref="Settings.UiZoom"/>, as the View menu does, 0.5 to 8 with a decimal point, 2026-11-01, the user's
	/// request for the figure harness; an unknown switch is logged and ignored), and the first other argument is the
	/// project file to open, returned, or null.
	/// </summary>
	private static string? ReadArguments(string[] args)
	{
		if (args.Length > 0)
		{
			Logger.Info("Startup arguments: {Args}", string.Join(" ", args));
		}
		string? file = null;
		foreach (string arg in args)
		{
			const string zoomSwitch = "--zoom=";
			if (arg.StartsWith(zoomSwitch, StringComparison.OrdinalIgnoreCase))
			{
				if (double.TryParse(arg.AsSpan(zoomSwitch.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom) && zoom is >= 0.5 and <= 8)
				{
					Settings.Default.UiZoom = zoom;
					Logger.Info("UI zoom {Zoom} ({Switch}): the setting, kept for the next start too", zoom, arg);
				}
				else
				{
					Logger.Warn("The start-up switch {Switch} is ignored: the zoom must be a number from 0.5 to 8", arg);
				}
			}
			else if (arg.StartsWith("--", StringComparison.Ordinal))
			{
				Logger.Warn("The start-up switch {Switch} is unknown and ignored", arg);
			}
			else
			{
				file ??= arg;
			}
		}
		return file;
	}

	/// <summary>
	/// Reads the machine's facts, sets the preview quality of its tier (<see cref="PreviewDefaults"/>) and writes the
	/// settings file at once, so the choice is made once and deleting the file redoes it; the fixed defaults stay the
	/// initialisers, which the figure harness and the designer rely on. Returns the facts and the tier for the log.
	/// </summary>
	private static (MachineFacts Facts, PreviewTier Tier) ChoosePreviewDefaults()
	{
		long started = Stopwatch.GetTimestamp();
		MachineFacts facts = MachineFacts.Read();
		PreviewTier tier = PreviewDefaults.TierOf(facts);
		Settings.Default.SetPreviewQuality(PreviewDefaults.QualityOf(tier), PreviewDefaults.DitherBitsOf(tier));
		Logger.Debug("First start: the machine's facts read in {Ms:0} ms, tier {Tier}", Stopwatch.GetElapsedTime(started).TotalMilliseconds, tier);
		try
		{
			Settings.Default.Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.Warn(ex, "The settings file could not be written at the first start; the preview quality will be chosen again at the next start");
		}
		return (facts, tier);
	}

	/// <inheritdoc/>
	protected override void OnExit(ExitEventArgs e)
	{
		Logger.Info("Application exit, code {Code}", e.ApplicationExitCode);
		base.OnExit(e);
		LogManager.Shutdown();
	}
}
