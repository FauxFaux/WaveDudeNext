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

using System.Text.Json;
using System.Text.Json.Serialization;
using WaveDudeNext.Model;

namespace WaveDudeNext.Wasm;

// Source-generated metadata keeps project input compatible with trimming and browser AOT.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	UseStringEnumConverter = true, IgnoreReadOnlyProperties = true,
	Converters = [typeof(OpticalPartJsonConverter)])]
[JsonSerializable(typeof(Project))]
[JsonSerializable(typeof(LensPart))]
[JsonSerializable(typeof(PolygonPart))]
[JsonSerializable(typeof(RectanglePart))]
[JsonSerializable(typeof(EllipsePart))]
[JsonSerializable(typeof(TrianglePart))]
[JsonSerializable(typeof(OutlinePart))]
[JsonSerializable(typeof(FiberPart))]
[JsonSerializable(typeof(BraggReflectorPart))]
[JsonSerializable(typeof(GrinLensPart))]
[JsonSerializable(typeof(XorGatePart))]
[JsonSerializable(typeof(AndGatePart))]
[JsonSerializable(typeof(OrGatePart))]
[JsonSerializable(typeof(ZonePlatePart))]
internal sealed partial class ProjectJsonContext : JsonSerializerContext;

// The frontend uses a "kind" discriminator; keep that wire detail out of the shared model.
internal sealed class OpticalPartJsonConverter : JsonConverter<OpticalPart>
{
	public override OpticalPart Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		using var document = JsonDocument.ParseValue(ref reader);
		JsonElement part = document.RootElement;
		return part.GetProperty("kind").GetString() switch
		{
			"Lens" => part.Deserialize(ProjectJsonContext.Default.LensPart)!,
			"Polygon" => part.Deserialize(ProjectJsonContext.Default.PolygonPart)!,
			"Rectangle" => part.Deserialize(ProjectJsonContext.Default.RectanglePart)!,
			"Ellipse" => part.Deserialize(ProjectJsonContext.Default.EllipsePart)!,
			"Triangle" => part.Deserialize(ProjectJsonContext.Default.TrianglePart)!,
			"Outline" => part.Deserialize(ProjectJsonContext.Default.OutlinePart)!,
			"Fiber" => part.Deserialize(ProjectJsonContext.Default.FiberPart)!,
			"BraggReflector" => part.Deserialize(ProjectJsonContext.Default.BraggReflectorPart)!,
			"GrinLens" => part.Deserialize(ProjectJsonContext.Default.GrinLensPart)!,
			"XorGate" => part.Deserialize(ProjectJsonContext.Default.XorGatePart)!,
			"AndGate" => part.Deserialize(ProjectJsonContext.Default.AndGatePart)!,
			"OrGate" => part.Deserialize(ProjectJsonContext.Default.OrGatePart)!,
			"ZonePlate" => part.Deserialize(ProjectJsonContext.Default.ZonePlatePart)!,
			_ => throw new JsonException("Unknown optical part kind."),
		};
	}

	public override void Write(Utf8JsonWriter writer, OpticalPart value, JsonSerializerOptions options)
		=> throw new NotSupportedException("The browser project boundary only reads JSON.");
}
