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
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using WaveDudeNext.Probes;

namespace WaveDudeNext.Wasm;

public static partial class Program
{
	private static ProbeSampler? _probeSampler;
	private static ProbeCollector? _probeCollector;
	private static byte[] _probeFrame = [];

	/// <summary>
	/// Returns the last captured frame's probe data on the simulation worker thread. Little-endian packet:
	/// int32 probe count and completed steps, then per active probe (project order) int32 sample count,
	/// int32 profile count (-1 for point probes), float64 line length in canvas pixels, and float32 samples
	/// followed by float32 profile values. Point samples cover steps since the previous frame; profiles
	/// belong to this frame. Integrated measurements and start-after gating use the shared collector/engine.
	/// </summary>
	[JSExport]
	public static byte[] GetProbeFrame()
	{
		if (_engine == null || _nextFrame == 0)
			throw new InvalidOperationException("There is no captured probe frame.");
		return _probeFrame;
	}

	private static byte[] CaptureProbeFrame()
	{
		_probeCollector!.Collect(_engine!, _stepsDone);
		ProbeUpdate batch = _probeCollector.TakeForUi();
		float[]?[] profiles = _engine!.ReadProbeProfiles();
		int count = _probeSampler!.Count;
		int size = 8 + count * 16;
		for (int i = 0; i < count; i++)
			size = checked(size + (batch.Samples[i].Length + (profiles[i]?.Length ?? 0)) * sizeof(float));
		byte[] bytes = new byte[size];
		BinaryPrimitives.WriteInt32LittleEndian(bytes, count);
		BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), _stepsDone);
		int offset = 8;
		for (int i = 0; i < count; i++)
		{
			float[] samples = batch.Samples[i];
			float[]? profile = profiles[i];
			BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), samples.Length);
			BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), profile?.Length ?? -1);
			BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(offset + 8), _probeSampler.Lengths[i]);
			offset += 16;
			MemoryMarshal.AsBytes(samples.AsSpan()).CopyTo(bytes.AsSpan(offset));
			offset += samples.Length * sizeof(float);
			if (profile != null)
			{
				MemoryMarshal.AsBytes(profile.AsSpan()).CopyTo(bytes.AsSpan(offset));
				offset += profile.Length * sizeof(float);
			}
			// The browser keeps point histories once. Retain only the collector's integration totals
			// between frames; do not accumulate a second history or any line-profile list in Wasm.
			_probeCollector.Traces[i].Series.Clear();
		}
		return bytes;
	}
}
