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
using WaveDudeNext.Engine;
using WaveDudeNext.Model;
using WaveDudeNext.Parts;
using WaveDudeNext.Probes;

namespace WaveDudeNext.Wasm;

public static partial class Program
{
	/// <summary>
	/// Prepares a normalised canvas project on the calling thread without creating or changing a simulation.
	/// Returns an owned WDPK version-1 package: little-endian metadata, padded per-sub-step maps,
	/// resolved emitters and active probe layouts. All byte offsets are relative to the package start.
	/// See PREPARE.md for the JavaScript/Rust boundary; no mutable run buffers are included.
	/// </summary>
	/// <param name="projectJson">The current project in the frontend's JSON format; image scenes are unsupported.</param>
	/// <param name="maxFrames">Positive maximum frame count, used to derive the capture interval as in StartSimulation.</param>
	[JSExport]
	public static byte[] Prepare(string projectJson, int maxFrames)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);
		Project project = ReadCanvasProject(projectJson);
		return SimulationPreparation.Create(project, ReadSimulationScene(project), maxFrames);
	}

	private static SceneData ReadSimulationScene(Project project)
	{
		Scene scene = project.Scene;
		var image = PartPainter.Paint(null, scene.Width, scene.Height, scene.RefractiveIndex, project.Parts);
		return image == null
			? SceneData.Empty(scene.Width, scene.Height, scene.DampingEdge, scene.RefractiveIndex, scene.Edges == EdgeMode.Open)
			: SceneData.FromBgra(image.Bgra, image.Width, image.Height, image.StrideBytes, false, scene.DampingEdge, scene.Edges == EdgeMode.Open);
	}
}

// This wire format belongs to the browser adapter, not the shared model or the Rust kernel's ABI.
// Keep offsets, flags and record sizes in sync with PREPARE.md and checks/check-prepare.mjs.
internal static class SimulationPreparation
{
	private const int HeaderBytes = 160;
	private const int EmitterBytes = 64;
	private const int ProbeBytes = 32;

	internal static byte[] Create(Project project, SceneData scene, int maxFrames)
	{
		int stride = checked(scene.Width + 2);
		int cells = checked(stride * checked(scene.Height + 2));
		Emitter[] emitters = Emitter.FromProject(project)
			.Where(source => source.X >= 0 && source.X < scene.CanvasWidth && source.Y >= 0 && source.Y < scene.CanvasHeight).ToArray();
		var active = project.Probes.Select((probe, index) => (Probe: probe, ProjectIndex: index))
			.Where(entry => entry.Probe.Active).ToArray();
		ProbeSampler probes = ProbeSampler.Build(active.Select(entry => ProbeSpec.From(entry.Probe)).ToArray(), scene.CanvasWidth, scene.CanvasHeight, scene.Rim);
		int pixelCount = probes.Pixels.Sum(pixels => pixels.Length);
		int c2Offset = HeaderBytes;
		int dampingOffset = Align(checked(c2Offset + cells * sizeof(float)));
		int emittersOffset = Align(checked(dampingOffset + cells * sizeof(float)));
		int probesOffset = Align(checked(emittersOffset + emitters.Length * EmitterBytes));
		int pixelsOffset = Align(checked(probesOffset + probes.Count * ProbeBytes));
		byte[] bytes = new byte[Align(checked(pixelsOffset + pixelCount * sizeof(int)))];

		// WaveSimulator.ComputeSubSteps/LoadScene and IntensityRetention/SetEmitters, without an engine
		// allocation. These methods are internal to the shared library; comparison checks guard this adapter.
		float maxC2 = scene.C2.Max();
		int subSteps = project.Simulation.FourthOrderLaplacian
			? Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)maxC2 * (32f / 3f) / 2f))) : 1;
		float c2Mult = 1f / (subSteps * subSteps);
		double lowestLevel = emitters.Where(source => source.Level > 0).Select(source => source.Level).DefaultIfEmpty(100).Min();
		float alpha = (float)Math.Exp(-1.0 / Math.Max(1.0, project.Simulation.TimeDecay * 100 / lowestLevel));
		int interval = Math.Max(project.Simulation.VisualizeEvery, (project.Simulation.Steps - 1) / maxFrames + 1);
		int frames = (project.Simulation.Steps - 1) / interval + 1;

		WriteInt(bytes, 0, 0x4b504457); // ASCII WDPK
		WriteInt(bytes, 4, 1);
		WriteInt(bytes, 8, bytes.Length);
		WriteInt(bytes, 12, HeaderBytes);
		WriteInt(bytes, 16, scene.CanvasWidth);
		WriteInt(bytes, 20, scene.CanvasHeight);
		WriteInt(bytes, 24, scene.Width);
		WriteInt(bytes, 28, scene.Height);
		WriteInt(bytes, 32, scene.Rim);
		WriteInt(bytes, 36, stride);
		WriteInt(bytes, 40, cells);
		WriteInt(bytes, 44, (scene.OpenEdges ? 1 : 0) | (project.Simulation.FourthOrderLaplacian ? 2 : 0) | 4);
		WriteInt(bytes, 48, subSteps);
		WriteInt(bytes, 52, project.Simulation.Steps);
		WriteInt(bytes, 56, interval);
		WriteInt(bytes, 60, frames);
		WriteInt(bytes, 64, checked((frames - 1) * interval + 1));
		WriteInt(bytes, 68, emitters.Length);
		WriteInt(bytes, 72, probes.Count);
		WriteInt(bytes, 76, pixelCount);
		WriteInt(bytes, 80, c2Offset);
		WriteInt(bytes, 84, dampingOffset);
		WriteInt(bytes, 88, emittersOffset);
		WriteInt(bytes, 92, probesOffset);
		WriteInt(bytes, 96, pixelsOffset);
		WriteFloat(bytes, 104, c2Mult);
		WriteFloat(bytes, 108, alpha);
		WriteFloat(bytes, 112, (float)project.Simulation.IntensityScale);
		WriteFloat(bytes, 116, 0.9f);
		WriteFloat(bytes, 120, 0.5f);
		WriteFloat(bytes, 124, 0.56213877f);
		WriteDouble(bytes, 128, project.Simulation.SourceFrequencyScale);
		WriteDouble(bytes, 136, project.Simulation.TimeDecay);
		WriteInt(bytes, 144, EmitterBytes);
		WriteInt(bytes, 148, ProbeBytes);
		WriteInt(bytes, 152, Emitter.DetachRampSteps);

		for (int y = 0; y < scene.Height; y++)
		{
			for (int x = 0; x < scene.Width; x++)
			{
				int from = y * scene.Width + x;
				int to = ((y + 1) * stride + x + 1) * sizeof(float);
				WriteFloat(bytes, c2Offset + to, scene.C2[from]);
				float damping = subSteps > 1 ? (float)Math.Pow(scene.D[from], 1.0 / subSteps) : scene.D[from];
				WriteFloat(bytes, dampingOffset + to, damping);
			}
		}
		for (int i = 0; i < emitters.Length; i++)
		{
			Emitter source = emitters[i];
			int offset = emittersOffset + i * EmitterBytes;
			WriteInt(bytes, offset, (source.Y + scene.Rim + 1) * stride + source.X + scene.Rim + 1);
			WriteInt(bytes, offset + 4, (int)source.Mode);
			WriteInt(bytes, offset + 8, (int)source.PulseShape);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 12), source.RngSeed);
			WriteInt(bytes, offset + 16, source.OnWidthCycles);
			WriteInt(bytes, offset + 20, source.OffWidthCycles);
			WriteInt(bytes, offset + 24, source.OffWidthBase);
			WriteInt(bytes, offset + 28, source.RandomizeOffWidthAmount);
			WriteInt(bytes, offset + 32, source.DetachAfterStep);
			// Match WaveSimulator.LevelFrequency: float-rounded omega, then promoted to double.
			WriteDouble(bytes, offset + 40, (float)(source.Level / 255.0 * 0.5 * project.Simulation.SourceFrequencyScale));
			WriteDouble(bytes, offset + 48, source.Amplitude);
			WriteDouble(bytes, offset + 56, source.PhaseRadians);
		}
		int pixelStart = 0;
		for (int i = 0; i < probes.Count; i++)
		{
			int offset = probesOffset + i * ProbeBytes;
			WriteInt(bytes, offset, pixelStart);
			WriteInt(bytes, offset + 4, probes.Pixels[i].Length);
			WriteInt(bytes, offset + 8, (probes.IsProfile[i] ? 1 : 0) | (probes.UseIntensity[i] ? 2 : 0)
				| (probes.Cumulative[i] ? 4 : 0) | (probes.Integrate[i] ? 8 : 0) | (probes.UseStartAfterStep[i] ? 16 : 0));
			WriteInt(bytes, offset + 12, probes.StartAfterStep[i]);
			WriteDouble(bytes, offset + 16, probes.Lengths[i]);
			WriteInt(bytes, offset + 24, active[i].ProjectIndex);
			foreach (int pixel in probes.Pixels[i])
			{
				WriteInt(bytes, pixelsOffset + pixelStart * sizeof(int), pixel);
				pixelStart++;
			}
		}
		return bytes;
	}

	private static int Align(int bytes) => checked(bytes + 15) & ~15;

	private static void WriteInt(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);

	private static void WriteFloat(byte[] bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);

	private static void WriteDouble(byte[] bytes, int offset, double value) => BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(offset), value);
}
