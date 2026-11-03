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

using System.Globalization;
using System.Numerics;
using System.Numerics.Tensors;
using WaveDudeNext.Model;
using WaveDudeNext.Probes;

namespace WaveDudeNext.Engine;

/// <summary>
/// CPU implementation of <see cref="IWaveEngine"/>: an explicit leapfrog finite-difference solver for the
/// scalar 2D wave equation on a grid padded by one cell on each side, a port of the Python class
/// WaveSimulator2D (which runs on the GPU with CuPy). Created by
/// <see cref="WaveEngineFactory"/> on the run's worker, which is the only thread that advances it and reads
/// <see cref="FieldData"/> / <see cref="IntensityData"/>. The engine has no thread affinity of its own: any
/// single thread may drive it.
/// </summary>
/// <remarks>
/// <para>
/// Units: grid spacing and time step are both 1, so the wave speed c is in cells (canvas pixels) per step,
/// the per-cell <c>c2</c> is c² = 1/n² for refractive index n, and <see cref="Time"/> counts steps. Source
/// frequencies are angular frequencies in radians per step (<see cref="_levelFrequency"/>).
/// </para>
/// <para>
/// The scheme: laplacian = 3x3 convolution with [[0.066, 0.184, 0.066], [0.184, -1, 0.184], [0.066, 0.184, 0.066]],
/// new u = u + (u - previous u) * d + laplacian * c², with the field outside the domain fixed at 0. The sources
/// are applied before each step as in update_scene / StaticImageScene.update_field:
/// u = u * 0.9 + sin(frequency * t) * 0.1 (<see cref="ApplySources"/>). The field is then advanced by
/// <see cref="_subSteps"/> leapfrog sub-steps (<see cref="UpdateField"/>), the last of which also updates the
/// running intensity. The arrays have a border of one cell (which always stays 0) so that the step needs no edge
/// tests. The rows are divided over all processor cores.
/// </para>
/// <para>
/// The intensity is no longer a single exponential average of WaveVisualizer.update (I = I * 0.98 + u² * 0.02):
/// a single-pole filter like that only rolls off at 6 dB/octave, so it never fully removes the ripple at the
/// source's own oscillation frequency, just delays it - lengthening its time constant mostly adds trail without
/// buying much extra ripple suppression. Instead the same one-pole filter (I = I * alpha + x * (1 - alpha)) is run
/// twice in a row (<see cref="_intensityStage1"/>, then <see cref="_intensity"/>), a critically-damped 2-pole
/// low-pass that rolls off at 12 dB/octave, so for the same responsiveness it suppresses the ripple far more
/// without needing to know the local frequency at every pixel. Two full stages turned out to add more delay than
/// wanted at low frequencies (a short pulse can come and go before the second stage has caught up, understating
/// its peak) for not that much extra benefit over one, so what is actually shown/exported
/// (<see cref="_intensityOut"/>) is a blend of the one-stage and two-stage outputs (<see cref="IntensityBlend"/>) -
/// roughly the middle ground between the two (about 9, not 6 or 12, dB/octave), still well ahead of a single
/// stage at rejecting the ripple but reacting closer to as fast. alpha (<see cref="_intensityAlpha"/>) is derived
/// once per simulation so the time constant of each stage scales inversely with the frequency of the slowest
/// active source, times the user's <see cref="Simulation.TimeDecay"/> - see that property's doc
/// for why.
/// </para>
/// <para>
/// Optionally (<see cref="Simulation.FourthOrderLaplacian"/>), the Laplacian is instead computed with a
/// 4th order accurate, 5-point "cross" stencil (reaching 2 cells away along x and along y) wherever there is
/// room for it - see <see cref="UpdateField"/>. This reduces numerical dispersion (short, under-resolved
/// wavelengths otherwise travel slower than long ones - see the <see cref="Simulation.WaveSpeed"/> doc)
/// at roughly twice the memory reads per step. The outermost ring of the domain still uses the original 3x3
/// kernel, since it only has the usual one cell of border to read from, not the two the wider stencil needs.
/// </para>
/// <para>
/// The 4th order stencil's coefficients (-1/12, 4/3, -5) reach much further from zero than the 3x3 kernel's
/// (0.066, 0.184, -1) - about 7 times further at the highest spatial frequency the grid can represent - so the
/// same c² that is safely stable with the 3x3 kernel can be wildly unstable with the 4th order one (the field
/// blows up: everywhere saturates except at object edges, which is what an invalid/unstable Laplacian looks
/// like). To stay stable without changing the physical wave speed, <see cref="AdvanceOneStep"/> instead takes
/// <see cref="_subSteps"/> smaller time steps per call when the 4th order stencil is used: c² is scaled by
/// 1 / _subSteps² (matching a time step _subSteps times smaller) and the damping factor d by its _subSteps-th
/// root (so _subSteps applications of it compound back to the original d), so the net effect after _subSteps
/// steps is exactly the same physical propagation and damping as one ordinary step - just spread over more,
/// individually stable, time steps. _subSteps is computed once per scene from its highest c²
/// (<see cref="ComputeSubSteps"/>) and is 1 (no sub-stepping, and a multiply by the exact literal 1.0F)
/// whenever the 3x3 kernel is used or the scene's c² does not need it.
/// </para>
/// </remarks>
public sealed class WaveSimulator : IWaveEngine, IDisposable
{
	/// <summary>The engine's log: the grid of every build, at Debug.</summary>
	private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

	/// <summary>
	/// Copies the scene's c² and damping maps into the padded grid, derives the sub-step count (4th order
	/// only, and only when the scene's highest c² needs it) and the matching per-sub-step damping and c²
	/// corrections, indexes the image and object sources, and sets the intensity averaging constant from the
	/// slowest source level in use.
	/// </summary>
	/// <param name="scene">Per-cell c² and damping maps plus the image (green-channel) sources; its arrays are copied.</param>
	/// <param name="options">Frequency scale (see <see cref="_levelFrequency"/>), image source amplitude (1 = the value of the Python code; object sources carry their own), intensity tracking (when off <see cref="IntensityData"/> stays null), stencil order (see the class remarks), time decay (see <see cref="_intensityAlpha"/>) and intensity scale.</param>
	/// <param name="emitters">The emitters of the object sources, or null/empty for none; those positioned outside the canvas are dropped.</param>
	public WaveSimulator(SceneData scene, EngineOptions options, IReadOnlyList<Emitter>? emitters = null)
	{
		double sourceFrequencyScale = options.SourceFrequencyScale;
		_sourceFrequencyScale = sourceFrequencyScale;
		bool trackIntensity = options.TrackIntensity;
		bool use4thOrderLaplacian = options.FourthOrderLaplacian;
		_sourceAmplitude = options.SourceAmplitude;
		_use4thOrderLaplacian = use4thOrderLaplacian;
		_intensityScale = (float)options.IntensityScale;
		_width = scene.Width;
		_height = scene.Height;
		_rim = scene.Rim;
		_canvasWidth = scene.CanvasWidth;
		_canvasHeight = scene.CanvasHeight;
		_openEdges = scene.OpenEdges;
		_stride = _width + 2;
		int cellCount = _stride * (_height + 2);
		_u = new float[cellCount];
		_uPrev = new float[cellCount];
		_c2 = new float[cellCount];
		_damping = new float[cellCount];
		if (trackIntensity)
		{
			_intensity = new float[cellCount];
			_intensityStage1 = new float[cellCount];
			_intensityOut = new float[cellCount];
		}
		_sourceIndex = new int[scene.SourceCount];
		_sourceLevel = new byte[scene.SourceCount];
		LoadScene(scene);
		for (int level = 0; level <= 255; level++)
		{
			_levelFrequency[level] = level / 255.0 * 0.5 * sourceFrequencyScale;
		}

		_timeDecay = options.TimeDecay;
		// the same facts the OpenCL engine logs when its kernels are built (2026-10-27)
		Logger.Debug("CPU engine built: grid {Width} × {Height} (canvas {CanvasWidth} × {CanvasHeight}, rim {Rim}, {Edges}), stride {Stride}, {Stencil}, {Threads} threads",
			_width, _height, _canvasWidth, _canvasHeight, _rim, _openEdges ? "open" : "reflective", _stride, StencilText(_use4thOrderLaplacian, _subSteps), Environment.ProcessorCount);
		SetEmitters(emitters);
	}

	/// <inheritdoc/>
	public void SetEmitters(IReadOnlyList<Emitter>? emitters)
	{
		// object sources: keep only the elements whose position is inside the canvas (a randomized composite
		// element can fall outside)
		var insideSources = new List<Emitter>();
		var insideIndices = new List<int>();
		if (emitters != null)
		{
			foreach (Emitter source in emitters)
			{
				if (source.X >= 0 && source.X < _canvasWidth && source.Y >= 0 && source.Y < _canvasHeight)
				{
					insideSources.Add(source);
					insideIndices.Add((source.Y + 1 + _rim) * _stride + source.X + 1 + _rim);
				}
			}
		}
		_objSources = insideSources.ToArray();
		_objIndex = insideIndices.ToArray();
		_objCycleAnchor = new double[_objSources.Length];
		_objCurrentOffWidth = new int[_objSources.Length];
		_objRngState = new uint[_objSources.Length];
		ResetEmitterState();

		// the intensity's averaging time constant scales inversely with the frequency of the slowest active
		// source (image or object) - see TimeDecay's doc (Simulation) for why, and
		// NominalDecayLevel's doc for the calibration this reproduces
		_intensityAlpha = IntensityRetention(_timeDecay, LowestSourceLevel(_levelUsed, _objSources));
	}

	/// <summary>Every emitter back to its first cycle: the off-width rolled at generate time and the seed it started with.</summary>
	private void ResetEmitterState()
	{
		for (int i = 0; i < _objSources.Length; i++)
		{
			// the very first cycle uses the off-width already rolled once at generate time; later cycles (for
			// elements that opt into it) redraw from OffWidthBase - see ComputePulsePosition
			_objCycleAnchor[i] = 0.0;
			_objCurrentOffWidth[i] = _objSources[i].OffWidthCycles;
			_objRngState[i] = _objSources[i].RngSeed;
		}
	}

	/// <inheritdoc/>
	public void Reset()
	{
		Array.Clear(_u);
		Array.Clear(_uPrev);
		if (_intensity != null)
		{
			Array.Clear(_intensity);
			Array.Clear(_intensityStage1!);
			Array.Clear(_intensityOut!);
		}
		ResetEmitterState();
		_time = 0.0;
		SetProbes(null);
	}

	/// <inheritdoc/>
	public bool TryReplaceScene(SceneData scene)
	{
		if (scene.Width != _width || scene.Height != _height || scene.Rim != _rim || scene.OpenEdges != _openEdges || scene.SourceCount != _sourceIndex.Length)
		{
			return false;
		}
		LoadScene(scene);
		Reset();
		// Reset keeps the emitters' definitions; their intensity constant follows the new image sources' levels
		SetEmitters(_objSources);
		return true;
	}

	/// <summary>
	/// Fills the per-cell maps and the image-source tables from <paramref name="scene"/>, which must have this engine's
	/// grid and source count: c² and d into the padded layout, the sub-steps and the per-sub-step damping and c² corrections
	/// for the 4th-order stencil, the sources indexed, the levels in use noted. The constructor's and <see cref="TryReplaceScene"/>'s.
	/// </summary>
	private void LoadScene(SceneData scene)
	{
		int cellCount = _c2.Length;
		for (int y = 0; y < _height; y++)
		{
			Array.Copy(scene.C2, y * _width, _c2, (y + 1) * _stride + 1, _width);
			Array.Copy(scene.D, y * _width, _damping, (y + 1) * _stride + 1, _width);
		}
		// see the class remarks: sub-stepping keeps the 4th order stencil stable without changing the physical
		// wave speed or damping - _subSteps stays 1 (no sub-stepping) unless the 4th order stencil is in use and
		// this scene's highest c² actually needs it
		_subSteps = _use4thOrderLaplacian ? ComputeSubSteps(MaxOf(_c2)) : 1;
		if (_subSteps > 1)
		{
			// Damping is applied once per sub-step, so take the sub-step root to keep the per-step decay unchanged
			// (_subSteps applications of it compound back to the original d).
			double exponent = 1.0 / _subSteps;
			for (int i = 0; i < cellCount; i++)
			{
				_damping[i] = (float)Math.Pow(_damping[i], exponent);
			}
			// A sub-step is 1/subSteps long, so c²·dt² shrinks by 1/subSteps².
			_c2Mult = 1f / (_subSteps * _subSteps);
		}
		else
		{
			_c2Mult = 1f;
		}
		Array.Clear(_levelUsed);
		for (int i = 0; i < _sourceIndex.Length; i++)
		{
			_sourceIndex[i] = (scene.SourceY[i] + 1) * _stride + scene.SourceX[i] + 1;
			_sourceLevel[i] = scene.SourceLevel[i];
			_levelUsed[scene.SourceLevel[i]] = true;
		}
	}

	/// <summary>An ordinary snapshot (pinned arrays): the CPU converts into it on the awaiting thread, and no memory is faster for that.</summary>
	public FrameSnapshot CreateSnapshot(FrameShape shape, bool withIntensity) => new(shape, withIntensity);

	/// <summary>
	/// Copies the interior of the current buffers into the snapshot's float staging (a plain memory copy, so the
	/// stepping thread loses little); <see cref="AwaitFrame"/> converts to <see cref="Half"/> on the thread that awaits.
	/// </summary>
	public void CaptureFrame(FrameSnapshot snapshot, bool needIntensity)
	{
		if (snapshot.Shape.CanvasWidth != _canvasWidth || snapshot.Shape.CanvasHeight != _canvasHeight)
		{
			throw new ArgumentException("The snapshot's canvas does not match the engine's size.", nameof(snapshot));
		}
		int cells = _canvasWidth * _canvasHeight;
		snapshot.FieldFloats ??= new float[cells];
		CopyInterior(_u, snapshot.FieldFloats);
		snapshot.HasIntensity = needIntensity && _intensityOut != null && snapshot.HasIntensityPlane;
		if (snapshot.HasIntensity)
		{
			snapshot.IntensityFloats ??= new float[cells];
			CopyInterior(_intensityOut!, snapshot.IntensityFloats);
		}
		snapshot.PendingConversion = true;
	}

	/// <summary>Converts the staged floats to the snapshot's <see cref="Half"/> planes; the work of the capture, done where it is awaited.</summary>
	public void AwaitFrame(FrameSnapshot snapshot)
	{
		if (!snapshot.PendingConversion)
		{
			return;
		}
		snapshot.PendingConversion = false;
		// the shape's shrinking and format (FrameConversion), on the awaiting thread
		FrameConversion.Convert(snapshot.FieldFloats!, 0, _canvasWidth, snapshot.Shape, false, snapshot.FieldMemory);
		if (snapshot.HasIntensity)
		{
			FrameConversion.Convert(snapshot.IntensityFloats!, 0, _canvasWidth, snapshot.Shape, true, snapshot.IntensityMemory);
		}
	}

	/// <summary>The interior rows of a padded buffer into a tightly packed plane.</summary>
	private void CopyInterior(float[] source, float[] target)
	{
		int width = _canvasWidth;
		int stride = _stride;
		int rim = _rim;
		Parallel.For(0, _canvasHeight, y => source.AsSpan((y + 1 + rim) * stride + 1 + rim, width).CopyTo(target.AsSpan(y * width, width)));
	}

	/// <summary>Canvas width in cells, excluding the padding and the rim.</summary>
	public int Width => _canvasWidth;

	/// <inheritdoc/>
	public int Rim => _rim;

	/// <summary>Scene height in cells (canvas pixels), excluding the padding.</summary>
	public int Height => _canvasHeight;

	/// <summary>Row pitch of the padded buffers: <see cref="Width"/> + 2. Pixel (x, y) is at (y + 1) * Stride + x + 1.</summary>
	public int Stride => _stride;

	/// <summary>
	/// The current field u of the last completed step (padded layout). The array instance changes every sub-step
	/// because the two field buffers are swapped, so callers must re-read this property per frame rather than
	/// cache the array. Do not change.
	/// </summary>
	public float[] FieldData => _u;

	/// <summary>Scaled running intensity (padded layout), or null when the engine was created without intensity tracking. Do not change.</summary>
	public float[]? IntensityData => _intensityOut;

	/// <summary>Engine label for the UI/result: "CPU (n threads)" with the logical processor count.</summary>
	public string Description => "CPU (" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " threads)";

	/// <summary>The stencil for the log, both engines alike: "9-point stencil", or "4th-order stencil, 3 sub-steps".</summary>
	internal static string StencilText(bool fourthOrder, int subSteps) =>
		fourthOrder ? "4th-order stencil, " + subSteps.ToString(CultureInfo.InvariantCulture) + (subSteps == 1 ? " sub-step" : " sub-steps") : "9-point stencil";

	/// <summary>Elapsed simulation time in steps (one per <see cref="AdvanceOneStep"/>).</summary>
	public double Time => _time;

	/// <summary>No-op: the CPU buffers are always host-resident and up to date.</summary>
	public void PrepareFrame(bool needField, bool needIntensity)
	{
	}

	/// <summary>No-op: the engine owns only managed arrays.</summary>
	public void Dispose()
	{
	}

	/// <summary>
	/// Advances the simulation by one step: blends the sources in once, runs <see cref="_subSteps"/> leapfrog
	/// sub-steps (the intensity is updated on the last one only) and increments <see cref="Time"/>.
	/// </summary>
	public void AdvanceOneStep()
	{
		ApplySources();
		for (int subStep = 1; subStep <= _subSteps; subStep++)
		{
			UpdateField(subStep == _subSteps);
		}
		_time += 1.0;
	}

	/// <summary>
	/// Installs the probe layout (or clears it when <paramref name="sampler"/> is null or empty), discarding any
	/// pending samples and integrated totals, and allocates the per-pixel totals for integrated line probes.
	/// </summary>
	/// <exception cref="InvalidOperationException">The sampler needs intensity but this engine does not track it.</exception>
	public void SetProbes(ProbeSampler? sampler)
	{
		_probeValues.Clear();
		_profileTotals = null;
		if (sampler == null || sampler.Count == 0)
		{
			_sampler = null;
			return;
		}
		if (sampler.NeedsIntensity && _intensityOut == null)
		{
			throw new InvalidOperationException("the intensity is not tracked");
		}
		_sampler = sampler;
		_profileTotals = new double[sampler.Count][];
		for (int i = 0; i < sampler.Count; i++)
		{
			_probeValues.Add(new List<float>());
			if (sampler.Integrate[i])
			{
				_profileTotals[i] = new double[sampler.Pixels[i].Length];
			}
		}
	}

	/// <summary>
	/// Called by the runner after every step: appends one field or intensity value per point probe, and adds
	/// the current intensity to the per-pixel totals of integrated line probes (once <see cref="Time"/> has
	/// reached their start step). Non-integrated line probes are read live by <see cref="ReadProbeProfiles"/>.
	/// </summary>
	public void SampleProbes()
	{
		if (_sampler == null)
		{
			return;
		}
		int step = (int)Math.Round(_time);
		for (int p = 0; p < _sampler.Count; p++)
		{
			if (!_sampler.IsProfile[p])
			{
				float[]? data = _sampler.UseIntensity[p] ? _intensityOut : _u;
				_probeValues[p].Add(_sampler.Compute(p, data));
			}
			else if (_sampler.Integrate[p] && _intensityOut != null && _profileTotals != null)
			{
				// before "start after step" the totals are simply never added to, so they stay at zero: any
				// intensity that arrived earlier is purged rather than being baked into the total once recording starts
				if (_sampler.UseStartAfterStep[p] && step < _sampler.StartAfterStep[p])
				{
					continue;
				}
				// the intensity that has passed every point of the line, added up over the steps
				int[] pixels = _sampler.Pixels[p];
				double[] totals = _profileTotals[p]!;
				for (int k = 0; k < pixels.Length; k++)
				{
					totals[k] += _intensityOut[pixels[k]];
				}
			}
		}
	}

	/// <summary>Returns and clears the point-probe values collected since the previous call.</summary>
	/// <returns>One array per probe, one value per step (empty for line probes); an empty outer array when no sampler is set.</returns>
	public float[][] TakeProbeSamples()
	{
		if (_sampler == null)
		{
			return [];
		}
		float[][] samples = new float[_sampler.Count][];
		for (int p = 0; p < _sampler.Count; p++)
		{
			samples[p] = _probeValues[p].ToArray();
			_probeValues[p].Clear();
		}
		return samples;
	}

	/// <summary>Reads the current values along every line probe; nothing is cleared.</summary>
	/// <returns>
	/// One array per probe in pixel order along the line, holding the integrated intensity totals or the live
	/// field/intensity values; null entries for point probes; an empty outer array when no sampler is set.
	/// </returns>
	public float[]?[] ReadProbeProfiles()
	{
		if (_sampler == null)
		{
			return [];
		}
		float[]?[] profiles = new float[_sampler.Count][];
		for (int p = 0; p < _sampler.Count; p++)
		{
			if (!_sampler.IsProfile[p])
			{
				continue;
			}
			int[] pixels = _sampler.Pixels[p];
			float[] profile = new float[pixels.Length];
			if (_sampler.Integrate[p] && _profileTotals?[p] is { } totals)
			{
				for (int k = 0; k < pixels.Length; k++)
				{
					profile[k] = (float)totals[k];
				}
			}
			else if ((_sampler.UseIntensity[p] ? _intensityOut : _u) is { } data)
			{
				for (int k = 0; k < pixels.Length; k++)
				{
					profile[k] = data[pixels[k]];
				}
			}
			profiles[p] = profile;
		}
		return profiles;
	}

	/// <summary>
	/// Number of sub-steps per time step needed to keep the 4th-order stencil stable for the largest c² in the
	/// scene: the smallest n with c²·λmax/n² ≤ <see cref="SubStepTargetLimit"/>, at least 1. Shared with
	/// <see cref="GpuWaveSimulator"/>.
	/// </summary>
	internal static int ComputeSubSteps(float maxC2)
	{
		if (maxC2 <= 0f)
		{
			return 1;
		}
		double subSteps = Math.Ceiling(Math.Sqrt((double)maxC2 * FourthOrderLambdaMax / SubStepTargetLimit));
		return Math.Max(1, (int)Math.Round(subSteps));
	}

	/// <summary>Largest value in <paramref name="values"/>, or 0 for an empty or all-negative array.</summary>
	internal static float MaxOf(float[] values)
	{
		float max = 0f;
		foreach (float value in values)
		{
			if (value > max)
			{
				max = value;
			}
		}
		return max;
	}

	/// <summary>
	/// The lowest (slowest) source level in use, image or object (a level of 0 means "no source" and is
	/// excluded), or <see cref="NominalDecayLevel"/> when no source has a level above 0. Shared with
	/// <see cref="GpuWaveSimulator"/>.
	/// </summary>
	/// <param name="levelUsed">Per level 0..255: whether an image source uses it.</param>
	/// <param name="objectSources">Object sources to consider as well.</param>
	internal static double LowestSourceLevel(bool[] levelUsed, IEnumerable<Emitter> objectSources)
	{
		double minActiveLevel = 0;
		for (int level = 1; level <= 255; level++)
		{
			if (levelUsed[level])
			{
				minActiveLevel = level;
				break;
			}
		}
		foreach (Emitter source in objectSources)
		{
			if (source.Level > 0 && (minActiveLevel == 0 || source.Level < minActiveLevel))
			{
				minActiveLevel = source.Level;
			}
		}
		return minActiveLevel == 0 ? NominalDecayLevel : minActiveLevel;
	}

	/// <summary>
	/// Per-step retention factor of the intensity averages: exp(-1/tauSteps) with
	/// tauSteps = <paramref name="timeDecayFactor"/>·<see cref="NominalDecayLevel"/>/<paramref name="lowestLevel"/>,
	/// at least 1 step, so the time constant scales inversely with the slowest source's frequency and
	/// <see cref="Simulation.TimeDecay"/> counts steps at a nominal source level of
	/// <see cref="NominalDecayLevel"/>. Shared with <see cref="GpuWaveSimulator"/>.
	/// </summary>
	internal static float IntensityRetention(double timeDecayFactor, double lowestLevel)
	{
		double tauSteps = Math.Max(1.0, timeDecayFactor * NominalDecayLevel / lowestLevel);
		return (float)Math.Exp(-1.0 / tauSteps);
	}

	/// <summary>
	/// Soft-blends the current source values into the field (<c>u = 0.9·u + 0.1·value</c>, see
	/// <see cref="SourceOpacity"/>): image sources driven by their level's frequency, then object sources
	/// with their own phase, amplitude, pulse envelope and detach fade. Object sources that have been
	/// detached for <see cref="Emitter.DetachRampSteps"/> steps or longer are skipped.
	/// </summary>
	private void ApplySources()
	{
		float[] u = _u;
		if (_sourceIndex.Length > 0)
		{
			// sin(phase + frequency * t) * amplitude is the same for all sources with the same green value:
			// one sine per level in use, not per source pixel
			for (int level = 1; level <= 255; level++)
			{
				if (_levelUsed[level])
				{
					_levelValue[level] = (float)(Math.Sin(_levelFrequency[level] * _time) * _sourceAmplitude);
				}
			}
			for (int i = 0; i < _sourceIndex.Length; i++)
			{
				int idx = _sourceIndex[i];
				u[idx] = u[idx] * SourceOpacity + _levelValue[_sourceLevel[i]] * (1f - SourceOpacity);
			}
		}
		for (int i = 0; i < _objSources.Length; i++)
		{
			Emitter source = _objSources[i];
			// "detach source": once the run reaches the source's detach step, its drive is faded out linearly over
			// the next DetachRampSteps steps (rather than cut instantly), then it stops driving the field entirely
			// (not even towards 0) so it releases its hold and stops diffracting waves through that point. A hard
			// cutoff would otherwise leave a small kink in the forcing right at the detach step, which can ring.
			float detachFade = 1f;
			if (source.DetachAfterStep >= 0)
			{
				double stepsSinceDetach = _time - source.DetachAfterStep;
				if (stepsSinceDetach >= Emitter.DetachRampSteps)
				{
					continue;
				}
				if (stepsSinceDetach > 0.0)
				{
					detachFade = (float)(1.0 - stepsSinceDetach / Emitter.DetachRampSteps);
				}
			}
			double freq = LevelFrequency(source.Level);
			double envelope = 1.0;
			if (source.Mode != SourceMode.Continuous)
			{
				double pulsePos = ComputePulsePosition(i, source, freq, out int offWidth, out bool isFirstCycle);
				envelope = PulseEnvelope(source, pulsePos, offWidth, isFirstCycle);
			}
			float value = (float)(Math.Sin(source.PhaseRadians + freq * _time) * source.Amplitude * envelope) * detachFade;
			int idx = _objIndex[i];
			u[idx] = u[idx] * SourceOpacity + value * (1f - SourceOpacity);
		}
	}

	/// <summary>
	/// Returns the element's position within its current off+on cycle (0 = the very start of the "off" span, up
	/// to but not including its current period, in cycles of the source frequency), and reports the off-width
	/// in effect for that cycle via <paramref name="offWidth"/>. For most elements
	/// (<see cref="Emitter.RandomizeOffWidthAmount"/> &lt;= 0) the off-width never changes, so this is a
	/// pure, stateless function of <see cref="_time"/> - except that the cycle clock is shifted by the
	/// off-width, so the run starts already at the beginning of the "on" span (pos = offWidth) instead of at the
	/// start of an off span: a single/non-randomized source sitting silent for its off-width at the very start of
	/// a run was never the intent, only ever a side effect of the cycle position formula happening to start at 0.
	/// An element with RandomizeOffWidthAmount &gt; 0 (only ever a composite source with "Randomize off-width"
	/// ticked) keeps the original start-in-off-state behavior instead, since there the elements are meant to
	/// desynchronize from a common starting point rather than all begin already mid-pulse. For such an element
	/// this also owns its runtime state (<see cref="_objCycleAnchor"/>, <see cref="_objCurrentOffWidth"/>): each
	/// time <see cref="_time"/> has advanced far enough to complete the current cycle, it advances the element's
	/// cycle anchor and redraws a fresh off-width (uniformly within ± RandomizeOffWidthAmount of
	/// <see cref="Emitter.OffWidthBase"/>, at least 1, at most <see cref="CycleAdvanceGuard"/> cycles at a
	/// time) before computing the position - so every source keeps its own, independently-drifting rhythm
	/// instead of staying locked to whatever it rolled once at generate time.
	/// <paramref name="isFirstCycle"/> reports whether pos falls within the element's very first off+on cycle
	/// since the run began - see <see cref="PulseEnvelope"/> for why that matters for the SmoothedSquare shape.
	/// </summary>
	/// <param name="i">Index of the source in <see cref="_objSources"/> (selects its anchor/off-width slot).</param>
	/// <param name="el">The source whose pulse timing is evaluated.</param>
	/// <param name="freq">Angular frequency of the source in radians per step.</param>
	/// <param name="offWidth">Receives the off-width (cycles) in effect for the current period.</param>
	/// <param name="isFirstCycle">Receives whether the source is still in its first period.</param>
	private double ComputePulsePosition(int i, Emitter el, double freq, out int offWidth, out bool isFirstCycle)
	{
		// cycles of the source's own frequency elapsed since t = 0 (not folding in the phase, so the on/off timing
		// does not shift when only the phase of the wave itself is changed)
		double cycleClock = freq * _time / (2.0 * Math.PI);
		if (el.RandomizeOffWidthAmount <= 0)
		{
			offWidth = el.OffWidthCycles;
			int period = el.OnWidthCycles + offWidth;
			if (period <= 0)
			{
				isFirstCycle = true;
				return 0.0;
			}
			// shifted so cycleClock = 0 lands at pos = offWidth (the start of the "on" span), not pos = 0 (the start
			// of an "off" span) - see the summary above
			double adjustedClock = cycleClock + offWidth;
			isFirstCycle = Math.Floor(adjustedClock / period) == 0.0;
			return adjustedClock - period * Math.Floor(adjustedClock / period);   // 0 .. period
		}
		else
		{
			offWidth = _objCurrentOffWidth[i];
			int dynPeriod = el.OnWidthCycles + offWidth;
			if (dynPeriod <= 0)
			{
				isFirstCycle = true;
				return 0.0;
			}
			double sinceAnchor = cycleClock - _objCycleAnchor[i];
			int guard = 0;
			while (sinceAnchor >= dynPeriod && guard < CycleAdvanceGuard)
			{
				_objCycleAnchor[i] += dynPeriod;
				sinceAnchor -= dynPeriod;
				// the same xorshift32 draw as the GPU kernel (apply_object_sources), so both engines produce
				// the same gap sequence from the same seed
				uint state = Emitter.Xorshift32(_objRngState[i]);
				_objRngState[i] = state;
				int amount = el.RandomizeOffWidthAmount;
				offWidth = Math.Max(1, el.OffWidthBase + (int)(state % (uint)(2 * amount + 1)) - amount);
				_objCurrentOffWidth[i] = offWidth;
				dynPeriod = el.OnWidthCycles + offWidth;
				guard++;
			}
			if (sinceAnchor < 0.0)
			{
				sinceAnchor = 0.0;   // should not happen (_time only increases), guarded for safety
			}
			// the anchor only ever advances (above) once a cycle has actually completed, so it still reading its
			// initial 0 means no cycle has completed yet - we are still in the element's very first (off-first) cycle
			isFirstCycle = _objCycleAnchor[i] == 0.0;
			return sinceAnchor;
		}
	}

	/// <summary>
	/// Amplitude envelope (0..1) of a pulsed source: the source is "off" for <paramref name="offWidth"/> cycles
	/// of its own frequency, then "on" for <see cref="Emitter.OnWidthCycles"/> cycles, repeating (off precedes on
	/// within the cycle; whether a run starts in the off span or at the first burst is <see cref="ComputePulsePosition"/>'s
	/// business: an element without a randomised off-width fires at step 0, one with it stays silent for its
	/// off-width first; a non-positive period yields 1). Within
	/// the "on" span the amplitude is shaped by the pulse shape: Gaussian tapers smoothly to (about) 0 at both
	/// ends of the "on" span (a bell centred in the on-window with σ = on-width/6), and SmoothedSquare rises and
	/// falls quickly (an atan-smoothed square wave, avoiding a hard on/off step that would ring the field).
	/// <paramref name="pos"/> and <paramref name="offWidth"/> come from <see cref="ComputePulsePosition"/>, as
	/// does <paramref name="isFirstCycle"/> - see the SmoothedSquare branch for why that one matters.
	/// </summary>
	/// <param name="el">The source; supplies the pulse shape and on-width.</param>
	/// <param name="pos">Position within the period in cycles, as returned by <see cref="ComputePulsePosition"/>.</param>
	/// <param name="offWidth">Off-width (cycles) in effect for this period.</param>
	/// <param name="isFirstCycle">Whether this is the first period (suppresses the tail wrapping of the smoothed square).</param>
	private static double PulseEnvelope(Emitter el, double pos, int offWidth, bool isFirstCycle)
	{
		int onWidthCycles = el.OnWidthCycles;
		int period = onWidthCycles + offWidth;
		if (period <= 0)
		{
			return 1.0;
		}
		double onPos = pos - offWidth;   // 0 .. onWidth, position within the "on" span
		if (el.PulseShape == PulseShape.Gaussian)
		{
			if (onPos < 0.0)
			{
				return 0.0;
			}
			double center = onWidthCycles / 2.0;
			double sigma = Math.Max(onWidthCycles / 6.0, 1E-06);
			double z = (onPos - center) / sigma;
			return Math.Exp(-0.5 * z * z);
		}
		// a flat-topped pulse (envelope 1 while "on", 0 while "off") with a short, FIXED-width (in cycles, not a
		// fraction of the on/off span) smoothed edge at the rise and the fall, evaluated on a coordinate shifted
		// so the "on" span starts at 0 (folding around the period).
		if (onPos < 0.0 && !isFirstCycle)
		{
			// wrapping onPos forward by a full period makes it continue the falling tail of the on-span that
			// JUST ended - correct from the second off-span onward, where that on-span really happened. A
			// source's very first off-span (isFirstCycle) has no real preceding on-emission to continue from, so
			// wrapping there would fabricate one: pos = 0 would land exactly on that phantom falling edge and
			// start the run at envelope ~0.5 instead of ~0, emitting a spurious one-cycle "blip" before the
			// source actually goes quiet. Left un-wrapped (deeply negative) instead, riseArg/fallArg below
			// correctly place it far from both edges, so the envelope is (very close to) 0 until the real rising edge.
			onPos += period;
		}
		const double EdgeWidth = 0.35;
		double fallArg = onPos - onWidthCycles;   // 0 at the falling edge, grows past it
		double riseArg = onPos;                   // 0 at the rising edge
		if (riseArg > period / 2.0)
		{
			riseArg -= period;   // signed distance to the *nearest* rising edge
		}
		double rise = 0.5 + Math.Atan(riseArg / EdgeWidth) / Math.PI;
		double fall = 0.5 - Math.Atan(fallArg / EdgeWidth) / Math.PI;
		return rise * fall;
	}

	/// <summary>
	/// One leapfrog sub-step over all interior cells, parallelised over row bands:
	/// <c>next = u + (u - uPrev)·d + lap·c2·c2Mult</c>, where d is the per-cell damping (1 = lossless, applied
	/// to the velocity term) and lap is the Laplacian: the 9-point stencil (corners <see cref="KernelCorner"/>,
	/// edges <see cref="KernelEdge"/>, centre -1), or for cells at least two cells away from the border and
	/// when enabled, the 4th-order 5-point-per-axis stencil (<see cref="FourthOrderFar"/>,
	/// <see cref="FourthOrderNear"/>, <see cref="FourthOrderCenter"/>). Each cell reads only <see cref="_u"/>
	/// and overwrites its own slot in <see cref="_uPrev"/>, so the bands do not race; afterwards the two
	/// buffers are swapped. Border cells are never written and stay 0.
	/// When tracking, the intensity is a cascade of two exponential moving averages of u² (retention
	/// <see cref="_intensityAlpha"/>); the output mixes both stages (<see cref="IntensityBlend"/>) times
	/// <see cref="_intensityScale"/>. The damping array is <see cref="_damping"/> (the scene's d itself when
	/// <see cref="_subSteps"/> = 1, otherwise its 1/_subSteps-th root) and c² is scaled by
	/// <see cref="_c2Mult"/> (1.0F, an exact no-op, when _subSteps = 1).
	/// </summary>
	/// <param name="updateIntensity">
	/// Whether to update the intensity buffers in this sub-step: only on the last one, so that its own exponential
	/// averaging still runs once per <see cref="AdvanceOneStep"/> call, from the field after all of its sub-steps,
	/// matching its calibration (<see cref="_intensityAlpha"/>) regardless of how many sub-steps that call takes.
	/// </param>
	private void UpdateField(bool updateIntensity)
	{
		// Local copies for the parallel loop: the lambda should not touch fields.
		float[] u = _u;
		float[] uPrev = _uPrev;
		float[] c2 = _c2;
		float[] d = _damping;
		float[]? intensity = _intensity;
		float[]? stage1 = _intensityStage1;
		float[]? intensityOut = _intensityOut;
		bool track = intensity != null && stage1 != null && intensityOut != null && updateIntensity;
		int stride = _stride;
		int w = _width;
		int h = _height;
		bool use4th = _use4thOrderLaplacian;
		float mult = _c2Mult;
		float alpha = _intensityAlpha;
		float scale = _intensityScale;
		int bands = Math.Min(h, Environment.ProcessorCount * 4);

		// the cells go through the stencil in lanes of Vector<float> (4 on SSE, 8 with AVX2; 2026-11-04), each lane the
		// scalar arithmetic in the scalar order, so the field is the same bit for bit as cell by cell (and as the GPU's,
		// which library_check proves); the columns the lanes do not cover and the first and last column of a 4th-order
		// row, which take the 9-point stencil, go cell by cell
		int lanes = Vector<float>.Count;
		Parallel.For(0, bands, band =>
		{
			int rowStart = (int)((long)h * band / bands);
			int rowEnd = (int)((long)h * (band + 1) / bands) - 1;
			for (int y = rowStart; y <= rowEnd; y++)
			{
				int row = (y + 1) * stride + 1;
				// the 4th order stencil reaches 2 cells away, so it is only safe for domain rows/columns that
				// have that much room before the (1 cell wide) border - the outermost ring of the domain (y = 0,
				// y = h - 1, and, per column below, x = 0, x = w - 1) falls back to the original 3x3 kernel
				bool rowAllows4th = use4th && y >= 1 && y <= h - 2;
				int x = 0;
				if (rowAllows4th)
				{
					StepCell9(u, uPrev, c2, d, stage1, intensity, intensityOut, track, mult, alpha, scale, stride, row);
					int end = w - 1;
					for (x = 1; x + lanes <= end; x += lanes)
					{
						StepLanes4(u, uPrev, c2, d, stage1, intensity, intensityOut, track, mult, alpha, scale, stride, row + x);
					}
					for (; x < end; x++)
					{
						StepCell4(u, uPrev, c2, d, stage1, intensity, intensityOut, track, mult, alpha, scale, stride, row + x);
					}
					StepCell9(u, uPrev, c2, d, stage1, intensity, intensityOut, track, mult, alpha, scale, stride, row + w - 1);
					continue;
				}
				for (; x + lanes <= w; x += lanes)
				{
					StepLanes9(u, uPrev, c2, d, stage1, intensity, intensityOut, track, mult, alpha, scale, stride, row + x);
				}
				for (; x < w; x++)
				{
					StepCell9(u, uPrev, c2, d, stage1, intensity, intensityOut, track, mult, alpha, scale, stride, row + x);
				}
			}
		});

		if (_openEdges)
		{
			ApplyOpenEdges(u, uPrev, c2, mult, w, h, stride);
		}
		// the array that received the new values is now the current field
		_uPrev = u;
		_u = uPrev;
	}

	/// <summary>One cell of <see cref="UpdateField"/> with the 9-point stencil: the Laplacian, then <see cref="FinishCell"/>.</summary>
	private static void StepCell9(float[] u, float[] uPrev, float[] c2, float[] d, float[]? stage1, float[]? intensity, float[]? intensityOut, bool track, float mult, float alpha, float scale, int stride, int idx)
	{
		float center = u[idx];
		float laplacian = KernelCorner * (u[idx - stride - 1] + u[idx - stride + 1] + u[idx + stride - 1] + u[idx + stride + 1])
			+ KernelEdge * (u[idx - 1] + u[idx + 1] + u[idx - stride] + u[idx + stride])
			- center;
		FinishCell(uPrev, c2, d, stage1, intensity, intensityOut, track, mult, alpha, scale, idx, center, laplacian);
	}

	/// <summary>One cell of <see cref="UpdateField"/> with the 4th-order stencil: the Laplacian, then <see cref="FinishCell"/>.</summary>
	private static void StepCell4(float[] u, float[] uPrev, float[] c2, float[] d, float[]? stage1, float[]? intensity, float[]? intensityOut, bool track, float mult, float alpha, float scale, int stride, int idx)
	{
		float center = u[idx];
		float laplacian = FourthOrderFar * (u[idx - 2] + u[idx + 2] + u[idx - 2 * stride] + u[idx + 2 * stride])
			+ FourthOrderNear * (u[idx - 1] + u[idx + 1] + u[idx - stride] + u[idx + stride])
			+ FourthOrderCenter * center;
		FinishCell(uPrev, c2, d, stage1, intensity, intensityOut, track, mult, alpha, scale, idx, center, laplacian);
	}

	/// <summary>
	/// The leapfrog update of one cell from its Laplacian, <c>next = u + (u − uPrev)·d + lap·c²·mult</c>, written over
	/// its previous value, and when tracking the intensity's two cascaded one-pole stages (see the class remarks),
	/// blended with the 1-stage result (<see cref="IntensityBlend"/>) for the shown/exported intensity rather than
	/// using the full 2-stage cascade outright.
	/// </summary>
	private static void FinishCell(float[] uPrev, float[] c2, float[] d, float[]? stage1, float[]? intensity, float[]? intensityOut, bool track, float mult, float alpha, float scale, int idx, float center, float laplacian)
	{
		float next = center + (center - uPrev[idx]) * d[idx] + laplacian * c2[idx] * mult;
		uPrev[idx] = next;
		if (track)
		{
			float s1 = stage1![idx] * alpha + next * next * (1f - alpha);
			stage1[idx] = s1;
			float s2 = intensity![idx] * alpha + s1 * (1f - alpha);
			intensity[idx] = s2;
			intensityOut![idx] = (s1 * (1f - IntensityBlend) + s2 * IntensityBlend) * scale;
		}
	}

	/// <summary><see cref="StepCell9"/> for <see cref="Vector{T}.Count"/> cells from <paramref name="idx"/> on, each lane the scalar's arithmetic in its order.</summary>
	private static void StepLanes9(float[] u, float[] uPrev, float[] c2, float[] d, float[]? stage1, float[]? intensity, float[]? intensityOut, bool track, float mult, float alpha, float scale, int stride, int idx)
	{
		var center = new Vector<float>(u, idx);
		Vector<float> laplacian = KernelCorner * (new Vector<float>(u, idx - stride - 1) + new Vector<float>(u, idx - stride + 1) + new Vector<float>(u, idx + stride - 1) + new Vector<float>(u, idx + stride + 1))
			+ KernelEdge * (new Vector<float>(u, idx - 1) + new Vector<float>(u, idx + 1) + new Vector<float>(u, idx - stride) + new Vector<float>(u, idx + stride))
			- center;
		FinishLanes(uPrev, c2, d, stage1, intensity, intensityOut, track, mult, alpha, scale, idx, center, laplacian);
	}

	/// <summary><see cref="StepCell4"/> for <see cref="Vector{T}.Count"/> cells from <paramref name="idx"/> on, each lane the scalar's arithmetic in its order.</summary>
	private static void StepLanes4(float[] u, float[] uPrev, float[] c2, float[] d, float[]? stage1, float[]? intensity, float[]? intensityOut, bool track, float mult, float alpha, float scale, int stride, int idx)
	{
		var center = new Vector<float>(u, idx);
		Vector<float> laplacian = FourthOrderFar * (new Vector<float>(u, idx - 2) + new Vector<float>(u, idx + 2) + new Vector<float>(u, idx - 2 * stride) + new Vector<float>(u, idx + 2 * stride))
			+ FourthOrderNear * (new Vector<float>(u, idx - 1) + new Vector<float>(u, idx + 1) + new Vector<float>(u, idx - stride) + new Vector<float>(u, idx + stride))
			+ FourthOrderCenter * center;
		FinishLanes(uPrev, c2, d, stage1, intensity, intensityOut, track, mult, alpha, scale, idx, center, laplacian);
	}

	/// <summary><see cref="FinishCell"/> for <see cref="Vector{T}.Count"/> cells from <paramref name="idx"/> on.</summary>
	private static void FinishLanes(float[] uPrev, float[] c2, float[] d, float[]? stage1, float[]? intensity, float[]? intensityOut, bool track, float mult, float alpha, float scale, int idx, Vector<float> center, Vector<float> laplacian)
	{
		Vector<float> next = center + (center - new Vector<float>(uPrev, idx)) * new Vector<float>(d, idx) + laplacian * new Vector<float>(c2, idx) * mult;
		next.CopyTo(uPrev, idx);
		if (track)
		{
			Vector<float> s1 = new Vector<float>(stage1!, idx) * alpha + next * next * (1f - alpha);
			s1.CopyTo(stage1!, idx);
			Vector<float> s2 = new Vector<float>(intensity!, idx) * alpha + s1 * (1f - alpha);
			s2.CopyTo(intensity!, idx);
			((s1 * (1f - IntensityBlend) + s2 * IntensityBlend) * scale).CopyTo(intensityOut!, idx);
		}
	}

	/// <summary>
	/// The phase speed of the 9-point stencil for a wave of c² = 1 in cells per step: a plane wave of small k sees
	/// lap ≈ −(0.184 + 2 × 0.066) k² = −0.316 k², so the speed is √0.316 (Jeroen's c of 0.563). The cell next to the
	/// ring always uses that stencil, so the open edge is tuned to it in both stencil modes.
	/// </summary>
	internal const float EdgeSpeedFactor = 0.56213877f;

	/// <summary>
	/// Mur's first-order absorbing condition on the ring outside the grid, after a sub-step (2026-10-19): each ring cell
	/// takes what a wave travelling straight out would leave behind, u_ring' = u_in + k (u_in' − u_ring) with
	/// k = (s − 1) / (s + 1) and s the local speed in cells per sub-step, so a head-on wave walks off the grid; an
	/// oblique one partly returns, which the damping rim in front of the ring takes care of. A corner takes its
	/// diagonal neighbour. The GPU kernel <c>open_edges</c> does the same operations in the same order.
	/// </summary>
	/// <param name="old">The field before the sub-step.</param>
	/// <param name="next">The field after it, whose ring this writes.</param>
	/// <param name="c2">Squared speed per cell (the padded layout).</param>
	/// <param name="mult">The sub-step's c² scale.</param>
	/// <param name="w">Grid width without padding.</param>
	/// <param name="h">Grid height without padding.</param>
	/// <param name="stride">Row pitch.</param>
	internal static void ApplyOpenEdges(float[] old, float[] next, float[] c2, float mult, int w, int h, int stride)
	{
		for (int x = 0; x <= w + 1; x++)
		{
			int ix = Math.Clamp(x, 1, w);
			Absorb(old, next, c2, mult, x, stride + ix);
			Absorb(old, next, c2, mult, (h + 1) * stride + x, h * stride + ix);
		}
		for (int y = 1; y <= h; y++)
		{
			Absorb(old, next, c2, mult, y * stride, y * stride + 1);
			Absorb(old, next, c2, mult, y * stride + w + 1, y * stride + w);
		}
	}

	private static void Absorb(float[] old, float[] next, float[] c2, float mult, int ring, int inner)
	{
		float s = MathF.Sqrt(c2[inner] * mult) * EdgeSpeedFactor;
		float k = (s - 1f) / (s + 1f);
		next[ring] = old[inner] + k * (next[inner] - old[ring]);
	}

	/// <summary>Weight of the four diagonal neighbours in the 9-point Laplacian.</summary>
	private const float KernelCorner = 0.066f;

	/// <summary>Weight of the four edge neighbours in the 9-point Laplacian (4·corner + 4·edge = 1 balances the centre weight of -1).</summary>
	private const float KernelEdge = 0.184f;

	/// <summary>Fraction of the old field value kept when a source value is blended in (soft source injection).</summary>
	private const float SourceOpacity = 0.9f;

	/// <summary>
	/// Nominal source level (of 255) that <see cref="Simulation.TimeDecay"/>'s steps count is
	/// calibrated against - see that property's doc and <see cref="IntensityRetention"/>; also the level assumed
	/// when no source has a level above 0 (<see cref="LowestSourceLevel"/>).
	/// </summary>
	internal const int NominalDecayLevel = 100;

	/// <summary>
	/// How much of the fully cascaded (2-stage, 12 dB/octave) intensity is blended into the shown/exported value
	/// versus the 1-stage (6 dB/octave) intensity (0 = first stage only, 1 = second stage only) - see the class
	/// remarks; 0.5 is the middle ground, about 9 dB/octave.
	/// </summary>
	private const float IntensityBlend = 0.5f;

	// the 4th order "cross" stencil's coefficients: (-f(-2) + 16f(-1) - 30f(0) + 16f(+1) - f(+2)) / 12 per axis,
	// the two axes' center terms combined into one (-60/12 = -5) since both apply to the same center pixel

	/// <summary>4th-order stencil weight of the nearest neighbours (16 / 12).</summary>
	private const float FourthOrderNear = 4.0f / 3.0f;

	/// <summary>4th-order stencil weight of the second-nearest neighbours (-1 / 12).</summary>
	private const float FourthOrderFar = -1.0f / 12.0f;

	/// <summary>4th-order stencil centre weight (-60 / 12, the two axes' centre terms combined).</summary>
	private const float FourthOrderCenter = -5f;

	/// <summary>
	/// The 4th order stencil's largest |eigenvalue| (at the grid's highest spatial frequency, kx = ky = π):
	/// 2 * (-1/12 * cos(2k) + 4/3 * cos(k) - 5/2)|k=π per axis, summed over x and y = -32/3. Used by the
	/// stability estimate in <see cref="ComputeSubSteps"/>.
	/// </summary>
	private const float FourthOrderLambdaMax = 32.0f / 3.0f;

	/// <summary>
	/// How far below the hard stability limit (c² * |λmax| &lt;= 4) sub-stepping aims to stay - chosen to give
	/// roughly the same safety margin the 3x3 kernel already has in practice (its own c² * |λmax| is about 1.8
	/// at the highest c² the app allows, i.e. about 2.2 times under the limit of 4).
	/// </summary>
	private const float SubStepTargetLimit = 2f;

	/// <summary>Upper bound on the periods <see cref="ComputePulsePosition"/> advances at once (guards against a tiny period).</summary>
	private const int CycleAdvanceGuard = 1000;

	/// <summary>Scene width in cells.</summary>
	private readonly int _width;

	/// <summary>Scene height in cells.</summary>
	private readonly int _height;

	/// <summary>Padded row pitch (<see cref="_width"/> + 2).</summary>
	private readonly int _stride;

	/// <summary>Whether interior cells use the 4th-order stencil.</summary>
	private readonly bool _use4thOrderLaplacian;

	/// <summary>
	/// Leapfrog sub-steps per simulation step (see the class remarks); 1 (no sub-stepping) unless the 4th-order
	/// stencil is on and this scene's highest c² needs it.
	/// </summary>
	private int _subSteps;

	/// <summary>
	/// Per-cell damping applied per sub-step (padded), 1 = lossless: the scene's map itself when
	/// <see cref="_subSteps"/> = 1 (same array, so multiplying by it is bit-for-bit identical to before),
	/// otherwise a separate array of it raised to the power 1/<see cref="_subSteps"/>.
	/// </summary>
	private readonly float[] _damping;
	private readonly int _rim;
	private readonly int _canvasWidth;
	private readonly int _canvasHeight;
	private readonly bool _openEdges;

	/// <summary>1/<see cref="_subSteps"/>², scaling c² for the shorter sub-step (or exactly 1.0F when _subSteps = 1, a bit-for-bit no-op multiply).</summary>
	private float _c2Mult;

	/// <summary>
	/// The intensity's exponential-average decay factor for this simulation (replaces the old fixed 0.98F) - see
	/// <see cref="Simulation.TimeDecay"/>'s doc and <see cref="IntensityRetention"/>.
	/// </summary>
	private float _intensityAlpha;

	/// <summary><see cref="Simulation.TimeDecay"/>, kept so <see cref="SetEmitters"/> can re-derive <see cref="_intensityAlpha"/>.</summary>
	private readonly double _timeDecay;

	/// <summary><see cref="Simulation.IntensityScale"/>, the multiplier on the intensity output - see that property's doc.</summary>
	private readonly float _intensityScale;

	/// <summary>Current field (padded); exchanged with <see cref="_uPrev"/> after every sub-step.</summary>
	private float[] _u;

	/// <summary>Previous field (padded); receives the new field during a sub-step and then becomes <see cref="_u"/>.</summary>
	private float[] _uPrev;

	/// <summary>Per-cell c² = 1/n² (padded; border cells 0).</summary>
	private readonly float[] _c2;

	/// <summary>
	/// Second-stage running average of u² (padded): the 2-stage cascade's internal recursive state (12 dB/octave),
	/// no longer what is shown/exported - see <see cref="_intensityOut"/> and the class remarks. Null when
	/// intensity is not tracked.
	/// </summary>
	private readonly float[]? _intensity;

	/// <summary>
	/// The first of the two cascaded exponential-filter stages that make up the intensity (padded; see the class
	/// remarks), or null when intensity is not tracked, same as <see cref="_intensity"/>.
	/// </summary>
	private readonly float[]? _intensityStage1;

	/// <summary>
	/// The blend of <see cref="_intensityStage1"/> and <see cref="_intensity"/> (<see cref="IntensityBlend"/>)
	/// times <see cref="_intensityScale"/> that is actually shown/exported as <see cref="IntensityData"/>, or null
	/// when not tracked.
	/// </summary>
	private readonly float[]? _intensityOut;

	/// <summary>Padded cell index of every image source.</summary>
	private readonly int[] _sourceIndex;

	/// <summary>Green level (1..255) of every image source, indexing <see cref="_levelFrequency"/>.</summary>
	private readonly byte[] _sourceLevel;

	/// <summary>Angular frequency in radians per step for each image level 0..255: level/255 × 0.5 × sourceFrequencyScale.</summary>
	private readonly double[] _levelFrequency = new double[256];

	/// <summary>The scale of the level-to-frequency mapping, for the object sources' decimal levels.</summary>
	private readonly double _sourceFrequencyScale;

	/// <summary>Angular frequency in radians per step of a decimal <paramref name="level"/>: the table's formula, rounded to float as the GPU holds it, so CPU = GPU.</summary>
	private double LevelFrequency(double level) => (float)(level / 255.0 * 0.5 * _sourceFrequencyScale);

	/// <summary>Which levels have at least one image source; only those get a value computed per step.</summary>
	private readonly bool[] _levelUsed = new bool[256];

	/// <summary>
	/// Scratch for <see cref="ApplySources"/>: the source value of each level in use for the current step,
	/// computed once per level and shared by every image source of that level. Kept as a field so it is not
	/// re-allocated every step; written and read only by the driving thread.
	/// </summary>
	private readonly float[] _levelValue = new float[256];

	/// <summary>Peak amplitude of the image sources.</summary>
	private readonly double _sourceAmplitude;

	/// <summary>Object sources that lie inside the canvas.</summary>
	private Emitter[] _objSources = [];

	/// <summary>Padded cell index of every entry in <see cref="_objSources"/>.</summary>
	private int[] _objIndex = [];

	/// <summary>Per object source (parallel to <see cref="_objSources"/>): xorshift32 state for the off-width redraws, seeded from <see cref="Emitter.RngSeed"/>; used by the driving thread only.</summary>
	private uint[] _objRngState = [];

	/// <summary>
	/// Per object source (parallel to <see cref="_objSources"/>): the cycle-clock value (in cycles of the
	/// element's own frequency) at which its current off+on cycle started. Runtime state for pulsed sources
	/// whose off-width is re-randomized at runtime (<see cref="Emitter.RandomizeOffWidthAmount"/>);
	/// elements that never re-randomize (the common case) never touch it: their position within the cycle is
	/// a pure function of <see cref="_time"/>, exactly as before.
	/// </summary>
	private double[] _objCycleAnchor = [];

	/// <summary>Per object source (parallel to <see cref="_objSources"/>): off-width (cycles) in effect for its current period; see <see cref="_objCycleAnchor"/>.</summary>
	private int[] _objCurrentOffWidth = [];

	/// <summary>Elapsed time in steps.</summary>
	private double _time;

	/// <summary>Probe layout in use, or null when no probes are set.</summary>
	private ProbeSampler? _sampler;

	/// <summary>Point-probe samples gathered since the last <see cref="TakeProbeSamples"/>, one list per probe.</summary>
	private readonly List<List<float>> _probeValues = [];

	/// <summary>Running per-pixel intensity sums for integrated line probes (null entries for other probes); null without a sampler.</summary>
	private double[]?[]? _profileTotals;
}
