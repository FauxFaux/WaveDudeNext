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


using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// The build's Markdown converter: a Markdown file becomes a WPF FlowDocument in XAML, which the app loads with
/// XamlReader. An MSBuild task, compiled from this file by RoslynCodeTaskFactory (<c>Documents.targets</c>) and never
/// part of the app, so the app carries no Markdown code and the build needs no package; kept to netstandard2.0 and
/// C# 7.3, which Visual Studio's MSBuild compiles it with on .NET Framework, as the SDK's does on .NET.
/// </summary>
/// <remarks>
/// The CommonMark it knows: ATX and setext headings; paragraphs, a line break inside one a space, two trailing spaces
/// or a backslash a hard break; bullet and ordered lists nested by indentation, with lazy continuation lines, tight
/// or loose; block quotes; indented code (the block's common indentation removed) and fenced code; thematic breaks;
/// HTML comments, dropped. Inline: emphasis and strong emphasis by CommonMark's delimiter rules, code spans, inline
/// links, angle-bracket autolinks and GitHub's bare URLs, backslash escapes and entities. Other Markdown (tables,
/// raw HTML, reference links) shows as its text. An image alone in its paragraph is a figure: an SVG of the kind
/// <c>_tools/ui_check/Screenshots.ps1</c> writes (embedded PNG or JPEG pictures, circles, texts, groups with a title
/// as their tooltip, nothing else) becomes a canvas of WPF shapes over the pictures, which go to
/// <see cref="FigureFolder"/>; a PNG, JPEG, GIF or BMP file is copied there. An SVG element or attribute it does not
/// draw stops the build with an error naming it, so nothing is left out unseen. Thread-free; one instance per
/// conversion.
/// </remarks>
public class MarkdownToFlowDocument : Task
{
	/// <summary>The Markdown file; images are found relative to its folder.</summary>
	[Required]
	public string Source { get; set; }

	/// <summary>The XAML file written (UTF-8).</summary>
	[Required]
	public string Destination { get; set; }

	/// <summary>
	/// Folder the figures' pictures are written to, created when missing and cleared of pictures the document no
	/// longer shows; a document with an image needs it and <see cref="FigureUri"/>.
	/// </summary>
	public string FigureFolder { get; set; }

	/// <summary>Prefix of the URI an Image element loads its picture by, the file name appended: a pack URI of the app's resources.</summary>
	public string FigureUri { get; set; }

	/// <summary>
	/// Whether each heading's paragraph carries <c>Tag="h{level}#{anchor}"</c>, the anchor GitHub's for the heading (lower
	/// case, punctuation dropped, a hyphen for each space, a repeated one numbered), so the app can list the headings and
	/// follow a link to "#anchor".
	/// </summary>
	public bool TagHeadings { get; set; }

	/// <summary>
	/// Whether a tight list (no blank line between its items) shows its items without paragraph spacing, as GitHub does;
	/// without it every item's paragraph keeps WPF's automatic spacing, the license's look.
	/// </summary>
	public bool TightLists { get; set; }

	private const string SvgNamespace = "http://www.w3.org/2000/svg";
	private const string XLinkNamespace = "http://www.w3.org/1999/xlink";
	/// <summary>Half the width of the box a figure's text is centred in, in pixels: WPF's Canvas places a box, not a text anchor.</summary>
	private const double TextReach = 1000;

	private static readonly Regex Atx = new Regex(@"^(#{1,6})(?:[ \t]+(.*))?$");
	private static readonly Regex AtxClosing = new Regex(@"(?:^|[ \t]+)#+[ \t]*$");
	private static readonly Regex Setext = new Regex(@"^(?:=+|-+)[ \t]*$");
	private static readonly Regex Rule = new Regex(@"^(?:(?:\*[ \t]*){3,}|(?:-[ \t]*){3,}|(?:_[ \t]*){3,})$");
	private static readonly Regex Fence = new Regex(@"^(`{3,}|~{3,})(.*)$");
	private static readonly Regex AutoLink = new Regex(@"\G<([A-Za-z][A-Za-z0-9+.\-]{1,31}:[^\s<>]*)>");
	private static readonly Regex EmailLink = new Regex(@"\G<([A-Za-z0-9.!#$%&'*+/=?^_`{|}~\-]+@[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*)>");
	private static readonly Regex BareUrl = new Regex(@"\G(?:https?://|www\.)[^\s<]+", RegexOptions.IgnoreCase);
	private static readonly Regex Entity = new Regex(@"\G&(?:#[xX]([0-9A-Fa-f]{1,6})|#([0-9]{1,7})|([A-Za-z][A-Za-z0-9]{1,31}));");
	private static readonly Regex DataUri = new Regex(@"^data:image/(png|jpeg);base64,(.*)$", RegexOptions.Singleline);
	private static readonly Dictionary<string, string> Entities = new Dictionary<string, string>
	{
		{ "amp", "&" }, { "lt", "<" }, { "gt", ">" }, { "quot", "\"" }, { "apos", "'" }, { "nbsp", " " },
		{ "copy", "©" }, { "reg", "®" }, { "trade", "™" }, { "mdash", "—" }, { "ndash", "–" },
		{ "hellip", "…" }, { "times", "×" }, { "deg", "°" }, { "plusmn", "±" }, { "middot", "·" },
		{ "larr", "←" }, { "rarr", "→" }, { "uarr", "↑" }, { "darr", "↓" }, { "harr", "↔" },
		{ "lsquo", "‘" }, { "rsquo", "’" }, { "ldquo", "“" }, { "rdquo", "”" }, { "laquo", "«" },
		{ "raquo", "»" }, { "bull", "•" }, { "micro", "µ" }, { "lambda", "λ" }, { "radic", "√" },
	};

	private string _folder;
	private readonly HashSet<string> _anchors = new HashSet<string>(StringComparer.Ordinal);
	private readonly HashSet<string> _figureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _figureNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	private int _figures;

	/// <summary>Converts <see cref="Source"/> into <see cref="Destination"/>; false after an error, which names the line.</summary>
	public override bool Execute()
	{
		try
		{
			_folder = Path.GetDirectoryName(Path.GetFullPath(Source));
			string[] text = File.ReadAllLines(Source);
			var lines = new List<SourceLine>(text.Length);
			for (int i = 0; i < text.Length; i++)
			{
				lines.Add(new SourceLine(ExpandTabs(text[i]), i + 1));
			}
			List<Block> blocks = ParseBlocks(lines);
			Prepare(blocks);
			var sb = new StringBuilder();
			sb.AppendLine("<FlowDocument xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" FontFamily=\"Segoe UI\" FontSize=\"12\" PagePadding=\"10\" TextAlignment=\"Left\" IsHyphenationEnabled=\"False\">");
			RenderBlocks(blocks, sb, 0, false);
			sb.AppendLine("</FlowDocument>");
			if (Log.HasLoggedErrors)
			{
				return false;
			}
			string xaml = sb.ToString();
			// malformed output fails the build here rather than the app at XamlReader.Load
			XDocument.Parse(xaml);
			Directory.CreateDirectory(Path.GetDirectoryName(Destination));
			File.WriteAllText(Destination, xaml, new UTF8Encoding(false));
			RemoveOldFigures();
			Log.LogMessage(MessageImportance.Normal, "{0}", "Markdown document: " + Destination + " from " + Source + (_figures > 0 ? ", " + _figures + " figures" : ""));
			return true;
		}
		catch (Exception ex)
		{
			Log.LogErrorFromException(ex, true, true, Source);
			return false;
		}
	}

	// ------------------------------------------------------------------ blocks

	private enum BlockKind { Paragraph, Heading, Code, Rule, List, Quote }

	private struct SourceLine
	{
		public readonly string Text;
		public readonly int Number;

		public SourceLine(string text, int number)
		{
			Text = text;
			Number = number;
		}
	}

	private sealed class Block
	{
		public BlockKind Kind;
		/// <summary>The 1-based line of the Markdown file the block starts on, for messages.</summary>
		public int Line;
		/// <summary>The last line the block takes; a blank line between two blocks of a list item makes the list loose.</summary>
		public int EndLine;
		public int Level;
		/// <summary>A paragraph's or heading's text lines, or a code block's lines.</summary>
		public readonly List<string> Lines = new List<string>();
		public bool Ordered;
		/// <summary>A list's bullet character, or an ordered list's delimiter ('.' or ')').</summary>
		public char Marker;
		public int Start = 1;
		public bool Tight = true;
		public readonly List<List<Block>> Items = new List<List<Block>>();
		public List<Block> Children;
		public Node Inlines;
		public string Anchor;
	}

	private struct ListMarker
	{
		public bool Ordered;
		public char Char;
		public int Number;
		/// <summary>The column the item's content starts at; its other lines belong to it from this indentation on.</summary>
		public int ContentIndent;
		public string Content;
		public bool Empty;
	}

	private List<Block> ParseBlocks(List<SourceLine> lines)
	{
		var blocks = new List<Block>();
		int i = 0;
		while (i < lines.Count)
		{
			string line = lines[i].Text;
			if (IsBlank(line))
			{
				i++;
				continue;
			}
			int indent = Indent(line);
			if (indent >= 4)
			{
				i = ParseIndentedCode(lines, i, blocks);
				continue;
			}
			string rest = line.Substring(indent);
			Match match;
			ListMarker marker;
			if (rest.StartsWith("<!--", StringComparison.Ordinal))
			{
				i = SkipComment(lines, i);
			}
			else if (IsFence(rest, out match))
			{
				i = ParseFence(lines, i, indent, match.Groups[1].Value, blocks);
			}
			else if ((match = Atx.Match(rest)).Success)
			{
				var heading = new Block { Kind = BlockKind.Heading, Level = match.Groups[1].Length, Line = lines[i].Number, EndLine = lines[i].Number };
				heading.Lines.Add(AtxClosing.Replace(match.Groups[2].Value.Trim(), "").Trim());
				blocks.Add(heading);
				i++;
			}
			else if (Rule.IsMatch(rest))
			{
				blocks.Add(new Block { Kind = BlockKind.Rule, Line = lines[i].Number, EndLine = lines[i].Number });
				i++;
			}
			else if (rest[0] == '>')
			{
				i = ParseQuote(lines, i, blocks);
			}
			else if (TryListMarker(line, out marker))
			{
				i = ParseList(lines, i, marker, blocks);
			}
			else
			{
				i = ParseParagraph(lines, i, blocks);
			}
		}
		return blocks;
	}

	private static bool IsFence(string rest, out Match match)
	{
		match = Fence.Match(rest);
		return match.Success && !(match.Groups[1].Value[0] == '`' && match.Groups[2].Value.IndexOf('`') >= 0);
	}

	// the lines indented by four or more, blank ones between them included; the common indentation is removed
	private static int ParseIndentedCode(List<SourceLine> lines, int i, List<Block> blocks)
	{
		var block = new Block { Kind = BlockKind.Code, Line = lines[i].Number };
		var code = new List<string>();
		int j = i;
		while (j < lines.Count && (Indent(lines[j].Text) >= 4 || IsBlank(lines[j].Text)))
		{
			if (!IsBlank(lines[j].Text))
			{
				block.EndLine = lines[j].Number;
			}
			code.Add(lines[j].Text);
			j++;
		}
		while (code.Count > 0 && IsBlank(code[code.Count - 1]))
		{
			code.RemoveAt(code.Count - 1);
		}
		int indent = int.MaxValue;
		foreach (string c in code)
		{
			if (!IsBlank(c))
			{
				indent = Math.Min(indent, Indent(c));
			}
		}
		foreach (string c in code)
		{
			block.Lines.Add(c.Length >= indent ? c.Substring(indent) : "");
		}
		blocks.Add(block);
		return j;
	}

	// an HTML comment block, dropped up to the line that closes it
	private static int SkipComment(List<SourceLine> lines, int i)
	{
		int start = lines[i].Text.IndexOf("<!--", StringComparison.Ordinal) + 4;
		for (int j = i; j < lines.Count; j++)
		{
			if (lines[j].Text.IndexOf("-->", j == i ? start : 0, StringComparison.Ordinal) >= 0)
			{
				return j + 1;
			}
		}
		return lines.Count;
	}

	private static int ParseFence(List<SourceLine> lines, int i, int indent, string fence, List<Block> blocks)
	{
		var block = new Block { Kind = BlockKind.Code, Line = lines[i].Number };
		int j = i + 1;
		while (j < lines.Count)
		{
			string line = lines[j].Text;
			int lineIndent = Indent(line);
			string rest = line.Substring(lineIndent).TrimEnd();
			j++;
			if (lineIndent < 4 && rest.Length >= fence.Length && rest.Trim(fence[0]).Length == 0)
			{
				break;
			}
			block.Lines.Add(line.Substring(Math.Min(indent, lineIndent)));
		}
		block.EndLine = lines[j - 1].Number;
		blocks.Add(block);
		return j;
	}

	private int ParseQuote(List<SourceLine> lines, int i, List<Block> blocks)
	{
		int start = i;
		var inner = new List<SourceLine>();
		while (i < lines.Count)
		{
			string line = lines[i].Text;
			int indent = Indent(line);
			if (indent < 4 && indent < line.Length && line[indent] == '>')
			{
				string rest = line.Substring(indent + 1);
				inner.Add(new SourceLine(rest.StartsWith(" ", StringComparison.Ordinal) ? rest.Substring(1) : rest, lines[i].Number));
			}
			else if (!IsBlank(line) && inner.Count > 0 && Continues(inner) && !InterruptsParagraph(line))
			{
				inner.Add(new SourceLine(line.TrimStart(' '), lines[i].Number));
			}
			else
			{
				break;
			}
			i++;
		}
		blocks.Add(new Block { Kind = BlockKind.Quote, Line = lines[start].Number, EndLine = lines[i - 1].Number, Children = ParseBlocks(inner) });
		return i;
	}

	private int ParseList(List<SourceLine> lines, int i, ListMarker first, List<Block> blocks)
	{
		var list = new Block { Kind = BlockKind.List, Line = lines[i].Number, Ordered = first.Ordered, Marker = first.Char, Start = first.Number };
		ListMarker marker;
		while (i < lines.Count && TryListMarker(lines[i].Text, out marker) && marker.Ordered == list.Ordered && marker.Char == list.Marker)
		{
			var item = new List<SourceLine> { new SourceLine(marker.Content, lines[i].Number) };
			list.EndLine = lines[i].Number;
			i++;
			while (i < lines.Count)
			{
				string line = lines[i].Text;
				if (IsBlank(line))
				{
					int k = i;
					while (k < lines.Count && IsBlank(lines[k].Text))
					{
						k++;
					}
					// blank lines belong to the item when an indented line follows; an item that began empty ends at the first
					if (k < lines.Count && Indent(lines[k].Text) >= marker.ContentIndent && !(marker.Empty && item.Count == 1))
					{
						for (; i < k; i++)
						{
							item.Add(new SourceLine("", lines[i].Number));
						}
						continue;
					}
					break;
				}
				if (Indent(line) >= marker.ContentIndent)
				{
					item.Add(new SourceLine(line.Substring(marker.ContentIndent), lines[i].Number));
					list.EndLine = lines[i].Number;
					i++;
					continue;
				}
				ListMarker next;
				if (TryListMarker(line, out next) || !Continues(item) || InterruptsParagraph(line))
				{
					break;
				}
				// a lazy continuation line of the item's paragraph
				item.Add(new SourceLine(line.TrimStart(' '), lines[i].Number));
				list.EndLine = lines[i].Number;
				i++;
			}
			List<Block> content = ParseBlocks(item);
			// a blank line between two of the item's own blocks makes the list loose (one inside a nested list does not)
			for (int b = 1; b < content.Count; b++)
			{
				if (content[b].Line > content[b - 1].EndLine + 1)
				{
					list.Tight = false;
				}
			}
			list.Items.Add(content);
			int after = i;
			while (after < lines.Count && IsBlank(lines[after].Text))
			{
				after++;
			}
			if (after > i)
			{
				ListMarker next;
				if (after < lines.Count && TryListMarker(lines[after].Text, out next) && next.Ordered == list.Ordered && next.Char == list.Marker)
				{
					list.Tight = false;
					i = after;
				}
				else
				{
					break;
				}
			}
		}
		blocks.Add(list);
		return i;
	}

	private static int ParseParagraph(List<SourceLine> lines, int i, List<Block> blocks)
	{
		var block = new Block { Kind = BlockKind.Paragraph, Line = lines[i].Number };
		block.Lines.Add(lines[i].Text.TrimStart(' '));
		int j = i + 1;
		for (; j < lines.Count; j++)
		{
			string line = lines[j].Text;
			if (IsBlank(line))
			{
				break;
			}
			int indent = Indent(line);
			if (indent < 4 && Setext.IsMatch(line.Substring(indent)))
			{
				block.Kind = BlockKind.Heading;
				block.Level = line[indent] == '=' ? 1 : 2;
				j++;
				break;
			}
			if (InterruptsParagraph(line))
			{
				break;
			}
			block.Lines.Add(line.TrimStart(' '));
		}
		block.EndLine = lines[j - 1].Number;
		blocks.Add(block);
		return j;
	}

	// whether a line starts a block that ends a paragraph before it: a heading, a fence, a rule, a comment, a quote, or
	// a list item with content (an ordered one only from 1)
	private static bool InterruptsParagraph(string line)
	{
		int indent = Indent(line);
		if (indent >= 4 || IsBlank(line))
		{
			return false;
		}
		string rest = line.Substring(indent);
		Match fence;
		ListMarker marker;
		return rest.StartsWith("<!--", StringComparison.Ordinal) || rest[0] == '>' || Atx.IsMatch(rest) || Rule.IsMatch(rest) || IsFence(rest, out fence)
			|| (TryListMarker(line, out marker) && !marker.Empty && (!marker.Ordered || marker.Number == 1));
	}

	// whether the last line taken so far is paragraph text a lazy line may continue
	private static bool Continues(List<SourceLine> taken)
	{
		string last = taken[taken.Count - 1].Text;
		if (IsBlank(last) || Indent(last) >= 4)
		{
			return false;
		}
		string rest = last.TrimStart(' ');
		Match fence;
		return !Atx.IsMatch(rest) && !Rule.IsMatch(rest) && !IsFence(rest, out fence);
	}

	private static bool TryListMarker(string line, out ListMarker marker)
	{
		marker = new ListMarker();
		int indent = Indent(line);
		if (indent > 3)
		{
			return false;
		}
		string rest = line.Substring(indent);
		if (rest.Length == 0 || Rule.IsMatch(rest))
		{
			return false;
		}
		int width;
		if (rest[0] == '-' || rest[0] == '+' || rest[0] == '*')
		{
			marker.Char = rest[0];
			width = 1;
		}
		else
		{
			int digits = 0;
			while (digits < rest.Length && digits < 10 && rest[digits] >= '0' && rest[digits] <= '9')
			{
				digits++;
			}
			if (digits == 0 || digits > 9 || digits >= rest.Length || (rest[digits] != '.' && rest[digits] != ')'))
			{
				return false;
			}
			marker.Ordered = true;
			marker.Char = rest[digits];
			marker.Number = int.Parse(rest.Substring(0, digits), CultureInfo.InvariantCulture);
			width = digits + 1;
		}
		if (width < rest.Length && rest[width] != ' ')
		{
			return false;
		}
		int spaces = 0;
		while (width + spaces < rest.Length && rest[width + spaces] == ' ')
		{
			spaces++;
		}
		if (width + spaces == rest.Length)
		{
			marker.Empty = true;
			marker.Content = "";
			marker.ContentIndent = indent + width + 1;
		}
		else if (spaces > 4)
		{
			// five spaces or more: the item starts with indented code, its content one space after the marker
			marker.Content = rest.Substring(width + 1);
			marker.ContentIndent = indent + width + 1;
		}
		else
		{
			marker.Content = rest.Substring(width + spaces);
			marker.ContentIndent = indent + width + spaces;
		}
		return true;
	}

	// the inline content of every paragraph and heading, and the headings' anchors
	private void Prepare(List<Block> blocks)
	{
		foreach (Block block in blocks)
		{
			switch (block.Kind)
			{
				case BlockKind.Paragraph:
					block.Inlines = new InlineParser(string.Join("\n", block.Lines).TrimEnd()).Parse();
					break;
				case BlockKind.Heading:
					block.Inlines = new InlineParser(string.Join("\n", block.Lines).Trim()).Parse();
					block.Anchor = UniqueAnchor(Slug(PlainText(block.Inlines)));
					break;
				case BlockKind.List:
					foreach (List<Block> item in block.Items)
					{
						Prepare(item);
					}
					break;
				case BlockKind.Quote:
					Prepare(block.Children);
					break;
			}
		}
	}

	// GitHub's anchor: lower case, letters, digits, marks, hyphens and underscores kept, a hyphen for each space
	private static string Slug(string text)
	{
		var sb = new StringBuilder();
		foreach (char c in text.Trim().ToLowerInvariant())
		{
			UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
			if (c == ' ')
			{
				sb.Append('-');
			}
			else if (c == '-' || char.IsLetterOrDigit(c) || category == UnicodeCategory.ConnectorPunctuation || category == UnicodeCategory.NonSpacingMark
				|| category == UnicodeCategory.SpacingCombiningMark || category == UnicodeCategory.EnclosingMark)
			{
				sb.Append(c);
			}
		}
		return sb.ToString();
	}

	private string UniqueAnchor(string slug)
	{
		string anchor = slug;
		for (int n = 1; _anchors.Contains(anchor); n++)
		{
			anchor = slug + "-" + n.ToString(CultureInfo.InvariantCulture);
		}
		_anchors.Add(anchor);
		return anchor;
	}

	// ------------------------------------------------------------------ rendering

	private void RenderBlocks(List<Block> blocks, StringBuilder sb, int depth, bool tight)
	{
		foreach (Block block in blocks)
		{
			switch (block.Kind)
			{
				case BlockKind.Heading:
					string style = block.Level == 1 ? "FontSize=\"18\" FontWeight=\"SemiBold\" Margin=\"0,6,0,10\""
						: block.Level == 2 ? "FontSize=\"15\" FontWeight=\"SemiBold\" Margin=\"0,14,0,8\""
						: "FontSize=\"13\" FontWeight=\"SemiBold\" Margin=\"0,10,0,6\"";
					string tag = TagHeadings ? "Tag=\"h" + block.Level.ToString(CultureInfo.InvariantCulture) + "#" + Escape(block.Anchor) + "\" " : "";
					sb.AppendLine("<Paragraph " + tag + style + ">" + Inlines(block) + "</Paragraph>");
					break;
				case BlockKind.Paragraph:
					Node image = Figure(block.Inlines);
					if (image != null)
					{
						RenderFigure(image, block.Line, sb);
					}
					else
					{
						sb.AppendLine("<Paragraph Margin=\"" + (tight ? "0" : "0,0,0,10") + "\">" + Inlines(block) + "</Paragraph>");
					}
					break;
				case BlockKind.Code:
					sb.AppendLine("<Paragraph FontFamily=\"Consolas\" FontSize=\"11\" Margin=\"16,0,0,10\" xml:space=\"preserve\">" + string.Join("<LineBreak />", block.Lines.Select(Escape)) + "</Paragraph>");
					break;
				case BlockKind.Rule:
					sb.AppendLine("<Paragraph BorderThickness=\"0,0,0,1\" BorderBrush=\"{DynamicResource ControlBorder}\" Margin=\"0,6,0,14\" FontSize=\"1\" />");
					break;
				case BlockKind.List:
					RenderList(block, sb, depth);
					break;
				case BlockKind.Quote:
					sb.AppendLine("<Section Margin=\"0,0,0,10\" Padding=\"10,0,0,0\" BorderThickness=\"3,0,0,0\" BorderBrush=\"{DynamicResource ControlBorder}\">");
					RenderBlocks(block.Children, sb, depth, false);
					sb.AppendLine("</Section>");
					break;
			}
		}
	}

	private void RenderList(Block list, StringBuilder sb, int depth)
	{
		string marker = list.Ordered ? "Decimal" : depth == 0 ? "Disc" : depth == 1 ? "Circle" : "Square";
		string start = list.Ordered && list.Start != 1 ? " StartIndex=\"" + list.Start.ToString(CultureInfo.InvariantCulture) + "\"" : "";
		sb.AppendLine("<List MarkerStyle=\"" + marker + "\"" + start + " Margin=\"" + (depth == 0 ? "0,0,0,10" : "0,4,0,0") + "\" Padding=\"" + (list.Ordered ? "28,0,0,0" : "20,0,0,0") + "\">");
		bool tight = list.Tight && TightLists;
		foreach (List<Block> item in list.Items)
		{
			if (item.Count == 1 && item[0].Kind == BlockKind.Paragraph && Figure(item[0].Inlines) == null)
			{
				sb.AppendLine("<ListItem Margin=\"0,0,0,4\"><Paragraph" + (tight ? " Margin=\"0\"" : "") + ">" + Inlines(item[0]) + "</Paragraph></ListItem>");
				continue;
			}
			sb.AppendLine("<ListItem Margin=\"0,0,0,4\">");
			if (item.Count == 0)
			{
				sb.AppendLine("<Paragraph />");
			}
			RenderBlocks(item, sb, depth + 1, tight);
			sb.AppendLine("</ListItem>");
		}
		sb.AppendLine("</List>");
	}

	private string Inlines(Block block)
	{
		var sb = new StringBuilder();
		RenderInlines(block.Inlines, sb, block.Line, false);
		return sb.ToString();
	}

	private void RenderInlines(Node parent, StringBuilder sb, int line, bool inLink)
	{
		for (Node n = parent.First; n != null; n = n.Next)
		{
			switch (n.Kind)
			{
				case NodeKind.Text:
					sb.Append(Escape(n.Text));
					break;
				case NodeKind.SoftBreak:
					sb.Append(' ');
					break;
				case NodeKind.HardBreak:
					sb.Append("<LineBreak />");
					break;
				case NodeKind.Code:
					sb.Append("<Run FontFamily=\"Consolas\" Text=\"").Append(Escape(n.Text)).Append("\" />");
					break;
				case NodeKind.Emphasis:
					sb.Append("<Italic>");
					RenderInlines(n, sb, line, inLink);
					sb.Append("</Italic>");
					break;
				case NodeKind.Strong:
					sb.Append("<Span FontWeight=\"SemiBold\">");
					RenderInlines(n, sb, line, inLink);
					sb.Append("</Span>");
					break;
				case NodeKind.Link:
					RenderLink(n, sb, line, inLink);
					break;
				case NodeKind.Image:
					Warning(line, "the image " + n.Text + " shares its paragraph with text; only an image alone in its paragraph is a figure, here its description shows");
					sb.Append("<Italic>");
					RenderInlines(n, sb, line, true);
					sb.Append("</Italic>");
					break;
			}
		}
	}

	// a link to a heading ("#anchor") or to the web; one to a file shows as its text, the app having nothing to open there
	private void RenderLink(Node link, StringBuilder sb, int line, bool inLink)
	{
		string target = link.Text;
		bool anchor = target.StartsWith("#", StringComparison.Ordinal);
		Uri uri;
		bool web = Uri.TryCreate(target, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "mailto");
		if (inLink || !(anchor || web))
		{
			if (!inLink)
			{
				Log.LogMessage(MessageImportance.Normal, "{0}", Source + "(" + line + "): the link to " + target + " shows as its text, the app has nothing to open there");
			}
			RenderInlines(link, sb, line, true);
			return;
		}
		if (anchor && !_anchors.Contains(target.Substring(1)))
		{
			Warning(line, "the link " + target + " names no heading of the document");
		}
		sb.Append("<Hyperlink NavigateUri=\"").Append(Escape(target)).Append('"');
		if (link.Title != null)
		{
			sb.Append(" ToolTip=\"").Append(Escape(link.Title)).Append('"');
		}
		sb.Append('>');
		RenderInlines(link, sb, line, true);
		sb.Append("</Hyperlink>");
	}

	// the image when it stands alone in its paragraph, else null
	private static Node Figure(Node inlines)
	{
		Node image = null;
		for (Node n = inlines.First; n != null; n = n.Next)
		{
			if (n.Kind == NodeKind.Image && image == null)
			{
				image = n;
			}
			else if (!(n.Kind == NodeKind.Text && IsBlank(n.Text)))
			{
				return null;
			}
		}
		return image;
	}

	// a figure: the picture scaled down to the column when it is wider, never up, its pixels whole while it fits (a
	// zoomed figure's at its 100 % size, see SvgReader)
	private void RenderFigure(Node image, int line, StringBuilder sb)
	{
		string body = FigureBody(image.Text, line);
		if (body == null)
		{
			return;
		}
		_figures++;
		sb.AppendLine("<BlockUIContainer Margin=\"0,4,0,12\">");
		sb.Append("<Viewbox Stretch=\"Uniform\" StretchDirection=\"DownOnly\" HorizontalAlignment=\"Left\" UseLayoutRounding=\"True\" AutomationProperties.Name=\"").Append(Escape(PlainText(image))).Append('"');
		if (image.Title != null)
		{
			sb.Append(" ToolTip=\"").Append(Escape(image.Title)).Append('"');
		}
		sb.AppendLine(">");
		sb.Append(body);
		sb.AppendLine("</Viewbox>");
		sb.AppendLine("</BlockUIContainer>");
	}

	private string FigureBody(string source, int line)
	{
		if (string.IsNullOrEmpty(FigureFolder) || string.IsNullOrEmpty(FigureUri))
		{
			Error(line, "the image " + source + " needs a folder and a URI for the figures (FigureFolder, FigureUri)");
			return null;
		}
		Uri uri;
		if (Uri.TryCreate(source, UriKind.Absolute, out uri) && !uri.IsFile)
		{
			Error(line, "the image " + source + " is not a file; only a picture beside the document can be embedded");
			return null;
		}
		string path = Path.GetFullPath(Path.Combine(_folder, Uri.UnescapeDataString(source).Replace('/', Path.DirectorySeparatorChar)));
		if (!File.Exists(path))
		{
			Error(line, "the image " + path + " does not exist");
			return null;
		}
		string extension = Path.GetExtension(path).ToLowerInvariant();
		string name = Regex.Replace(Path.GetFileNameWithoutExtension(path), "[^A-Za-z0-9._-]", "-");
		if (extension == ".svg")
		{
			XDocument document;
			try
			{
				document = XDocument.Load(path, LoadOptions.SetLineInfo);
			}
			catch (XmlException ex)
			{
				Error(line, path + " is not well-formed XML: " + ex.Message);
				return null;
			}
			var reader = new SvgReader(this, path, name, line);
			string canvas = reader.Canvas(document.Root);
			return reader.Failed ? null : canvas;
		}
		if (extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".gif" || extension == ".bmp")
		{
			string file = WriteFigure(path, name, extension, File.ReadAllBytes(path));
			return "<Image Stretch=\"None\" RenderOptions.BitmapScalingMode=\"HighQuality\" Source=\"" + Escape(FigureUri + file) + "\" />" + Environment.NewLine;
		}
		Error(line, "the image " + path + " is neither an SVG nor a PNG, JPEG, GIF or BMP picture");
		return null;
	}

	// writes a figure's picture under a name of its own (unchanged bytes are left alone) and returns the file name
	private string WriteFigure(string key, string name, string extension, byte[] bytes)
	{
		string file;
		if (_figureNames.TryGetValue(key, out file))
		{
			return file;
		}
		file = name + extension;
		for (int n = 2; _figureFiles.Contains(file); n++)
		{
			file = name + "-" + n.ToString(CultureInfo.InvariantCulture) + extension;
		}
		_figureFiles.Add(file);
		_figureNames.Add(key, file);
		Directory.CreateDirectory(FigureFolder);
		string path = Path.Combine(FigureFolder, file);
		if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes))
		{
			File.WriteAllBytes(path, bytes);
		}
		return file;
	}

	private void RemoveOldFigures()
	{
		if (string.IsNullOrEmpty(FigureFolder) || !Directory.Exists(FigureFolder))
		{
			return;
		}
		foreach (string file in Directory.GetFiles(FigureFolder))
		{
			if (!_figureFiles.Contains(Path.GetFileName(file)))
			{
				File.Delete(file);
			}
		}
	}

	// ------------------------------------------------------------------ SVG figures

	/// <summary>
	/// Draws one SVG as a WPF Canvas: images, circles, texts and groups, a group's title its tooltip, the view box's
	/// origin at the canvas's corner, and a view box scaled to the SVG's size drawn at that scale (the harness's figures
	/// at a zoom, 2026-11-01: the pixels in the box, the size in CSS pixels, so the figure keeps its place on the page
	/// and its pixels show one to one on a monitor at the zoom's scale). Anything else fails the figure with an error
	/// naming it.
	/// </summary>
	private sealed class SvgReader
	{
		private readonly MarkdownToFlowDocument _task;
		private readonly string _path;
		private readonly string _name;
		private readonly int _line;
		private double _left;
		private double _top;
		/// <summary>Device-independent pixels per unit of the view box: 1, or a half for a figure at zoom 2.</summary>
		private double _unit = 1;
		private int _images;

		public SvgReader(MarkdownToFlowDocument task, string path, string name, int line)
		{
			_task = task;
			_path = path;
			_name = name;
			_line = line;
		}

		/// <summary>Whether an error was logged; the figure is left out then.</summary>
		public bool Failed { get; private set; }

		public string Canvas(XElement root)
		{
			if (root == null || root.Name != XName.Get("svg", SvgNamespace))
			{
				Fail(root, "the root element is not an SVG <svg>");
				return null;
			}
			Check(root, "width", "height", "viewBox", "version");
			double width = Number(root, "width", double.NaN), height = Number(root, "height", double.NaN);
			double boxWidth = width, boxHeight = height;
			string box = (string)root.Attribute("viewBox");
			if (box != null)
			{
				string[] parts = box.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
				double[] values = new double[4];
				bool valid = parts.Length == 4;
				for (int k = 0; valid && k < 4; k++)
				{
					valid = TryNumber(parts[k], out values[k]);
				}
				if (!valid)
				{
					Fail(root, "the viewBox \"" + box + "\" is not four numbers");
					return null;
				}
				_left = values[0];
				_top = values[1];
				boxWidth = values[2];
				boxHeight = values[3];
			}
			if (double.IsNaN(width))
			{
				width = boxWidth;
			}
			if (double.IsNaN(height))
			{
				height = boxHeight;
			}
			if (double.IsNaN(width) || double.IsNaN(height))
			{
				Fail(root, "it has neither a size nor a viewBox");
				return null;
			}
			if (boxWidth <= 0 || boxHeight <= 0)
			{
				Fail(root, "its viewBox is empty");
				return null;
			}
			_unit = width / boxWidth;
			if (Math.Abs(height / boxHeight - _unit) > 1e-6)
			{
				Fail(root, "its viewBox is scaled to another aspect, which the converter does not draw");
				return null;
			}
			var sb = new StringBuilder();
			sb.AppendLine("<Canvas Width=\"" + N(width) + "\" Height=\"" + N(height) + "\">");
			Children(root, new SvgStyle(), sb);
			sb.AppendLine("</Canvas>");
			return sb.ToString();
		}

		private void Children(XElement parent, SvgStyle style, StringBuilder sb)
		{
			foreach (XElement e in parent.Elements())
			{
				if (e.Name.NamespaceName != SvgNamespace)
				{
					Fail(e, "<" + e.Name.LocalName + "> is not an SVG element");
					continue;
				}
				switch (e.Name.LocalName)
				{
					case "g":
						Group(e, style, sb);
						break;
					case "image":
						Image(e, sb);
						break;
					case "circle":
						Circle(e, style, sb);
						break;
					case "text":
						Text(e, style, sb);
						break;
					case "title":
						// a group's title is its tooltip (Group reads it); the document's own is not drawn
						Check(e);
						break;
					default:
						Fail(e, "<" + e.Name.LocalName + "> is not one of the elements the converter draws (g, title, image, circle, text)");
						break;
				}
			}
		}

		private void Group(XElement e, SvgStyle style, StringBuilder sb)
		{
			Check(e, "fill", "font-family", "font-size", "font-weight", "text-anchor");
			SvgStyle own = style.With(e, this);
			XElement title = e.Element(XName.Get("title", SvgNamespace));
			string tip = title == null ? null : Collapse(title.Value);
			if (Badge(e, own, tip, sb))
			{
				return;
			}
			sb.AppendLine(tip == null ? "<Canvas>" : "<Canvas ToolTip=\"" + Escape(tip) + "\">");
			Children(e, own, sb);
			sb.AppendLine("</Canvas>");
		}

		// a badge, a group of a circle and a text centred on it (Screenshots.ps1's numbers), as one round Border holding
		// the text: two elements where the general form takes four, which saves the manual's opening a fifth of its time;
		// anything else (or anything odd, which the general form then reports) is left to the general form
		private bool Badge(XElement g, SvgStyle style, string tip, StringBuilder sb)
		{
			List<XElement> parts = g.Elements().Where(c => c.Name != XName.Get("title", SvgNamespace)).ToList();
			if (parts.Count != 2 || parts[0].Name != XName.Get("circle", SvgNamespace) || parts[1].Name != XName.Get("text", SvgNamespace)
				|| parts[1].Elements().Any() || Unknown(parts[0], "cx", "cy", "r", "fill", "stroke", "stroke-width") != null
				|| Unknown(parts[1], "x", "y", "dy", "fill", "font-family", "font-size", "font-weight", "text-anchor") != null)
			{
				return false;
			}
			XElement circle = parts[0], text = parts[1];
			double size = Plain(text, "font-size", style.FontSize);
			string dy = ((string)text.Attribute("dy") ?? "0").Trim();
			double shift = dy.EndsWith("em", StringComparison.Ordinal) ? Plain(dy.Substring(0, dy.Length - 2), double.NaN) * size : Plain(dy, double.NaN);
			double cx = Plain(circle, "cx", 0), cy = Plain(circle, "cy", 0), r = Plain(circle, "r", 0);
			double x = Plain(text, "x", 0), y = Plain(text, "y", 0);
			if (((string)text.Attribute("text-anchor") ?? style.Anchor) != "middle" || double.IsNaN(cx + cy + r + x + y + size + shift)
				|| Math.Abs(x - cx) > 0.01 || Math.Abs(y + shift - 0.36 * size - cy) > 0.01)
			{
				return false;
			}
			SvgStyle own = style.With(text, this);
			string fill = Paint(circle, (string)circle.Attribute("fill") ?? style.Fill ?? "black");
			string stroke = Paint(circle, (string)circle.Attribute("stroke") ?? "none");
			double strokeWidth = stroke == null ? 0 : Number(circle, "stroke-width", 1);
			string ink = Paint(text, own.Fill ?? "black");
			// the Border's edge, like SVG's stroke, is centred on the radius: the box grows by the stroke
			double box = (2 * r + strokeWidth) * _unit;
			sb.Append("<Border Canvas.Left=\"" + N((cx - _left) * _unit - box / 2) + "\" Canvas.Top=\"" + N((cy - _top) * _unit - box / 2) + "\" Width=\"" + N(box) + "\" Height=\"" + N(box)
				+ "\" CornerRadius=\"" + N(box / 2) + "\"");
			if (fill != null)
			{
				sb.Append(" Background=\"" + Escape(fill) + "\"");
			}
			if (stroke != null)
			{
				sb.Append(" BorderBrush=\"" + Escape(stroke) + "\" BorderThickness=\"" + N(strokeWidth * _unit) + "\"");
			}
			if (tip != null)
			{
				sb.Append(" ToolTip=\"" + Escape(tip) + "\"");
			}
			sb.Append("><TextBlock HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"");
			if (own.FontFamily != null)
			{
				sb.Append(" FontFamily=\"" + Escape(own.FontFamily) + "\"");
			}
			sb.Append(" FontSize=\"" + N(own.FontSize * _unit) + "\"");
			if (own.FontWeight != null)
			{
				sb.Append(" FontWeight=\"" + own.FontWeight + "\"");
			}
			if (ink != null)
			{
				sb.Append(" Foreground=\"" + Escape(ink) + "\"");
			}
			sb.AppendLine(" Text=\"" + Escape(Collapse(text.Value)) + "\" /></Border>");
			return true;
		}

		// a number of pixels for the badge's test, NaN when it is not one (the general form then reports it)
		private static double Plain(XElement e, string name, double fallback)
		{
			string value = (string)e.Attribute(name);
			return value == null ? fallback : Plain(value.Trim().EndsWith("px", StringComparison.Ordinal) ? value.Trim().Substring(0, value.Trim().Length - 2) : value, double.NaN);
		}

		private static double Plain(string value, double fallback)
		{
			double number;
			return TryNumber(value.Trim(), out number) ? number : fallback;
		}

		private void Image(XElement e, StringBuilder sb)
		{
			Check(e, "x", "y", "width", "height", "href", "xlink:href");
			string href = (string)e.Attribute(XName.Get("href", XLinkNamespace)) ?? (string)e.Attribute("href");
			Match match = href == null ? Match.Empty : DataUri.Match(href.Trim());
			if (!match.Success)
			{
				Fail(e, "the <image>'s picture is not embedded as data:image/png;base64 or data:image/jpeg;base64");
				return;
			}
			byte[] bytes;
			try
			{
				bytes = Convert.FromBase64String(Regex.Replace(match.Groups[2].Value, @"\s+", ""));
			}
			catch (FormatException)
			{
				Fail(e, "the <image>'s base64 data is broken");
				return;
			}
			double x = Number(e, "x", 0), y = Number(e, "y", 0), width = Number(e, "width", double.NaN), height = Number(e, "height", double.NaN);
			if (double.IsNaN(width) || double.IsNaN(height))
			{
				Fail(e, "the <image> needs a width and a height");
				return;
			}
			_images++;
			string name = _images == 1 ? _name : _name + "-" + _images.ToString(CultureInfo.InvariantCulture);
			string file = _task.WriteFigure(_path + "#" + _images.ToString(CultureInfo.InvariantCulture), name, match.Groups[1].Value == "png" ? ".png" : ".jpg", bytes);
			sb.AppendLine("<Image Canvas.Left=\"" + N((x - _left) * _unit) + "\" Canvas.Top=\"" + N((y - _top) * _unit) + "\" Width=\"" + N(width * _unit) + "\" Height=\"" + N(height * _unit)
				+ "\" Stretch=\"Fill\" RenderOptions.BitmapScalingMode=\"HighQuality\" Source=\"" + Escape(_task.FigureUri + file) + "\" />");
		}

		// SVG strokes a circle on its radius; WPF's Ellipse keeps the stroke inside its box, so the box grows by the stroke
		private void Circle(XElement e, SvgStyle style, StringBuilder sb)
		{
			Check(e, "cx", "cy", "r", "fill", "stroke", "stroke-width");
			double cx = Number(e, "cx", 0), cy = Number(e, "cy", 0), r = Number(e, "r", 0);
			string fill = Paint(e, (string)e.Attribute("fill") ?? style.Fill ?? "black");
			string stroke = Paint(e, (string)e.Attribute("stroke") ?? "none");
			double strokeWidth = stroke == null ? 0 : Number(e, "stroke-width", 1);
			double size = (2 * r + strokeWidth) * _unit;
			sb.Append("<Ellipse Canvas.Left=\"" + N((cx - _left) * _unit - size / 2) + "\" Canvas.Top=\"" + N((cy - _top) * _unit - size / 2) + "\" Width=\"" + N(size) + "\" Height=\"" + N(size) + "\"");
			if (fill != null)
			{
				sb.Append(" Fill=\"" + Escape(fill) + "\"");
			}
			if (stroke != null)
			{
				sb.Append(" Stroke=\"" + Escape(stroke) + "\" StrokeThickness=\"" + N(strokeWidth * _unit) + "\"");
			}
			sb.AppendLine(" />");
		}

		// WPF's Canvas places a text by its box, not by a baseline: the box is centred where the digits and capitals of
		// SVG's text sit, 0.36 em above its baseline, which centres a badge's number on its circle (dy=".36em")
		private void Text(XElement e, SvgStyle style, StringBuilder sb)
		{
			Check(e, "x", "y", "dy", "fill", "font-family", "font-size", "font-weight", "text-anchor");
			if (e.Elements().Any())
			{
				Fail(e, "the <text> has child elements, which the converter does not draw");
				return;
			}
			SvgStyle own = style.With(e, this);
			double size = own.FontSize * _unit;
			double x = Number(e, "x", 0), y = Number(e, "y", 0), dy = Length(e, "dy", own.FontSize);
			double middle = (y + dy - 0.36 * own.FontSize - _top) * _unit;
			string fill = Paint(e, own.Fill ?? "black");
			string alignment;
			double left = (x - _left) * _unit;
			switch (own.Anchor)
			{
				case "middle":
					alignment = "Center";
					left -= TextReach;
					break;
				case "end":
					alignment = "Right";
					left -= 2 * TextReach;
					break;
				default:
					alignment = "Left";
					break;
			}
			sb.Append("<Grid Canvas.Left=\"" + N(left) + "\" Canvas.Top=\"" + N(middle - 2 * size) + "\" Height=\"" + N(4 * size) + "\"");
			if (alignment != "Left")
			{
				sb.Append(" Width=\"" + N(2 * TextReach) + "\"");
			}
			sb.Append("><TextBlock HorizontalAlignment=\"" + alignment + "\" VerticalAlignment=\"Center\"");
			if (own.FontFamily != null)
			{
				sb.Append(" FontFamily=\"" + Escape(own.FontFamily) + "\"");
			}
			sb.Append(" FontSize=\"" + N(size) + "\"");
			if (own.FontWeight != null)
			{
				sb.Append(" FontWeight=\"" + own.FontWeight + "\"");
			}
			if (fill != null)
			{
				sb.Append(" Foreground=\"" + Escape(fill) + "\"");
			}
			sb.AppendLine(" Text=\"" + Escape(Collapse(e.Value)) + "\" /></Grid>");
		}

		/// <summary>A colour as WPF reads it (#RGB, #RRGGBB or a name), or null for "none".</summary>
		public string Paint(XElement e, string value)
		{
			string v = value.Trim();
			if (v == "none")
			{
				return null;
			}
			if (Regex.IsMatch(v, "^#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$") || Regex.IsMatch(v, "^[A-Za-z]+$"))
			{
				return v;
			}
			Fail(e, "the colour " + v + " is neither #RGB, #RRGGBB nor a name");
			return null;
		}

		public double Number(XElement e, string name, double fallback)
		{
			string value = (string)e.Attribute(name);
			if (value == null)
			{
				return fallback;
			}
			double number;
			string v = value.Trim();
			if (TryNumber(v.EndsWith("px", StringComparison.Ordinal) ? v.Substring(0, v.Length - 2) : v, out number))
			{
				return number;
			}
			Fail(e, "the " + name + " \"" + value + "\" is not a number of pixels");
			return fallback;
		}

		// a length in pixels or ems
		private double Length(XElement e, string name, double em)
		{
			string value = (string)e.Attribute(name);
			double number;
			if (value != null && value.Trim().EndsWith("em", StringComparison.Ordinal) && TryNumber(value.Trim().Substring(0, value.Trim().Length - 2), out number))
			{
				return number * em;
			}
			return Number(e, name, 0);
		}

		// every attribute must be one the converter draws (namespace declarations aside)
		private void Check(XElement e, params string[] allowed)
		{
			foreach (string name in Names(e).Where(n => Array.IndexOf(allowed, n) < 0))
			{
				Fail(e, "the <" + e.Name.LocalName + "> attribute " + name + " is not one the converter draws");
			}
		}

		// the first attribute not among the allowed ones, or null
		private static string Unknown(XElement e, params string[] allowed) => Names(e).FirstOrDefault(n => Array.IndexOf(allowed, n) < 0);

		// the attributes' names, xlink's with its prefix, namespace declarations aside
		private static IEnumerable<string> Names(XElement e) => e.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a =>
			a.Name.NamespaceName == XLinkNamespace ? "xlink:" + a.Name.LocalName
			: a.Name.NamespaceName.Length == 0 ? a.Name.LocalName
			: "{" + a.Name.NamespaceName + "}" + a.Name.LocalName);

		public void Fail(XElement e, string message)
		{
			var info = (IXmlLineInfo)e;
			string where = info != null && info.HasLineInfo() ? _path + "(" + info.LineNumber.ToString(CultureInfo.InvariantCulture) + ")" : _path;
			_task.Error(_line, where + ": " + message);
			Failed = true;
		}
	}

	/// <summary>The presentation attributes a group passes down to its texts and circles.</summary>
	private sealed class SvgStyle
	{
		/// <summary>WPF's font list, generic families dropped; null for the document's font.</summary>
		public string FontFamily;
		public double FontSize = 16;
		/// <summary>A WPF FontWeight name; null for the inherited weight.</summary>
		public string FontWeight;
		public string Anchor = "start";
		public string Fill;

		public SvgStyle With(XElement e, SvgReader reader)
		{
			var style = (SvgStyle)MemberwiseClone();
			string family = (string)e.Attribute("font-family");
			if (family != null)
			{
				string[] generic = { "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui" };
				string list = string.Join(", ", family.Split(',').Select(f => f.Trim().Trim('"', '\'')).Where(f => f.Length > 0 && Array.IndexOf(generic, f) < 0));
				style.FontFamily = list.Length > 0 ? list : null;
			}
			if (e.Attribute("font-size") != null)
			{
				style.FontSize = reader.Number(e, "font-size", style.FontSize);
			}
			string weight = (string)e.Attribute("font-weight");
			if (weight != null)
			{
				switch (weight.Trim())
				{
					case "normal": case "400": style.FontWeight = "Normal"; break;
					case "bold": case "bolder": case "700": style.FontWeight = "Bold"; break;
					case "lighter": case "300": style.FontWeight = "Light"; break;
					case "100": style.FontWeight = "Thin"; break;
					case "200": style.FontWeight = "ExtraLight"; break;
					case "500": style.FontWeight = "Medium"; break;
					case "600": style.FontWeight = "SemiBold"; break;
					case "800": style.FontWeight = "ExtraBold"; break;
					case "900": style.FontWeight = "Black"; break;
					default: reader.Fail(e, "the font-weight " + weight + " is not one WPF knows"); break;
				}
			}
			string anchor = (string)e.Attribute("text-anchor");
			if (anchor != null)
			{
				if (anchor == "start" || anchor == "middle" || anchor == "end")
				{
					style.Anchor = anchor;
				}
				else
				{
					reader.Fail(e, "the text-anchor " + anchor + " is not start, middle or end");
				}
			}
			string fill = (string)e.Attribute("fill");
			if (fill != null)
			{
				style.Fill = fill;
			}
			return style;
		}
	}

	// ------------------------------------------------------------------ inlines

	private enum NodeKind { Root, Text, Code, SoftBreak, HardBreak, Emphasis, Strong, Link, Image }

	/// <summary>An inline node in a doubly linked list of siblings; Link and Image keep their destination in Text.</summary>
	private sealed class Node
	{
		public readonly NodeKind Kind;
		public string Text;
		public string Title;
		public Node Parent;
		public Node Previous;
		public Node Next;
		public Node First;
		public Node Last;

		public Node(NodeKind kind, string text = null)
		{
			Kind = kind;
			Text = text;
		}

		public void Append(Node child)
		{
			child.Parent = this;
			child.Previous = Last;
			child.Next = null;
			if (Last == null)
			{
				First = child;
			}
			else
			{
				Last.Next = child;
			}
			Last = child;
		}

		/// <summary>Puts this node right after <paramref name="sibling"/>, in its parent.</summary>
		public void InsertAfter(Node sibling)
		{
			Parent = sibling.Parent;
			Previous = sibling;
			Next = sibling.Next;
			if (sibling.Next == null)
			{
				Parent.Last = this;
			}
			else
			{
				sibling.Next.Previous = this;
			}
			sibling.Next = this;
		}

		public void Unlink()
		{
			if (Previous == null)
			{
				Parent.First = Next;
			}
			else
			{
				Previous.Next = Next;
			}
			if (Next == null)
			{
				Parent.Last = Previous;
			}
			else
			{
				Next.Previous = Previous;
			}
			Parent = null;
			Previous = null;
			Next = null;
		}
	}

	/// <summary>A run of * or _ that may open or close emphasis, on CommonMark's delimiter stack.</summary>
	private sealed class Delimiter
	{
		public Node Node;
		public char Char;
		public int Count;
		public int Original;
		public bool CanOpen;
		public bool CanClose;
		public Delimiter Previous;
		public Delimiter Next;
	}

	/// <summary>A [ or ![ waiting for its ], with the delimiter stack's top when it was met.</summary>
	private sealed class Bracket
	{
		public Node Node;
		public bool Image;
		public bool Active = true;
		public Delimiter Bottom;
	}

	/// <summary>CommonMark's inline parsing of one paragraph or heading: the nodes first, then the emphasis.</summary>
	private sealed class InlineParser
	{
		private readonly string _s;
		private readonly Node _root = new Node(NodeKind.Root);
		private readonly StringBuilder _text = new StringBuilder();
		private readonly List<Bracket> _brackets = new List<Bracket>();
		private Delimiter _top;
		private int _pos;

		public InlineParser(string s)
		{
			_s = s;
		}

		public Node Parse()
		{
			while (_pos < _s.Length)
			{
				char c = _s[_pos];
				switch (c)
				{
					case '\\':
						Backslash();
						break;
					case '`':
						Backticks();
						break;
					case '*':
					case '_':
						Run(c);
						break;
					case '[':
						OpenBracket(false);
						break;
					case '!':
						if (_pos + 1 < _s.Length && _s[_pos + 1] == '[')
						{
							OpenBracket(true);
						}
						else
						{
							_text.Append(c);
							_pos++;
						}
						break;
					case ']':
						CloseBracket();
						break;
					case '<':
						Angle();
						break;
					case '&':
						Ampersand();
						break;
					case '\n':
						LineEnd();
						break;
					default:
						if (!Bare())
						{
							_text.Append(c);
							_pos++;
						}
						break;
				}
			}
			Flush();
			ProcessEmphasis(null);
			return _root;
		}

		private void Flush()
		{
			if (_text.Length > 0)
			{
				_root.Append(new Node(NodeKind.Text, _text.ToString()));
				_text.Clear();
			}
		}

		private Node Add(Node node)
		{
			Flush();
			_root.Append(node);
			return node;
		}

		private void AddLink(string destination, string text)
		{
			Add(new Node(NodeKind.Link, destination)).Append(new Node(NodeKind.Text, text));
		}

		private void Backslash()
		{
			char next = _pos + 1 < _s.Length ? _s[_pos + 1] : '\0';
			if (next == '\n')
			{
				Add(new Node(NodeKind.HardBreak));
				_pos += 2;
				SkipLeadingSpace();
			}
			else if (IsAsciiPunctuation(next))
			{
				_text.Append(next);
				_pos += 2;
			}
			else
			{
				_text.Append('\\');
				_pos++;
			}
		}

		private void Backticks()
		{
			int start = _pos;
			while (_pos < _s.Length && _s[_pos] == '`')
			{
				_pos++;
			}
			int count = _pos - start;
			for (int p = _pos; p < _s.Length;)
			{
				if (_s[p] != '`')
				{
					p++;
					continue;
				}
				int q = p;
				while (q < _s.Length && _s[q] == '`')
				{
					q++;
				}
				if (q - p == count)
				{
					string code = _s.Substring(_pos, p - _pos).Replace('\n', ' ');
					if (code.Length >= 2 && code[0] == ' ' && code[code.Length - 1] == ' ' && code.Trim(' ').Length > 0)
					{
						code = code.Substring(1, code.Length - 2);
					}
					Add(new Node(NodeKind.Code, code));
					_pos = q;
					return;
				}
				p = q;
			}
			_text.Append('`', count);
		}

		// a delimiter run, left- or right-flanking by CommonMark's rules (_ not inside a word)
		private void Run(char c)
		{
			int start = _pos;
			while (_pos < _s.Length && _s[_pos] == c)
			{
				_pos++;
			}
			int count = _pos - start;
			char before = start > 0 ? _s[start - 1] : '\n', after = _pos < _s.Length ? _s[_pos] : '\n';
			bool beforeSpace = char.IsWhiteSpace(before), afterSpace = char.IsWhiteSpace(after);
			bool beforePunctuation = IsPunctuation(before), afterPunctuation = IsPunctuation(after);
			bool left = !afterSpace && (!afterPunctuation || beforeSpace || beforePunctuation);
			bool right = !beforeSpace && (!beforePunctuation || afterSpace || afterPunctuation);
			bool open = c == '*' ? left : left && (!right || beforePunctuation);
			bool close = c == '*' ? right : right && (!left || afterPunctuation);
			Node node = Add(new Node(NodeKind.Text, new string(c, count)));
			if (open || close)
			{
				var d = new Delimiter { Node = node, Char = c, Count = count, Original = count, CanOpen = open, CanClose = close, Previous = _top };
				if (_top != null)
				{
					_top.Next = d;
				}
				_top = d;
			}
		}

		private void Remove(Delimiter d)
		{
			if (d.Previous != null)
			{
				d.Previous.Next = d.Next;
			}
			if (d.Next != null)
			{
				d.Next.Previous = d.Previous;
			}
			else
			{
				_top = d.Previous;
			}
		}

		// CommonMark's "process emphasis" over the delimiters above bottom (null: all of them)
		private void ProcessEmphasis(Delimiter bottom)
		{
			var openersBottom = new Delimiter[12];
			var bottomSet = new bool[12];
			Delimiter closer = null;
			for (Delimiter d = _top; d != null && d != bottom; d = d.Previous)
			{
				closer = d;
			}
			while (closer != null)
			{
				if (!closer.CanClose)
				{
					closer = closer.Next;
					continue;
				}
				int key = (closer.Char == '*' ? 0 : 6) + (closer.CanOpen ? 3 : 0) + closer.Original % 3;
				Delimiter opener = closer.Previous;
				bool found = false;
				while (opener != null && opener != bottom && !(bottomSet[key] && opener == openersBottom[key]))
				{
					bool multipleOfThree = (opener.CanClose || closer.CanOpen) && (opener.Original + closer.Original) % 3 == 0
						&& !(opener.Original % 3 == 0 && closer.Original % 3 == 0);
					if (opener.Char == closer.Char && opener.CanOpen && !multipleOfThree)
					{
						found = true;
						break;
					}
					opener = opener.Previous;
				}
				if (!found)
				{
					openersBottom[key] = closer.Previous;
					bottomSet[key] = true;
					Delimiter next = closer.Next;
					if (!closer.CanOpen)
					{
						Remove(closer);
					}
					closer = next;
					continue;
				}
				int use = closer.Count >= 2 && opener.Count >= 2 ? 2 : 1;
				opener.Count -= use;
				closer.Count -= use;
				opener.Node.Text = opener.Node.Text.Substring(use);
				closer.Node.Text = closer.Node.Text.Substring(use);
				var emphasis = new Node(use == 2 ? NodeKind.Strong : NodeKind.Emphasis);
				for (Node n = opener.Node.Next; n != null && n != closer.Node;)
				{
					Node next = n.Next;
					n.Unlink();
					emphasis.Append(n);
					n = next;
				}
				emphasis.InsertAfter(opener.Node);
				// the delimiters between the two are plain text now
				for (Delimiter d = closer.Previous; d != null && d != opener;)
				{
					Delimiter previous = d.Previous;
					Remove(d);
					d = previous;
				}
				if (opener.Count == 0)
				{
					opener.Node.Unlink();
					Remove(opener);
				}
				if (closer.Count == 0)
				{
					Delimiter next = closer.Next;
					closer.Node.Unlink();
					Remove(closer);
					closer = next;
				}
			}
			while (_top != null && _top != bottom)
			{
				Remove(_top);
			}
		}

		private void OpenBracket(bool image)
		{
			Node node = Add(new Node(NodeKind.Text, image ? "![" : "["));
			_pos += image ? 2 : 1;
			_brackets.Add(new Bracket { Node = node, Image = image, Bottom = _top });
		}

		private void CloseBracket()
		{
			_pos++;
			Flush();
			Bracket opener = _brackets.Count > 0 ? _brackets[_brackets.Count - 1] : null;
			string destination = null, title = null;
			int end = 0;
			if (opener == null || !opener.Active || !TryLinkTail(_pos, out destination, out title, out end))
			{
				if (opener != null)
				{
					_brackets.RemoveAt(_brackets.Count - 1);
				}
				_root.Append(new Node(NodeKind.Text, "]"));
				return;
			}
			_brackets.RemoveAt(_brackets.Count - 1);
			_pos = end;
			var link = new Node(opener.Image ? NodeKind.Image : NodeKind.Link, destination) { Title = title };
			for (Node n = opener.Node.Next; n != null;)
			{
				Node next = n.Next;
				n.Unlink();
				link.Append(n);
				n = next;
			}
			link.InsertAfter(opener.Node);
			opener.Node.Unlink();
			ProcessEmphasis(opener.Bottom);
			if (!opener.Image)
			{
				// no link inside a link
				foreach (Bracket b in _brackets)
				{
					if (!b.Image)
					{
						b.Active = false;
					}
				}
			}
		}

		// an inline link's "(destination "title")" right after the ]
		private bool TryLinkTail(int p, out string destination, out string title, out int end)
		{
			destination = null;
			title = null;
			end = p;
			if (p >= _s.Length || _s[p] != '(')
			{
				return false;
			}
			p = SkipSpace(p + 1);
			if (p < _s.Length && _s[p] == '<')
			{
				int q = p + 1;
				while (q < _s.Length && _s[q] != '>' && _s[q] != '<' && _s[q] != '\n')
				{
					q += _s[q] == '\\' && q + 1 < _s.Length ? 2 : 1;
				}
				if (q >= _s.Length || _s[q] != '>')
				{
					return false;
				}
				destination = Unescape(_s.Substring(p + 1, q - p - 1));
				p = q + 1;
			}
			else
			{
				int q = p, depth = 0;
				while (q < _s.Length)
				{
					char c = _s[q];
					if (c == '\\' && q + 1 < _s.Length && IsAsciiPunctuation(_s[q + 1]))
					{
						q += 2;
						continue;
					}
					if (c == '(')
					{
						depth++;
					}
					else if (c == ')')
					{
						if (depth == 0)
						{
							break;
						}
						depth--;
					}
					else if (char.IsWhiteSpace(c) || char.IsControl(c))
					{
						break;
					}
					q++;
				}
				if (depth != 0)
				{
					return false;
				}
				destination = Unescape(_s.Substring(p, q - p));
				p = q;
			}
			int beforeSpace = p;
			p = SkipSpace(p);
			if (p > beforeSpace && p < _s.Length && (_s[p] == '"' || _s[p] == '\'' || _s[p] == '('))
			{
				char close = _s[p] == '(' ? ')' : _s[p];
				int q = p + 1;
				while (q < _s.Length && _s[q] != close)
				{
					q += _s[q] == '\\' && q + 1 < _s.Length ? 2 : 1;
				}
				if (q >= _s.Length)
				{
					return false;
				}
				title = Unescape(_s.Substring(p + 1, q - p - 1));
				p = SkipSpace(q + 1);
			}
			if (p >= _s.Length || _s[p] != ')')
			{
				return false;
			}
			end = p + 1;
			return true;
		}

		// spaces and tabs with at most one line end among them
		private int SkipSpace(int p)
		{
			bool lineEnd = false;
			while (p < _s.Length && (_s[p] == ' ' || _s[p] == '\t' || (_s[p] == '\n' && !lineEnd)))
			{
				lineEnd |= _s[p] == '\n';
				p++;
			}
			return p;
		}

		// an HTML comment (dropped), an autolink, or a plain <
		private void Angle()
		{
			if (string.CompareOrdinal(_s, _pos, "<!--", 0, 4) == 0)
			{
				int close = _s.IndexOf("-->", _pos + 4, StringComparison.Ordinal);
				if (close >= 0)
				{
					Flush();
					_pos = close + 3;
					return;
				}
			}
			Match match = AutoLink.Match(_s, _pos);
			if (match.Success)
			{
				AddLink(match.Groups[1].Value, match.Groups[1].Value);
				_pos += match.Length;
				return;
			}
			match = EmailLink.Match(_s, _pos);
			if (match.Success)
			{
				AddLink("mailto:" + match.Groups[1].Value, match.Groups[1].Value);
				_pos += match.Length;
				return;
			}
			_text.Append('<');
			_pos++;
		}

		private void Ampersand()
		{
			Match match = Entity.Match(_s, _pos);
			string decoded = match.Success ? Decode(match) : null;
			if (decoded != null)
			{
				_text.Append(decoded);
				_pos += match.Length;
			}
			else
			{
				_text.Append('&');
				_pos++;
			}
		}

		// a line end: a hard break after two spaces or more, else a soft one; the next line's indentation goes
		private void LineEnd()
		{
			int spaces = 0;
			while (_text.Length > 0 && _text[_text.Length - 1] == ' ')
			{
				_text.Length--;
				spaces++;
			}
			Add(new Node(spaces >= 2 ? NodeKind.HardBreak : NodeKind.SoftBreak));
			_pos++;
			SkipLeadingSpace();
		}

		private void SkipLeadingSpace()
		{
			while (_pos < _s.Length && (_s[_pos] == ' ' || _s[_pos] == '\t'))
			{
				_pos++;
			}
		}

		// GitHub's bare URL at the start of a word: http://, https:// or www., trailing punctuation left out
		private bool Bare()
		{
			char c = _s[_pos];
			if (c != 'h' && c != 'H' && c != 'w' && c != 'W')
			{
				return false;
			}
			char before = _pos > 0 ? _s[_pos - 1] : '\n';
			if (!char.IsWhiteSpace(before) && before != '*' && before != '_' && before != '~' && before != '(')
			{
				return false;
			}
			Match match = BareUrl.Match(_s, _pos);
			if (!match.Success)
			{
				return false;
			}
			string url = match.Value;
			while (url.Length > 0)
			{
				char last = url[url.Length - 1];
				if ("?!.,:*_~'\"".IndexOf(last) >= 0 || (last == ')' && url.Count(ch => ch == '(') < url.Count(ch => ch == ')')))
				{
					url = url.Substring(0, url.Length - 1);
				}
				else if (last == ';' && Regex.IsMatch(url, "&[A-Za-z0-9]+;$"))
				{
					url = url.Substring(0, url.LastIndexOf('&'));
				}
				else
				{
					break;
				}
			}
			bool www = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
			string host = url.Substring(www ? 4 : url.IndexOf("://", StringComparison.Ordinal) + 3);
			int stop = host.IndexOfAny(new[] { '/', '?', '#' });
			if (stop >= 0)
			{
				host = host.Substring(0, stop);
			}
			if (host.Length == 0 || (www && host.IndexOf('.') < 0) || !Regex.IsMatch(host, @"^[A-Za-z0-9_.\-:]+$"))
			{
				return false;
			}
			AddLink(www ? "http://" + url : url, url);
			_pos += url.Length;
			return true;
		}

		private static string Unescape(string s)
		{
			var sb = new StringBuilder();
			for (int i = 0; i < s.Length; i++)
			{
				if (s[i] == '\\' && i + 1 < s.Length && IsAsciiPunctuation(s[i + 1]))
				{
					sb.Append(s[++i]);
					continue;
				}
				if (s[i] == '&')
				{
					Match match = Entity.Match(s, i);
					string decoded = match.Success ? Decode(match) : null;
					if (decoded != null)
					{
						sb.Append(decoded);
						i += match.Length - 1;
						continue;
					}
				}
				sb.Append(s[i]);
			}
			return sb.ToString();
		}

		private static string Decode(Match match)
		{
			int code;
			if (match.Groups[1].Success)
			{
				code = int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			}
			else if (match.Groups[2].Success)
			{
				code = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
			}
			else
			{
				string named;
				return Entities.TryGetValue(match.Groups[3].Value, out named) ? named : null;
			}
			if (code == 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
			{
				code = 0xFFFD;
			}
			return char.ConvertFromUtf32(code);
		}

		private static bool IsAsciiPunctuation(char c) => c < 128 && "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~".IndexOf(c) >= 0;

		// CommonMark's punctuation: Unicode's punctuation and symbols
		private static bool IsPunctuation(char c) => char.IsPunctuation(c) || char.IsSymbol(c);
	}

	// ------------------------------------------------------------------ helpers

	private static string PlainText(Node parent)
	{
		var sb = new StringBuilder();
		for (Node n = parent.First; n != null; n = n.Next)
		{
			if (n.Kind == NodeKind.Text || n.Kind == NodeKind.Code)
			{
				sb.Append(n.Text);
			}
			else if (n.Kind == NodeKind.SoftBreak || n.Kind == NodeKind.HardBreak)
			{
				sb.Append(' ');
			}
			else
			{
				sb.Append(PlainText(n));
			}
		}
		return sb.ToString();
	}

	private void Error(int line, string message) => Log.LogError(null, null, null, Source, line, 0, 0, 0, "{0}", message);

	private void Warning(int line, string message) => Log.LogWarning(null, null, null, Source, line, 0, 0, 0, "{0}", message);

	private static bool TryNumber(string s, out double value) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

	private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

	private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

	private static bool IsBlank(string line) => line.Trim().Length == 0;

	private static int Indent(string line)
	{
		int i = 0;
		while (i < line.Length && line[i] == ' ')
		{
			i++;
		}
		return i;
	}

	private static string ExpandTabs(string line)
	{
		if (line.IndexOf('\t') < 0)
		{
			return line;
		}
		var sb = new StringBuilder();
		foreach (char c in line)
		{
			if (c == '\t')
			{
				sb.Append(' ', 4 - sb.Length % 4);
			}
			else
			{
				sb.Append(c);
			}
		}
		return sb.ToString();
	}

	private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
