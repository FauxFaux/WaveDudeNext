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
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using NLog;
using WaveDudeNext.Model;

namespace WaveDudeNext.Rendering;

/// <summary>
/// Reads and writes <see cref="ColorScheme"/> files: the program's XML (<see cref="SaveXml"/> /
/// <see cref="LoadXml(string)"/>, extension <see cref="XmlExtension"/>, the same element a project file embeds;
/// the shipped schemes are these, embedded in the assembly) and the author's JSON (<see cref="Parse"/>,
/// read for files a user brings along from his program).
/// </summary>
/// <remarks>
/// JSON format, the files exported from the author's Python: an object with "name", "origin", optional
/// "category" and "scheme_type" ("field" or "intensity"), "bits_per_channel" (8), "entry_count" (255) and a
/// "colors" list of <c>[r,g,b]</c> triplets, plus, for schemes made in the stop editor, a "stops" list of 9
/// triplets. Reading consolidates (2026-10-13): when the table equals the interpolation of the stops, which
/// holds for every shipped file, the scheme keeps the 9 stops only; otherwise the table. Writing JSON
/// (<see cref="Save"/>) emits the author's layout, so a stop scheme round-trips byte for byte; the "colors"
/// table there is redundant for a stop scheme (his program reads only the table). The shipped schemes are
/// embedded in this assembly as XML (<see cref="LoadBuiltIn"/>; the library's <c>ColorSchemes</c> folder is
/// their source, converted from his JSON on 2026-10-13 and renamed to plain names with spaces on 2026-10-15,
/// the old underscore names still found by <see cref="Find"/>); the user's own schemes live in
/// <see cref="UserFolder"/>. UI thread.
/// </remarks>
public static class ColorSchemeStore
{
	/// <summary>Extension of a standalone XML scheme file.</summary>
	public const string XmlExtension = ".wdscheme";

	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

	/// <summary>Prefix of the embedded scheme resources (see the csproj's <c>LogicalName</c>).</summary>
	private const string ResourcePrefix = "ColorSchemes/";

	/// <summary>Entries of the author's JSON table ("entry_count"): his palette of 255 colours (<see cref="ColorLut.Size"/>), the part his program reads.</summary>
	private const int AuthorEntryCount = ColorLut.Size;

	private static readonly XmlSerializer Serializer = new(typeof(ColorScheme));
	private static readonly XmlSerializerNamespaces NoNamespaces = new([XmlQualifiedName.Empty]);
	private static readonly XmlWriterSettings WriterSettings = new()
	{
		Indent = true,
		IndentChars = "\t",
		Encoding = new UTF8Encoding(false),
	};

	/// <summary>The user's own scheme folder, <c>%LOCALAPPDATA%\WaveDudeNext\ColorSchemes</c>; created on first use by a save, may not exist.</summary>
	public static string UserFolder { get; } =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveDudeNext", "ColorSchemes");

	/// <summary>
	/// The schemes shipped with the program, read from this assembly's resources in name order. A resource that
	/// does not parse is reported in <paramref name="errors"/> (a build problem, not a user one) and skipped.
	/// </summary>
	/// <param name="errors">Receives one line per unreadable resource; may be null to discard them.</param>
	public static List<ColorScheme> LoadBuiltIn(List<string>? errors)
	{
		var schemes = new List<ColorScheme>();
		Assembly assembly = typeof(ColorSchemeStore).Assembly;
		var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToList();
		names.Sort(StringComparer.OrdinalIgnoreCase);
		foreach (string name in names)
		{
			try
			{
				using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("The resource stream is missing.");
				string fallbackName = Path.GetFileNameWithoutExtension(name[ResourcePrefix.Length..]);
				ColorScheme scheme;
				if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
				{
					using var reader = new StreamReader(stream, Encoding.UTF8);
					scheme = Parse(reader.ReadToEnd(), fallbackName);
				}
				else
				{
					scheme = LoadXml(stream, fallbackName);
				}
				scheme.IsBuiltIn = true;
				schemes.Add(scheme);
			}
			catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or InvalidOperationException)
			{
				errors?.Add(name + ": " + ex.Message);
			}
		}
		return schemes;
	}

	/// <summary>Reads a scheme file of either format by its extension: <c>.json</c> through <see cref="Parse"/>, anything else as XML.</summary>
	/// <exception cref="InvalidDataException">Not a scheme in either format.</exception>
	/// <exception cref="JsonException">A JSON file that is not valid JSON.</exception>
	/// <exception cref="InvalidOperationException">An XML file that is not a scheme.</exception>
	public static ColorScheme LoadFile(string filePath) =>
		string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase) ? Load(filePath) : LoadXml(filePath);

	/// <summary>
	/// Reads one JSON scheme file (see the remarks of the class); "name" falls back to the file name and
	/// <see cref="ColorScheme.FilePath"/> is set to <paramref name="filePath"/>.
	/// </summary>
	/// <exception cref="InvalidDataException">The file is not a JSON object, has no usable color list or contains malformed triplets.</exception>
	/// <exception cref="JsonException">The file is not valid JSON.</exception>
	public static ColorScheme Load(string filePath)
	{
		ColorScheme scheme = Parse(File.ReadAllText(filePath, Encoding.UTF8), Path.GetFileNameWithoutExtension(filePath));
		scheme.FilePath = filePath;
		return scheme;
	}

	/// <summary>Parses scheme JSON; <paramref name="fallbackName"/> stands in for a missing "name". Consolidates stops and table (see the class remarks).</summary>
	/// <exception cref="InvalidDataException">Not a JSON object, no usable color list or malformed triplets.</exception>
	/// <exception cref="JsonException">Not valid JSON.</exception>
	public static ColorScheme Parse(string json, string fallbackName)
	{
		using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
		JsonElement root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidDataException("Not a color scheme file (a JSON object was expected).");
		}
		string schemeName = GetString(root, "name", fallbackName);
		List<Rgb>? colors = root.TryGetProperty("colors", out JsonElement colorsNode) ? ParseColorList(colorsNode) : null;
		List<Rgb>? stops = root.TryGetProperty("stops", out JsonElement stopsNode) ? ParseColorList(stopsNode) : null;
		if (stops != null && stops.Count != ColorScheme.StopCount)
		{
			stops = null;
		}
		IReadOnlyList<Rgb> kept;
		if (stops != null && (colors == null || colors.Count < 2 || IsInterpolationOf(colors, stops)))
		{
			kept = stops;
		}
		else if (colors != null && colors.Count >= 2)
		{
			kept = colors;
		}
		else
		{
			throw new InvalidDataException("The file contains no usable 'colors' list.");
		}
		var scheme = new ColorScheme(schemeName, kept);
		scheme.Description = GetString(root, "origin", scheme.Description);
		scheme.Kind = KindOf(GetString(root, "scheme_type", ""));
		return scheme;
	}

	/// <summary>
	/// Whether a table equals the author's Python resampling of these evenly spaced colours exactly (the consolidation
	/// test of <see cref="Parse"/>): entry i at position i × (n−1) / (count−1) on the colours, interpolated linearly,
	/// each channel rounded half away from zero.
	/// </summary>
	private static bool IsInterpolationOf(IReadOnlyList<Rgb> table, IReadOnlyList<Rgb> stops)
	{
		if (table.Count != AuthorEntryCount || stops.Count < 2)
		{
			return false;
		}
		for (int i = 0; i < AuthorEntryCount; i++)
		{
			double position = (double)(i * (stops.Count - 1)) / (AuthorEntryCount - 1);
			int index = (int)Math.Floor(position);
			Rgb expected = index >= stops.Count - 1 ? stops[^1] : ColorScheme.Lerp(stops[index], stops[index + 1], position - index);
			if (expected != table[i])
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>Reads a standalone XML scheme file; <see cref="ColorScheme.FilePath"/> is set.</summary>
	/// <exception cref="InvalidOperationException">The file is not an XML colour scheme.</exception>
	/// <exception cref="FormatException">A colour is not <c>#RRGGBB</c>.</exception>
	public static ColorScheme LoadXml(string filePath)
	{
		using FileStream stream = File.OpenRead(filePath);
		ColorScheme scheme = LoadXml(stream, Path.GetFileNameWithoutExtension(filePath));
		scheme.FilePath = filePath;
		return scheme;
	}

	/// <summary>Reads an XML scheme from a stream; <paramref name="fallbackName"/> stands in for a missing name. Positions are clamped and sorted.</summary>
	/// <exception cref="InvalidOperationException">The document is not a colour scheme.</exception>
	public static ColorScheme LoadXml(Stream stream, string fallbackName)
	{
		using var reader = XmlReader.Create(stream);
		if (Serializer.Deserialize(reader) is not ColorScheme scheme)
		{
			throw new InvalidOperationException("The document is not a colour scheme.");
		}
		if (scheme.Name.Length == 0)
		{
			scheme.Name = fallbackName;
		}
		scheme.Normalize();
		return scheme;
	}

	/// <summary>Writes a standalone XML scheme file (the project file embeds the same element); creates the folder, writes through a temporary file, sets <see cref="ColorScheme.FilePath"/>.</summary>
	public static void SaveXml(ColorScheme scheme, string filePath)
	{
		PrepareWrite(filePath, out string tempPath);
		using (FileStream stream = File.Create(tempPath))
		using (var writer = XmlWriter.Create(stream, WriterSettings))
		{
			Serializer.Serialize(writer, scheme, NoNamespaces);
		}
		File.Move(tempPath, filePath, overwrite: true);
		scheme.FilePath = filePath;
	}

	/// <summary>
	/// Reads every <c>*.json</c> and <c>*.wdscheme</c> file of a folder, in name order; a missing folder gives an empty list.
	/// Unreadable files are reported in <paramref name="errors"/> and skipped.
	/// </summary>
	/// <param name="folder">The folder to read; "" or a missing folder gives an empty list.</param>
	/// <param name="errors">Receives one line per unreadable file; may be null to discard them.</param>
	public static List<ColorScheme> LoadFolder(string folder, List<string>? errors)
	{
		var schemes = new List<ColorScheme>();
		if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
		{
			return schemes;
		}
		var files = Directory.GetFiles(folder, "*.json").Concat(Directory.GetFiles(folder, "*" + XmlExtension)).ToList();
		files.Sort(StringComparer.OrdinalIgnoreCase);
		foreach (string file in files)
		{
			try
			{
				schemes.Add(LoadFile(file));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or FormatException)
			{
				// the file's text goes into the log under the error (2026-11-03, the user's rule); the caller's list gets the line
				Logger.Warn(new UnreadableFileException(file, ex), "Colour scheme file '{File}' not loaded: {Message}", file, ex.Message);
				errors?.Add(Path.GetFileName(file) + ": " + ex.Message);
			}
		}
		return schemes;
	}

	/// <summary>
	/// The old names of the shipped schemes (the author's files and projects saved before 2026-10-15) whose new name is
	/// not just the underscores turned into spaces; the rest is covered by that rule in <see cref="Find"/>.
	/// </summary>
	private static readonly Dictionary<string, string> LegacyNames = new(StringComparer.OrdinalIgnoreCase)
	{
		["Blue_to_yellow_GNU"] = "Blue to yellow",
		["Flare_inv"] = "Flare inverted",
		["Gray_scales_inv"] = "Gray scales inverted",
		["Turky_brown"] = "Turkey brown",
	};

	/// <summary>
	/// The scheme called <paramref name="name"/>, compared case-insensitively; an old underscore name finds the
	/// renamed shipped scheme ("Default_intensity" finds "Default intensity", "Flare_inv" "Flare inverted", 2026-10-15).
	/// Null when absent.
	/// </summary>
	public static ColorScheme? Find(IEnumerable<ColorScheme> schemes, string name)
	{
		List<ColorScheme> list = schemes as List<ColorScheme> ?? [.. schemes];
		ColorScheme? found = list.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
		if (found != null || name.IndexOf('_') < 0)
		{
			return found;
		}
		string renamed = LegacyNames.TryGetValue(name, out string? mapped) ? mapped : name.Replace('_', ' ');
		return list.FirstOrDefault(s => string.Equals(s.Name, renamed, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Writes the author's JSON layout: "stops" for a scheme of 9 evenly spaced stops, then the interpolated
	/// "colors" table (redundant then, but what his program reads), eight triplets per line. Creates the folder, writes through a temporary file and sets
	/// <see cref="ColorScheme.FilePath"/>.
	/// </summary>
	public static void Save(ColorScheme scheme, string filePath)
	{
		var json = new StringBuilder();
		json.AppendLine("{");
		json.AppendLine("  \"name\": " + Quote(scheme.Name) + ",");
		json.AppendLine("  \"origin\": " + Quote(scheme.Description) + ",");
		if (scheme.Kind != ColorSchemeKind.Unspecified)
		{
			json.AppendLine("  \"scheme_type\": " + Quote(scheme.Kind.ToString().ToLowerInvariant()) + ",");
		}
		json.AppendLine("  \"bits_per_channel\": 8,");
		json.AppendLine("  \"entry_count\": " + AuthorEntryCount.ToString(CultureInfo.InvariantCulture) + ",");
		if (scheme.IsStopScheme && scheme.IsEvenlySpaced)
		{
			json.AppendLine("  \"stops\": [" + string.Join(", ", scheme.Colors.Select(Triplet)) + "],");
		}
		Rgb[] entries = ColorScheme.Interpolate(scheme.SortedStops(), AuthorEntryCount);
		json.AppendLine("  \"colors\": [");
		for (int rowStart = 0; rowStart < entries.Length; rowStart += 8)
		{
			int rowEnd = Math.Min(rowStart + 7, entries.Length - 1);
			json.Append("    ");
			json.Append(string.Join(", ", entries[rowStart..(rowEnd + 1)].Select(Triplet)));
			if (rowEnd < entries.Length - 1)
			{
				json.Append(',');
			}
			json.AppendLine();
		}
		json.AppendLine("  ]");
		json.AppendLine("}");
		PrepareWrite(filePath, out string tempPath);
		File.WriteAllText(tempPath, json.ToString(), new UTF8Encoding(false));
		File.Move(tempPath, filePath, overwrite: true);
		scheme.FilePath = filePath;
	}

	/// <summary>A scheme name as a file name: invalid characters become '_', trailing dots go; "color_scheme" for an empty name.</summary>
	public static string SafeFileName(string schemeName)
	{
		if (string.IsNullOrEmpty(schemeName))
		{
			return "color_scheme";
		}
		char[] invalidChars = Path.GetInvalidFileNameChars();
		var safeName = new StringBuilder();
		foreach (char c in schemeName.Trim())
		{
			safeName.Append(Array.IndexOf(invalidChars, c) >= 0 ? '_' : c);
		}
		string result = safeName.ToString().Trim().TrimEnd('.');
		return result.Length == 0 ? "color_scheme" : result;
	}

	/// <summary>The XML file path a scheme of this name gets inside <paramref name="folder"/>.</summary>
	public static string PathFor(string folder, string schemeName) => Path.Combine(folder, SafeFileName(schemeName) + XmlExtension);

	/// <summary>The author's "scheme_type" text as a kind: "field", "intensity", anything else unspecified.</summary>
	public static ColorSchemeKind KindOf(string schemeType) => schemeType.Trim().ToLowerInvariant() switch
	{
		"field" => ColorSchemeKind.Field,
		"intensity" => ColorSchemeKind.Intensity,
		_ => ColorSchemeKind.Unspecified,
	};

	/// <summary>Creates the folder and names the temporary file a write goes through, so a failed save never leaves a half-written scheme behind.</summary>
	private static void PrepareWrite(string filePath, out string tempPath)
	{
		string? directoryName = Path.GetDirectoryName(filePath);
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		tempPath = filePath + ".tmp";
	}

	/// <summary>The author's JSON spelling of a colour, <c>[r,g,b]</c>.</summary>
	private static string Triplet(Rgb color) =>
		"[" + color.R.ToString(CultureInfo.InvariantCulture) + "," + color.G.ToString(CultureInfo.InvariantCulture) + "," + color.B.ToString(CultureInfo.InvariantCulture) + "]";

	private static string GetString(JsonElement root, string key, string fallback)
	{
		if (!root.TryGetProperty(key, out JsonElement value))
		{
			return fallback;
		}
		return value.ValueKind switch
		{
			JsonValueKind.String => value.GetString() ?? fallback,
			JsonValueKind.Number => value.GetDouble().ToString(CultureInfo.InvariantCulture),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => fallback,
		};
	}

	private static List<Rgb> ParseColorList(JsonElement node)
	{
		if (node.ValueKind != JsonValueKind.Array)
		{
			throw new InvalidDataException("Expected a list of [r,g,b] triplets.");
		}
		var colors = new List<Rgb>();
		foreach (JsonElement item in node.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidDataException("Expected a list of [r,g,b] triplets.");
			}
			var values = new List<int>();
			foreach (JsonElement channel in item.EnumerateArray())
			{
				if (channel.ValueKind != JsonValueKind.Number)
				{
					throw new InvalidDataException("A color channel must be a number.");
				}
				values.Add((int)Math.Round(channel.GetDouble(), MidpointRounding.AwayFromZero));
			}
			if (values.Count < 3)
			{
				throw new InvalidDataException("A color needs three values [r,g,b].");
			}
			colors.Add(Rgb.Clamped(values[0], values[1], values[2]));
		}
		return colors;
	}

	private static string Quote(string text)
	{
		var quoted = new StringBuilder();
		quoted.Append('"');
		foreach (char c in text)
		{
			switch (c)
			{
				case '"':
					quoted.Append("\\\"");
					break;
				case '\\':
					quoted.Append("\\\\");
					break;
				case '\n':
					quoted.Append("\\n");
					break;
				case '\r':
					quoted.Append("\\r");
					break;
				case '\t':
					quoted.Append("\\t");
					break;
				default:
					if (c < ' ')
					{
						quoted.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
					}
					else
					{
						quoted.Append(c);
					}
					break;
			}
		}
		quoted.Append('"');
		return quoted.ToString();
	}
}
