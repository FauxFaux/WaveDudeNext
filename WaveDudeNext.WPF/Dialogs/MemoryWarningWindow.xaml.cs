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


using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NLog;
using WaveDudeNext.Engine;
using WaveDudeNext.Runs;
using WaveDudeNext.WPF.Theming;

namespace WaveDudeNext.WPF.Dialogs;

/// <summary>What the memory warning was answered with.</summary>
/// <param name="Quality">The preview quality to run at: the current one with each ticked lever a notch lower, the current one itself when nothing was ticked.</param>
/// <param name="CacheAuto">"RAM cache size to Auto" was ticked: the caller sets the share to automatic.</param>
/// <param name="Mute">"Do not warn me again" was ticked: the caller clears the setting.</param>
public sealed record MemoryWarningChoice(PreviewQuality Quality, bool CacheAuto, bool Mute);

/// <summary>
/// The memory warning (2026-11-03, the user's design): shown before a preview run whose frames would not all fit in the
/// RAM cache (Simulation > Warn when a run does not fit in RAM). Names the run's need and the cache's budget, offers
/// a lever that sets the RAM cache size to Auto (the budget rises; greyed when that gains less than a frame) and three
/// that each lower the preview quality a notch (half the resolution, half the frames, 8-bit samples), pre-ticked with
/// the fewest that make the run fit and the mildest among as many, a line under them with the ticks' result, and "Do
/// not warn me again". Run returns the choice, Cancel or Esc null; the caller applies it. The arithmetic is
/// <see cref="RunSpec.StoreBytesAt"/>. UI thread only.
/// </summary>
public partial class MemoryWarningWindow : Window
{
	/// <summary>The largest divisor the View menu offers: a lever there has no notch left.</summary>
	private const int MaxDivisor = 4;

	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
	private readonly RunSpec _spec;
	private readonly long _budget;
	private readonly long _autoBudget;
	private readonly PreviewQuality _current;
	private MemoryWarningChoice? _choice;

	private MemoryWarningWindow(RunSpec spec, long budget, long autoBudget, PreviewQuality current)
	{
		InitializeComponent();
		SourceInitialized += (_, _) => ThemeController.Instance.ApplyTitleBar(this);
		_spec = spec;
		_budget = budget;
		_autoBudget = autoBudget;
		_current = current;
		messageText.Text = string.Format((string)FindResource("MemoryWarningTextFormat"), FrameMemory.Gigabytes(spec.StoreBytes), FrameMemory.Gigabytes(budget));
		cacheBox.Content = string.Format((string)FindResource("MemoryWarningCacheFormat"), FrameMemory.Gigabytes(autoBudget));
		// a lever with nothing to offer is greyed: the cache when Auto gains less than a frame (the share is Auto already, or
		// the free memory binds it, which two readings of the memory status can differ about by a few kilobytes), a quality
		// lever at its lowest notch
		cacheBox.IsEnabled = autoBudget - budget >= FrameStore.MemoryOf(spec.Shape, spec.Layers, 1);
		resolutionBox.IsEnabled = current.ResolutionDivisor < MaxDivisor;
		framesBox.IsEnabled = current.FrameDivisor < MaxDivisor;
		samplesBox.IsEnabled = current.Format == FrameFormat.Half;
		(bool cache, bool resolution, bool frames, bool samples) = Proposal();
		cacheBox.IsChecked = cache;
		resolutionBox.IsChecked = resolution;
		framesBox.IsChecked = frames;
		samplesBox.IsChecked = samples;
		foreach (CheckBox box in Levers)
		{
			box.Checked += (_, _) => ShowResult();
			box.Unchecked += (_, _) => ShowResult();
		}
		ShowResult();
		FocusManager.SetFocusedElement(this, Levers.FirstOrDefault(box => box.IsEnabled) ?? (UIElement)runButton);
	}

	/// <summary>The lever boxes in their order on screen.</summary>
	private CheckBox[] Levers => [cacheBox, resolutionBox, framesBox, samplesBox];

	/// <summary>
	/// Shows the warning modally over <paramref name="owner"/> for a run of <paramref name="spec"/> whose frames exceed
	/// <paramref name="budget"/> bytes of memory, <paramref name="autoBudget"/> being the budget with the RAM cache size at
	/// Auto; <paramref name="current"/> is the preview quality the spec was made with.
	/// </summary>
	/// <returns>The choice Run was pressed with, or null for Cancel, Esc or the close box.</returns>
	public static MemoryWarningChoice? Show(Window owner, RunSpec spec, long budget, long autoBudget, PreviewQuality current)
	{
		Logger.Debug("Memory warning: the run's frames need {Need:0.0} GB, the RAM cache has {Budget:0.0} GB ({Auto:0.0} GB at Auto)", FrameMemory.ToGigabytes(spec.StoreBytes), FrameMemory.ToGigabytes(budget), FrameMemory.ToGigabytes(autoBudget));
		SystemSounds.Question.Play();
		var window = new MemoryWarningWindow(spec, budget, autoBudget, current) { Owner = owner };
		window.ShowDialog();
		if (window._choice == null)
		{
			Logger.Debug("Memory warning answered: cancelled");
		}
		else
		{
			Logger.Debug("Memory warning answered: run at {Quality} ({Need:0.0} GB), RAM cache to Auto: {Cache}, warn again: {Again}", window._choice.Quality, FrameMemory.ToGigabytes(spec.StoreBytesAt(window._choice.Quality)), window._choice.CacheAuto, !window._choice.Mute);
		}
		return window._choice;
	}

	/// <summary>The budget the cache box stands for: the automatic one when ticked, else the current.</summary>
	private long BudgetOf(bool cache) => cache ? _autoBudget : _budget;

	/// <summary>The quality the ticks stand for: the current one with each ticked divisor doubled (to the View menu's largest) and the samples to palette indices.</summary>
	private PreviewQuality QualityOf(bool resolution, bool frames, bool samples) => _current with
	{
		ResolutionDivisor = resolution ? Math.Min(_current.ResolutionDivisor * 2, MaxDivisor) : _current.ResolutionDivisor,
		FrameDivisor = frames ? Math.Min(_current.FrameDivisor * 2, MaxDivisor) : _current.FrameDivisor,
		Format = samples ? FrameFormat.Index : _current.Format,
	};

	/// <summary>The quality the boxes are ticked for right now.</summary>
	private PreviewQuality Chosen => QualityOf(resolutionBox.IsChecked == true, framesBox.IsChecked == true, samplesBox.IsChecked == true);

	/// <summary>
	/// The ticks to propose: among the combinations of the levers that have something to offer, the fewest ticks that
	/// make the run fit, and the mildest among as many (the largest need that still fits, so the cache, which leaves the
	/// need as it is, comes before any loss of quality); a tie goes to the first-start ladder's order, 8-bit samples
	/// before half the frames before half the resolution (the mask's bits, the sort being stable); every lever that can
	/// when none does.
	/// </summary>
	private (bool Cache, bool Resolution, bool Frames, bool Samples) Proposal()
	{
		var fitting = new List<(bool Cache, bool Resolution, bool Frames, bool Samples, int Ticks, long Bytes)>();
		for (int mask = 1; mask < 16; mask++)
		{
			bool samples = (mask & 1) != 0;
			bool frames = (mask & 2) != 0;
			bool resolution = (mask & 4) != 0;
			bool cache = (mask & 8) != 0;
			if ((cache && !cacheBox.IsEnabled) || (resolution && !resolutionBox.IsEnabled) || (frames && !framesBox.IsEnabled) || (samples && !samplesBox.IsEnabled))
			{
				continue;
			}
			long bytes = _spec.StoreBytesAt(QualityOf(resolution, frames, samples));
			if (bytes <= BudgetOf(cache))
			{
				fitting.Add((cache, resolution, frames, samples, (cache ? 1 : 0) + (resolution ? 1 : 0) + (frames ? 1 : 0) + (samples ? 1 : 0), bytes));
			}
		}
		if (fitting.Count == 0)
		{
			return (cacheBox.IsEnabled, resolutionBox.IsEnabled, framesBox.IsEnabled, samplesBox.IsEnabled);
		}
		var best = fitting.OrderBy(c => c.Ticks).ThenByDescending(c => c.Bytes).First();
		return (best.Cache, best.Resolution, best.Frames, best.Samples);
	}

	/// <summary>The line under the levers: what the ticked choice needs, and whether it fits the ticked budget or how much of it still goes to disk.</summary>
	private void ShowResult()
	{
		long bytes = _spec.StoreBytesAt(Chosen);
		long budget = BudgetOf(cacheBox.IsChecked == true);
		resultText.Text = bytes <= budget
			? string.Format((string)FindResource("MemoryWarningFitsFormat"), FrameMemory.Gigabytes(bytes))
			: string.Format((string)FindResource("MemoryWarningSpillsFormat"), FrameMemory.Gigabytes(bytes), FrameMemory.Gigabytes(bytes - budget));
	}

	private void OnRun(object sender, RoutedEventArgs e)
	{
		_choice = new MemoryWarningChoice(Chosen, cacheBox.IsChecked == true, muteBox.IsChecked == true);
		DialogResult = true;
	}
}
