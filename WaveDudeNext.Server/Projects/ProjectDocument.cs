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

using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using WaveDudeNext.Model;
using WaveDudeNext.Parts;

namespace WaveDudeNext.Server.Projects;

/// <summary>An editable model and workspace-only identities/assets; never changes the wdproj format.</summary>
internal sealed class ProjectDocument(Project model)
{
	internal Project Model { get; } = model;
	internal Dictionary<object, Guid> Ids { get; } = new(ReferenceEqualityComparer.Instance);
	internal Dictionary<Guid, byte[]> Assets { get; } = [];

	internal JsonObject Write() => (JsonObject)WriteValue(Model)!;

	private JsonNode? WriteValue(object? value)
	{
		if (value is null) return null;
		var type = value.GetType();
		if (value is string text) return JsonValue.Create(text);
		if (value is bool flag) return JsonValue.Create(flag);
		if (value is int integer) return JsonValue.Create(integer);
		if (value is double number) return JsonValue.Create(number);
		if (value is Enum) return JsonValue.Create(value.ToString());
		if (value is IList list) return new JsonArray(list.Cast<object>().Select(WriteValue).ToArray());
		var result = new JsonObject();
		if (ProjectContract.HasId(type))
		{
			if (!Ids.TryGetValue(value, out var id)) Ids.Add(value, id = Guid.NewGuid());
			result["id"] = id.ToString();
		}
		if (value is OpticalPart) result["kind"] = ProjectContract.Kind(type);
		foreach (var member in ProjectContract.Objects[type]) result[member.Name] = WriteValue(member.Property.GetValue(value));
		if (value is Scene scene)
		{
			Guid? assetId = null;
			if (scene.ImageData is { } bytes)
			{
				assetId = Assets.FirstOrDefault(pair => ReferenceEquals(pair.Value, bytes)).Key;
				if (assetId == Guid.Empty) Assets.Add((assetId = Guid.NewGuid()).Value, bytes);
			}
			result["imageAssetId"] = assetId?.ToString();
		}
		return result;
	}

	internal static ProjectDocument Read(JsonElement json, IReadOnlyDictionary<Guid, byte[]> assets)
	{
		var result = new ProjectDocument(new Project());
		var usedIds = new HashSet<Guid>();
		object? ReadValue(JsonElement node, Type type, bool nullable = false)
		{
			if (node.ValueKind == JsonValueKind.Null)
			{
				if (!nullable) throw new JsonException($"{type.Name} cannot be null.");
				return null;
			}
			if (type == typeof(string)) return node.GetString()!;
			if (type == typeof(bool)) return node.GetBoolean();
			if (type == typeof(int)) return node.GetInt32();
			if (type == typeof(double)) return node.GetDouble();
			if (type.IsEnum)
			{
				var name = node.GetString();
				if (name is null || !Enum.GetNames(type).Contains(name)) throw new JsonException($"Invalid {type.Name}.");
				return Enum.Parse(type, name);
			}
			if (ProjectContract.ItemType(type) is { } itemType)
			{
				var list = (IList)Activator.CreateInstance(type)!;
				foreach (var item in node.EnumerateArray()) list.Add(ReadValue(item, itemType));
				return list;
			}
			if (type == typeof(OpticalPart))
			{
				if (!ProjectContract.PartKinds.TryGetValue(node.GetProperty("kind").GetString() ?? "", out type!)) throw new JsonException("Unknown part kind.");
			}
			var members = ProjectContract.Objects[type];
			var expected = members.Select(member => member.Name).ToHashSet();
			if (ProjectContract.HasId(type)) expected.Add("id");
			if (typeof(OpticalPart).IsAssignableFrom(type)) expected.Add("kind");
			if (type == typeof(Scene)) expected.Add("imageAssetId");
			var names = node.EnumerateObject().Select(property => property.Name).ToArray();
			if (names.Length != expected.Count || !expected.SetEquals(names)) throw new JsonException($"Missing, duplicate or unknown members on {type.Name}.");
			var instance = Activator.CreateInstance(type)!;
			if (ProjectContract.HasId(type))
			{
				var id = node.GetProperty("id").GetGuid();
				if (id == Guid.Empty || !usedIds.Add(id)) throw new JsonException("Object IDs must be nonempty and unique.");
				result.Ids.Add(instance, id);
			}
			foreach (var member in members)
			{
				var value = ReadValue(node.GetProperty(member.Name), member.Type, member.Nullable);
				if (value is not null) ProjectContract.ValidateScalar(value, member);
				if (member.Property.SetMethod is not null) member.Property.SetValue(instance, value);
				else if (member.Property.GetValue(instance) is IList list && value is IList incoming)
				{
					list.Clear();
					foreach (var item in incoming) list.Add(item);
				}
				else throw new NotSupportedException($"Read-only member {member.Name}.");
			}
			if (instance is Scene scene && node.GetProperty("imageAssetId").ValueKind != JsonValueKind.Null)
			{
				var id = node.GetProperty("imageAssetId").GetGuid();
				if (!assets.TryGetValue(id, out var bytes)) throw new JsonException("Unknown scene image asset.");
				result.Assets.Add(id, bytes);
				scene.ImageData = bytes;
			}
			return instance;
		}
		try
		{
			var parsed = (Project)ReadValue(json, typeof(Project))!;
			if (parsed.Version != Project.CurrentVersion) throw new JsonException("Unsupported project version.");
			parsed.Normalize();
			var document = new ProjectDocument(parsed);
			foreach (var pair in result.Ids) document.Ids.Add(pair.Key, pair.Value);
			foreach (var pair in result.Assets) document.Assets.Add(pair.Key, pair.Value);
			return document;
		}
		catch (Exception error) when (error is InvalidOperationException or FormatException or KeyNotFoundException or System.Reflection.TargetInvocationException or OverflowException)
		{
			throw new JsonException("Invalid project document.", error);
		}
	}

	/// <summary>Applies an edit to a private copy, preserving model rename and gate-following rules.</summary>
	internal ProjectDocument Reconcile(ProjectDocument incoming)
	{
		var copy = Read(JsonSerializer.SerializeToElement(Write()), Assets);
		var known = copy.Ids.ToDictionary(pair => pair.Value, pair => pair.Key);
		using var follower = new GatePortFollower(copy.Model);
		void Apply(object target, object source)
		{
			var members = ProjectContract.Objects[source.GetType()].AsEnumerable();
			if (source is Project)
			{
				string[] order = ["sources", "scene", "display", "probes", "colorSchemes", "simulation", "parts"];
				members = members.OrderBy(member => Array.IndexOf(order, member.Name));
			}
			// Compare with the pre-edit values: an unchanged field must not undo another setter's side effects.
			var before = members.ToDictionary(member => member, member => member.Property.GetValue(target));
			foreach (var member in members)
			{
				var value = member.Property.GetValue(source);
				var previous = before[member];
				if (value is IList list && previous is IList existing)
				{
					var next = new List<object>();
					foreach (var item in list)
					{
						if (incoming.Ids.TryGetValue(item!, out var id))
						{
							if (known.TryGetValue(id, out var original))
							{
								if (original.GetType() != item!.GetType()) throw new JsonException("An object ID cannot change kind.");
								Apply(original, item);
								next.Add(original);
								continue;
							}
							copy.Ids.Add(item!, id);
						}
						next.Add(item!);
					}
					existing.Clear();
					foreach (var item in next) existing.Add(item);
				}
				else if (value is not null && previous is not null && ProjectContract.Objects.ContainsKey(value.GetType())) Apply(previous, value);
				else if (!Equals(previous, value)) member.Property.SetValue(target, value);
			}
		}
		Apply(copy.Model, incoming.Model);
		copy.Model.Scene.ImageData = incoming.Model.Scene.ImageData;
		copy.Assets.Clear();
		foreach (var pair in incoming.Assets) copy.Assets.Add(pair.Key, pair.Value);
		copy.Model.Normalize();
		return copy;
	}
}
