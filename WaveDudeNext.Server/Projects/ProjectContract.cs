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

using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using WaveDudeNext.Rendering;
using System.Xml.Serialization;
using WaveDudeNext.Model;

namespace WaveDudeNext.Server.Projects;

/// <summary>The closed editable JSON contract, shared by the server and Zod generator.</summary>
internal static class ProjectContract
{
	internal sealed record Member(PropertyInfo Property, string Name, bool Nullable)
	{
		internal Type Type => Property.PropertyType;
		internal RangeAttribute? Range => Property.GetCustomAttribute<RangeAttribute>();
		internal object? Constant => Property.DeclaringType == typeof(Project) && Property.Name == nameof(Project.Version) ? Project.CurrentVersion : null;
		internal string? Pattern => Property.DeclaringType == typeof(ColorStop) && Property.Name == nameof(ColorStop.ColorText) ? "^#[0-9A-Fa-f]{6}$" : null;
	}

	internal static readonly IReadOnlyDictionary<string, Type> PartKinds = typeof(Project)
		.GetProperty(nameof(Project.Parts))!.GetCustomAttributes<XmlElementAttribute>()
		.ToDictionary(attribute => attribute.ElementName, attribute => attribute.Type!);
	internal static readonly IReadOnlyDictionary<Type, Member[]> Objects = Build();

	internal static bool HasId(Type type) => typeof(INamed).IsAssignableFrom(type);
	internal static Type? ItemType(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Collections.ObjectModel.ObservableCollection<>)
		? type.GetGenericArguments()[0] : null;
	internal static string Name(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
	internal static string Kind(Type type) => PartKinds.Single(pair => pair.Value == type).Key;

	private static IReadOnlyDictionary<Type, Member[]> Build()
	{
		var objects = new Dictionary<Type, Member[]>();
		var nullability = new NullabilityInfoContext();
		void Visit(Type type)
		{
			if (ItemType(type) is { } item) { Visit(item); return; }
			if (type == typeof(OpticalPart))
			{
				foreach (var part in PartKinds.Values) Visit(part);
				return;
			}
			if (type == typeof(string) || type == typeof(bool) || type == typeof(int) || type == typeof(double) || type.IsEnum) return;
			if (objects.ContainsKey(type)) return;
			if (type.Assembly != typeof(Project).Assembly) throw new NotSupportedException($"Unsupported contract type: {type}.");
			var members = type.GetProperties().Where(property =>
				(property.IsDefined(typeof(XmlAttributeAttribute)) || property.IsDefined(typeof(XmlElementAttribute)))
				&& !(type == typeof(Scene) && property.Name is nameof(Scene.ImageData) or nameof(Scene.ImagePath)))
				.Select(property => new Member(property, Name(property.Name), nullability.Create(property).ReadState == NullabilityState.Nullable)).ToArray();
			if (members.Length == 0) throw new NotSupportedException($"No editable members on {type}.");
			objects.Add(type, members);
			foreach (var member in members) Visit(member.Type);
		}
		Visit(typeof(Project));
		return objects;
	}

	internal static void ValidateScalar(object value, Member member)
	{
		if (member.Constant is { } constant && !Equals(value, constant)) throw new JsonException($"Unsupported {member.Name}.");
		if (member.Pattern is { } pattern && value is string text && !Regex.IsMatch(text, pattern)) throw new JsonException($"Invalid {member.Name}.");
		if (value is double number && !double.IsFinite(number)) throw new JsonException($"{member.Name} must be finite.");
		if (member.Range is { } range && !range.IsValid(value)) throw new JsonException($"{member.Name} is outside its range.");
		if (member.Type.IsEnum && !Enum.IsDefined(member.Type, value)) throw new JsonException($"Invalid {member.Name}.");
	}
}
