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

using System.Buffers.Binary;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using WaveDudeNext.Model;
using WaveDudeNext.Parts;
using WaveDudeNext.Rendering;

namespace WaveDudeNext.Wasm;

public static partial class Program
{
	/// <summary>
	/// Paints a normalised canvas and its glowing edges on the calling worker thread, independently of simulation.
	/// Returns little-endian 32-bit width and height, then two tightly packed row-major RGBA byte planes
	/// (scene, edges). An unpainted canvas has only the eight-byte header. Image scenes are unsupported.
	/// </summary>
	/// <param name="projectJson">The frontend project, or just its scene and parts.</param>
	[JSExport]
	public static byte[] RenderScene(string projectJson)
	{
		Project project = ReadCanvasProject(projectJson);
		Scene scene = project.Scene;
		var pixels = PartPainter.Paint(null, scene.Width, scene.Height, scene.RefractiveIndex, project.Parts);
		byte[] result = new byte[checked(8 + (pixels == null ? 0 : pixels.Bgra.Length * 2))];
		BinaryPrimitives.WriteInt32LittleEndian(result, scene.Width);
		BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), scene.Height);
		if (pixels == null) return result;
		var edges = GlowingEdges.Compute(pixels.Bgra, pixels.Width, pixels.Height, pixels.StrideBytes);
		pixels.Bgra.CopyTo(result, 8);
		edges.CopyTo(result, 8 + pixels.Bgra.Length);
		// Canvas ImageData uses RGBA; the shared painter and edge detector use BGRA.
		for (int i = 8; i < result.Length; i += 4)
			(result[i], result[i + 2]) = (result[i + 2], result[i]);
		return result;
	}

	private static Project ReadCanvasProject(string projectJson)
	{
		Project project = JsonSerializer.Deserialize(projectJson, ProjectJsonContext.Default.Project)
			?? throw new ArgumentException("The project is empty.", nameof(projectJson));
		project.Normalize();
		if (project.Scene.Mode != SceneMode.Canvas)
			throw new InvalidOperationException("Image scenes are not supported in the browser yet. Choose a canvas scene.");
		return project;
	}
}
