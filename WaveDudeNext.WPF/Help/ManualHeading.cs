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


using System.Windows.Documents;

namespace WaveDudeNext.WPF.Help;

/// <summary>
/// A heading of the manual as a row of the manual window's contents tree, with the headings under it. Made by
/// <see cref="ManualDocument.Contents"/>; UI thread (it holds the document's paragraph).
/// </summary>
/// <param name="text">The heading's text.</param>
/// <param name="level">1 to 6, the Markdown heading's level.</param>
/// <param name="anchor">GitHub's anchor of the heading, what a link "#anchor" names.</param>
/// <param name="paragraph">The heading's paragraph in the document, where the tree goes to.</param>
public sealed class ManualHeading(string text, int level, string anchor, Paragraph paragraph)
{
	/// <summary>The heading's text.</summary>
	public string Text { get; } = text;

	/// <summary>1 to 6, the Markdown heading's level.</summary>
	public int Level { get; } = level;

	/// <summary>GitHub's anchor of the heading (lower case, a hyphen for each space), what a link "#anchor" names.</summary>
	public string Anchor { get; } = anchor;

	/// <summary>The heading's paragraph in the document; pointed at a new copy's when the window loads the document again.</summary>
	public Paragraph Paragraph { get; internal set; } = paragraph;

	/// <summary>The headings of a higher level up to the next one of this level or lower, in document order.</summary>
	public List<ManualHeading> Children { get; } = [];

	/// <summary>The heading's text, which UI Automation reads as the row's name.</summary>
	public override string ToString() => Text;
}
