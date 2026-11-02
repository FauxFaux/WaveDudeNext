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

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WaveDudeNext.Engine;
using WaveDudeNext.Model;
using WaveDudeNext.Runs;
using WaveDudeNext.WPF.Rendering;

namespace WaveDudeNext.WPF;

/// <summary>
/// Handlers of <see cref="Commands.SimulationCommands"/>: the window owns the current
/// <see cref="Run"/> and its frame store. A run snapshots the project when it starts; editing
/// the project meanwhile is allowed and does not affect it. The store lives until the run is
/// replaced, the project is replaced or the window closes.
/// </summary>
public partial class MainWindow
{
	/// <summary>Identifies the <see cref="Run"/> property.</summary>
	public static readonly DependencyProperty RunProperty = DependencyProperty.Register(
		nameof(Run), typeof(SimulationRun), typeof(MainWindow),
		new PropertyMetadata(null, (_, _) => CommandManager.InvalidateRequerySuggested()));

	/// <summary>The current run (active or finished, its frames in the preview), or null.</summary>
	public SimulationRun? Run
	{
		get => (SimulationRun?)GetValue(RunProperty);
		set => SetValue(RunProperty, value);
	}

	private void CanRun(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Run?.IsActive != true && !IsExporting;

	private void OnRun(object sender, ExecutedRoutedEventArgs e)
	{
		try
		{
			StartRun();
		}
		catch (Exception ex) when (ex is RunSpecException or IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Logger.Error(ex, "The run could not start: {Message}", ex.Message);
			ShowError((string)FindResource("RunError"), ex);
		}
	}

	/// <summary>
	/// The choices of Simulation > RAM cache size: <see cref="FrameMemory.Automatic"/> (the application
	/// decides from what is free) and 10 % to 100 % of the RAM as an upper bound (2026-10-15, the user's design).
	/// </summary>
	public static int[] FrameMemoryOptions { get; } = [FrameMemory.Automatic, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

	/// <summary>
	/// The preview quality chosen in the View menu (persisted user settings, 2026-10-15): how much of a run is
	/// captured and kept, for weaker machines. Applied to the preview's runs and scratch frames; an export's run
	/// is always <see cref="PreviewQuality.Full"/>.
	/// </summary>
	private static PreviewQuality PreviewQualitySetting => new(
		Math.Max(1, Settings.Default.PreviewResolution),
		Math.Max(1, Settings.Default.PreviewFrames),
		Settings.Default.PreviewSampleBytes == 1 ? FrameFormat.Index : FrameFormat.Half);

	/// <summary>Decodes the scene image, snapshots the project, checks the disk, creates the store and starts the worker.</summary>
	/// <param name="automatic">Started after an edit rather than by the user: the preview keeps its position.</param>
	/// <param name="trackIntensity">Store the intensity plane whatever the project needs (an export of the intensity asks for it).</param>
	/// <param name="fullQuality">Capture every cell of every frame as a half whatever the View menu says (an export needs that).</param>
	/// <param name="intensityRoot">With <paramref name="fullQuality"/>: store the intensity as its square root, the preview's, or as I (an OpenEXR export of I).</param>
	private void StartRun(bool automatic = false, bool trackIntensity = false, bool fullQuality = false, bool intensityRoot = true)
	{
		if (!Settings.Default.UseGpu)
		{
			Logger.Info("Running on the CPU: 'Use the GPU' is off in the Simulation menu");
		}
		PreviewQuality quality = fullQuality ? PreviewQuality.Full with { IntensityRoot = intensityRoot } : PreviewQualitySetting;
		RunSpec spec = RunSpec.Create(Project, LoadSceneImage(), Settings.Default.UseGpu, trackIntensity, quality);
		// the frames stay in memory up to the budget (the user's share of the RAM, capped by what is free now); only
		// the rest needs room on the cache drive (2026-10-14). The outgoing run's frames count as free: DiscardRun
		// below gives them back, and the new store allocates its memory tier only once they are gone (2026-10-16)
		long budget = FrameMemory.Budget(Settings.Default.FrameMemoryPercent, Run?.Store.RamBytes ?? 0);
		int ramFrames = Math.Min(spec.StoredFrameCount, FrameStore.FramesInBudget(spec.Shape, spec.Layers, budget));
		if (ramFrames < spec.StoredFrameCount)
		{
			FrameCache.CheckSpace(FrameStore.SizeOf(spec.Shape, spec.Layers, spec.StoredFrameCount - ramFrames));
		}
		Task memoryReleased = DiscardRun();
		FrameStore store = FrameStore.Create(FrameCache.NewPath(), spec.Shape, spec.Layers, spec.StoredFrameCount, spec.CaptureEvery, budget, memoryReleased);
		// the run's progress lands on the UI thread below input priority (2026-10-16): posted at normal priority, the
		// frame counts kept the input queue starved for the whole run, since a new one was posted the moment the last
		// was handled, and every click and key was delivered after the run (the watchdog's input-priority ticks were
		// late, the normal-priority ones on time)
		var run = new SimulationRun(spec, store, new DispatcherSynchronizationContext(Dispatcher, DispatcherPriority.Background), automatic);
		run.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(SimulationRun.State))
			{
				CommandManager.InvalidateRequerySuggested();
			}
		};
		run.Finished += (_, _) => OnRunFinished();
		Logger.Info("{Kind} started: {Steps} steps, {Stored} of {Frames} frames stored as {Width}×{Height} ({Quality}), {RamFrames} in memory ({RamBytes:0.0} GB) and {DiskFrames} on disk; edits made from now on do not affect it",
			automatic ? "Re-run" : "Run", spec.Steps, spec.StoredFrameCount, spec.FrameCount, spec.Shape.Width, spec.Shape.Height, spec.Quality, store.RamFrames, FrameMemory.ToGigabytes(store.RamBytes), spec.StoredFrameCount - store.RamFrames);
		IsRunStale = false;
		IsProbesStale = false;
		Run = run;
		run.Start();
	}

	/// <summary>The scene image's full path: as given when rooted, otherwise relative to the project file.</summary>
	/// <exception cref="RunSpecException">The path is relative and the project has not been saved.</exception>
	private string ResolveScenePath()
	{
		string image = Project.Scene.Image;
		if (image.Length == 0)
		{
			throw new RunSpecException((string)FindResource("RunErrorNoImage"));
		}
		if (Path.IsPathRooted(image))
		{
			return image;
		}
		string? folder = Filename != null ? Path.GetDirectoryName(Filename) : null;
		if (folder == null)
		{
			throw new RunSpecException((string)FindResource("RunErrorUnsavedImagePath"));
		}
		return Path.GetFullPath(image, folder);
	}

	private void CanControlRun(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Run?.IsActive == true;

	/// <summary>Stop applies to a run in progress, or to an export in progress (Esc cancels it).</summary>
	private void CanStop(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Run?.IsActive == true || Export?.IsActive == true;

	/// <summary>Play pressed: a missing or stale run is started like an automatic one (position kept, not followed); errors go to the log and status bar. Not while an export reads the store.</summary>
	private void OnEnsureRun(object sender, ExecutedRoutedEventArgs e)
	{
		if (IsExporting || Run?.IsActive == true || (Run != null && !IsRunStale && !IsProbesStale))
		{
			return;
		}
		try
		{
			StartRun(automatic: true);
		}
		catch (Exception ex) when (ex is RunSpecException or IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Logger.Error(ex, "The run could not start: {Message}", ex.Message);
		}
	}

	private void OnTogglePause(object sender, ExecutedRoutedEventArgs e) => Run?.TogglePause();

	/// <summary>Cancels the export when one is running (it has the user's attention then), else stops the run.</summary>
	private void OnStop(object sender, ExecutedRoutedEventArgs e)
	{
		if (Export?.IsActive == true)
		{
			Export.Cancel();
		}
		else
		{
			Run?.Stop();
		}
	}

	/// <summary>
	/// Drops the current run: stops it and disposes its store (deleting the cache file) once the worker has
	/// let go of it. Called before a new run, when the project is replaced and on closing.
	/// </summary>
	/// <returns>Completes when the old store is disposed and its memory given back (at once when there was no run); the next run's store waits for it before allocating its memory tier.</returns>
	private Task DiscardRun()
	{
		if (Export?.IsActive == true)
		{
			// the export reads this run's store; a project replaced under it ends it with what it has
			Logger.Warn("Export cancelled: its run is being discarded");
			Export.Cancel();
		}
		SimulationRun? old = Run;
		if (old != null)
		{
			Logger.Debug("Run discarded: {State}, {Frames} frames, cache {Path}", old.State, old.FrameCount, old.Store.Path);
		}
		// the run goes first: with it gone the preview shows the scratch frame; clearing the stale flag before
		// would make it pick the old store for a moment (and wait on its file being deleted)
		Run = null;
		IsRunStale = false;
		IsProbesStale = false;
		if (old == null)
		{
			return Task.CompletedTask;
		}
		old.Stop();
		// on the thread pool: unmapping a run's cache file is quick, but never the UI thread's wait
		return old.Completion.ContinueWith(_ =>
		{
			old.Store.Dispose();
			old.Dispose();
		}, TaskScheduler.Default);
	}

	/// <summary>Synchronous variant for closing: waits (briefly) for the worker so the file is closed before the process ends.</summary>
	private void ShutdownRun()
	{
		SimulationRun? old = Run;
		if (old == null)
		{
			return;
		}
		Run = null;
		old.Stop();
		if (!old.Completion.Wait(TimeSpan.FromSeconds(5)))
		{
			Logger.Warn("The run did not stop in time; its cache file is deleted by the system when the process ends");
		}
		// not awaited: the process exit closes the handles and deletes the file in any case
		Task.Run(() =>
		{
			old.Store.Dispose();
			old.Dispose();
		});
	}
}
