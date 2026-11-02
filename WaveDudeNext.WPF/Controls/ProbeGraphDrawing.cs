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
using System.Windows;
using System.Windows.Media;
using WaveDudeNext.Probes;

namespace WaveDudeNext.WPF.Controls;

/// <summary>
/// Draws a probe graph box from its <see cref="ProbeGraphLayout"/> with WPF drawing primitives: the
/// translucent dark box, the axes with their labels, the curve and the title strip. Shared by the preview's
/// <see cref="ProbeGraphOverlay"/> (screen pixels, the box scaled with the picture) and the export's painter
/// (canvas pixels, rendered into the frame). Colours are the author's, fixed, not themed: the box sits on
/// the picture. All brushes are frozen, so any thread may draw with them into its own visuals.
/// </summary>
public static class ProbeGraphDrawing
{
	/// <summary>The fixed label size in pixels (text does not scale with the box).</summary>
	public const double FontSize = 11.0;

	/// <summary>The graph font.</summary>
	public static readonly Typeface Typeface = new("Segoe UI");

	private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromArgb(235, 14, 16, 22)));
	private static readonly Brush BorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(170, 200, 200, 210)));
	private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)));
	private static readonly Brush ZeroBrush = Freeze(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)));
	private static readonly Brush FrameBrush = Freeze(new SolidColorBrush(Color.FromArgb(130, 200, 200, 210)));
	private static readonly Dictionary<(Brush Brush, double Thickness), Pen> Pens = [];
	private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(225, 225, 225, 230)));
	private static readonly Brush TitleBrush = Freeze(new SolidColorBrush(Color.FromArgb(240, 240, 240, 245)));
	private static readonly Brush CurveBrush = Freeze(new SolidColorBrush(Color.FromArgb(255, 255, 208, 64)));

	/// <summary>The text metrics of the graph font at a DPI: the line height and a measure of label widths, for <see cref="ProbeGraph.Layout"/>.</summary>
	public static ProbeGraphStyle MakeStyle(double pixelsPerDip)
	{
		float lineHeight = (float)Text("0", FontSize, LabelBrush, pixelsPerDip).Height;
		return new ProbeGraphStyle((float)FontSize, lineHeight, text => (float)Text(text, FontSize, LabelBrush, pixelsPerDip).Width);
	}

	/// <summary>Draws one box from its layout, with the origin at the box's top-left corner.</summary>
	/// <param name="dc">The drawing context.</param>
	/// <param name="layout">The box's geometry and text.</param>
	/// <param name="border">The border pen for a selected box, or null for the usual one.</param>
	/// <param name="pixelsPerDip">The DPI scale for the text.</param>
	public static void DrawBox(DrawingContext dc, ProbeGraphLayout layout, Pen? border, double pixelsPerDip)
	{
		double w = layout.Width;
		double h = layout.Height;
		// a line is a whole number of device pixels thick (one at 100 %, two at 150 and 200 %), and every edge drawn
		// below lies on a guideline, which WPF moves onto the device grid under any scale, so the box's lines come out
		// whole at a UI zoom and on a 150 % monitor as at 100 % (2026-11-02, the user's rule); at 100 %, and in an
		// export, the arithmetic is the one before, pixel for pixel
		double t = DevicePixels.Hairline(pixelsPerDip);
		double half = t / 2;
		var xs = new List<double> { 0, t, w - t, w };
		var ys = new List<double> { 0, t, h - t, h };
		if (layout.HasPlot)
		{
			double right = layout.PlotX + layout.PlotWidth;
			double bottom = layout.PlotY + layout.PlotHeight;
			xs.AddRange([layout.PlotX, layout.PlotX + t, right, right + t]);
			ys.AddRange([layout.PlotY, layout.PlotY + t, bottom, bottom + t]);
			foreach (GraphTick tick in layout.YTicks)
			{
				double row = Math.Round(tick.Position);
				ys.Add(row);
				ys.Add(row + t);
			}
			foreach (GraphTick tick in layout.XTicks)
			{
				double column = Math.Round(tick.Position);
				xs.Add(column);
				xs.Add(column + t);
			}
		}
		dc.PushGuidelineSet(new GuidelineSet([.. xs], [.. ys]));
		dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, w, h));
		dc.DrawRectangle(null, border ?? PenOf(BorderBrush, t), new Rect(half, half, w - t, h - t));
		double fontPx = layout.FontSize;
		if (layout.HasPlot)
		{
			double plotLeft = layout.PlotX;
			double plotTop = layout.PlotY;
			double plotRight = layout.PlotX + layout.PlotWidth;
			double plotBottom = layout.PlotY + layout.PlotHeight;
			// value axis: grid lines (the zero line brighter) with right-aligned labels in the left margin
			foreach (GraphTick tick in layout.YTicks)
			{
				double y = Math.Round(tick.Position) + half;
				dc.DrawLine(PenOf(tick.IsZero ? ZeroBrush : GridBrush, t), new Point(plotLeft, y), new Point(plotRight, y));
				if (tick.Label.Length == 0)
				{
					continue;
				}
				// right-aligned by position: a MaxTextWidth equal to the measured width made WPF wrap the label onto a
				// second line, which the one-line limit dropped (the labels vanished, 2026-10-14)
				FormattedText label = Text(tick.Label, fontPx, LabelBrush, pixelsPerDip);
				double lx = Math.Max(layout.LabelLeft, plotLeft - ProbeGraph.LabelGap - label.Width);
				dc.DrawText(label, new Point(lx, Math.Clamp(tick.Position - label.Height / 2, plotTop - label.Height / 2, plotBottom - label.Height / 2)));
			}
			// step axis (point probe) or position on the line (line probe): labels centred below the plot, kept inside the box
			foreach (GraphTick tick in layout.XTicks)
			{
				double x = Math.Round(tick.Position) + half;
				dc.DrawLine(PenOf(GridBrush, t), new Point(x, plotTop), new Point(x, plotBottom));
				if (tick.Label.Length == 0)
				{
					continue;
				}
				FormattedText label = Text(tick.Label, fontPx, LabelBrush, pixelsPerDip);
				double lx = Math.Clamp(tick.Position - label.Width / 2, ProbeGraph.EdgePad, Math.Max(ProbeGraph.EdgePad, w - label.Width - ProbeGraph.EdgePad));
				dc.DrawText(label, new Point(lx, plotBottom + 2));
			}
			dc.DrawRectangle(null, PenOf(FrameBrush, t), new Rect(plotLeft + half, plotTop + half, layout.PlotWidth, layout.PlotHeight));
			DrawCurve(dc, layout);
		}
		// the title strip: the readout right-aligned in its own colour, the title in the remaining width
		double readoutWidth = 0;
		if (layout.Readout.Length > 0)
		{
			FormattedText readout = Text(layout.Readout, fontPx, CurveBrush, pixelsPerDip);
			readoutWidth = readout.Width;
			dc.DrawText(readout, new Point(w - readoutWidth - 8, layout.TitleTop));
		}
		FormattedText title = Text(layout.Title, fontPx, TitleBrush, pixelsPerDip);
		title.MaxTextWidth = Math.Max(10, w - readoutWidth - 16);
		title.Trimming = TextTrimming.CharacterEllipsis;
		dc.DrawText(title, new Point(ProbeGraph.EdgePad + 3, layout.TitleTop));
		dc.Pop();
	}

	/// <summary>A frozen pen of a brush and a thickness, made once per pair; the thicknesses are the hairlines of the scales in use. Any thread.</summary>
	private static Pen PenOf(Brush brush, double thickness)
	{
		lock (Pens)
		{
			if (!Pens.TryGetValue((brush, thickness), out Pen? pen))
			{
				pen = Freeze(new Pen(brush, thickness));
				Pens[(brush, thickness)] = pen;
			}
			return pen;
		}
	}

	/// <summary>Strokes the curve clipped to the plot area; a single point is a small dot.</summary>
	private static void DrawCurve(DrawingContext dc, ProbeGraphLayout layout)
	{
		if (layout.Curve.Length == 0)
		{
			return;
		}
		if (layout.Curve.Length == 1)
		{
			dc.DrawEllipse(CurveBrush, null, new Point(layout.Curve[0].X, layout.Curve[0].Y), 2, 2);
			return;
		}
		var geometry = new StreamGeometry();
		using (StreamGeometryContext context = geometry.Open())
		{
			context.BeginFigure(new Point(layout.Curve[0].X, layout.Curve[0].Y), false, false);
			for (int i = 1; i < layout.Curve.Length; i++)
			{
				context.LineTo(new Point(layout.Curve[i].X, layout.Curve[i].Y), true, false);
			}
		}
		geometry.Freeze();
		dc.PushClip(new RectangleGeometry(new Rect(layout.PlotX, layout.PlotY, layout.PlotWidth, layout.PlotHeight)));
		dc.DrawGeometry(null, CurvePen(layout.LineWidth), geometry);
		dc.Pop();
	}

	/// <summary>One line of text in the graph font.</summary>
	// cached (2026-10-16): built afresh, a draw of the boxes cost over 2 ms per picture, mostly FormattedText, and the
	// boxes are drawn on every position change and probe update; the readout changes per frame, so the cache is
	// cleared when it grows large; the culture is part of the key
	private static readonly Dictionary<(string Text, double Size, Brush Brush, double Dpi, string Culture), FormattedText> TextCache = new();
	private static readonly Dictionary<double, Pen> CurvePens = new();
	private const int TextCacheLimit = 4096;

	public static FormattedText Text(string text, double size, Brush brush, double pixelsPerDip)
	{
		var key = (text, size, brush, pixelsPerDip, CultureInfo.CurrentUICulture.Name);
		if (!TextCache.TryGetValue(key, out FormattedText? formatted))
		{
			if (TextCache.Count >= TextCacheLimit)
			{
				TextCache.Clear();
			}
			formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface, size, brush, pixelsPerDip) { MaxLineCount = 1 };
			TextCache[key] = formatted;
		}
		return formatted;
	}

	private static Pen CurvePen(double width)
	{
		if (!CurvePens.TryGetValue(width, out Pen? pen))
		{
			pen = new Pen(CurveBrush, width) { LineJoin = PenLineJoin.Round };
			pen.Freeze();
			CurvePens[width] = pen;
		}
		return pen;
	}

	private static T Freeze<T>(T freezable) where T : Freezable
	{
		freezable.Freeze();
		return freezable;
	}
}
