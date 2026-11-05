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
using WaveDudeNext.Engine;
using WaveDudeNext.Model;

namespace WaveDudeNext.Wasm;

/// <summary>Browser entry point and initial JavaScript exports for the shared CPU solver.</summary>
[SupportedOSPlatform("browser")]
public static partial class Program
{
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
