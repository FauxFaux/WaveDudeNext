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

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using WaveDudeNext.Engine;
using WaveDudeNext.Model;
using WaveDudeNext.Parts;

namespace WaveDudeNext.Wasm;

/// <summary>Browser entry point and initial JavaScript exports for the shared CPU solver.</summary>
[SupportedOSPlatform("browser")]
public static partial class Program
{
	private static WaveSimulator? _engine;
	private static FrameSnapshot? _snapshot;
	private static int _frameCount;
	private static int _nextFrame;
	private static int _visualizeEvery;
	private static int _stepsDone;

	/// <summary>
	/// Starts a canvas project on the calling worker thread. Returns width, height, bounded step count,
	/// frame interval and frame count. At most three frames are computed; the input project is unchanged.
	/// Image scenes require image delivery, which is not yet supported by this boundary.
	/// </summary>
	[JSExport]
	public static int[] StartSimulation(string projectJson)
	{
		EndSimulation();
		Project project = JsonSerializer.Deserialize(projectJson, ProjectJsonContext.Default.Project)
			?? throw new ArgumentException("The project is empty.", nameof(projectJson));
		project.Normalize();
		if (project.Scene.Mode != SceneMode.Canvas)
			throw new InvalidOperationException("Image scenes are not supported in the browser yet. Choose a canvas scene.");
		Scene scene = project.Scene;
		var image = PartPainter.Paint(null, scene.Width, scene.Height, scene.RefractiveIndex, project.Parts);
		SceneData data = image == null
			? SceneData.Empty(scene.Width, scene.Height, scene.DampingEdge, scene.RefractiveIndex, scene.Edges == EdgeMode.Open)
			: SceneData.FromBgra(image.Bgra, image.Width, image.Height, image.StrideBytes, false, scene.DampingEdge, scene.Edges == EdgeMode.Open);
		_engine = new WaveSimulator(data, EngineOptions.From(project.Simulation, scene, true), Emitter.FromProject(project));
		_snapshot = _engine.CreateSnapshot(FrameShape.Full(scene.Width, scene.Height), true);
		_visualizeEvery = project.Simulation.VisualizeEvery;
		_frameCount = Math.Min(3, (project.Simulation.Steps - 1) / _visualizeEvery + 1);
		return [scene.Width, scene.Height, (_frameCount - 1) * _visualizeEvery + 1, _visualizeEvery, _frameCount];
	}

	/// <summary>
	/// Advances to the next preview frame and returns its row-major half-float planes as bytes:
	/// field first, then square-root intensity. Frame k follows 0-based step k × the frame interval.
	/// Only the calling worker thread may use an active simulation.
	/// </summary>
	[JSExport]
	public static byte[] NextFrame()
	{
		if (_engine == null || _snapshot == null || _nextFrame >= _frameCount)
			throw new InvalidOperationException("There are no more preview frames.");
		int target = _nextFrame * _visualizeEvery + 1;
		while (_stepsDone < target)
		{
			_engine.AdvanceOneStep();
			_stepsDone++;
		}
		_engine.CaptureFrame(_snapshot, true);
		_engine.AwaitFrame(_snapshot);
		byte[] planes = new byte[_snapshot.FieldBytes.Length * 2];
		_snapshot.FieldBytes.CopyTo(planes);
		_snapshot.IntensityBytes.CopyTo(planes.AsSpan(_snapshot.FieldBytes.Length));
		_nextFrame++;
		return planes;
	}

	/// <summary>Releases the active engine and snapshot on the calling worker thread; safe to call repeatedly.</summary>
	[JSExport]
	public static void EndSimulation()
	{
		_engine?.Dispose();
		_engine = null;
		_snapshot = null;
		_nextFrame = 0;
		_stepsDone = 0;
	}

	/// <summary>Leaves the runtime available for JavaScript calls; no simulation starts automatically.</summary>
	public static void Main()
	{
	}

	/// <summary>
	/// Runs 16 steps synchronously on the calling runtime thread and returns the peak absolute field amplitude.
	/// This small deterministic scene checks that JavaScript can execute the real shared solver.
	/// </summary>
	[JSExport]
	public static double SmokeTest()
	{
		var scene = SceneData.Empty(32, 24, -8, 1, true);
		var source = new Source { X = 16, Y = 12, Frequency = 110, Amplitude = 70 };
		source.GenerateElements(123456);
		using var engine = new WaveSimulator(scene, new EngineOptions(1, 1, true, false, 5, 1), Emitter.FromSource(source, 123456));
		for (int step = 0; step < 16; step++)
			engine.AdvanceOneStep();
		if (engine.FieldData.Any(value => !float.IsFinite(value)))
			throw new InvalidOperationException("The Wasm solver produced a nonfinite field.");
		var peak = engine.FieldData.Max(value => Math.Abs(value));
		if (peak <= 0)
			throw new InvalidOperationException("The Wasm solver produced an empty field.");
		return peak;
	}
}
