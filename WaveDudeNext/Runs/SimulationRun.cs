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

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using NLog;
using WaveDudeNext.Engine;
using WaveDudeNext.Probes;

namespace WaveDudeNext.Runs;

/// <summary>
/// Drives one engine over one <see cref="RunSpec"/> on a thread-pool task: every step advances the engine and
/// samples the probes, every <see cref="RunSpec.VisualizeEvery"/>-th step is captured into a
/// <see cref="FrameSnapshot"/> and written to the <see cref="FrameStore"/> by a second task, and probe batches
/// and progress are handed to the UI at a bounded rate. The run is never paced: it goes as fast as the engine
/// and the store allow, and the preview plays from the store at its own speed.
/// </summary>
/// <remarks>
/// Threads: the worker owns the engine's stepping; it captures frames (on the GPU a DMA read into a snapshot the engine
/// made in page-locked memory) and hands them to the writer task, which awaits the capture, writes the store and returns
/// the snapshot to a small pool, so converting and storing one frame overlaps computing the next. Every public
/// property and event is set or raised through the <see cref="SynchronizationContext"/> given to the constructor
/// (the WPF dispatcher), so they can be bound directly; with a null context they run on the worker (for
/// headless use). The <see cref="Store"/> stays the caller's to dispose, after <see cref="Completion"/> has
/// completed.
/// </remarks>
public sealed class SimulationRun : INotifyPropertyChanged, IDisposable
{
	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	/// <summary>Least interval between progress (<see cref="StepsDone"/>) posts.</summary>
	/// <summary>
	/// The one heartbeat to the user interface (2026-10-16): steps done, frames stored and the probe batch go in a
	/// single post at most thirty times a second. Separate posts (steps at 10 Hz, frames at 30, probes at 20) were
	/// about 60 operations a second, each buying WPF a full render pass of the window, and with the presenter's and
	/// the pane's own work the UI thread ran over 200 render passes a second and served input late.
	/// </summary>
	private const long HeartbeatIntervalMs = 33;

	/// <summary>Least interval between probe batches (<see cref="ProbesUpdated"/>).</summary>

	/// <summary>Snapshots in circulation between the stepping task and the writer: one being captured, one or two being written.</summary>
	private const int SnapshotPoolSize = 3;

	private readonly SynchronizationContext? _context;
	private readonly ManualResetEventSlim _gate = new(true);
	private readonly CancellationTokenSource _cancel = new();
	private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _latestFrameCount;
	private RunState _state = RunState.Created;
	private int _stepsDone;
	private int _frameCount;
	private string _engineDescription = "";
	private string _engineNote = "";
	private TimeSpan _elapsed;
	private Exception? _error;

	/// <summary>Prepares a run; nothing happens until <see cref="Start"/>.</summary>
	/// <param name="spec">What to run.</param>
	/// <param name="store">Where the frames go; created for <see cref="RunSpec.FrameCount"/> frames of the spec's size and layers.</param>
	/// <param name="context">Where properties and events are published; null runs them on the worker.</param>
	/// <param name="isAutomatic">Started by the application after an edit rather than by the user (<see cref="IsAutomatic"/>).</param>
	/// <exception cref="ArgumentException">The store does not match the spec.</exception>
	public SimulationRun(RunSpec spec, FrameStore store, SynchronizationContext? context, bool isAutomatic = false)
	{
		IsAutomatic = isAutomatic;
		if (!store.Shape.SameLayoutAs(spec.Shape) || store.Layers != spec.Layers || store.Capacity < spec.StoredFrameCount)
		{
			throw new ArgumentException("The frame store does not fit the run.", nameof(store));
		}
		Spec = spec;
		Store = store;
		Frames = spec.FrameStride == 1 ? store : new StridedFrameSource(store, spec.FrameStride, spec.FrameCount);
		_context = context;
		Traces = new ProbeTrace[spec.Probes.Length];
		for (int i = 0; i < Traces.Length; i++)
		{
			Traces[i] = new ProbeTrace();
		}
	}

	/// <inheritdoc/>
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>A probe batch was applied to <see cref="Traces"/>; at most every 50 ms, plus a final one.</summary>
	public event EventHandler<ProbeUpdate>? ProbesUpdated;

	/// <summary>The run reached a final state; raised exactly once, after <see cref="State"/> changed.</summary>
	public event EventHandler? Finished;

	/// <summary>What is being run.</summary>
	public RunSpec Spec { get; }

	/// <summary>Where the stored frames are (every <see cref="RunSpec.FrameStride"/>-th one); readable from any thread up to <see cref="FrameStore.Count"/>.</summary>
	public FrameStore Store { get; }

	/// <summary>
	/// The frames in timeline numbering, what the preview reads: the store itself when every frame is stored,
	/// else a <see cref="StridedFrameSource"/> in which a stored frame stands in for the frames up to the next one.
	/// </summary>
	public IFrameSource Frames { get; }

	/// <summary>Whether the application started the run on its own (re-run after an edit); the preview then keeps its position instead of following.</summary>
	public bool IsAutomatic { get; }

	/// <summary>Current state; final states are Completed, Cancelled and Failed.</summary>
	public RunState State
	{
		get => _state;
		private set => Set(ref _state, value);
	}

	/// <summary>Whether the run has started and not yet ended.</summary>
	public bool IsActive => State is RunState.Starting or RunState.Running or RunState.Paused;

	/// <summary>Whether the run has reached a final state (Completed, Cancelled or Failed).</summary>
	public bool IsFinished => State is RunState.Completed or RunState.Cancelled or RunState.Failed;

	/// <summary>Steps completed, refreshed about ten times a second and exactly on pause and at the end.</summary>
	public int StepsDone
	{
		get => _stepsDone;
		private set => Set(ref _stepsDone, value);
	}

	/// <summary>Steps the run will do.</summary>
	public int TotalSteps => Spec.Steps;

	/// <summary>Timeline frames that can be shown so far: the stored frames × <see cref="RunSpec.FrameStride"/>, capped at <see cref="RunSpec.FrameCount"/> (equals <see cref="IFrameSource.Count"/> of <see cref="Frames"/> once the post has arrived).</summary>
	public int FrameCount
	{
		get => _frameCount;
		private set => Set(ref _frameCount, value);
	}

	/// <summary>The engine in use ("CPU (16 threads)", "GPU: … (OpenCL)"); empty until the engine exists.</summary>
	public string EngineDescription
	{
		get => _engineDescription;
		private set => Set(ref _engineDescription, value);
	}

	/// <summary>"GPU not used: …" when the GPU was asked for but the CPU engine runs; otherwise empty.</summary>
	public string EngineNote
	{
		get => _engineNote;
		private set => Set(ref _engineNote, value);
	}

	/// <summary>Wall time spent stepping, excluding pauses; final once the run ended.</summary>
	public TimeSpan Elapsed
	{
		get => _elapsed;
		private set => Set(ref _elapsed, value);
	}

	/// <summary>Why the run failed, when <see cref="State"/> is <see cref="RunState.Failed"/>.</summary>
	public Exception? Error
	{
		get => _error;
		private set => Set(ref _error, value);
	}

	/// <summary>The UI-side copy of every probe's recording, one per <see cref="RunSpec.Probes"/>, filled from the batches.</summary>
	public ProbeTrace[] Traces { get; }

	/// <summary>Completes (never faults) once the worker has disposed the engine and published the final state.</summary>
	public Task Completion => _completion.Task;

	/// <summary>Starts the worker. May be called once.</summary>
	/// <exception cref="InvalidOperationException">Already started.</exception>
	public void Start()
	{
		if (State != RunState.Created)
		{
			throw new InvalidOperationException("The run has already been started.");
		}
		State = RunState.Starting;
		// a thread of its own, not a pool thread (2026-10-16): the loop lives for the whole run and mostly waits on the
		// GPU, which is not what pool threads are for; the writer below is the same. (A below-normal priority for the
		// two was tried, to keep their busy-waits off the renderer's cores, and slowed the run by a quarter.)
		Task.Factory.StartNew(RunLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
	}

	/// <summary>Parks the worker before its next step; <see cref="State"/> becomes Paused when it gets there.</summary>
	public void Pause() => _gate.Reset();

	/// <summary>Lets a paused worker continue.</summary>
	public void Resume() => _gate.Set();

	/// <summary>Pauses a running run or resumes a paused one, judged by the request, not by the (lagging) <see cref="State"/>.</summary>
	public void TogglePause()
	{
		if (_gate.IsSet)
		{
			Pause();
		}
		else
		{
			Resume();
		}
	}

	/// <summary>Whether a pause has been requested (and not yet resumed).</summary>
	public bool IsPauseRequested => !_gate.IsSet;

	/// <summary>Ends the run before its last step; also wakes a paused worker. Harmless after the end.</summary>
	public void Stop()
	{
		_cancel.Cancel();
		_gate.Set();
	}

	/// <summary>Stops the run. The store is not disposed here.</summary>
	public void Dispose()
	{
		Stop();
		_cancel.Dispose();
		_gate.Dispose();
	}

	/// <summary>The worker: a port of the legacy runner's loop without video sinks and bitmaps.</summary>
	private void RunLoop()
	{
		var stopwatch = Stopwatch.StartNew();
		IWaveEngine? engine = null;
		RunState final = RunState.Completed;
		Exception? error = null;
		int stepsDone = 0;
		try
		{
			CancellationToken token = _cancel.Token;
			SceneData scene = Spec.BuildScene();
			ProbeSampler? sampler = Spec.Probes.Length > 0 ? ProbeSampler.Build(Spec.Probes, scene.CanvasWidth, scene.CanvasHeight, scene.Rim) : null;
			ProbeCollector? collector = sampler != null ? new ProbeCollector(sampler) : null;
			engine = WaveEngineFactory.Create(scene, Spec.Engine, Spec.PreferGpu, Spec.Emitters, out string note);
			Logger.Info("Engine: {0}", engine.Description);
			if (note.Length > 0)
			{
				Logger.Warn(note);
			}
			string description = engine.Description;
			Post(() =>
			{
				EngineDescription = description;
				EngineNote = note;
			});
			engine.SetProbes(sampler);
			token.ThrowIfCancellationRequested();
			Post(() => State = RunState.Running);

			int every = Spec.VisualizeEvery;
			int captureEvery = Spec.CaptureEvery;
			bool trackIntensity = Spec.TrackIntensity;
			int frame = 0;
			long lastProgressMs = 0;
			// where the time goes, for the log: stepping the engine, issuing the frame captures, and (on the
			// writer task) waiting for the captures and storing them
			long computeTicks = 0;
			long captureTicks = 0;
			long storeTicks = 0;
			long writerWaitTicks = 0;
			var pool = new BlockingCollection<FrameSnapshot>();
			for (int i = 0; i < SnapshotPoolSize; i++)
			{
				// the engine's own snapshots: page-locked on the GPU, so its reads are DMA the stepping thread does not wait for (2026-11-03)
				pool.Add(engine.CreateSnapshot(Spec.Shape, trackIntensity));
			}
			var pending = new BlockingCollection<FrameSnapshot>();
			IWaveEngine captured = engine;
			Task? writer = null;
			_drainWriter = () =>
			{
				if (!pending.IsAddingCompleted)
				{
					pending.CompleteAdding();
				}
				writer?.Wait();
			};
			writer = Task.Factory.StartNew(() =>
			{
				foreach (FrameSnapshot snapshot in pending.GetConsumingEnumerable())
				{
					long w0 = Stopwatch.GetTimestamp();
					captured.AwaitFrame(snapshot);
					long w1 = Stopwatch.GetTimestamp();
					Store.Write(snapshot.Frame, snapshot);
					PublishFrameCount(CoveredFrames(snapshot.Frame + 1));
					Interlocked.Add(ref writerWaitTicks, w1 - w0);
					Interlocked.Add(ref storeTicks, Stopwatch.GetTimestamp() - w1);
					pool.Add(snapshot);
				}
			}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
			for (int i = 0; i < Spec.Steps; i++)
			{
				if (!_gate.IsSet)
				{
					// paused: report the exact step count; the time of the pause does not count
					stopwatch.Stop();
					int done = stepsDone;
					Post(() =>
					{
						StepsDone = done;
						FrameCount = Volatile.Read(ref _latestFrameCount); // the paced publisher may have skipped the newest
						State = RunState.Paused;
					});
					_gate.Wait(token);
					Post(() => State = RunState.Running);
					stopwatch.Start();
				}
				token.ThrowIfCancellationRequested();
				long t0 = Stopwatch.GetTimestamp();
				engine.AdvanceOneStep();
				if (sampler != null)
				{
					engine.SampleProbes();
				}
				stepsDone = i + 1;
				long t1 = Stopwatch.GetTimestamp();
				computeTicks += t1 - t0;
				if (i % every == 0)
				{
					// the series up to this step and the line profiles of this timeline frame (kept per frame, stored
					// or not, so the preview shows the profile of the frame it scrubs to)
					collector?.Collect(engine, stepsDone);
					collector?.CollectProfiles(engine, stepsDone);
				}
				if (i % captureEvery == 0 && frame < Store.Capacity)
				{
					// a free snapshot (waiting here is the back pressure when the writer lags), captured
					// without waiting for the copy; the writer awaits and stores it
					FrameSnapshot snapshot = pool.Take(token);
					snapshot.Frame = frame;
					snapshot.Step = i;
					engine.CaptureFrame(snapshot, trackIntensity);
					pending.Add(snapshot);
					frame++;
					captureTicks += Stopwatch.GetTimestamp() - t1;
				}
				if (stopwatch.ElapsedMilliseconds - lastProgressMs >= HeartbeatIntervalMs)
				{
					lastProgressMs = stopwatch.ElapsedMilliseconds;
					PublishHeartbeat(engine, collector, stepsDone);
				}
			}
			PublishHeartbeat(engine, collector, stepsDone);
			DrainWriter();
			Logger.Debug("Run timing: {Compute:0.00} s stepping ({PerStep:0.00} ms/step), {Capture:0.00} s issuing captures; writer: {Wait:0.00} s waiting for frames, {Store:0.00} s storing ({PerFrame:0.0} ms/frame)",
				computeTicks / (double)Stopwatch.Frequency, computeTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, stepsDone),
				captureTicks / (double)Stopwatch.Frequency, writerWaitTicks / (double)Stopwatch.Frequency,
				storeTicks / (double)Stopwatch.Frequency, storeTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, frame));
		}
		catch (OperationCanceledException)
		{
			final = RunState.Cancelled;
		}
		catch (OutOfMemoryException)
		{
			final = RunState.Failed;
			error = new OutOfMemoryException("There is not enough memory for a simulation of this size. Use a smaller canvas.");
			Logger.Error(error.Message);
		}
		catch (Exception ex)
		{
			final = RunState.Failed;
			error = ex;
			Logger.Error(ex, "The simulation failed: {0}", ex.Message);
		}
		finally
		{
			try
			{
				// captures still in flight must land before the engine goes (the writer stores them, cheaply)
				DrainWriter();
				(engine as IDisposable)?.Dispose();
			}
			catch (Exception ex)
			{
				Logger.Warn(ex, "The engine could not be released cleanly.");
			}
		}
		stopwatch.Stop();
		TimeSpan elapsed = stopwatch.Elapsed;
		int finalSteps = stepsDone;
		int finalFrames = CoveredFrames(Store.Count);
		// where the stored frames landed, which the start line can only plan: a failed allocation sends the rest to disk
		int inMemory = Store.FramesInMemory, onDisk = Store.FramesOnDisk;
		Post(() =>
		{
			StepsDone = finalSteps;
			FrameCount = finalFrames;
			Elapsed = elapsed;
			Error = error;
			State = final;
			Logger.Info("Run {0}: {1} of {2} steps, {3} frames, {4:0.0} s; {5} stored, {6} in memory ({7:0.0} GB) and {8} on disk ({9:0.0} GB)",
				final, finalSteps, Spec.Steps, finalFrames, elapsed.TotalSeconds, inMemory + onDisk,
				inMemory, FrameMemory.ToGigabytes(inMemory * Store.FrameBytes), onDisk, FrameMemory.ToGigabytes(onDisk * Store.FrameBytes));
			Finished?.Invoke(this, EventArgs.Empty);
		});
		_completion.TrySetResult();
	}

	/// <summary>Lets the writer finish the captures it holds; set by the loop once the writer exists.</summary>
	private Action? _drainWriter;

	private void DrainWriter()
	{
		Action? drain = _drainWriter;
		_drainWriter = null;
		drain?.Invoke();
	}

	/// <summary>Collects the series the engine has (the profiles are collected per frame), takes a batch and applies it to <see cref="Traces"/> on the UI context.</summary>
	/// <summary>The heartbeat: steps done, the frames the writer has stored so far, and the probe batch, in one post.</summary>
	private void PublishHeartbeat(IWaveEngine engine, ProbeCollector? collector, int stepsDone)
	{
		ProbeUpdate? update = null;
		if (collector != null)
		{
			collector.Collect(engine, stepsDone);
			update = collector.TakeForUi();
		}
		int done = stepsDone;
		Post(() =>
		{
			StepsDone = done;
			FrameCount = Volatile.Read(ref _latestFrameCount);
			if (update != null)
			{
				for (int i = 0; i < Traces.Length && i < update.Samples.Length; i++)
				{
					Traces[i].Series.Add(update.Samples[i]);
					foreach (float[] profile in update.Profiles[i])
					{
						Traces[i].AddProfile(profile, update.Lengths[i]);
					}
					Traces[i].StepsSampled = update.StepsDone;
				}
				ProbesUpdated?.Invoke(this, update);
			}
		});
	}

	/// <summary>Timeline frames covered by the first <paramref name="stored"/> stored frames: each stands for <see cref="RunSpec.FrameStride"/> of them, never beyond the run.</summary>
	private int CoveredFrames(int stored) => (int)Math.Min((long)stored * Spec.FrameStride, Spec.FrameCount);

	/// <summary>Records the frames stored so far; the heartbeat, a pause and the end of the run publish it.</summary>
	private void PublishFrameCount(int count) => Volatile.Write(ref _latestFrameCount, count);

	/// <summary>Runs <paramref name="action"/> on the UI context, or inline when there is none.</summary>
	private void Post(Action action)
	{
		if (_context != null)
		{
			_context.Post(_ => action(), null);
		}
		else
		{
			action();
		}
	}

	private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return;
		}
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		if (name == nameof(State))
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFinished)));
		}
	}
}
