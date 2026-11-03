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
using System.Text;
using WaveDudeNext.Model;
using WaveDudeNext.Probes;

namespace WaveDudeNext.Engine;

/// <summary>
/// OpenCL implementation of <see cref="IWaveEngine"/>: the 2D wave equation on the graphics card, the same
/// algorithm as <see cref="WaveSimulator"/> (padded grid with a border of one cell, 9-point or sub-stepped
/// 4th-order Laplacian, soft source blending, two-stage intensity average), with the field, the scene maps,
/// the source tables and the probe buffers all resident on the device. Units are those of the CPU engine
/// (dx = dt = 1, c in cells per step, frequencies in radians per step, time in steps):
/// <code>
///   new u = u + (u - previous u) * d + laplacian * c^2     (3x3 kernel 0.066 / 0.184 / -1)
///   sources: u = u * 0.9 + sin(frequency * t) * amplitude * 0.1
///   intensity: two cascaded one-pole filters, I1 = I1 * alpha + u^2 * (1 - alpha), I2 = I2 * alpha + I1 * (1 - alpha),
///   blended (IntensityBlend) into what is shown/exported: Iout = (I1 * (1 - blend) + I2 * blend) * scale
/// </code>
/// alpha is derived once per simulation - see the <see cref="WaveSimulator"/> class summary (why two stages
/// and the blend, not a single, fixed-0.98 filter) and <see cref="Simulation.TimeDecay"/> (why
/// alpha); scale is <see cref="Simulation.IntensityScale"/> (why it exists). The field, the previous
/// field, c², d and the intensity stay in the memory of the graphics card; only when a frame is drawn
/// (<see cref="PrepareFrame"/>) the field and/or intensity are copied back. The arrays have the same layout as
/// those of the CPU engine, so the same renderer draws both.
/// Created through <see cref="TryCreate"/> by <see cref="WaveEngineFactory"/> and driven by the run on
/// its worker, which is the only thread that enqueues work and reads <see cref="FieldData"/> /
/// <see cref="IntensityData"/>. Any single thread may drive the engine; it is not safe for concurrent use.
/// </summary>
/// <remarks>
/// <para>
/// Optionally (<c>use4thOrderLaplacian</c>), the <c>wave_step</c> kernel instead computes the Laplacian with a
/// 4th order accurate, 5-point "cross" stencil wherever there is room for it, the same as
/// <see cref="WaveSimulator"/>'s <c>UpdateField</c> on the CPU (see that class's summary for why). Which form to
/// use is baked into the generated kernel source as a compile-time <c>#define</c>, not a runtime branch, so
/// there is no extra cost when it is off.
/// </para>
/// <para>
/// The 4th order stencil needs the same time sub-stepping as the CPU engine to stay stable, for the same
/// reason (see <see cref="WaveSimulator"/>'s class summary): <see cref="AdvanceOneStep"/> launches
/// <c>wave_step</c> <see cref="_subSteps"/> times instead of once when <see cref="_subSteps"/> &gt; 1, with c²
/// scaled by a <c>c2Scale</c> kernel argument (1 / <see cref="_subSteps"/>²) and the d buffer pre-scaled (on
/// the host, once, at <see cref="Build"/> time) to its 1 / <see cref="_subSteps"/>-th root, so
/// <see cref="_subSteps"/> kernel launches move the field by exactly the same physical time and damping as
/// one ordinary launch. The intensity buffer is only updated (via the kernel's <c>track</c> argument) on the
/// last of the <see cref="_subSteps"/> launches, so its own exponential averaging still runs once per
/// <see cref="AdvanceOneStep"/> call, matching the CPU engine and its own earlier calibration.
/// <see cref="_subSteps"/> is 1 (<c>c2Scale</c> exactly 1.0f, an exact no-op multiply, and the d buffer
/// unchanged) whenever the 3x3 kernel is used or this scene's highest c² does not need sub-stepping - see
/// <see cref="WaveSimulator.ComputeSubSteps"/>.
/// </para>
/// <para>
/// The field lives in two ping-pong buffers (<see cref="_field"/>) that take turns as "current field" and
/// "previous field"; every kernel that reads the field exists twice (index 0/1) with the buffers bound in
/// opposite order, so a step merely picks the kernel for <see cref="_current"/> instead of re-binding
/// arguments. The kernels over the grid and the captures run in explicit work groups, the global size rounded up
/// to whole groups (see <see cref="WorkGroup2D"/> for why). Kernel launches are asynchronous; uploads and the float
/// read-backs are blocking; a capture's reads are DMA transfers into the page-locked planes of a snapshot made by
/// <see cref="CreateSnapshot"/>, on a transfer queue of their own so they overlap the steps that follow (two staging
/// sets in turn, ordered by events; <see cref="EnqueueCapture"/>), awaited later (<see cref="AwaitFrame"/>). Every OpenCL handle
/// created here is registered in <see cref="_scene"/> (the simulation program, kernels and buffers) or
/// <see cref="_probes"/> (everything <see cref="SetProbes"/> creates) and released by <see cref="Dispose"/>,
/// by the constructor when building fails, or — for the probe set — when probes are installed again. The
/// managed arrays (<see cref="FieldData"/>, <see cref="IntensityData"/>) are plain host copies.
/// </para>
/// <para>
/// Like <see cref="WaveSimulator"/>, portions of this engine are based on WaveSimulator2D by 0x23
/// (https://github.com/0x23/WaveSimulator2D).
/// </para>
/// </remarks>
public sealed class GpuWaveSimulator : IWaveEngine, IDisposable
{
	/// <summary>
	/// Tries the GPUs one after the other, the best first (<see cref="GpuDeviceInfo.FindAll"/>), and returns
	/// the first that works. Parameters are those of the <see cref="WaveSimulator"/> constructor.
	/// </summary>
	/// <param name="scene">Per-cell c² and damping maps plus the image (green-channel) sources.</param>
	/// <param name="options">Frequency scale (level/255 × 0.5 × scale, radians per step), image source amplitude (object sources carry their own), intensity tracking (when off <see cref="IntensityData"/> stays null), stencil order (see the class summary), time decay (the retention is derived by <see cref="WaveSimulator.IntensityRetention"/>) and intensity scale.</param>
	/// <param name="emitters">The emitters of the object sources, or null/empty for none; those outside the canvas are dropped.</param>
	/// <param name="problems">Receives "" on success, otherwise why no device worked ("no GPU found …" or the
	/// per-device failure messages joined by "; ").</param>
	/// <returns>The engine, or null when no device could be used (see <paramref name="problems"/>).</returns>
	/// <exception cref="DllNotFoundException">There is no OpenCL driver (OpenCL.dll cannot be loaded).</exception>
	public static GpuWaveSimulator? TryCreate(SceneData scene, EngineOptions options, IReadOnlyList<Emitter>? emitters, out string problems)
	{
		problems = "";
		List<GpuDeviceInfo> devices = GpuDeviceInfo.FindAll();
		if (devices.Count == 0)
		{
			problems = "no GPU found through OpenCL" + ((IntPtr.Size == 4) ? " (this is a 32-bit process; run the 64-bit build)" : "");
			return null;
		}
		var errors = new StringBuilder();
		foreach (GpuDeviceInfo device in devices)
		{
			try
			{
				DescribeOnce(devices, device);
				return new GpuWaveSimulator(device, scene, options, emitters);
			}
			catch (Exception ex)
			{
				if (errors.Length > 0)
				{
					errors.Append("; ");
				}
				errors.Append(device.Name).Append(": ").Append(ex.Message);
			}
		}
		problems = errors.ToString();
		return null;
	}

	/// <summary>
	/// Records the scene geometry, rejects scenes whose per-cell buffer would exceed the device's maximum
	/// allocation, keeps only the object sources inside the canvas and then runs <see cref="Build"/>; if that
	/// throws, everything allocated so far is released before the exception propagates.
	/// </summary>
	/// <exception cref="InvalidOperationException">The scene is too large for the device, or the OpenCL program could not be built.</exception>
	private GpuWaveSimulator(GpuDeviceInfo device, SceneData scene, EngineOptions options, IReadOnlyList<Emitter>? emitters)
	{
		Description = "GPU: " + device.Name + " (OpenCL)";
		_device = device;
		_width = scene.Width;
		_height = scene.Height;
		_rim = scene.Rim;
		_canvasWidth = scene.CanvasWidth;
		_canvasHeight = scene.CanvasHeight;
		_openEdges = scene.OpenEdges;
		_stride = _width + 2;
		_cells = _stride * (_height + 2);
		_track = options.TrackIntensity;
		_fourthOrder = options.FourthOrderLaplacian;
		_sourceCount = scene.SourceCount;
		if (device.MaxAllocation > 0L && (long)_cells * 4L > device.MaxAllocation)
		{
			throw new InvalidOperationException("the scene is too large for this GPU");
		}
		_grid1D = new[] { new IntPtr(Math.Max(1, _sourceCount)) };
		_hostField = new float[_cells];
		_hostIntensity = _track ? new float[_cells] : null;
		_timeDecay = options.TimeDecay;
		try
		{
			Build(scene, options);
			SetEmitters(emitters);
		}
		catch
		{
			ReleaseAll();
			throw;
		}
	}

	/// <inheritdoc/>
	public void SetEmitters(IReadOnlyList<Emitter>? emitters)
	{
		// object sources: keep only the elements whose position is inside the canvas (a randomized composite
		// element can fall outside), same filtering as the CPU engine (WaveSimulator)
		var sourcesInScene = new List<Emitter>();
		if (emitters != null)
		{
			foreach (Emitter source in emitters)
			{
				if (source.X >= 0 && source.X < _canvasWidth && source.Y >= 0 && source.Y < _canvasHeight)
				{
					sourcesInScene.Add(source);
				}
			}
		}
		// the tables and kernels of the previous emitters go; the program and the scene stay
		Quietly(() => OpenClApi.clFinish(_queue));
		_emitters.Release();
		_objSourceCount = sourcesInScene.Count;
		_objGrid1D = new[] { new IntPtr(Math.Max(1, _objSourceCount)) };
		_objSources = sourcesInScene;
		if (_objSourceCount > 0)
		{
			BuildObjectSourceKernels(sourcesInScene);
		}
		// the intensity's averaging time constant follows the lowest source level in use (image or object)
		float intensityAlpha = WaveSimulator.IntensityRetention(_timeDecay, WaveSimulator.LowestSourceLevel(_levelUsed, sourcesInScene));
		for (int k = 0; k <= 1; k++)
		{
			SetFloat(_stepKernel[k], 10U, intensityAlpha);
		}
	}

	/// <inheritdoc/>
	public void Reset()
	{
		OpenClApi.Check(OpenClApi.clFinish(_queue), "clFinish");
		float[] zeros = new float[_cells];
		foreach (IntPtr buffer in new[] { _field[0], _field[1], _intensityOutBuffer, _intensityBuffer, _intensityStage1Buffer })
		{
			if (buffer != IntPtr.Zero)
			{
				Upload(buffer, zeros, _track || buffer == _field[0] || buffer == _field[1] ? _cells : 1);
			}
		}
		_current = 0;
		_time = 0.0;
		_sinceFinish = 0;
		// the emitters' cycle state lives on the card: rebuild their tables from the seeds
		SetEmitters(_objSources);
		SetProbes(null);
	}

	/// <summary>
	/// A snapshot whose planes are page-locked host memory of this context (<c>CL_MEM_ALLOC_HOST_PTR</c> buffers, mapped
	/// for the engine's life), so <see cref="CaptureFrame"/>'s reads are DMA transfers the stepping thread does not wait
	/// for: into an ordinary snapshot's arrays, pageable to the driver, the "non-blocking" read drained the queue and
	/// copied on the calling thread, 2.9 of a full-HD frame's 2.96 ms (2026-11-03). Released with the engine, after which
	/// the snapshot's planes throw.
	/// </summary>
	public FrameSnapshot CreateSnapshot(FrameShape shape, bool withIntensity)
	{
		long bytes = shape.Validate().PlaneBytes;
		IntPtr field = MapHostBuffer(bytes);
		IntPtr intensity = withIntensity ? MapHostBuffer(bytes) : IntPtr.Zero;
		var snapshot = new FrameSnapshot(shape, field, intensity);
		_hostSnapshots.Add(snapshot);
		return snapshot;
	}

	/// <summary>A page-locked host buffer of <paramref name="bytes"/>, mapped for reading and writing; the handle and the mapping go into <see cref="_hostPlanes"/> for <see cref="ReleaseAll"/>.</summary>
	private IntPtr MapHostBuffer(long bytes)
	{
		int err = 0;
		IntPtr buffer = OpenClApi.clCreateBuffer(_context, OpenClApi.MemReadWrite | OpenClApi.MemAllocHostPtr, new IntPtr(bytes), IntPtr.Zero, ref err);
		OpenClApi.Check(err, "clCreateBuffer (page-locked plane)");
		IntPtr mapped = OpenClApi.clEnqueueMapBuffer(_queue, buffer, 1U, OpenClApi.MapReadWrite, IntPtr.Zero, new IntPtr(bytes), 0U, IntPtr.Zero, IntPtr.Zero, ref err);
		if (err != 0)
		{
			Quietly(() => OpenClApi.clReleaseMemObject(buffer));
			OpenClApi.Check(err, "clEnqueueMapBuffer (page-locked plane)");
		}
		_hostPlanes.Add((buffer, mapped));
		return mapped;
	}

	/// <inheritdoc/>
	public void CaptureFrame(FrameSnapshot snapshot, bool needIntensity)
	{
		FrameShape shape = snapshot.Shape;
		if (shape.CanvasWidth != _canvasWidth || shape.CanvasHeight != _canvasHeight)
		{
			throw new ArgumentException("The snapshot's canvas does not match the engine's size.", nameof(snapshot));
		}
		if (snapshot.EventCount > 0)
		{
			throw new InvalidOperationException("The snapshot has a capture in flight; await it first.");
		}
		if (_stagingField[0] == IntPtr.Zero)
		{
			// lazily: most engines never capture (the harness, the probes-only paths). The staging planes have room
			// for any shape: a plane is at most the whole canvas as halves
			for (int set = 0; set < 2; set++)
			{
				_stagingField[set] = CreateBuffer((long)_canvasWidth * _canvasHeight * 2, _scene);
				_stagingIntensity[set] = _track ? CreateBuffer((long)_canvasWidth * _canvasHeight * 2, _scene) : IntPtr.Zero;
			}
			_captureHalfKernel = CreateKernel(_scene, _scene.Program, "capture_half");
			_captureIndexKernel = CreateKernel(_scene, _scene.Program, "capture_index");
			_captureLocal = WorkGroup2D(_captureHalfKernel, _captureIndexKernel);
			foreach (IntPtr kernel in new[] { _captureHalfKernel, _captureIndexKernel })
			{
				// the canvas, which the captures cover, and where it starts in the grid
				SetInt(kernel, 2U, _canvasWidth);
				SetInt(kernel, 3U, _canvasHeight);
				SetInt(kernel, 4U, _stride);
			}
			SetInt(_captureHalfKernel, 8U, _rim);
			SetInt(_captureIndexKernel, 10U, _rim);
		}
		if (_captureShape == null || _captureShape.Divisor != shape.Divisor || !_captureShape.SameLayoutAs(shape))
		{
			// the shape's geometry, set once per shape (every capture of a run shares one)
			_captureShape = shape;
			_captureGrid = RoundUp([shape.Width, shape.Height], _captureLocal);
			foreach (IntPtr kernel in new[] { _captureHalfKernel, _captureIndexKernel })
			{
				SetInt(kernel, 5U, shape.Divisor);
				SetInt(kernel, 6U, shape.Width);
				SetInt(kernel, 7U, shape.Height);
			}
		}
		snapshot.EventCount = 0;
		int stagingSet = _stagingSet;
		_stagingSet = 1 - stagingSet;
		EnqueueCapture(_field[_current], stagingSet, snapshot.FieldAddress, snapshot, false);
		snapshot.HasIntensity = needIntensity && _track && snapshot.HasIntensityPlane;
		if (snapshot.HasIntensity)
		{
			EnqueueCapture(_intensityOutBuffer, stagingSet, snapshot.IntensityAddress, snapshot, true);
		}
		// submitted now, for two reasons: the next capture's kernel waits for these reads, which the spec allows only
		// once their queue is flushed; and NVIDIA's driver holds a small batch back until a blocking call, so with the
		// run's stepping thread parked for a free snapshot the writer's wait had been starting the DMA instead of finding
		// it done (2026-11-03: one DMA time a frame, 0.8 ms at full HD)
		OpenClApi.Check(OpenClApi.clFlush(_transferQueue), "clFlush (transfer)");
		// everything is submitted, and the run's pool bounds how far the host gets ahead of the card, so the periodic
		// finish of AdvanceOneStep is not needed here (it had drained the card every 16 steps and cost 0.17 ms a step, 2026-11-03)
		_sinceFinish = 0;
	}

	/// <summary>
	/// Shrinks and converts a padded float buffer to a plane of the snapshot's shape on the card (staging set
	/// <paramref name="set"/>) and enqueues its read into the plane at <paramref name="plane"/>
	/// (<see cref="FrameSnapshot.PlaneBytes"/> bytes, pinned or page-locked) on <see cref="_transferQueue"/>, so the DMA
	/// overlaps the steps that follow on <see cref="_queue"/>: the capture kernel waits for the read that last left its
	/// staging set (two captures ago), the read waits for the kernel, and each queue is flushed before the other waits
	/// on its event, as the spec requires (2026-11-03: 1.9 → 1.4 ms a full-HD frame on the card).
	/// </summary>
	private void EnqueueCapture(IntPtr source, int set, IntPtr plane, FrameSnapshot snapshot, bool intensity)
	{
		FrameShape shape = snapshot.Shape;
		IntPtr staging = (intensity ? _stagingIntensity : _stagingField)[set];
		IntPtr kernel = shape.Format == FrameFormat.Half ? _captureHalfKernel : _captureIndexKernel;
		SetMem(kernel, 0U, source);
		SetMem(kernel, 1U, staging);
		if (shape.Format == FrameFormat.Index)
		{
			SetFloat(kernel, 8U, shape.BrightnessOf(intensity));
			// 0 the field, 1 the intensity, 2 its root (the preview's)
			SetInt(kernel, 9U, intensity ? (shape.IntensityRoot ? 2 : 1) : 0);
		}
		else
		{
			SetInt(kernel, 9U, intensity && shape.IntensityRoot ? 1 : 0);
		}
		int planeIndex = intensity ? 1 : 0;
		IntPtr busy = _stagingBusy[set][planeIndex];
		IntPtr[]? afterRead = busy != IntPtr.Zero ? [busy] : null;
		OpenClApi.Check(OpenClApi.clEnqueueNDRangeKernelAfter(_queue, kernel, (uint)_captureGrid!.Length, null, _captureGrid, _captureLocal, (uint)(afterRead?.Length ?? 0), afterRead, out IntPtr captured), "clEnqueueNDRangeKernel (capture)");
		if (busy != IntPtr.Zero)
		{
			Quietly(() => OpenClApi.clReleaseEvent(busy));
			_stagingBusy[set][planeIndex] = IntPtr.Zero;
		}
		OpenClApi.Check(OpenClApi.clFlush(_queue), "clFlush (capture)");
		OpenClApi.Check(OpenClApi.clEnqueueReadBufferPtrAfter(_transferQueue, staging, 0U, IntPtr.Zero, new IntPtr(snapshot.PlaneBytes), plane, 1U, [captured], out IntPtr read), "clEnqueueReadBuffer (capture)");
		Quietly(() => OpenClApi.clReleaseEvent(captured));
		// the snapshot's owner releases its reference in AwaitFrame; the staging set keeps one until the next capture into it
		OpenClApi.Check(OpenClApi.clRetainEvent(read), "clRetainEvent (capture)");
		_stagingBusy[set][planeIndex] = read;
		snapshot.Events[snapshot.EventCount++] = read;
	}

	/// <inheritdoc/>
	public void AwaitFrame(FrameSnapshot snapshot)
	{
		if (snapshot.EventCount == 0)
		{
			return;
		}
		int code = OpenClApi.clWaitForEvents((uint)snapshot.EventCount, snapshot.Events);
		for (int i = 0; i < snapshot.EventCount; i++)
		{
			Quietly(() => OpenClApi.clReleaseEvent(snapshot.Events[i]));
			snapshot.Events[i] = IntPtr.Zero;
		}
		snapshot.EventCount = 0;
		OpenClApi.Check(code, "clWaitForEvents (capture)");
	}

	/// <summary>Blocking upload of the first <paramref name="count"/> floats of <paramref name="data"/> into a device buffer.</summary>
	private void Upload(IntPtr buffer, float[] data, int count)
	{
		OpenClApi.Check(OpenClApi.clEnqueueWriteBufferFloat(_queue, buffer, 1U, IntPtr.Zero, new IntPtr((long)count * 4L), data, 0U, IntPtr.Zero, IntPtr.Zero), "clEnqueueWriteBuffer (reset)");
	}

	/// <inheritdoc/>
	public bool TryReplaceScene(SceneData scene)
	{
		if (scene.Width != _width || scene.Height != _height || scene.Rim != _rim || scene.OpenEdges != _openEdges || scene.SourceCount != _sourceCount)
		{
			return false;
		}
		OpenClApi.Check(OpenClApi.clFinish(_queue), "clFinish");
		(float[] c2, float[] damping, float c2Mult) = SceneMaps(scene);
		Upload(_c2Buffer, c2, _cells);
		Upload(_dBuffer, damping, _cells);
		for (int k = 0; k <= 1; k++)
		{
			SetFloat(_stepKernel[k], 9U, c2Mult);
			if (_openEdges)
			{
				SetFloat(_edgeKernel[k], 6U, c2Mult);
			}
		}
		(int[] indices, int[] levels) = SourceTables(scene);
		if (_sourceCount > 0)
		{
			OpenClApi.Check(OpenClApi.clEnqueueWriteBufferInt(_queue, _sourceIndexBuffer, 1U, IntPtr.Zero, new IntPtr((long)_sourceCount * 4L), indices, 0U, IntPtr.Zero, IntPtr.Zero), "clEnqueueWriteBuffer (source indices)");
			OpenClApi.Check(OpenClApi.clEnqueueWriteBufferInt(_queue, _sourceLevelBuffer, 1U, IntPtr.Zero, new IntPtr((long)_sourceCount * 4L), levels, 0U, IntPtr.Zero, IntPtr.Zero), "clEnqueueWriteBuffer (source levels)");
		}
		// Reset re-derives the intensity constant from the levels now in use, through SetEmitters
		Reset();
		Logger.Debug("Scene replaced on {Device}: {Stencil}", _device.Name, WaveSimulator.StencilText(_fourthOrder, _subSteps));
		return true;
	}

	/// <summary>
	/// The scene's per-cell maps in the padded layout (a border of one cell, like the CPU engine), with the sub-steps
	/// (<see cref="_subSteps"/>, set here) and their corrections: sub-stepping keeps the 4th-order stencil stable without
	/// changing the physical wave speed or damping, and stays at 1 unless the stencil is in use and this scene's highest
	/// c² needs it. The damping is pre-scaled to its 1 / sub-steps-th root (unchanged at 1 sub-step), as the CPU engine's,
	/// and the c² multiplier is 1 / sub-steps², exactly 1f at one sub-step (a bit-for-bit no-op multiply in the kernel).
	/// </summary>
	private (float[] C2, float[] Damping, float C2Mult) SceneMaps(SceneData scene)
	{
		float[] c2 = new float[_cells];
		float[] damping = new float[_cells];
		for (int y = 0; y < _height; y++)
		{
			Array.Copy(scene.C2, y * _width, c2, (y + 1) * _stride + 1, _width);
			Array.Copy(scene.D, y * _width, damping, (y + 1) * _stride + 1, _width);
		}
		_subSteps = _fourthOrder ? WaveSimulator.ComputeSubSteps(WaveSimulator.MaxOf(c2)) : 1;
		float c2Mult = 1f;
		if (_subSteps > 1)
		{
			// Damping is applied once per sub-step, so take the sub-step root to keep the per-step decay unchanged.
			double exponent = 1.0 / _subSteps;
			for (int i = 0; i < damping.Length; i++)
			{
				damping[i] = (float)Math.Pow(damping[i], exponent);
			}
			// A sub-step is 1/subSteps long, so c²·dt² shrinks by 1/subSteps².
			c2Mult = 1f / (_subSteps * _subSteps);
		}
		return (c2, damping, c2Mult);
	}

	/// <summary>The image sources (the green channel of the scene image) as the kernel's tables, cell index and level per source, noting the levels in use in <see cref="_levelUsed"/> afresh.</summary>
	private (int[] Indices, int[] Levels) SourceTables(SceneData scene)
	{
		int[] indices = new int[_sourceCount];
		int[] levels = new int[_sourceCount];
		Array.Clear(_levelUsed);
		for (int i = 0; i < _sourceCount; i++)
		{
			indices[i] = (scene.SourceY[i] + 1) * _stride + scene.SourceX[i] + 1;
			levels[i] = scene.SourceLevel[i];
			_levelUsed[scene.SourceLevel[i]] = true;
		}
		return (indices, levels);
	}

	/// <summary>
	/// Creates the context and queue, compiles <see cref="KernelSource"/>, uploads every buffer for the scene
	/// and binds the kernel arguments. Mirrors the <see cref="WaveSimulator"/> constructor: c² and damping are
	/// copied into the padded layout, the sub-step count and the per-sub-step damping / c² corrections are
	/// derived for the 4th-order stencil, the sources are indexed, and the intensity averaging constant is set
	/// from the lowest source level in use. When intensity is not tracked the three intensity buffers are
	/// 1-float dummies so the kernel arguments are still valid. The kernel arguments that change per step
	/// (time, per-sub-step track flag) are left to <see cref="AdvanceOneStep"/>.
	/// </summary>
	/// <param name="scene">Per-cell c² and damping maps plus the image sources.</param>
	/// <param name="options">Frequency scale, image source amplitude, stencil order (compiles the program with <c>USE_4TH_ORDER</c> and sub-steps accordingly), time decay and intensity scale.</param>
	/// <exception cref="InvalidOperationException">The OpenCL program could not be built (message includes the build log).</exception>
	private void Build(SceneData scene, EngineOptions options)
	{
		double sourceFrequencyScale = options.SourceFrequencyScale;
		_sourceFrequencyScale = sourceFrequencyScale;
		float sourceAmplitude = (float)options.SourceAmplitude;
		bool use4thOrderLaplacian = options.FourthOrderLaplacian;
		double timeDecayFactor = options.TimeDecay;
		float intensityScale = (float)options.IntensityScale;
		int err = 0;
		IntPtr[] contextProperties = { new IntPtr(OpenClApi.ContextPlatformProperty), _device.PlatformId, IntPtr.Zero };
		IntPtr[] deviceIds = { _device.DeviceId };
		_context = OpenClApi.clCreateContext(contextProperties, 1U, deviceIds, IntPtr.Zero, IntPtr.Zero, ref err);
		OpenClApi.Check(err, "clCreateContext");
		_queue = OpenClApi.clCreateCommandQueue(_context, _device.DeviceId, 0UL, ref err);
		OpenClApi.Check(err, "clCreateCommandQueue");
		_transferQueue = OpenClApi.clCreateCommandQueue(_context, _device.DeviceId, 0UL, ref err);
		OpenClApi.Check(err, "clCreateCommandQueue (transfer)");
		_scene.Program = BuildProgram(KernelSource(use4thOrderLaplacian), "the program for the GPU");

		(float[] c2, float[] damping, float c2Mult) = SceneMaps(scene);
		IntPtr c2Buffer = _c2Buffer = CreateBuffer(c2, _scene);
		IntPtr dBuffer = _dBuffer = CreateBuffer(damping, _scene);
		float[] zeros = new float[_cells];
		_field[0] = CreateBuffer(zeros, _scene);
		_field[1] = CreateBuffer(zeros, _scene);
		// Not tracked: the kernels still need valid buffer arguments, and they never touch them.
		float[] intensityInit = _track ? zeros : new float[1];
		// the two cascaded exponential-filter stages that make up the intensity - see the class summary and
		// WaveSimulator._intensity / _intensityStage1 (CPU engine); used only inside wave_step, never read back -
		// what is read back/probed is their blend, _intensityOutBuffer (WaveSimulator._intensityOut on the CPU)
		IntPtr intensityBuffer = _intensityBuffer = CreateBuffer(intensityInit, _scene);
		IntPtr intensityStage1Buffer = _intensityStage1Buffer = CreateBuffer(intensityInit, _scene);
		_intensityOutBuffer = CreateBuffer(intensityInit, _scene);

		// the level-to-frequency table (level 0-255 -> frequency), shared by the image sources and the object sources
		float[] frequencies = new float[256];
		for (int level = 0; level <= 255; level++)
		{
			frequencies[level] = (float)(level / 255.0 * 0.5 * sourceFrequencyScale);
		}
		_frequencyBuffer = CreateBuffer(frequencies, _scene);
		(int[] indices, int[] levels) = SourceTables(scene);
		// the intensity's averaging time constant scales inversely with the frequency of the slowest active source
		// (image or object; a level of 0 means "no source" and is excluded) - see WaveSimulator's identical
		// computation (CPU engine) and Simulation.TimeDecay's comment for why. This exponential-average decay
		// factor replaces the old fixed 0.98f in the kernel; SetEmitters re-derives it with the object sources.
		float intensityAlpha = WaveSimulator.IntensityRetention(timeDecayFactor, WaveSimulator.LowestSourceLevel(_levelUsed, []));

		// Kernel k reads field[k] and writes field[1 - k]; argument 4 (sources) / 11 (object sources) is the
		// time and argument 8 of wave_step the track flag, both set per launch in AdvanceOneStep.
		for (int k = 0; k <= 1; k++)
		{
			IntPtr step = _stepKernel[k] = CreateKernel(_scene, "wave_step");
			SetMem(step, 0U, _field[k]);
			SetMem(step, 1U, _field[1 - k]);
			SetMem(step, 2U, c2Buffer);
			SetMem(step, 3U, dBuffer);
			SetMem(step, 4U, intensityBuffer);
			SetInt(step, 5U, _width);
			SetInt(step, 6U, _height);
			SetInt(step, 7U, _stride);
			SetInt(step, 8U, _track ? 1 : 0);
			SetFloat(step, 9U, c2Mult);
			SetFloat(step, 10U, intensityAlpha);
			SetMem(step, 11U, intensityStage1Buffer);
			SetMem(step, 12U, _intensityOutBuffer);
			SetFloat(step, 13U, intensityScale);
			if (_openEdges)
			{
				// the ring after the step, reading the field the step read and writing the one it wrote
				IntPtr edges = _edgeKernel[k] = CreateKernel(_scene, "open_edges");
				SetMem(edges, 0U, _field[k]);
				SetMem(edges, 1U, _field[1 - k]);
				SetMem(edges, 2U, c2Buffer);
				SetInt(edges, 3U, _width);
				SetInt(edges, 4U, _height);
				SetInt(edges, 5U, _stride);
				SetFloat(edges, 6U, c2Mult);
			}
		}
		_stepLocal = WorkGroup2D(_stepKernel[0], _stepKernel[1]);
		_grid2D = RoundUp([_width, _height], _stepLocal);
		_edgeLocal = _openEdges ? WorkGroup1D(_edgeKernel[0], _edgeKernel[1]) : null;
		_edgeGrid1D = RoundUp([_width + 2 + _height], _edgeLocal);
		// what a report needs to say (2026-10-27, after a log that could not show the grid): the grid the kernels run over, and how
		Logger.Debug("OpenCL kernels built on {Device}: grid {Width} × {Height} (canvas {CanvasWidth} × {CanvasHeight}, rim {Rim}, {Edges}), stride {Stride}, work groups {Groups}, {Stencil}",
			_device.Name, _width, _height, _canvasWidth, _canvasHeight, _rim, _openEdges ? "open" : "reflective", _stride, GroupText(_stepLocal), WaveSimulator.StencilText(use4thOrderLaplacian, _subSteps));
		if (_sourceCount > 0)
		{
			IntPtr indexBuffer = _sourceIndexBuffer = CreateBuffer(indices, _scene);
			IntPtr levelBuffer = _sourceLevelBuffer = CreateBuffer(levels, _scene);
			for (int k = 0; k <= 1; k++)
			{
				IntPtr kernel = _sourceKernel[k] = CreateKernel(_scene, "apply_sources");
				SetMem(kernel, 0U, _field[k]);
				SetMem(kernel, 1U, indexBuffer);
				SetMem(kernel, 2U, levelBuffer);
				SetMem(kernel, 3U, _frequencyBuffer);
				SetFloat(kernel, 5U, sourceAmplitude);
				SetInt(kernel, 6U, _sourceCount);
			}
		}
	}

	/// <summary>
	/// Object sources (Sources tab): the flat list of generated point-source elements that are within the
	/// canvas, one entry per element. Uploads one table per <see cref="Emitter"/> property (see the
	/// <c>apply_object_sources</c> kernel comment for the layout) and binds the two
	/// <c>apply_object_sources</c> kernels, all registered in <see cref="_emitters"/> so <see cref="SetEmitters"/>
	/// can replace them. Each object source carries its angular frequency as a float, computed by the image sources'
	/// level formula from its decimal level (2026-10-19; the 256-entry table serves the image sources only). The cycle anchors start at 0 and every
	/// source gets a non-zero xorshift32 seed.
	/// </summary>
	private void BuildObjectSourceKernels(List<Emitter> objectSources)
	{
		int n = _objSourceCount;
		int[] index = new int[n], mode = new int[n], pulseShape = new int[n];
		int[] onWidth = new int[n], offWidth = new int[n], detachStep = new int[n];
		int[] offWidthBase = new int[n], randomizeOffWidthAmount = new int[n], rngState = new int[n];
		float[] omega = new float[n], amplitude = new float[n], phase = new float[n], cycleAnchor = new float[n];
		for (int i = 0; i < n; i++)
		{
			Emitter source = objectSources[i];
			index[i] = (source.Y + 1 + _rim) * _stride + source.X + 1 + _rim;
			omega[i] = (float)(source.Level / 255.0 * 0.5 * _sourceFrequencyScale);
			amplitude[i] = (float)source.Amplitude;
			phase[i] = (float)source.PhaseRadians;
			mode[i] = source.Mode == SourceMode.Pulsed ? 1 : 0;
			pulseShape[i] = source.PulseShape == PulseShape.SmoothedSquare ? 1 : 0;
			onWidth[i] = source.OnWidthCycles;
			offWidth[i] = source.OffWidthCycles;
			detachStep[i] = source.DetachAfterStep;
			offWidthBase[i] = source.OffWidthBase;
			randomizeOffWidthAmount[i] = source.RandomizeOffWidthAmount;
			// the per-element seed drawn from the project seed (never 0, see Emitter.FromSource); the kernel
			// reinterprets it as uint, only the bits matter
			rngState[i] = (int)source.RngSeed;
		}
		// Buffer arguments by kernel argument index; 0 is the field (per kernel instance), 11 the time
		// (per launch) and 12 the count (scalar), see the apply_object_sources signature.
		IntPtr[] buffers = new IntPtr[17];
		buffers[1] = CreateBuffer(index, _emitters);
		buffers[2] = CreateBuffer(omega, _emitters);
		buffers[3] = CreateBuffer(amplitude, _emitters);
		buffers[4] = CreateBuffer(phase, _emitters);
		buffers[5] = CreateBuffer(mode, _emitters);
		buffers[6] = CreateBuffer(pulseShape, _emitters);
		buffers[7] = CreateBuffer(onWidth, _emitters);
		// the off-width currently in effect for each element, in cycles; read-write on the GPU (see
		// apply_object_sources) since an element with RandomizeOffWidthAmount > 0 redraws it there each time it
		// completes a cycle
		buffers[8] = CreateBuffer(offWidth, _emitters);
		// "detach source": the step at which each element stops driving the field (releasing its hold on it), or -1 to never
		buffers[9] = CreateBuffer(detachStep, _emitters);
		buffers[10] = _frequencyBuffer;
		// 13-16 were appended at the end rather than renumbering 0-12, so every SetMem/SetInt call (and the
		// per-launch SetFloat for t at index 11 in AdvanceOneStep) kept its existing, already-correct index.
		// Runtime state for off-width re-randomization (see Emitter.RandomizeOffWidthAmount): the
		// un-randomized anchor each redraw is centered on, the per-element +/- spread (0 = never redraw), the
		// cycle-clock value at which each element's current off+on cycle started, and a per-element xorshift32
		// PRNG seed (kernel-side reinterpreted as uint - only the entropy bits matter, not signedness)
		buffers[13] = CreateBuffer(offWidthBase, _emitters);
		buffers[14] = CreateBuffer(randomizeOffWidthAmount, _emitters);
		buffers[15] = CreateBuffer(cycleAnchor, _emitters);
		buffers[16] = CreateBuffer(rngState, _emitters);
		for (int k = 0; k <= 1; k++)
		{
			IntPtr kernel = _objSourceKernel[k] = CreateKernel(_emitters, _scene.Program, "apply_object_sources");
			SetMem(kernel, 0U, _field[k]);
			for (uint a = 1; a < buffers.Length; a++)
			{
				if (buffers[a] != IntPtr.Zero)
				{
					SetMem(kernel, a, buffers[a]);
				}
			}
			SetInt(kernel, 12U, n);
		}
	}

	/// <summary>
	/// OpenCL source of the simulation program: <c>wave_step</c>, <c>apply_sources</c> and
	/// <c>apply_object_sources</c>, each a GPU transcription of the corresponding <see cref="WaveSimulator"/>
	/// routine. <paramref name="use4thOrderLaplacian"/> selects, at compile time (a <c>#define</c>, not a
	/// runtime branch), which form of the Laplacian <c>wave_step</c> uses - see the class summary and
	/// <see cref="WaveSimulator"/>'s <c>UpdateField</c>, whose CPU-side stencils and boundary logic this mirrors
	/// exactly. <c>wave_step</c>'s <c>c2Scale</c> argument is the sub-stepping scale (see the class summary),
	/// and its <c>intensityAlpha</c> argument is the intensity's exponential-average decay factor - both are
	/// runtime kernel arguments rather than a <c>#define</c>, so they need no text formatting/locale handling
	/// here. The only value spliced into the text is <see cref="Emitter.DetachRampSteps"/>, an integer,
	/// which formats the same in every locale; floats are deliberately never spliced.
	/// </summary>
	private static string KernelSource(bool use4thOrderLaplacian)
	{
		StringBuilder source = new StringBuilder();
		source.AppendLine("#define USE_4TH_ORDER " + (use4thOrderLaplacian ? "1" : "0"));
		source.AppendLine();
		// wave_step: one leapfrog sub-step, one work item per interior cell (x, y in 0..w-1 / 0..h-1).
		//   u      current field, padded, stride floats per row; the cell is i = (y + 1) * stride + x + 1
		//   up     previous field on entry, overwritten in place with the new value (leapfrog), same layout
		//   c2, d  per-cell c² and per-sub-step damping, same layout
		//   lap    4th-order 5-point-per-axis stencil for cells at least two away from the border (needs
		//          USE_4TH_ORDER), otherwise the 9-point stencil (corners 0.066, edges 0.184, centre -1)
		//   nv     = u + (u - up)·d + lap·c2·c2Scale, with c2Scale = 1/subSteps²
		//   inten1 / inten  first and second stage exponential averages of nv², retention intensityAlpha
		//   intenOut        (stage1 + stage2)/2 · intensityScale; the three are only touched when track != 0
		source.AppendLine("__kernel void wave_step(__global const float* u, __global float* up, __global const float* c2,");
		source.AppendLine("                        __global const float* d, __global float* inten,");
		source.AppendLine("                        const int w, const int h, const int stride, const int track, const float c2Scale,");
		source.AppendLine("                        const float intensityAlpha, __global float* inten1, __global float* intenOut,");
		source.AppendLine("                        const float intensityScale)");
		source.AppendLine("{");
		source.AppendLine("    int x = get_global_id(0);");
		source.AppendLine("    int y = get_global_id(1);");
		source.AppendLine("    if (x >= w || y >= h) return;");
		source.AppendLine("    int i = (y + 1) * stride + x + 1;");
		source.AppendLine("    float uc = u[i];");
		source.AppendLine("    float lap;");
		source.AppendLine("#if USE_4TH_ORDER");
		// the 4th order 5-point "cross" stencil (reaches 2 cells away along x and along y), only where the domain
		// has that much room - the outermost ring (x = 0, x = w - 1, y = 0, y = h - 1) falls back to the 3x3
		// kernel below, the same boundary rule as WaveSimulator.UpdateField on the CPU
		source.AppendLine("    if (x >= 1 && x <= w - 2 && y >= 1 && y <= h - 2) {");
		source.AppendLine("        lap = (-1.0f / 12.0f) * (u[i - 2] + u[i + 2] + u[i - 2 * stride] + u[i + 2 * stride])");
		source.AppendLine("            + (4.0f / 3.0f) * (u[i - 1] + u[i + 1] + u[i - stride] + u[i + stride])");
		source.AppendLine("            - 5.0f * uc;");
		source.AppendLine("    } else {");
		source.AppendLine("        lap = 0.066f * (u[i - stride - 1] + u[i - stride + 1] + u[i + stride - 1] + u[i + stride + 1])");
		source.AppendLine("            + 0.184f * (u[i - 1] + u[i + 1] + u[i - stride] + u[i + stride]) - uc;");
		source.AppendLine("    }");
		source.AppendLine("#else");
		source.AppendLine("    lap = 0.066f * (u[i - stride - 1] + u[i - stride + 1] + u[i + stride - 1] + u[i + stride + 1])");
		source.AppendLine("        + 0.184f * (u[i - 1] + u[i + 1] + u[i - stride] + u[i + stride]) - uc;");
		source.AppendLine("#endif");
		source.AppendLine("    float nv = uc + (uc - up[i]) * d[i] + lap * c2[i] * c2Scale;");
		source.AppendLine("    up[i] = nv;");
		// two cascaded one-pole stages, blended (0.5f - see WaveSimulator.IntensityBlend, CPU engine) into what is
		// read back/probed (intenOut) instead of using the full 2-stage cascade outright - see the class summary
		source.AppendLine("    if (track != 0) {");
		source.AppendLine("        float s1 = inten1[i] * intensityAlpha + nv * nv * (1.0f - intensityAlpha);");
		source.AppendLine("        inten1[i] = s1;");
		source.AppendLine("        float s2 = inten[i] * intensityAlpha + s1 * (1.0f - intensityAlpha);");
		source.AppendLine("        inten[i] = s2;");
		source.AppendLine("        intenOut[i] = (s1 * 0.5f + s2 * 0.5f) * intensityScale;");
		source.AppendLine("    }");
		source.AppendLine("}");
		source.AppendLine();
		// apply_sources: soft-blends the image sources into the field, one work item per source k < n.
		//   idx[k]   padded cell index of the source; level[k] its green level (index into freq)
		//   freq     256 angular frequencies (radians per step); t the current step
		//   u[idx]   = 0.9·u + 0.1·sin(freq[level]·t)·amp   (same 0.9/0.1 blend as the CPU SourceOpacity)
		source.AppendLine("__kernel void apply_sources(__global float* u, __global const int* idx, __global const int* level,");
		source.AppendLine("                            __global const float* freq, const float t, const float amp, const int n)");
		source.AppendLine("{");
		source.AppendLine("    int k = get_global_id(0);");
		source.AppendLine("    if (k >= n) return;");
		source.AppendLine("    int p = idx[k];");
		source.AppendLine("    u[p] = u[p] * 0.9f + sin(freq[level[k]] * t) * amp * 0.1f;");
		source.AppendLine("}");
		source.AppendLine();
		// xorshift32: OpenCL has no built-in RNG, so object sources that re-randomize their off-width (see
		// Emitter.RandomizeOffWidthAmount) carry their own per-element state (rngState, seeded from the project
		// seed, nonzero) through this. WaveSimulator.ComputePulsePosition runs the identical generator and draw
		// (Emitter.Xorshift32), so both engines produce the same gap sequence from the same seed.
		source.AppendLine("inline uint xorshift32(uint x)");
		source.AppendLine("{");
		source.AppendLine("    x ^= x << 13;");
		source.AppendLine("    x ^= x >> 17;");
		source.AppendLine("    x ^= x << 5;");
		source.AppendLine("    return x;");
		source.AppendLine("}");
		source.AppendLine();
		// apply_object_sources (Sources tab): soft-blends the object sources into the field, one element per
		// work item k < n, same math as WaveSimulator.ApplySources/ComputePulsePosition/PulseEnvelope on the CPU.
		// All per-source tables are indexed by k:
		//   idx, omega, amp, phase       cell index, angular frequency (radians per step), amplitude, phase (radians)
		//   mode                         0 = continuous (envelope always 1), 1 = pulsed (Gaussian or smoothed-square
		//                                envelope, "off" preceding "on" within each onWidth/offWidth cycle of the
		//                                element's own frequency)
		//   pulseShape (0 Gaussian/1 smoothed square), onWidth (cycles)
		//   offWidth                     off-width in effect; read-write: for an element with
		//                                offWidthRandomizeAmount[k] > 0 this kernel redraws it (uniformly within
		//                                +/- offWidthRandomizeAmount of offWidthBase) every time that element
		//                                completes a full off+on cycle, advancing its own cycleAnchor[k] to mark
		//                                where the new cycle started
		//   detachStep                   step after which the source fades out linearly over
		//                                Emitter.DetachRampSteps steps (-1 = never); work items past the
		//                                fade return without writing
		//   offWidthBase ± offWidthRandomizeAmount   range of a randomized off-width (amount <= 0 = fixed)
		//   cycleAnchor, rngState        kernel-owned state for the randomized mode: start (cycles) of the
		//                                current period and the xorshift32 state - so, unlike the other
		//                                object-source buffers, offWidth/cycleAnchor/rngState are genuinely
		//                                mutated by the GPU across steps
		// The result is u[idx] = 0.9·u + 0.1·sin(phase + f·t)·amp·envelope·detachFade.
		source.AppendLine("__kernel void apply_object_sources(__global float* u, __global const int* idx, __global const float* omega,");
		source.AppendLine("                                   __global const float* amp, __global const float* phase, __global const int* mode,");
		source.AppendLine("                                   __global const int* pulseShape, __global const int* onWidth, __global int* offWidth,");
		source.AppendLine("                                   __global const int* detachStep,");
		source.AppendLine("                                   __global const float* freq, const float t, const int n,");
		source.AppendLine("                                   __global const int* offWidthBase, __global const int* offWidthRandomizeAmount,");
		source.AppendLine("                                   __global float* cycleAnchor, __global int* rngState)");
		source.AppendLine("{");
		source.AppendLine("    int k = get_global_id(0);");
		source.AppendLine("    if (k >= n) return;");
		// "detach source": once the run reaches the element's detach step, its drive is faded out linearly over the
		// next DetachRampSteps steps (rather than cut instantly), then it stops driving the field entirely (not even
		// writing towards 0) so it releases its hold and stops diffracting waves through that point - matching
		// WaveSimulator.ApplySources exactly (Emitter.DetachRampSteps is the single shared constant, spliced
		// into the kernel text).
		source.AppendLine("    float detachFade = 1.0f;");
		source.AppendLine("    if (detachStep[k] >= 0) {");
		source.AppendLine("        float stepsSinceDetach = t - (float)detachStep[k];");
		string ramp = Emitter.DetachRampSteps.ToString(CultureInfo.InvariantCulture);
		source.AppendLine("        if (stepsSinceDetach >= " + ramp + ".0f) return;");
		source.AppendLine("        if (stepsSinceDetach > 0.0f) detachFade = 1.0f - stepsSinceDetach / " + ramp + ".0f;");
		source.AppendLine("    }");
		source.AppendLine("    float f = omega[k];");
		source.AppendLine("    float envelope = 1.0f;");
		source.AppendLine("    if (mode[k] != 0) {");
		source.AppendLine("        int ow = offWidth[k];");
		source.AppendLine("        int period = onWidth[k] + ow;");
		source.AppendLine("        if (period > 0) {");
		source.AppendLine("            float cycleClock = f * t / (2.0f * M_PI_F);");
		source.AppendLine("            float pos;");
		source.AppendLine("            int isFirstCycle;");
		source.AppendLine("            if (offWidthRandomizeAmount[k] <= 0) {");
		// shifted so t = 0 lands at pos = ow (the start of the "on" span), not pos = 0 (the start of an "off" span) -
		// a single/non-randomized source starting silent for its off-width was never the intent (see
		// WaveSimulator.ComputePulsePosition, kept in exact agreement with this kernel)
		source.AppendLine("                float adjustedClock = cycleClock + (float)ow;");
		source.AppendLine("                isFirstCycle = (floor(adjustedClock / (float)period) == 0.0f) ? 1 : 0;");
		source.AppendLine("                pos = adjustedClock - period * floor(adjustedClock / (float)period);");
		source.AppendLine("            } else {");
		source.AppendLine("                pos = cycleClock - cycleAnchor[k];");
		source.AppendLine("                int guard = 0;");
		source.AppendLine("                while (pos >= (float)period && guard < 1000) {");
		source.AppendLine("                    cycleAnchor[k] += (float)period;");
		source.AppendLine("                    pos -= (float)period;");
		source.AppendLine("                    uint state = xorshift32((uint)rngState[k]);");
		source.AppendLine("                    rngState[k] = (int)state;");
		source.AppendLine("                    int amt = offWidthRandomizeAmount[k];");
		source.AppendLine("                    int span = 2 * amt + 1;");
		source.AppendLine("                    int r = (int)(state % (uint)span) - amt;");
		source.AppendLine("                    ow = offWidthBase[k] + r;");
		source.AppendLine("                    if (ow < 1) ow = 1;");
		source.AppendLine("                    offWidth[k] = ow;");
		source.AppendLine("                    period = onWidth[k] + ow;");
		source.AppendLine("                    guard++;");
		source.AppendLine("                }");
		source.AppendLine("                if (pos < 0.0f) pos = 0.0f;");
		// the anchor only ever advances (above) once a cycle has actually completed, so it still reading its initial
		// 0 means no cycle has completed yet - we are still in the element's very first (off-first) cycle
		source.AppendLine("                isFirstCycle = (cycleAnchor[k] == 0.0f) ? 1 : 0;");
		source.AppendLine("            }");
		source.AppendLine("            if (pulseShape[k] == 0) {");
		source.AppendLine("                if (pos < (float)ow) {");
		source.AppendLine("                    envelope = 0.0f;");
		source.AppendLine("                } else {");
		source.AppendLine("                    float onPos = pos - (float)ow;");
		source.AppendLine("                    float center = onWidth[k] / 2.0f;");
		source.AppendLine("                    float sigma = max(onWidth[k] / 6.0f, 1.0e-6f);");
		source.AppendLine("                    float dd = (onPos - center) / sigma;");
		source.AppendLine("                    envelope = exp(-0.5f * dd * dd);");
		source.AppendLine("                }");
		source.AppendLine("            } else {");
		source.AppendLine("                // a flat-topped pulse (envelope 1 while on, 0 while off) with a short, FIXED-width");
		source.AppendLine("                // (in cycles) smoothed edge at the rise and the fall - fixed, so a long off span");
		source.AppendLine("                // sits at (very close to) 0 for nearly all of it instead of lingering. The math is");
		source.AppendLine("                // exactly the on-first formula, fed a coordinate shifted so the on span starts at 0.");
		source.AppendLine("                float shifted = pos - (float)ow;");
		// wrapping shifted forward by a full period makes it continue the falling tail of the on-span that JUST
		// ended - correct from the second off-span onward, where that on-span really happened. The very first
		// off-span (isFirstCycle) has no real preceding on-emission to continue from, so wrapping there would
		// fabricate one: pos = 0 would land exactly on that phantom falling edge and start the run at envelope ~0.5
		// instead of ~0, emitting a spurious one-cycle "blip" before the source actually goes quiet. Left un-wrapped
		// (deeply negative) instead, dRise/dFall below correctly place it far from both edges, keeping the envelope
		// at (very close to) 0 until the real rising edge - matching WaveSimulator.PulseEnvelope exactly.
		source.AppendLine("                if (shifted < 0.0f && !isFirstCycle) shifted += (float)period;");
		source.AppendLine("                float edgeWidth = 0.35f;");
		source.AppendLine("                float dFall = shifted - (float)onWidth[k];");
		source.AppendLine("                float dRise = shifted;");
		source.AppendLine("                if (dRise > (float)period / 2.0f) dRise -= (float)period;");
		source.AppendLine("                float rising = 0.5f + atan(dRise / edgeWidth) / M_PI_F;");
		source.AppendLine("                float falling = 0.5f - atan(dFall / edgeWidth) / M_PI_F;");
		source.AppendLine("                envelope = rising * falling;");
		source.AppendLine("            }");
		source.AppendLine("        }");
		source.AppendLine("    }");
		source.AppendLine("    float v = sin(phase[k] + f * t) * amp[k] * envelope * detachFade;");
		source.AppendLine("    int p = idx[k];");
		source.AppendLine("    u[p] = u[p] * 0.9f + v * 0.1f;");
		source.AppendLine("}");
		source.AppendLine();
		// capture_half / capture_index: the interior of a padded float buffer as a tightly packed plane of the
		// frame store's shape (FrameConversion is the reference): every div × div block of cells, clipped at the
		// right and bottom edges, averaged into one sample, stored as a half or as the palette index the renderer
		// would compute at the given brightness; so a frame crosses the bus at half the size or less and the host
		// converts nothing. vstore_half is core OpenCL C; only half arithmetic would need cl_khr_fp16.
		source.AppendLine("float capture_sample(__global const float* src, const int w, const int h, const int stride, const int div, const int rim, const int x, const int y)");
		source.AppendLine("{");
		source.AppendLine("    if (div == 1) return src[(y + 1 + rim) * stride + x + 1 + rim];");
		source.AppendLine("    int x0 = x * div, y0 = y * div;");
		source.AppendLine("    int x1 = min(w, x0 + div), y1 = min(h, y0 + div);");
		source.AppendLine("    float sum = 0.0f;");
		source.AppendLine("    for (int cy = y0; cy < y1; cy++)");
		source.AppendLine("        for (int cx = x0; cx < x1; cx++)");
		source.AppendLine("            sum += src[(cy + 1 + rim) * stride + cx + 1 + rim];");
		source.AppendLine("    return sum / (float)((y1 - y0) * (x1 - x0));");
		source.AppendLine("}");
		source.AppendLine();
		// the intensity as its square root when the shape says so (2026-10-23, FrameConversion): the block's mean energy, then the root
		source.AppendLine("__kernel void capture_half(__global const float* src, __global half* dst, const int w, const int h, const int stride, const int div, const int fw, const int fh, const int rim, const int root)");
		source.AppendLine("{");
		source.AppendLine("    int x = get_global_id(0);");
		source.AppendLine("    int y = get_global_id(1);");
		source.AppendLine("    if (x >= fw || y >= fh) return;");
		source.AppendLine("    float v = capture_sample(src, w, h, stride, div, rim, x, y);");
		source.AppendLine("    vstore_half(root ? sqrt(v) : v, y * fw + x, dst);");
		source.AppendLine("}");
		source.AppendLine();
		// the index mapping of ColorLut.FieldIndex / IntensityRootIndex / IntensityIndex: a NaN field gives the centre, a NaN
		// or non-positive intensity 0. The index is that of exactly the half a 16-bit frame stores, rounded as the CPU's
		// capture rounds it, bit for bit (2026-10-19: an 8-bit run and a 16-bit run of the same project had differed by a
		// palette step in half a percent of the pixels): the field, the intensity's root (intensity 2, the preview's since
		// 2026-10-23: rooted, then rounded) or the intensity itself (intensity 1).
		// The rounding is done on the float's bits, since a half variable needs an extension; it is round to nearest
		// even, half's subnormals and overflow included. No contraction here: the CPU's multiply and add are separate
		source.AppendLine("float round_to_half(float f)");
		source.AppendLine("{");
		source.AppendLine("    uint u = as_uint(f);");
		source.AppendLine("    uint sign = u & 0x80000000u;");
		source.AppendLine("    uint a = u & 0x7FFFFFFFu;");
		source.AppendLine("    if (a >= 0x7F800000u) return f;                                        // inf or nan");
		source.AppendLine("    if (a >= 0x477FF000u) return as_float(sign | 0x7F800000u);          // 65520 and up round to inf");
		source.AppendLine("    if (a < 0x38800000u) {                                                 // below 2^-14: a subnormal half, a multiple of 2^-24");
		source.AppendLine("        float q = rint(as_float(a) * 16777216.0f) * 5.9604644775390625e-8f;");
		source.AppendLine("        return as_float(sign | as_uint(q));");
		source.AppendLine("    }");
		source.AppendLine("    uint rest = a & 0x1FFFu;                                               // the 13 bits a half drops");
		source.AppendLine("    uint kept = a & ~0x1FFFu;");
		source.AppendLine("    if (rest > 0x1000u || (rest == 0x1000u && (kept & 0x2000u))) kept += 0x2000u;");
		source.AppendLine("    return as_float(sign | kept);");
		source.AppendLine("}");
		source.AppendLine();
		source.AppendLine("#pragma OPENCL FP_CONTRACT OFF");
		source.AppendLine("__kernel void capture_index(__global const float* src, __global uchar* dst, const int w, const int h, const int stride, const int div, const int fw, const int fh, const float brightness, const int intensity, const int rim)");
		source.AppendLine("{");
		source.AppendLine("    int x = get_global_id(0);");
		source.AppendLine("    int y = get_global_id(1);");
		source.AppendLine("    if (x >= fw || y >= fh) return;");
		source.AppendLine("    float raw = capture_sample(src, w, h, stride, div, rim, x, y);");
		source.AppendLine("    float v = round_to_half(intensity == 2 ? sqrt(raw) : raw);");
		source.AppendLine("    int index;");
		source.AppendLine("    if (intensity == 2)");
		source.AppendLine("        index = v > 0.0f ? (int)(fmin(v * brightness, 1.0f) * 254.0f) : 0;");
		source.AppendLine("    else if (intensity == 1)");
		source.AppendLine("        index = v > 0.0f ? (int)(fmin(sqrt(v) * brightness, 1.0f) * 254.0f) : 0;");
		source.AppendLine("    else {");
		source.AppendLine("        float s = v * brightness;");
		source.AppendLine("        index = isnan(s) ? 127 : (int)(clamp(s, -1.0f, 1.0f) * 127.0f + 127.0f);");
		source.AppendLine("    }");
		source.AppendLine("    dst[y * fw + x] = (uchar)index;");
		source.AppendLine("}");
		source.AppendLine();
		// open_edges: Mur's first-order absorbing condition on the ring, WaveSimulator.ApplyOpenEdges cell for cell and
		// operation for operation (no contraction, see above): one work item per column of the top and bottom rows
		// (corners take their diagonal neighbour), then one per row of the left and right columns
		source.AppendLine("void absorb(__global const float* u, __global float* up, __global const float* c2, const float c2Scale, const int ring, const int inner)");
		source.AppendLine("{");
		source.AppendLine("    float s = sqrt(c2[inner] * c2Scale) * " + EdgeSpeedFactorLiteral + ";");
		source.AppendLine("    float k = (s - 1.0f) / (s + 1.0f);");
		source.AppendLine("    up[ring] = u[inner] + k * (up[inner] - u[ring]);");
		source.AppendLine("}");
		source.AppendLine();
		source.AppendLine("__kernel void open_edges(__global const float* u, __global float* up, __global const float* c2, const int w, const int h, const int stride, const float c2Scale)");
		source.AppendLine("{");
		source.AppendLine("    int id = get_global_id(0);");
		source.AppendLine("    if (id < w + 2) {");
		source.AppendLine("        int x = id;");
		source.AppendLine("        int ix = clamp(x, 1, w);");
		source.AppendLine("        absorb(u, up, c2, c2Scale, x, stride + ix);");
		source.AppendLine("        absorb(u, up, c2, c2Scale, (h + 1) * stride + x, h * stride + ix);");
		source.AppendLine("    } else {");
		source.AppendLine("        int y = id - (w + 2) + 1;");
		source.AppendLine("        if (y > h) return;");
		source.AppendLine("        absorb(u, up, c2, c2Scale, y * stride, y * stride + 1);");
		source.AppendLine("        absorb(u, up, c2, c2Scale, y * stride + w + 1, y * stride + w);");
		source.AppendLine("    }");
		source.AppendLine("}");
		return source.ToString();
	}

	/// <summary>Compiles <paramref name="source"/> for <see cref="_device"/>; <paramref name="what"/> names the program in the error message.</summary>
	/// <exception cref="InvalidOperationException">The build failed; the message carries the OpenCL error and the build log.</exception>
	private IntPtr BuildProgram(string source, string what)
	{
		int err = 0;
		IntPtr program = OpenClApi.clCreateProgramWithSource(_context, 1U, new[] { source }, null, ref err);
		OpenClApi.Check(err, "clCreateProgramWithSource");
		IntPtr[] deviceIds = { _device.DeviceId };
		// a correctly rounded square root where the card has one: the 8-bit index capture then equals the CPU's
		// MathF.Sqrt bit for bit (2026-10-19); the pulse shapes' divisions only come closer to the host's as well
		string options = _device.CorrectlyRoundedSqrt ? "-cl-fp32-correctly-rounded-divide-sqrt" : "";
		if (!_device.CorrectlyRoundedSqrt)
		{
			Logger.Debug("The card's single-precision square root is not correctly rounded: an 8-bit palette index may differ by one from a 16-bit render at a boundary");
		}
		err = OpenClApi.clBuildProgram(program, 1U, deviceIds, options, IntPtr.Zero, IntPtr.Zero);
		if (err != 0)
		{
			string buildLog = OpenClApi.BuildLog(program, _device.DeviceId);
			OpenClApi.clReleaseProgram(program);
			throw new InvalidOperationException(what + " could not be built (" + OpenClApi.ErrorName(err) + ")" + (buildLog.Length > 0 ? ": " + buildLog : ""));
		}
		return program;
	}

	/// <summary>Creates a read/write device buffer of <paramref name="bytes"/> bytes, registered in <paramref name="owner"/> for release.</summary>
	private IntPtr CreateBuffer(long bytes, Resources owner)
	{
		int err = 0;
		IntPtr buffer = OpenClApi.clCreateBuffer(_context, OpenClApi.MemReadWrite, new IntPtr(bytes), IntPtr.Zero, ref err);
		OpenClApi.Check(err, "clCreateBuffer");
		owner.Buffers.Add(buffer);
		return buffer;
	}

	/// <summary>Creates a device buffer the size of <paramref name="data"/>, uploads it (blocking) and registers it in <paramref name="owner"/>.</summary>
	private IntPtr CreateBuffer(float[] data, Resources owner)
	{
		IntPtr size = new IntPtr((long)data.Length * 4L);
		IntPtr buffer = CreateBuffer(size.ToInt64(), owner);
		OpenClApi.Check(OpenClApi.clEnqueueWriteBufferFloat(_queue, buffer, 1U, IntPtr.Zero, size, data, 0U, IntPtr.Zero, IntPtr.Zero), "clEnqueueWriteBuffer");
		return buffer;
	}

	/// <summary>Int counterpart of <see cref="CreateBuffer(float[], Resources)"/>.</summary>
	private IntPtr CreateBuffer(int[] data, Resources owner)
	{
		IntPtr size = new IntPtr((long)data.Length * 4L);
		IntPtr buffer = CreateBuffer(size.ToInt64(), owner);
		OpenClApi.Check(OpenClApi.clEnqueueWriteBufferInt(_queue, buffer, 1U, IntPtr.Zero, size, data, 0U, IntPtr.Zero, IntPtr.Zero), "clEnqueueWriteBuffer");
		return buffer;
	}

	/// <summary>Creates the kernel <paramref name="name"/> from <paramref name="owner"/>'s program and registers it there for release.</summary>
	private static IntPtr CreateKernel(Resources owner, string name) => CreateKernel(owner, owner.Program, name);

	/// <summary>Creates the kernel <paramref name="name"/> from <paramref name="program"/> and registers it in <paramref name="owner"/> for release (which keeps the program alive only when it owns it).</summary>
	private static IntPtr CreateKernel(Resources owner, IntPtr program, string name)
	{
		int err = 0;
		IntPtr kernel = OpenClApi.clCreateKernel(program, name, ref err);
		OpenClApi.Check(err, "clCreateKernel " + name);
		owner.Kernels.Add(kernel);
		return kernel;
	}

	/// <summary>Binds a device buffer to kernel argument <paramref name="index"/>.</summary>
	private static void SetMem(IntPtr kernel, uint index, IntPtr value)
	{
		OpenClApi.Check(OpenClApi.clSetKernelArgMem(kernel, index, new IntPtr(IntPtr.Size), ref value), "clSetKernelArg");
	}

	/// <summary>Sets an <c>int</c> kernel argument.</summary>
	private static void SetInt(IntPtr kernel, uint index, int value)
	{
		OpenClApi.Check(OpenClApi.clSetKernelArgInt(kernel, index, new IntPtr(4), ref value), "clSetKernelArg");
	}

	/// <summary>Sets a <c>float</c> kernel argument.</summary>
	private static void SetFloat(IntPtr kernel, uint index, float value)
	{
		OpenClApi.Check(OpenClApi.clSetKernelArgFloat(kernel, index, new IntPtr(4), ref value), "clSetKernelArg");
	}

	/// <summary>Enqueues <paramref name="kernel"/> with the given global work size, the work-group size left to the driver (the small 1D kernels); <paramref name="what"/> names the launch in the error message.</summary>
	private void Enqueue(IntPtr kernel, IntPtr[] globalSize, string what) => Enqueue(kernel, globalSize, null, what);

	/// <summary>Enqueues <paramref name="kernel"/> with the given global work size, in groups of <paramref name="localSize"/> (null: the driver's choice); <paramref name="what"/> names the launch in the error message.</summary>
	private void Enqueue(IntPtr kernel, IntPtr[] globalSize, IntPtr[]? localSize, string what)
	{
		OpenClApi.Check(OpenClApi.clEnqueueNDRangeKernel(_queue, kernel, (uint)globalSize.Length, null, globalSize, localSize, 0U, IntPtr.Zero, IntPtr.Zero), "clEnqueueNDRangeKernel (" + what + ")");
	}

	/// <summary>
	/// The work-group size for the kernels over the grid (<c>wave_step</c>, the captures): 32 × 8 work items, a row
	/// segment of 32 cells (128 bytes, what a warp or wavefront reads in one go) by 8 rows, so the stencil's
	/// neighbours above and below come from the group's own cache lines; shrunk to what the kernels can run with on
	/// this device (CL_KERNEL_WORK_GROUP_SIZE, which allows for the registers they use) and to the device's extents
	/// per dimension, the rows halved first. Null, leaving the choice to the driver, when a query fails. Left to the
	/// driver, the group has to divide the global size exactly, and a grid of prime width (1789 cells: a 1725 px
	/// canvas with the 32 px rim, 2026-10-26) got groups one cell wide, each warp reading a column of scattered
	/// addresses in place of a row: 13 to 19 times slower on an RTX 2080. With an explicit group the global size is
	/// rounded up to whole groups (<see cref="RoundUp"/>) and the kernels' own bounds checks drop the surplus work
	/// items.
	/// </summary>
	private IntPtr[]? WorkGroup2D(params IntPtr[] kernels) => FitGroup([32, 8], kernels);

	/// <summary>The work-group size for a kernel over a list (<c>open_edges</c>): 64 work items, shrunk as <see cref="WorkGroup2D"/> shrinks; null when a query fails.</summary>
	private IntPtr[]? WorkGroup1D(params IntPtr[] kernels) => FitGroup([64], kernels);

	/// <summary><paramref name="preferred"/> shrunk to the limit of every kernel in <paramref name="kernels"/> and to the device's extents, the last dimension halved first; null when a query fails.</summary>
	private IntPtr[]? FitGroup(int[] preferred, IntPtr[] kernels)
	{
		ulong limit = ulong.MaxValue;
		foreach (IntPtr kernel in kernels)
		{
			ulong own = OpenClApi.KernelWorkGroupLimit(kernel, _device.DeviceId);
			if (own == 0UL)
			{
				return null;
			}
			limit = Math.Min(limit, own);
		}
		ulong[] extents = OpenClApi.DeviceSizes(_device.DeviceId, OpenClApi.DeviceMaxWorkItemSizes);
		if (extents.Length < preferred.Length)
		{
			return null;
		}
		int[] size = (int[])preferred.Clone();
		for (int axis = 0; axis < size.Length; axis++)
		{
			while (size[axis] > 1 && (ulong)size[axis] > extents[axis])
			{
				size[axis] /= 2;
			}
		}
		for (int axis = size.Length - 1; axis >= 0 && Product(size) > limit;)
		{
			if (size[axis] > 1)
			{
				size[axis] /= 2;
			}
			else
			{
				axis--;
			}
		}
		return [.. size.Select(s => new IntPtr(s))];

		static ulong Product(int[] size)
		{
			ulong product = 1UL;
			foreach (int s in size)
			{
				product *= (ulong)s;
			}
			return product;
		}
	}

	/// <summary>A work-group size for the log: "32 × 8", or "the driver's" when the choice is left to the driver.</summary>
	private static string GroupText(IntPtr[]? local) => local == null ? "the driver's" : string.Join(" × ", local.Select(v => v.ToInt64().ToString(CultureInfo.InvariantCulture)));

	/// <summary>
	/// Logs, before a device first builds an engine in this process (2026-10-27, the user's wish: a report should say
	/// which card, driver and limits were at work, and which cards there were): the candidates in the order tried, once,
	/// then the device's versions, compute units, clock, memory, the largest buffer, the work-group limit and extents,
	/// and whether its square root is correctly rounded. Before the build, so a build that fails has its device on record.
	/// </summary>
	private static void DescribeOnce(List<GpuDeviceInfo> candidates, GpuDeviceInfo device)
	{
		lock (_described)
		{
			if (_described.Add("*"))
			{
				Logger.Debug("GPUs through OpenCL, the first that works is used: {Devices}", string.Join("; ", candidates.Select(d => d.Name + (d.Discrete ? " (discrete)" : " (integrated)"))));
			}
			if (!_described.Add(device.Name + "|" + device.DriverVersion))
			{
				return;
			}
		}
		Logger.Debug("GPU in use: {Name} ({Vendor}, {Kind}), {Version}, driver {Driver}, {Units} compute units at {Clock} MHz, {Memory:0.0} GB, largest buffer {Allocation:0.0} GB, work groups up to {MaxGroup} ({Extents}), {Sqrt}",
			device.Name, device.Vendor, device.Discrete ? "discrete" : "integrated", device.Version, device.DriverVersion, device.ComputeUnits, device.ClockMhz, device.GlobalMemory / 1073741824.0, device.MaxAllocation / 1073741824.0,
			device.MaxWorkGroupSize, string.Join(" × ", device.MaxWorkItemSizes), device.CorrectlyRoundedSqrt ? "correctly rounded sqrt" : "no correctly rounded sqrt");
	}

	/// <summary>The global work size for <paramref name="items"/> work items per dimension: rounded up to whole groups of <paramref name="local"/>, or as given when the driver chooses the group.</summary>
	private static IntPtr[] RoundUp(int[] items, IntPtr[]? local)
	{
		var global = new IntPtr[items.Length];
		for (int axis = 0; axis < items.Length; axis++)
		{
			int group = local == null ? 1 : (int)local[axis];
			global[axis] = new IntPtr((items[axis] + group - 1) / group * group);
		}
		return global;
	}

	/// <summary>Blocking device-to-host copy of <paramref name="target"/>.Length floats from <paramref name="buffer"/>; resets the clFinish counter since the read drains the queue.</summary>
	private void ReadBack(IntPtr buffer, float[] target, string what)
	{
		OpenClApi.Check(OpenClApi.clEnqueueReadBufferFloat(_queue, buffer, 1U, IntPtr.Zero, new IntPtr((long)target.Length * 4L), target, 0U, IntPtr.Zero, IntPtr.Zero), "clEnqueueReadBuffer (" + what + ")");
		_sinceFinish = 0;
	}

	/// <summary>Engine label for the UI/result: "GPU: &lt;device name&gt; (OpenCL)".</summary>
	public string Description { get; }

	/// <summary>The card this engine runs on.</summary>
	public GpuDeviceInfo Device => _device;

	/// <summary>The work-group size <c>wave_step</c> runs with (see <see cref="WorkGroup2D"/>); (0, 0) when the driver chooses.</summary>
	public (int Width, int Height) WorkGroup => _stepLocal == null ? (0, 0) : ((int)_stepLocal[0], (int)_stepLocal[1]);

	/// <summary>Scene width in cells (canvas pixels), excluding the padding.</summary>
	public int Width => _canvasWidth;

	/// <inheritdoc/>
	public int Rim => _rim;

	/// <summary>Scene height in cells (canvas pixels), excluding the padding.</summary>
	public int Height => _canvasHeight;

	/// <summary>Row pitch of the padded buffers: <see cref="Width"/> + 2.</summary>
	public int Stride => _stride;

	/// <summary>
	/// Host copy of the field (padded layout). Unlike the CPU engine the array instance never changes, but
	/// its contents are only as fresh as the last <see cref="PrepareFrame"/> with <c>needField</c>.
	/// </summary>
	public float[] FieldData => _hostField;

	/// <summary>Host copy of the scaled intensity (padded layout) as of the last <see cref="PrepareFrame"/> with <c>needIntensity</c>; null when the engine was created without intensity tracking.</summary>
	public float[]? IntensityData => _hostIntensity;

	/// <summary>
	/// Enqueues one simulation step (nothing is waited for): the source kernels bound to the current field
	/// blend the sources in once, then <c>wave_step</c> runs <see cref="_subSteps"/> times, alternating
	/// between the two kernel instances so each sub-step reads the newest buffer and overwrites the older one
	/// (the leapfrog "previous" field). The track flag is set so only the last sub-step updates the intensity
	/// (with a single sub-step the flag keeps the value bound in <see cref="Build"/>). Afterwards
	/// <see cref="_current"/> points at the newest buffer and <see cref="_time"/> advances by one step.
	/// Every <see cref="FinishInterval"/> steps the queue is drained with <c>clFinish</c>, so that the commands
	/// do not pile up and errors show up in time.
	/// </summary>
	public void AdvanceOneStep()
	{
		int current = _current;
		if (_sourceCount > 0)
		{
			SetFloat(_sourceKernel[current], 4U, (float)_time);
			Enqueue(_sourceKernel[current], _grid1D, "sources");
		}
		if (_objSourceCount > 0)
		{
			SetFloat(_objSourceKernel[current], 11U, (float)_time);
			Enqueue(_objSourceKernel[current], _objGrid1D, "object sources");
		}
		// _subSteps smaller launches (see the class summary); only the last one updates the intensity buffer, so
		// its own exponential averaging still runs once per AdvanceOneStep call, not _subSteps times. On the
		// single-launch path the "track" argument keeps the value bound once, at Build time.
		for (int subStep = 1; subStep <= _subSteps; subStep++)
		{
			if (_subSteps > 1)
			{
				SetInt(_stepKernel[current], 8U, subStep == _subSteps && _track ? 1 : 0);
			}
			Enqueue(_stepKernel[current], _grid2D, _stepLocal, "step");
			if (_openEdges)
			{
				Enqueue(_edgeKernel[current], _edgeGrid1D, _edgeLocal, "edges");
			}
			current = 1 - current;
		}
		// the buffer that received the new values is now the current field
		_current = current;
		_time += 1.0;
		// now and then wait for the card, so that the commands do not pile up and errors show up in time
		if (++_sinceFinish >= FinishInterval)
		{
			_sinceFinish = 0;
			OpenClApi.Check(OpenClApi.clFinish(_queue), "clFinish");
		}
	}

	/// <summary>
	/// Blocking device-to-host copies of the newest field into <see cref="FieldData"/> and/or of the intensity
	/// into <see cref="IntensityData"/> (the latter only when tracked). Called by the runner before rendering,
	/// and from the UI thread while paused.
	/// </summary>
	public void PrepareFrame(bool needField, bool needIntensity)
	{
		if (needField)
		{
			ReadBack(_field[_current], _hostField, "field");
		}
		if (needIntensity && _hostIntensity != null)
		{
			ReadBack(_intensityOutBuffer, _hostIntensity, "intensity");
		}
	}

	/// <summary>
	/// OpenCL source of the probe program (built lazily in <see cref="SetProbes"/>): <c>probe_sample</c> for
	/// point probes, <c>profile_accumulate</c> and <c>profile_gather</c> for line probes. Together they are
	/// the device side of <see cref="WaveSimulator.SampleProbes"/> / <see cref="WaveSimulator.ReadProbeProfiles"/>.
	/// </summary>
	private static string ProbeKernelSource()
	{
		StringBuilder source = new StringBuilder();
		// probe_sample (point probes): the value of the pixel of every probe, into row "row" of the history;
		// one work item per probe p < n (n = all probes, line probes included).
		//   u, inten   newest field and the intensity output, padded layout
		//   pix        pixel index lists of all point probes concatenated
		//   info       4 ints per probe: [start in pix, count, 1 = sample inten instead of u, 1 = divide by count];
		//              all 0 for line probes, which therefore write 0
		//   hist       ProbeRows × n floats, row-major: hist[row * n + p] receives this step's value
		source.AppendLine("__kernel void probe_sample(__global const float* u, __global const float* inten, __global const int* pix,");
		source.AppendLine("                           __global const int* info, __global float* hist, const int row, const int n)");
		source.AppendLine("{");
		source.AppendLine("    int p = get_global_id(0);");
		source.AppendLine("    if (p >= n) return;");
		source.AppendLine("    int start = info[p * 4];");
		source.AppendLine("    int count = info[p * 4 + 1];");
		source.AppendLine("    int useI = info[p * 4 + 2];");
		source.AppendLine("    int mean = info[p * 4 + 3];");
		source.AppendLine("    float s = 0.0f;");
		source.AppendLine("    if (useI != 0) {");
		source.AppendLine("        for (int k = 0; k < count; k++) s += inten[pix[start + k]];");
		source.AppendLine("    } else {");
		source.AppendLine("        for (int k = 0; k < count; k++) s += u[pix[start + k]];");
		source.AppendLine("    }");
		source.AppendLine("    if (mean != 0 && count > 0) s = s / (float)count;");
		source.AppendLine("    hist[row * n + p] = s;");
		source.AppendLine("}");
		source.AppendLine();
		// profile_accumulate (line probes): one work item per line-probe pixel k < n (all line probes flattened):
		//   ppix[k]  padded cell index; pmode[k] 0 = field, 1 = intensity, 2 = intensity totals over the steps
		//   total[k] += inten[ppix[k]] for mode 2 only, and only once step >= pStartStep[k] when pStartFlag[k] is set
		// pStartFlag/pStartStep are the probe's "start recording at step #" (0/1 and the step): before that step
		// the total is left at zero, so intensity that arrived earlier is purged rather than baked into the total
		// once recording starts
		source.AppendLine("__kernel void profile_accumulate(__global const float* inten, __global const int* ppix,");
		source.AppendLine("                                 __global const int* pmode, __global const int* pStartFlag, __global const int* pStartStep,");
		source.AppendLine("                                 __global float* total, const int step, const int n)");
		source.AppendLine("{");
		source.AppendLine("    int k = get_global_id(0);");
		source.AppendLine("    if (k >= n) return;");
		source.AppendLine("    if (pmode[k] != 2) return;");
		source.AppendLine("    if (pStartFlag[k] != 0 && step < pStartStep[k]) return;");
		source.AppendLine("    total[k] += inten[ppix[k]];");
		source.AppendLine("}");
		source.AppendLine();
		// profile_gather: one work item per line-probe pixel k < n; same ppix/pmode layout as above.
		//   outv[k] = total[k] (mode 2), inten[ppix[k]] (mode 1) or u[ppix[k]] (mode 0)
		source.AppendLine("__kernel void profile_gather(__global const float* u, __global const float* inten, __global const int* ppix,");
		source.AppendLine("                             __global const int* pmode, __global const float* total, __global float* outv, const int n)");
		source.AppendLine("{");
		source.AppendLine("    int k = get_global_id(0);");
		source.AppendLine("    if (k >= n) return;");
		source.AppendLine("    int m = pmode[k];");
		source.AppendLine("    float v;");
		source.AppendLine("    if (m == 2) v = total[k];");
		source.AppendLine("    else if (m == 1) v = inten[ppix[k]];");
		source.AppendLine("    else v = u[ppix[k]];");
		source.AppendLine("    outv[k] = v;");
		source.AppendLine("}");
		return source.ToString();
	}

	/// <summary>
	/// Installs the probe layout, replacing any previously installed one (whose device resources are released
	/// first): builds the probe program (<see cref="ProbeKernelSource"/>), then uploads the pixel tables and
	/// creates the kernels for point probes (<see cref="SetUpSeriesProbes"/>) and line probes
	/// (<see cref="SetUpProfileProbes"/>). A null or empty sampler just clears the probes.
	/// </summary>
	/// <exception cref="InvalidOperationException">The sampler needs intensity but this engine does not track it, or the probe program / buffers could not be created (inner exception has the OpenCL error).</exception>
	public void SetProbes(ProbeSampler? sampler)
	{
		_probes.Release();
		_probeSampler = null;
		_probeValues.Clear();
		_probeRow = 0;
		_profileIntegrates = false;
		if (sampler == null || sampler.Count == 0)
		{
			return;
		}
		if (sampler.NeedsIntensity && !_track)
		{
			throw new InvalidOperationException("the intensity is not tracked");
		}
		try
		{
			if (_probeProgram == IntPtr.Zero)
			{
				// compiled once per engine, not per SetProbes: the scratch runner installs the probes on every request
				// (2026-10-14), and a compile per request cost more than the frame it was for
				_probeProgram = BuildProgram(ProbeKernelSource(), "the probe program for the GPU");
			}
			if (sampler.HasSeries)
			{
				SetUpSeriesProbes(sampler);
			}
			if (sampler.HasProfiles)
			{
				SetUpProfileProbes(sampler);
			}
			_probeSampler = sampler;
		}
		catch (Exception ex)
		{
			_probes.Release();
			throw new InvalidOperationException("the probes could not be set up on the GPU: " + ex.Message, ex);
		}
	}

	/// <summary>
	/// Packs the pixel lists of all point (non-profile) probes into one buffer, describes each probe in an info
	/// table (point probes average their pixels, so a single-pixel probe yields that pixel's value like
	/// <see cref="ProbeSampler.Compute"/>), allocates the <see cref="ProbeRows"/>-row history buffer and creates
	/// the two <c>probe_sample</c> kernels.
	/// </summary>
	private void SetUpSeriesProbes(ProbeSampler sampler)
	{
		int count = sampler.Count;
		int totalPixels = 0;
		for (int i = 0; i < count; i++)
		{
			if (!sampler.IsProfile[i])
			{
				totalPixels += sampler.Pixels[i].Length;
			}
		}
		int[] pixels = new int[Math.Max(totalPixels, 1)];
		int[] info = new int[count * 4];
		int offset = 0;
		for (int i = 0; i < count; i++)
		{
			if (!sampler.IsProfile[i])
			{
				int[] probePixels = sampler.Pixels[i];
				Array.Copy(probePixels, 0, pixels, offset, probePixels.Length);
				info[i * 4] = offset;
				info[i * 4 + 1] = probePixels.Length;
				info[i * 4 + 2] = sampler.UseIntensity[i] ? 1 : 0;
				info[i * 4 + 3] = 1;
				offset += probePixels.Length;
			}
		}
		IntPtr pixelBuffer = CreateBuffer(pixels, _probes);
		IntPtr infoBuffer = CreateBuffer(info, _probes);
		// Never uploaded: the kernel writes every row before ReadProbeHistory reads it.
		_historyBuffer = CreateBuffer((long)ProbeRows * count * 4L, _probes);
		for (int k = 0; k <= 1; k++)
		{
			IntPtr kernel = _probeKernel[k] = CreateKernel(_probes, _probeProgram, "probe_sample");
			SetMem(kernel, 0U, _field[k]);
			SetMem(kernel, 1U, _intensityOutBuffer);
			SetMem(kernel, 2U, pixelBuffer);
			SetMem(kernel, 3U, infoBuffer);
			SetMem(kernel, 4U, _historyBuffer);
			SetInt(kernel, 5U, 0);
			SetInt(kernel, 6U, count);
		}
		_probeGrid[0] = new IntPtr(count);
		for (int i = 0; i < count; i++)
		{
			_probeValues.Add(new List<float>());
		}
	}

	/// <summary>
	/// Flattens the pixels of all line probes into per-pixel buffers (index, mode, start flag/step), records
	/// where each probe starts in <see cref="_profileStart"/>, zero-initialises the totals and output buffers
	/// and creates the two <c>profile_gather</c> kernels. The start-step buffers and the
	/// <c>profile_accumulate</c> kernel are only created when at least one probe integrates.
	/// </summary>
	private void SetUpProfileProbes(ProbeSampler sampler)
	{
		int count = sampler.Count;
		int totalPixels = 0;
		for (int i = 0; i < count; i++)
		{
			if (sampler.IsProfile[i])
			{
				totalPixels += sampler.Pixels[i].Length;
			}
		}
		int[] pixels = new int[totalPixels];
		int[] modes = new int[totalPixels];
		int[] startFlags = new int[totalPixels];
		int[] startSteps = new int[totalPixels];
		_profileStart = new int[count];
		int offset = 0;
		for (int i = 0; i < count; i++)
		{
			_profileStart[i] = offset;
			if (!sampler.IsProfile[i])
			{
				continue;
			}
			int[] probePixels = sampler.Pixels[i];
			int mode = sampler.Integrate[i] ? 2 : (sampler.UseIntensity[i] ? 1 : 0);
			if (mode == 2)
			{
				_profileIntegrates = true;
			}
			int startFlag = sampler.UseStartAfterStep[i] ? 1 : 0;
			int startStep = sampler.StartAfterStep[i];
			for (int k = 0; k < probePixels.Length; k++)
			{
				pixels[offset + k] = probePixels[k];
				modes[offset + k] = mode;
				startFlags[offset + k] = startFlag;
				startSteps[offset + k] = startStep;
			}
			offset += probePixels.Length;
		}
		_profileCount = totalPixels;
		IntPtr pixelBuffer = CreateBuffer(pixels, _probes);
		IntPtr modeBuffer = CreateBuffer(modes, _probes);
		IntPtr totalBuffer = CreateBuffer(new float[totalPixels], _probes);
		_profileOutBuffer = CreateBuffer(new float[totalPixels], _probes);
		for (int k = 0; k <= 1; k++)
		{
			IntPtr kernel = _gatherKernel[k] = CreateKernel(_probes, _probeProgram, "profile_gather");
			SetMem(kernel, 0U, _field[k]);
			SetMem(kernel, 1U, _intensityOutBuffer);
			SetMem(kernel, 2U, pixelBuffer);
			SetMem(kernel, 3U, modeBuffer);
			SetMem(kernel, 4U, totalBuffer);
			SetMem(kernel, 5U, _profileOutBuffer);
			SetInt(kernel, 6U, totalPixels);
		}
		if (_profileIntegrates)
		{
			IntPtr kernel = _accumulateKernel = CreateKernel(_probes, _probeProgram, "profile_accumulate");
			SetMem(kernel, 0U, _intensityOutBuffer);
			SetMem(kernel, 1U, pixelBuffer);
			SetMem(kernel, 2U, modeBuffer);
			// "start recording at step #" for the integrated line probes: one entry per pixel (same layout as the
			// pixel/mode buffers), so profile_accumulate can skip a pixel's contribution before its probe's start step
			SetMem(kernel, 3U, CreateBuffer(startFlags, _probes));
			SetMem(kernel, 4U, CreateBuffer(startSteps, _probes));
			SetMem(kernel, 5U, totalBuffer);
			SetInt(kernel, 7U, totalPixels);
		}
		_profileGrid[0] = new IntPtr(totalPixels);
	}

	/// <summary>
	/// Called by the runner after every step: enqueues <c>probe_sample</c> for the newest field into the next
	/// history row (first draining the history to the host when all <see cref="ProbeRows"/> rows are full),
	/// and <c>profile_accumulate</c> with the current step number when any line probe integrates. Nothing is
	/// waited for.
	/// </summary>
	public void SampleProbes()
	{
		if (_probeSampler == null)
		{
			return;
		}
		if (_probeSampler.HasSeries)
		{
			if (_probeRow >= ProbeRows)
			{
				ReadProbeHistory();
			}
			SetInt(_probeKernel[_current], 5U, _probeRow);
			Enqueue(_probeKernel[_current], _probeGrid, "probes");
			_probeRow++;
		}
		// the intensity totals of the lines that are integrated: add the intensity of this step (unless it is before
		// that probe's "start recording at step #", in which case the kernel leaves the total at zero)
		if (_profileIntegrates)
		{
			SetInt(_accumulateKernel, 6U, (int)Math.Round(_time));
			Enqueue(_accumulateKernel, _profileGrid, "totals");
		}
	}

	/// <summary>
	/// Blocking download of the <see cref="_probeRow"/> rows sampled so far from <see cref="_historyBuffer"/>,
	/// appending each row's point-probe values to <see cref="_probeValues"/> (line-probe columns are skipped)
	/// and rewinding the row counter. No-op when no row is pending.
	/// </summary>
	private void ReadProbeHistory()
	{
		if (_probeRow == 0 || _probeSampler == null)
		{
			return;
		}
		int count = _probeSampler.Count;
		float[] history = new float[_probeRow * count];
		ReadBack(_historyBuffer, history, "probes");
		for (int row = 0; row < _probeRow; row++)
		{
			for (int p = 0; p < count; p++)
			{
				if (!_probeSampler.IsProfile[p])
				{
					_probeValues[p].Add(history[row * count + p]);
				}
			}
		}
		_probeRow = 0;
	}

	/// <summary>Drains the pending history rows from the device, then returns and clears the point-probe values collected since the previous call.</summary>
	/// <returns>One array per probe, one value per step (empty for line probes); an empty outer array when no sampler is set.</returns>
	public float[][] TakeProbeSamples()
	{
		if (_probeSampler == null)
		{
			return [];
		}
		int count = _probeSampler.Count;
		float[][] samples = new float[count][];
		if (_probeSampler.HasSeries)
		{
			ReadProbeHistory();
		}
		for (int i = 0; i < count; i++)
		{
			if (_probeSampler.IsProfile[i])
			{
				samples[i] = new float[0];
			}
			else
			{
				samples[i] = _probeValues[i].ToArray();
				_probeValues[i].Clear();
			}
		}
		return samples;
	}

	/// <summary>
	/// Runs <c>profile_gather</c> on the newest field and reads the flat result back (blocking: this waits for
	/// the card, so do not call it every step), then slices it per line probe; nothing is cleared.
	/// </summary>
	/// <returns>
	/// One array per probe in pixel order along the line, holding the integrated intensity totals or the live
	/// field/intensity values; null entries for point probes; an empty outer array when no sampler is set.
	/// </returns>
	public float[]?[] ReadProbeProfiles()
	{
		if (_probeSampler == null)
		{
			return [];
		}
		int count = _probeSampler.Count;
		float[]?[] profiles = new float[count][];
		if (!_probeSampler.HasProfiles)
		{
			return profiles;
		}
		Enqueue(_gatherKernel[_current], _profileGrid, "profiles");
		float[] values = new float[_profileCount];
		ReadBack(_profileOutBuffer, values, "profiles");
		for (int i = 0; i < count; i++)
		{
			if (_probeSampler.IsProfile[i])
			{
				int length = _probeSampler.Pixels[i].Length;
				float[] profile = new float[length];
				Array.Copy(values, _profileStart[i], profile, 0, length);
				profiles[i] = profile;
			}
		}
		return profiles;
	}

	/// <summary>Releases every OpenCL handle; called by the runner when the run ends. Safe to call more than once.</summary>
	public void Dispose()
	{
		ReleaseAll();
	}

	/// <summary>
	/// Releases all device resources: drains the queue, then the probe set, the scene set, the queue and
	/// finally the context. OpenCL failures are swallowed, so the method is also usable from the constructor
	/// after a partial <see cref="Build"/>, and a second call finds nothing left to release.
	/// </summary>
	private void ReleaseAll()
	{
		if (_queue != IntPtr.Zero)
		{
			// if this fails the driver is gone or in trouble; release what can be released
			Quietly(() => OpenClApi.clFinish(_queue));
		}
		if (_transferQueue != IntPtr.Zero)
		{
			Quietly(() => OpenClApi.clFinish(_transferQueue));
		}
		foreach (IntPtr[] set in _stagingBusy)
		{
			for (int p = 0; p < set.Length; p++)
			{
				if (set[p] != IntPtr.Zero)
				{
					IntPtr evt = set[p];
					Quietly(() => OpenClApi.clReleaseEvent(evt));
					set[p] = IntPtr.Zero;
				}
			}
		}
		// the snapshots' planes: told first, so a late reader throws instead of reading freed memory
		foreach (FrameSnapshot snapshot in _hostSnapshots)
		{
			snapshot.Release();
		}
		_hostSnapshots.Clear();
		if (_queue != IntPtr.Zero)
		{
			foreach ((IntPtr buffer, IntPtr mapped) in _hostPlanes)
			{
				Quietly(() => OpenClApi.clEnqueueUnmapMemObject(_queue, buffer, mapped, 0U, IntPtr.Zero, IntPtr.Zero));
			}
			Quietly(() => OpenClApi.clFinish(_queue));
		}
		foreach ((IntPtr buffer, _) in _hostPlanes)
		{
			Quietly(() => OpenClApi.clReleaseMemObject(buffer));
		}
		_hostPlanes.Clear();
		_probes.Release();
		if (_probeProgram != IntPtr.Zero)
		{
			Quietly(() => OpenClApi.clReleaseProgram(_probeProgram));
			_probeProgram = IntPtr.Zero;
		}
		_emitters.Release();
		_scene.Release();
		if (_transferQueue != IntPtr.Zero)
		{
			Quietly(() => OpenClApi.clReleaseCommandQueue(_transferQueue));
			_transferQueue = IntPtr.Zero;
		}
		if (_queue != IntPtr.Zero)
		{
			Quietly(() => OpenClApi.clReleaseCommandQueue(_queue));
			_queue = IntPtr.Zero;
		}
		if (_context != IntPtr.Zero)
		{
			Quietly(() => OpenClApi.clReleaseContext(_context));
			_context = IntPtr.Zero;
		}
	}

	/// <summary>Runs an OpenCL release call, ignoring any failure (nothing more can be done; the handle is gone either way).</summary>
	private static void Quietly(Action release)
	{
		try
		{
			release();
		}
		catch
		{
		}
	}

	/// <summary>
	/// A program with the kernels and buffers created from/for it, released together: the scene set lives as
	/// long as the engine, the probe set is replaced by every <see cref="SetProbes"/> call.
	/// </summary>
	private sealed class Resources
	{
		/// <summary>The compiled program, or <see cref="IntPtr.Zero"/> when not (yet) built.</summary>
		public IntPtr Program;

		/// <summary>Kernels created from <see cref="Program"/>.</summary>
		public readonly List<IntPtr> Kernels = [];

		/// <summary>Device buffers bound to those kernels.</summary>
		public readonly List<IntPtr> Buffers = [];

		/// <summary>Releases kernels, buffers and the program (in that order) and forgets them; failures are ignored.</summary>
		public void Release()
		{
			foreach (IntPtr kernel in Kernels)
			{
				Quietly(() => OpenClApi.clReleaseKernel(kernel));
			}
			Kernels.Clear();
			foreach (IntPtr buffer in Buffers)
			{
				Quietly(() => OpenClApi.clReleaseMemObject(buffer));
			}
			Buffers.Clear();
			if (Program != IntPtr.Zero)
			{
				Quietly(() => OpenClApi.clReleaseProgram(Program));
				Program = IntPtr.Zero;
			}
		}
	}

	/// <summary>Steps enqueued between two <c>clFinish</c> calls in <see cref="AdvanceOneStep"/>.</summary>
	private const int FinishInterval = 16;

	/// <summary>The page-locked host buffers behind the snapshots of <see cref="CreateSnapshot"/>, each with its mapping; unmapped and released by <see cref="ReleaseAll"/>.</summary>
	private readonly List<(IntPtr Buffer, IntPtr Mapped)> _hostPlanes = [];

	/// <summary>The snapshots of <see cref="CreateSnapshot"/>, told by <see cref="ReleaseAll"/> that their planes are gone.</summary>
	private readonly List<FrameSnapshot> _hostSnapshots = [];

	/// <summary>Rows (steps) the probe history buffer holds before it must be read back to the host.</summary>
	private const int ProbeRows = 2048;

	/// <summary>The probe program (<see cref="ProbeKernelSource"/>), built on the first <see cref="SetProbes"/> and kept for the engine's life; its kernels and buffers live in <see cref="_probes"/>.</summary>
	private IntPtr _probeProgram;

	/// <summary>The engine's log: the device once per process and the grid of every build, both at Debug.</summary>
	private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

	/// <summary>The devices described in the log so far, by name and driver version (<see cref="DescribeOnce"/>); locked on itself, as engines are built on several threads.</summary>
	private static readonly HashSet<string> _described = [];

	/// <summary>The device the context lives on; needed again to build the probe program.</summary>
	private readonly GpuDeviceInfo _device;
	private readonly int _rim;
	private readonly int _canvasWidth;
	private readonly int _canvasHeight;
	private readonly bool _openEdges;
	private readonly IntPtr[] _edgeKernel = new IntPtr[2];
	private IntPtr[] _edgeGrid1D = [new IntPtr(1)];

	/// <summary><see cref="WaveSimulator.EdgeSpeedFactor"/> as the kernel source spells it, so both engines use the same float.</summary>
	private static readonly string EdgeSpeedFactorLiteral = WaveSimulator.EdgeSpeedFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";

	/// <summary>Scene width in cells (canvas pixels), without padding.</summary>
	private readonly int _width;

	/// <summary>Scene height in cells, without padding.</summary>
	private readonly int _height;

	/// <summary>Padded row pitch (<see cref="_width"/> + 2).</summary>
	private readonly int _stride;

	/// <summary>Number of floats in every padded per-cell buffer: <see cref="_stride"/> × (<see cref="_height"/> + 2).</summary>
	private readonly int _cells;

	/// <summary>Whether the intensity is tracked; when false the intensity buffers are 1-float dummies and <see cref="IntensityData"/> is null.</summary>
	private readonly bool _track;

	/// <summary>Whether the program was compiled for the 4th-order stencil (the sub-steps follow the scene's highest c² then).</summary>
	private readonly bool _fourthOrder;

	/// <summary>Number of image (green-channel) sources in the scene.</summary>
	private readonly int _sourceCount;

	/// <summary>Number of object sources that lie inside the canvas; set by <see cref="SetEmitters"/>.</summary>
	private int _objSourceCount;

	/// <summary>The emitters inside the canvas as last installed, so <see cref="Reset"/> can rebuild their on-card state.</summary>
	private List<Emitter> _objSources = [];

	/// <summary>The scale of the level-to-frequency mapping, for the object sources' decimal levels (the image sources use the table).</summary>
	private double _sourceFrequencyScale;

	/// <summary>Per level 0..255: whether an image source uses it (the object sources are added per <see cref="SetEmitters"/> call).</summary>
	private readonly bool[] _levelUsed = new bool[256];

	/// <summary><see cref="Simulation.TimeDecay"/>, kept so <see cref="SetEmitters"/> can re-derive the intensity retention.</summary>
	private readonly double _timeDecay;

	/// <summary>The level-to-frequency table (256 floats), shared by the image and the object sources.</summary>
	private IntPtr _frequencyBuffer;

	/// <summary>The padded c² and pre-scaled damping maps bound to the step and edge kernels; refilled by <see cref="TryReplaceScene"/>.</summary>
	private IntPtr _c2Buffer;
	private IntPtr _dBuffer;

	/// <summary>The image sources' cell indices and levels bound to <c>apply_sources</c> (unset without image sources); refilled by <see cref="TryReplaceScene"/>.</summary>
	private IntPtr _sourceIndexBuffer;
	private IntPtr _sourceLevelBuffer;

	/// <summary>The two cascaded intensity stages (1-float dummies when not tracking), kept so <see cref="Reset"/> can zero them.</summary>
	private IntPtr _intensityBuffer;
	private IntPtr _intensityStage1Buffer;

	/// <summary>Two sets of planes the capture kernels write for <see cref="CaptureFrame"/>, taken in turn so a capture never waits for the previous capture's read; created on first use, sized for any shape.</summary>
	private readonly IntPtr[] _stagingField = new IntPtr[2];
	private readonly IntPtr[] _stagingIntensity = new IntPtr[2];
	private IntPtr _captureHalfKernel;
	private IntPtr _captureIndexKernel;

	/// <summary>The staging set the next capture writes (0 or 1).</summary>
	private int _stagingSet;

	/// <summary>Per staging set and plane (0 the field, 1 the intensity): the retained event of the read that last left the set, which the next capture into the set waits for; released there or by <see cref="ReleaseAll"/>.</summary>
	private readonly IntPtr[][] _stagingBusy = [new IntPtr[2], new IntPtr[2]];

	/// <summary>The shape the capture kernels' geometry arguments are set for, and the matching global work size.</summary>
	private FrameShape? _captureShape;
	private IntPtr[]? _captureGrid;

	/// <summary>
	/// Leapfrog sub-steps per simulation step - see the class summary; 1 (no sub-stepping) unless the 4th-order
	/// stencil is on and this scene's highest c² needs it. Set once, in <see cref="Build"/>.
	/// </summary>
	private int _subSteps;

	/// <summary>OpenCL context on the chosen device; released last in <see cref="ReleaseAll"/>.</summary>
	private IntPtr _context;

	/// <summary>In-order command queue; every launch and the blocking transfers go through it, the captures' reads through <see cref="_transferQueue"/>.</summary>
	private IntPtr _queue;

	/// <summary>In-order queue for the captures' reads (<see cref="EnqueueCapture"/>): on a queue of their own the DMA transfers overlap the steps on <see cref="_queue"/>, ordered against them by events.</summary>
	private IntPtr _transferQueue;

	/// <summary>Simulation program (<c>wave_step</c>, <c>apply_sources</c>, <c>apply_object_sources</c>) with its kernels and scene buffers.</summary>
	private readonly Resources _scene = new Resources();

	/// <summary>
	/// The probes: a second small program (<c>probe_sample</c>, <c>profile_accumulate</c>, <c>profile_gather</c>)
	/// measures them on the card, with its kernels and buffers; empty until probes are set. Point probes: one
	/// value per step; the values wait in <see cref="_historyBuffer"/> until the next
	/// <see cref="TakeProbeSamples"/> (or until the buffer is full). Line probes: the pixels of all lines in one
	/// list; the totals (integrated intensity) are kept on the card, the profiles are gathered into
	/// <see cref="_profileOutBuffer"/> and copied back when they are asked for (<see cref="ReadProbeProfiles"/>).
	/// </summary>
	private readonly Resources _probes = new Resources();

	/// <summary>The object sources' tables and the two <c>apply_object_sources</c> kernels (created from the scene program); replaced by every <see cref="SetEmitters"/> call.</summary>
	private readonly Resources _emitters = new Resources();

	/// <summary>The two ping-pong field buffers (padded, <see cref="_cells"/> floats each); the one at <see cref="_current"/> holds the newest field, the other the previous one.</summary>
	private readonly IntPtr[] _field = new IntPtr[2];

	/// <summary>
	/// The blend of the two cascaded filter stages (the <c>inten1</c>/<c>inten</c> buffers, which live only
	/// inside <c>wave_step</c>), scaled - see the class summary and <c>WaveSimulator._intensityOut</c> (CPU
	/// engine). Padded; this is what is actually read back into <see cref="_hostIntensity"/> and sampled by the
	/// probe kernels; a 1-float dummy when not tracking.
	/// </summary>
	private IntPtr _intensityOutBuffer;

	/// <summary><c>wave_step</c> kernel per field index k: reads <see cref="_field"/>[k], writes <see cref="_field"/>[1 - k].</summary>
	private readonly IntPtr[] _stepKernel = new IntPtr[2];

	/// <summary><c>apply_sources</c> kernel per field index k, bound to <see cref="_field"/>[k]; unset when the scene has no image sources.</summary>
	private readonly IntPtr[] _sourceKernel = new IntPtr[2];

	/// <summary><c>apply_object_sources</c> kernel per field index k, bound to <see cref="_field"/>[k]; unset when there are no object sources.</summary>
	private readonly IntPtr[] _objSourceKernel = new IntPtr[2];

	/// <summary>Index (0/1) of the field buffer holding the newest field; flips after every sub-step.</summary>
	private int _current;

	/// <summary>Global work size of <c>wave_step</c>: <see cref="_width"/> × <see cref="_height"/> work items, one per interior cell, rounded up to whole groups of <see cref="_stepLocal"/>; set in <see cref="Build"/>.</summary>
	private IntPtr[] _grid2D = [new IntPtr(1), new IntPtr(1)];

	/// <summary>Work-group size of <c>wave_step</c> (<see cref="WorkGroup2D"/>); null leaves it to the driver.</summary>
	private IntPtr[]? _stepLocal;

	/// <summary>Work-group size of <c>open_edges</c> (<see cref="WorkGroup1D"/>); null leaves it to the driver.</summary>
	private IntPtr[]? _edgeLocal;

	/// <summary>Work-group size of the capture kernels (<see cref="WorkGroup2D"/>); null leaves it to the driver.</summary>
	private IntPtr[]? _captureLocal;

	/// <summary>Global work size of <c>apply_sources</c>: max(1, <see cref="_sourceCount"/>).</summary>
	private readonly IntPtr[] _grid1D;

	/// <summary>Global work size of <c>apply_object_sources</c>: max(1, <see cref="_objSourceCount"/>).</summary>
	private IntPtr[] _objGrid1D = [new IntPtr(1)];

	/// <summary>Host copy of the newest field, refreshed by <see cref="PrepareFrame"/>; exposed as <see cref="FieldData"/>.</summary>
	private readonly float[] _hostField;

	/// <summary>Host copy of <see cref="_intensityOutBuffer"/>, refreshed by <see cref="PrepareFrame"/>; null when not tracking.</summary>
	private readonly float[]? _hostIntensity;

	/// <summary>Elapsed time in steps; passed to the source kernels as the float argument <c>t</c>.</summary>
	private double _time;

	/// <summary>Steps enqueued since the queue was last drained; reset by every blocking read, which drains the queue anyway.</summary>
	private int _sinceFinish;

	/// <summary>Probe layout installed by <see cref="SetProbes"/>, or null when no probes are set.</summary>
	private ProbeSampler? _probeSampler;

	/// <summary><c>probe_sample</c> kernel per field index k, bound to <see cref="_field"/>[k]; unset without point probes.</summary>
	private readonly IntPtr[] _probeKernel = new IntPtr[2];

	/// <summary>Sample history, <see cref="ProbeRows"/> rows × probe count floats, row-major; row r holds the samples of the r-th step since the last read-back.</summary>
	private IntPtr _historyBuffer;

	/// <summary>Next row of <see cref="_historyBuffer"/> to write, i.e. the number of rows waiting to be read back.</summary>
	private int _probeRow;

	/// <summary>Global work size of <c>probe_sample</c>: one work item per probe.</summary>
	private readonly IntPtr[] _probeGrid = new IntPtr[1];

	/// <summary>Point-probe samples read back from the device and not yet taken by <see cref="TakeProbeSamples"/>, one list per probe (stays empty for line probes).</summary>
	private readonly List<List<float>> _probeValues = [];

	/// <summary><c>profile_gather</c> kernel per field index k, bound to <see cref="_field"/>[k]; unset without line probes.</summary>
	private readonly IntPtr[] _gatherKernel = new IntPtr[2];

	/// <summary><c>profile_accumulate</c> kernel; created only when some line probe integrates (<see cref="_profileIntegrates"/>).</summary>
	private IntPtr _accumulateKernel;

	/// <summary>Per line-probe pixel: value written by <c>profile_gather</c> and read back by <see cref="ReadProbeProfiles"/>.</summary>
	private IntPtr _profileOutBuffer;

	/// <summary>Total number of line-probe pixels (length of the profile buffers).</summary>
	private int _profileCount;

	/// <summary>Whether any line probe integrates the intensity, i.e. <c>profile_accumulate</c> must run every step.</summary>
	private bool _profileIntegrates;

	/// <summary>Per probe: offset of its first pixel in the profile buffers (only meaningful for line probes).</summary>
	private int[] _profileStart = [];

	/// <summary>Global work size of the profile kernels: one work item per line-probe pixel.</summary>
	private readonly IntPtr[] _profileGrid = new IntPtr[1];
}
