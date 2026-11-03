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


using System.Text;
using System.Xml;

namespace WaveDudeNext.Model;

/// <summary>
/// The exception a log entry carries for a file the application could not read (2026-11-03, the user's rule: a malformed
/// project, colour scheme or settings file goes into the log with its error, so a report can be diagnosed from the log
/// alone; the settings file in particular is overwritten by the defaults moments later). Wraps the error with the file's
/// text, which <see cref="ToString"/>, what the log's layouts and the Messages pane print, appends under the stack: the
/// first <see cref="MaxChars"/> characters, and the line an XML error names with its neighbours when that lies beyond
/// them. Thread-free.
/// </summary>
public sealed class UnreadableFileException : Exception
{
	/// <summary>
	/// Characters quoted from the file's start: a settings or scheme file is a few thousand, a project file without an
	/// image tens of thousands; a project with an embedded image is cut here, its head and the error's line being what matters.
	/// </summary>
	public const int MaxChars = 64 * 1024;

	/// <summary>Characters quoted each side of the position on a line the error names.</summary>
	private const int LineChars = 500;

	/// <summary>Wraps <paramref name="inner"/>, the error reading the file at <paramref name="path"/>, with the file's text.</summary>
	public UnreadableFileException(string path, Exception inner)
		: base($"The file '{path}' could not be read; its text is quoted under the error", inner)
	{
		Path = path;
		Text = Excerpt(path, inner);
	}

	/// <summary>The file's full path.</summary>
	public string Path { get; }

	/// <summary>The file's text as quoted: the first <see cref="MaxChars"/> characters, a note of what is cut, and an XML error's line when it lies beyond; or why the file could not be read at all.</summary>
	public string Text { get; }

	/// <summary>The error with its causes and stack, then the file's text under a rule naming the file.</summary>
	public override string ToString() => base.ToString() + Environment.NewLine + "--- the text of '" + Path + "' ---" + Environment.NewLine + Text;

	/// <summary>
	/// The quotation <see cref="Text"/> holds for a file at <paramref name="path"/> whose reading failed with
	/// <paramref name="error"/>: the head, cut at <see cref="MaxChars"/> with a note of the rest, and when an
	/// <see cref="XmlException"/> among the error's causes names a line beyond the head, that line cut to
	/// <see cref="LineChars"/> each side of its position. Invalid UTF-8 shows as the replacement character.
	/// </summary>
	public static string Excerpt(string path, Exception? error)
	{
		string text;
		try
		{
			text = File.ReadAllText(path, Encoding.UTF8);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return "(the file could not be read: " + ex.Message + ")";
		}
		if (text.Length <= MaxChars)
		{
			return text;
		}
		var quoted = new StringBuilder();
		quoted.Append(text, 0, MaxChars).Append(Environment.NewLine).Append("… ").Append(text.Length - MaxChars).Append(" more characters");
		if (XmlErrorOf(error) is { LineNumber: > 0 } xml && LineStart(text, xml.LineNumber) is int start && start >= MaxChars)
		{
			int end = text.IndexOf('\n', start);
			if (end < 0)
			{
				end = text.Length;
			}
			int at = Math.Clamp(start + Math.Max(0, xml.LinePosition - 1), start, end);
			int from = Math.Max(start, at - LineChars);
			int to = Math.Min(end, at + LineChars);
			quoted.Append(Environment.NewLine).Append("--- line ").Append(xml.LineNumber).Append(", position ").Append(xml.LinePosition)
				.Append(from > start || to < end ? " (cut) ---" : " ---").Append(Environment.NewLine).Append(text, from, to - from);
		}
		return quoted.ToString();
	}

	/// <summary>The XML error among <paramref name="error"/> and its causes, or null.</summary>
	private static XmlException? XmlErrorOf(Exception? error)
	{
		for (Exception? e = error; e != null; e = e.InnerException)
		{
			if (e is XmlException xml)
			{
				return xml;
			}
		}
		return null;
	}

	/// <summary>The offset at which the 1-based <paramref name="line"/> starts, or null when the text has fewer lines.</summary>
	private static int? LineStart(string text, int line)
	{
		int start = 0;
		for (int i = 1; i < line; i++)
		{
			start = text.IndexOf('\n', start);
			if (start < 0)
			{
				return null;
			}
			start++;
		}
		return start;
	}
}
