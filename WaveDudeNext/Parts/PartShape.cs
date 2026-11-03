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

using System.Runtime.CompilerServices;
using WaveDudeNext.Model;

namespace WaveDudeNext.Parts;

/// <summary>
/// The geometry of one <see cref="OpticalPart"/> as the painter sees it: a point of the canvas is mapped into the
/// part's frame (the centre at the canvas point (X, Y), the top-left corner of pixel (X, Y), so a part of even size
/// covers whole pixels; x' along the axis turned by the angle, counter-clockwise on the screen, y' across) and classified by <see cref="Depth"/>, how far inside the outline it lies, and
/// <see cref="BodyIndex"/>, the refractive index there. <see cref="IndexAt(double, double, double)"/> combines both with the coating band.
/// Immutable once built from a part; any thread.
/// </summary>
public abstract class PartShape
{
	private readonly double _centreX;
	private readonly double _centreY;
	private readonly double _cos;
	private readonly double _sin;

	/// <param name="part">The part; its position and angle are read once.</param>
	protected PartShape(OpticalPart part)
	{
		_centreX = part.X;
		_centreY = part.Y;
		double radians = part.Angle * Math.PI / 180.0;
		_cos = Math.Cos(radians);
		_sin = Math.Sin(radians);
		if (part is CoatedPart coated)
		{
			_profile = Enum.IsDefined(coated.CoatingProfile) ? coated.CoatingProfile : CoatedPart.DefaultCoatingProfile;
			_frontCoated = coated.FrontCoated;
			_backCoated = coated.BackCoated;
			_sidesCoated = coated.SidesCoated;
		}
		else
		{
			// a fibre, a gate: their bands are their own designs, a layer on every face they coat
			_profile = CoatingProfile.Layer;
			_frontCoated = _backCoated = _sidesCoated = true;
		}
	}

	private readonly CoatingProfile _profile;
	private readonly bool _frontCoated;
	private readonly bool _backCoated;
	private readonly bool _sidesCoated;

	/// <summary>cos 45°: the bound on the outward normal's x' component that makes a face a front or a back.</summary>
	protected const double Cos45 = 0.70710678118654752;

	/// <summary>A face of the outline by its outward normal in the part frame (<see cref="CoatedPart"/>): within 45° of −x' the front, of +x' the back, else a side.</summary>
	protected enum Face
	{
		/// <summary>Turned toward a source on the left (−x').</summary>
		Front,
		/// <summary>Turned away from it (+x').</summary>
		Back,
		/// <summary>Neither: a lens's rim, a slab's narrow ends.</summary>
		Sides,
	}

	/// <summary>
	/// The face nearest the point (x', y') of the part frame. Asked only inside the band of a part that leaves a face
	/// bare, so a shape without faces to tell apart answers <see cref="Face.Front"/>.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	protected virtual Face FaceAt(double x, double y) => Face.Front;

	/// <summary>Whether the band at (x', y') is painted: the flag of the nearest face, without the search when every face is coated.</summary>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	private bool Coated(double x, double y) =>
		_frontCoated && _backCoated && _sidesCoated || FaceAt(x, y) switch { Face.Front => _frontCoated, Face.Back => _backCoated, _ => _sidesCoated };

	/// <summary>
	/// The share of the index step a graded band has reached at <paramref name="t"/>, the depth inside the outline over the
	/// band's thickness: a raised cosine, (1 − cos πt) / 2. Measured 2026-10-29 with a plane pulse on a deep slab against a
	/// mirror's reflection (the table is under "Coatings" in OpticalParts.md): of linear, smooth-step, quintic and raised
	/// cosine bands the linear one is best at half a wavelength, the raised cosine best or tied at one and within a few
	/// tenths of a percent of the best at two, where the linear ramp reflects three to four times as much.
	/// </summary>
	/// <param name="t">Depth over thickness; clamped to 0..1.</param>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	public static double GradedProfile(double t)
	{
		t = Math.Clamp(t, 0.0, 1.0);
		return (1.0 - Math.Cos(Math.PI * t)) / 2.0;
	}

	/// <summary>
	/// The shape of <paramref name="part"/>, or null when it has nothing to paint (an outline of fewer than three
	/// points, a path of fewer than two). The part is read as it is, out-of-range values guarded here rather than
	/// written back: a form edit reaches the painter before the model's <see cref="OpticalPart.Normalize"/> runs,
	/// and normalising from inside a change handler would re-enter it.
	/// </summary>
	public static PartShape? For(OpticalPart part)
	{
		return part switch
		{
			LensPart lens => new LensShape(lens),
			ShapePart shape => shape.Vertices().Length < 3 ? null : new VertexShape(shape),
			FiberPart fiber => FiberPart.Flatten(fiber.Nodes) is { Length: >= 2 } path ? new FiberShape(fiber, path) : null,
			BraggReflectorPart bragg => new BraggShape(bragg),
			GrinLensPart grin => new GrinShape(grin),
			LogicGatePart gate => gate.Design is { } design ? new LogicGateShape(gate, design) : null,
			ZonePlatePart plate => double.IsFinite(plate.Wavelength) && plate.Wavelength > 0.0 ? new ZonePlateShape(plate) : null,
			_ => null,
		};
	}

	/// <summary>Whether the part is a mirror: painted as red 0 by the pixel-centre test, no index, no coating.</summary>
	public abstract bool IsMirror { get; }

	/// <summary>Thickness of the anti-reflection band inside the outline in pixels; 0 for none.</summary>
	protected abstract int Coating { get; }

	/// <summary>The coating band's thickness in pixels, for the painter's shortcut past pixels well inside the body or the band.</summary>
	public int CoatingWidth => Coating;

	/// <summary>The absorption at full depth, 0 to 255 as the blue channel; 0 for none (and always for a mirror).</summary>
	public virtual int Absorption => 0;

	/// <summary>Depth in pixels over which the absorption rises from 0 at the outline to <see cref="Absorption"/>, at least 1.</summary>
	public virtual int AbsorptionRamp => 1;

	/// <summary>
	/// The share of the full absorption at <paramref name="t"/>, the depth inside the outline over the ramp: 0 at the
	/// outline, 1 from the ramp's end on, t² between. Chosen by measuring what a ramp reflects (2026-10-20, a pulse on a
	/// deep absorbing slab against a mirror slab): a jump in damping reflects like a jump in index, and of linear,
	/// quadratic, cubic, smooth-step, error-function and √ ramps the quadratic did best over the strengths that absorb,
	/// cubic close behind; the figures are under "Absorption" in OpticalParts.md.
	/// </summary>
	/// <param name="t">Depth over ramp; clamped to 0..1.</param>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	public static double AbsorptionProfile(double t)
	{
		t = Math.Clamp(t, 0.0, 1.0);
		return t * t;
	}

	/// <summary>
	/// The share of <see cref="Absorption"/> the painter gives the canvas point (<paramref name="canvasX"/>,
	/// <paramref name="canvasY"/>), which lies <paramref name="depth"/> pixels inside the outline: by default
	/// <see cref="AbsorptionProfile"/> of the depth over <paramref name="ramp"/>, rising inward. A shape that absorbs at
	/// its rim instead (a logic gate's cladding) overrides it.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	public virtual double AbsorptionShareAt(double canvasX, double canvasY, double depth, double ramp) =>
		AbsorptionProfile(ramp > 0.0 ? depth / ramp : 1.0);

	/// <summary>Whether the part paints a refractive index into the red channel; false for a part that only absorbs (a zone plate), whose red stays as it was.</summary>
	public virtual bool PaintsIndex => true;

	/// <summary>
	/// Whether the index steps somewhere inside the outline (a Bragg stack's layers, a fibre's core), so a pixel near such
	/// an edge needs its samples (<see cref="InnerEdgeDistance"/> says how near); false where the inside is one index or
	/// varies smoothly (the GRIN profile).
	/// </summary>
	public virtual bool HasInnerEdges => false;

	/// <summary>
	/// How far the part-frame point lies from the nearest edge inside the outline where the index steps, in pixels:
	/// infinity for a shape without (<see cref="HasInnerEdges"/> false), so the painter samples a pixel once when neither
	/// the outline nor an inner edge is within reach (2026-11-03: every pixel of a fibre had taken 16 samples, 400 ms for
	/// Example 23's wide fibre). A shape with inner edges that does not say returns 0, which keeps every inside pixel sampled.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	protected virtual double InnerEdgeDistance(double x, double y) => HasInnerEdges ? 0.0 : double.PositiveInfinity;

	/// <summary>How far inside the outline the canvas point lies, in pixels (negative outside): the exact distance for straight and circular edges, so the painter can tell a pixel wholly inside or outside from one sample.</summary>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	public double DepthAt(double canvasX, double canvasY)
	{
		(double x, double y) = ToFrame(canvasX, canvasY);
		return Depth(x, y);
	}

	/// <summary><see cref="DepthAt"/> and the distance to the nearest inner edge (<see cref="InnerEdgeDistance"/>) of the canvas point, in one mapping into the part frame.</summary>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	public (double Depth, double InnerEdge) DepthsAt(double canvasX, double canvasY)
	{
		(double x, double y) = ToFrame(canvasX, canvasY);
		return (Depth(x, y), InnerEdgeDistance(x, y));
	}

	/// <summary>Maps a canvas point into the part frame: the inverse of the composite source's turn (x = dx cos + dy sin, y = dy cos - dx sin), the transpose.</summary>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	private (double X, double Y) ToFrame(double canvasX, double canvasY)
	{
		double ox = canvasX - _centreX;
		double oy = canvasY - _centreY;
		return (ox * _cos - oy * _sin, ox * _sin + oy * _cos);
	}

	/// <summary>Farthest the outline reaches from the centre, in pixels (a bound, not necessarily tight).</summary>
	protected abstract double Extent { get; }

	/// <summary>How far inside the outline the point (x', y') of the part frame lies, in pixels; negative outside. Exact for straight and circular edges.</summary>
	protected abstract double Depth(double x, double y);

	/// <summary>The refractive index at (x', y') of the part frame, the point being inside; a mirror returns 0.</summary>
	protected abstract double BodyIndex(double x, double y);

	/// <summary>The pixel rectangle (inclusive) the shape can touch, clipped to the canvas; empty (x0 &gt; x1) when it lies outside.</summary>
	public (int X0, int Y0, int X1, int Y1) Bounds(int width, int height)
	{
		double extent = Extent + 1.0;
		int x0 = Math.Max(0, (int)Math.Floor(_centreX - extent));
		int y0 = Math.Max(0, (int)Math.Floor(_centreY - extent));
		int x1 = Math.Min(width - 1, (int)Math.Ceiling(_centreX + extent));
		int y1 = Math.Min(height - 1, (int)Math.Ceiling(_centreY + extent));
		return (x0, y0, x1, y1);
	}

	/// <summary>The index at a canvas point as <see cref="IndexAt(double, double, double)"/> gives it over a medium of index 1.</summary>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	public double? IndexAt(double canvasX, double canvasY) => IndexAt(canvasX, canvasY, 1.0);

	/// <summary>
	/// The refractive index the part gives the canvas point (<paramref name="canvasX"/>, <paramref name="canvasY"/>)
	/// (continuous coordinates, pixel (x, y) spanning [x, x + 1)): null outside the outline; in the coating band of a
	/// coated face, √n rounded to 0.01 for a layer, or for a graded band the index risen from <paramref name="outside"/>
	/// toward the body's by <see cref="GradedProfile"/> of the depth over the thickness; else the body index; 0 inside a mirror.
	/// </summary>
	/// <param name="canvasX">Canvas x, continuous.</param>
	/// <param name="canvasY">Canvas y, continuous.</param>
	/// <param name="outside">The index the pixel had before the part, the medium a graded band rises from.</param>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	public double? IndexAt(double canvasX, double canvasY, double outside)
	{
		(double x, double y) = ToFrame(canvasX, canvasY);
		double depth = Depth(x, y);
		if (depth < 0.0)
		{
			return null;
		}
		if (IsMirror)
		{
			return 0.0;
		}
		double index = BodyIndex(x, y);
		if (Coating <= 0 || depth >= Coating || !Coated(x, y))
		{
			return index;
		}
		return _profile == CoatingProfile.Graded ? outside + (index - outside) * GradedProfile(depth / Coating) : Math.Round(Math.Sqrt(index), 2);
	}

	/// <summary>A refractive index within the part range.</summary>
	protected static double GuardIndex(double index) => double.IsFinite(index) ? Math.Clamp(index, OpticalPart.MinIndex, OpticalPart.MaxIndex) : OpticalPart.MinIndex;

	/// <summary>An absorption within range.</summary>
	protected static int GuardAbsorption(int absorption) => Math.Clamp(absorption, 0, OpticalPart.MaxAbsorption);

	/// <summary>An absorption ramp within range.</summary>
	protected static int GuardAbsorptionRamp(int ramp) => Math.Clamp(ramp, 1, OpticalPart.MaxAbsorptionRamp);

	/// <summary>A coating thickness within range.</summary>
	protected static int GuardCoating(int coating) => Math.Clamp(coating, 0, OpticalPart.MaxCoating);

	/// <summary>Signed distance of (x, y) inside a simple polygon: the distance to the nearest edge, negative outside (even-odd rule).</summary>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	protected static double PolygonDepth(ReadOnlySpan<(double X, double Y)> vertices, double x, double y)
	{
		bool inside = false;
		double nearest = double.PositiveInfinity;
		for (int i = 0, j = vertices.Length - 1; i < vertices.Length; j = i++)
		{
			(double ax, double ay) = vertices[j];
			(double bx, double by) = vertices[i];
			if (ay > y != by > y)
			{
				double crossX = ax + (y - ay) / (by - ay) * (bx - ax);
				if (x < crossX)
				{
					inside = !inside;
				}
			}
			nearest = Math.Min(nearest, SegmentDistance(ax, ay, bx, by, x, y));
		}
		return inside ? nearest : -nearest;
	}

	/// <summary>Distance from (x, y) to the segment a–b.</summary>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	protected static double SegmentDistance(double ax, double ay, double bx, double by, double x, double y)
	{
		double dx = bx - ax;
		double dy = by - ay;
		double length2 = dx * dx + dy * dy;
		double t = length2 > 0.0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / length2, 0.0, 1.0) : 0.0;
		double px = ax + t * dx - x;
		double py = ay + t * dy - y;
		return Math.Sqrt(px * px + py * py);
	}

	private sealed class LensShape : PartShape
	{
		private readonly double _half;
		private readonly double _vertex1;
		private readonly double _vertex2;
		private readonly double _r1;
		private readonly double _r2;
		private readonly double _index;
		private readonly double _extent;

		public LensShape(LensPart lens) : base(lens)
		{
			_half = Math.Max(1, lens.Diameter) / 2.0;
			_vertex1 = -Math.Max(1, lens.Thickness) / 2.0;
			_vertex2 = -_vertex1;
			(_r1, _r2) = LensPart.FitRadii(lens.Diameter, lens.Thickness, lens.R1, lens.R2);
			_index = GuardIndex(lens.RefractiveIndex);
			IsMirror = lens.Mirror;
			Coating = lens.Mirror ? 0 : GuardCoating(lens.Coating);
			Absorption = lens.Mirror ? 0 : GuardAbsorption(lens.Absorption);
			AbsorptionRamp = GuardAbsorptionRamp(lens.AbsorptionRamp);
			// the surfaces stay within the vertex and its sag at the lens edge, convex or concave
			double reach = _vertex2 + Math.Max(Sag(_r1), Sag(_r2));
			_extent = Math.Sqrt(reach * reach + _half * _half);
		}

		public override bool IsMirror { get; }
		protected override int Coating { get; }
		public override int Absorption { get; }
		public override int AbsorptionRamp { get; }
		protected override double Extent => _extent;
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double BodyIndex(double x, double y) => _index;

		/// <summary>How far a surface of radius <paramref name="radius"/> bows out beyond its vertex at the lens edge.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		private double Sag(double radius) => LensPart.Sag(radius, _half);

		/// <summary>The least of: inside the first surface, inside the second, within the half-diameter.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double Depth(double x, double y) =>
			Math.Min(Math.Min(SurfaceDepth(x, y, _vertex1, _r1, -1), SurfaceDepth(x, y, _vertex2, _r2, +1)), _half - Math.Abs(y));

		/// <summary>The nearest of the three: the first surface is the front, the second the back, the rim the sides.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override Face FaceAt(double x, double y)
		{
			double first = SurfaceDepth(x, y, _vertex1, _r1, -1);
			double second = SurfaceDepth(x, y, _vertex2, _r2, +1);
			double rim = _half - Math.Abs(y);
			return first <= second && first <= rim ? Face.Front : second <= rim ? Face.Back : Face.Sides;
		}

		/// <summary>
		/// How far inside one surface (x', y') lies: the surface has its vertex at <paramref name="vertex"/> on the axis,
		/// the lens interior on the side opposite to <paramref name="side"/> (-1 for the first surface, whose interior is
		/// toward +x'). A radius of 0 is flat; the circle's centre is at vertex + radius, so the surface is convex when
		/// radius and side have opposite signs. Distances are to the circle, so the coating band keeps its thickness
		/// along the normal.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		private static double SurfaceDepth(double x, double y, double vertex, double radius, int side)
		{
			if (radius == 0.0)
			{
				return (x - vertex) * -side;
			}
			double centre = vertex + radius;
			double along = (x - centre) * -side;        // positive past the centre in the interior direction
			double distance = Math.Sqrt((x - centre) * (x - centre) + y * y);
			double magnitude = Math.Abs(radius);
			if (radius * side < 0.0)
			{
				// convex: inside the circle, or beyond its centre (the arc is only the outer half)
				return along > 0.0 ? magnitude : magnitude - distance;
			}
			// concave: outside the circle and on the interior side of its centre
			return along < 0.0 ? double.NegativeInfinity : distance - magnitude;
		}
	}

	/// <summary>A polygon, rectangle, triangle or outline: the part's vertices as a simple polygon.</summary>
	private sealed class VertexShape : PartShape
	{
		private readonly (double X, double Y)[] _vertices;
		private readonly Face[] _faces;
		private readonly double _index;
		private readonly double _extent;

		public VertexShape(ShapePart shape) : base(shape)
		{
			_vertices = shape.Vertices();
			_index = GuardIndex(shape.RefractiveIndex);
			IsMirror = shape.Mirror;
			Coating = shape.Mirror ? 0 : GuardCoating(shape.Coating);
			Absorption = shape.Mirror ? 0 : GuardAbsorption(shape.Absorption);
			AbsorptionRamp = GuardAbsorptionRamp(shape.AbsorptionRamp);
			_extent = _vertices.Max(v => Math.Sqrt(v.X * v.X + v.Y * v.Y));
			// each edge's face by its outward normal: the normal that points out of the polygon at the edge's midpoint
			_faces = new Face[_vertices.Length];
			for (int i = 0, j = _vertices.Length - 1; i < _vertices.Length; j = i++)
			{
				(double ax, double ay) = _vertices[j];
				(double bx, double by) = _vertices[i];
				double dx = bx - ax;
				double dy = by - ay;
				double length = Math.Sqrt(dx * dx + dy * dy);
				if (length == 0.0)
				{
					_faces[i] = Face.Sides;
					continue;
				}
				double nx = dy / length;
				double ny = -dx / length;
				if (PolygonDepth(_vertices, (ax + bx) / 2.0 + nx * 0.01, (ay + by) / 2.0 + ny * 0.01) > 0.0)
				{
					nx = -nx;
				}
				_faces[i] = nx < -Cos45 ? Face.Front : nx > Cos45 ? Face.Back : Face.Sides;
			}
		}

		/// <summary>The face of the edge nearest the point.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override Face FaceAt(double x, double y)
		{
			int nearest = 0;
			double best = double.PositiveInfinity;
			for (int i = 0, j = _vertices.Length - 1; i < _vertices.Length; j = i++)
			{
				double distance = SegmentDistance(_vertices[j].X, _vertices[j].Y, _vertices[i].X, _vertices[i].Y, x, y);
				if (distance < best)
				{
					best = distance;
					nearest = i;
				}
			}
			return _faces[nearest];
		}

		public override bool IsMirror { get; }
		protected override int Coating { get; }
		public override int Absorption { get; }
		public override int AbsorptionRamp { get; }
		protected override double Extent => _extent;
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double Depth(double x, double y) => PolygonDepth(_vertices, x, y);
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double BodyIndex(double x, double y) => _index;
	}

	/// <summary>
	/// A fibre: the tube of the outer radius around the path's chords (<see cref="FiberPart.Flatten"/>), the core the
	/// inner tube, the ends flat or round. Flat (2026-10-22, the user's request): the path is cut by the plane across
	/// the curve's own direction at each endpoint (the handle's, which a bend tilts the first chord away from by a
	/// degree or two), so a point before the start or beyond the end lies outside however close to the line, and the
	/// depth inside counts the end face too, which the coating band then follows. The depth is the largest over the
	/// chords: exact inside, and outside a point's nearest chord gives the distance (a corner of a flat end makes it
	/// an under-estimate, which keeps the painter's single-sample shortcut safe). The chords are bucketed on a grid
	/// of <see cref="CellSize"/> cells over the part frame, each cell listing the chords whose tube can reach it, so
	/// a sample visits a handful of chords instead of all (2026-10-22: an S-bend across a Full HD canvas, 156 chords,
	/// took 1.3 s a paint without this); a point in a cell no chord reaches is at least <see cref="Margin"/> outside.
	/// </summary>
	private sealed class FiberShape : PartShape
	{
		/// <summary>Side of a grid cell in pixels of the part frame.</summary>
		private const int CellSize = 32;
		/// <summary>How far beyond the tube a chord is still listed for a cell, in pixels: more than the painter's sample reach, so an empty cell may report this depth outside.</summary>
		private const double Margin = 2.0;

		private readonly (double X, double Y)[] _path;
		private readonly bool _flat;
		private readonly (double X, double Y) _startTangent;
		private readonly (double X, double Y) _endTangent;
		private readonly double _coreRadius;
		private readonly double _outerRadius;
		private readonly double _coreIndex;
		private readonly double _claddingIndex;
		private readonly double _extent;
		private readonly double _gridX;
		private readonly double _gridY;
		private readonly int _columns;
		private readonly int _rows;
		private readonly int[] _cellStart;
		private readonly int[] _cellChords;
		private readonly int _startZone;
		private readonly int _endZone;

		public FiberShape(FiberPart fiber, (double X, double Y)[] path) : base(fiber)
		{
			_path = path;
			_flat = fiber.Ends == FiberEnds.Flat;
			FiberNode first = fiber.Nodes[0];
			FiberNode last = fiber.Nodes[^1];
			_startTangent = Unit(first.OutX, first.OutY) ?? Unit(path[1].X - path[0].X, path[1].Y - path[0].Y) ?? (1.0, 0.0);
			_endTangent = Unit(-last.InX, -last.InY) ?? Unit(path[^1].X - path[^2].X, path[^1].Y - path[^2].Y) ?? (1.0, 0.0);
			_coreRadius = Math.Max(1, fiber.CoreDiameter) / 2.0;
			_outerRadius = _coreRadius + Math.Max(0, fiber.CladdingThickness);
			_coreIndex = GuardIndex(fiber.CoreIndex);
			_claddingIndex = fiber.CladdingThickness > 0 ? GuardIndex(fiber.CladdingIndex) : _coreIndex;
			HasInnerEdges = fiber.CladdingThickness > 0;
			Coating = GuardCoating(fiber.Coating);
			// the cladding's absorbing band: a hump of 2 × ramp at the outer part of the cladding, squeezed into a thinner one; nothing on a bare core
			Absorption = fiber.CladdingThickness > 0 ? GuardAbsorption(fiber.Absorption) : 0;
			_band = Math.Min(GuardAbsorptionRamp(fiber.AbsorptionRamp), Math.Max(0, fiber.CladdingThickness) / 2.0);
			_extent = _path.Max(v => Math.Sqrt(v.X * v.X + v.Y * v.Y)) + _outerRadius;
			// the chords a flat face cuts: every chord is a capsule, and one starting within the outer radius of an
			// end (twice that, for a bend) reaches past the face with its round cap
			_startZone = 1;
			_endZone = _path.Length - 1;
			double arc = 0.0;
			for (int i = 1; i < _path.Length && arc <= 2.0 * _outerRadius; i++)
			{
				_startZone = i;
				arc += Math.Sqrt(Square(_path[i].X - _path[i - 1].X) + Square(_path[i].Y - _path[i - 1].Y));
			}
			arc = 0.0;
			for (int i = _path.Length - 1; i >= 1 && arc <= 2.0 * _outerRadius; i--)
			{
				_endZone = i;
				arc += Math.Sqrt(Square(_path[i].X - _path[i - 1].X) + Square(_path[i].Y - _path[i - 1].Y));
			}

			// the grid over the path's box widened by the tube and the margin; a chord is listed in every cell its
			// widened box touches: the counts first, then the lists laid end to end behind the starts
			double reach = _outerRadius + Margin;
			_gridX = _path.Min(v => v.X) - reach;
			_gridY = _path.Min(v => v.Y) - reach;
			_columns = Math.Max(1, (int)Math.Ceiling((_path.Max(v => v.X) + reach - _gridX) / CellSize));
			_rows = Math.Max(1, (int)Math.Ceiling((_path.Max(v => v.Y) + reach - _gridY) / CellSize));
			var counts = new int[_columns * _rows];
			VisitCells((cell, _) => counts[cell]++);
			_cellStart = new int[counts.Length + 1];
			for (int c = 0; c < counts.Length; c++)
			{
				_cellStart[c + 1] = _cellStart[c] + counts[c];
			}
			_cellChords = new int[_cellStart[^1]];
			var fill = (int[])_cellStart.Clone();
			VisitCells((cell, chord) => _cellChords[fill[cell]++] = chord);

			void VisitCells(Action<int, int> visit)
			{
				for (int i = 1; i < _path.Length; i++)
				{
					(int c0, int r0) = CellOf(Math.Min(_path[i - 1].X, _path[i].X) - reach, Math.Min(_path[i - 1].Y, _path[i].Y) - reach);
					(int c1, int r1) = CellOf(Math.Max(_path[i - 1].X, _path[i].X) + reach, Math.Max(_path[i - 1].Y, _path[i].Y) + reach);
					for (int r = r0; r <= r1; r++)
					{
						for (int c = c0; c <= c1; c++)
						{
							visit(r * _columns + c, i);
						}
					}
				}
			}
		}

		public override bool IsMirror => false;
		protected override int Coating { get; }
		private readonly double _band;
		public override int Absorption { get; }
		public override int AbsorptionRamp => Math.Max(1, (int)Math.Ceiling(_band));

		/// <summary>
		/// The share of the peak absorption at a canvas point: by the distance r from the path, nothing within the core and
		/// the inner cladding, rising as the quadratic profile over the band's half-width from the band's inner edge at
		/// outer radius − 2 × band to the peak, and falling the same way to nothing at the outer edge; the depth given by
		/// the painter is not used, so the band runs up to an end face and stops there instead of wrapping it.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		public override double AbsorptionShareAt(double canvasX, double canvasY, double depth, double ramp)
		{
			if (_band <= 0.0)
			{
				return 0.0;
			}
			(double x, double y) = ToFrame(canvasX, canvasY);
			double r = PathDistance(x, y);
			double inner = _outerRadius - 2.0 * _band;
			if (r <= inner || r >= _outerRadius)
			{
				return 0.0;
			}
			double t = (r - inner) / _band;
			return AbsorptionProfile(t <= 1.0 ? t : 2.0 - t);
		}

		/// <summary>
		/// The start face is the front, the end face the back, the tube the sides: with flat ends, whichever of the three
		/// the point is nearest to inside; with round ends, the caps before the start and beyond the end.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override Face FaceAt(double x, double y)
		{
			int last = _path.Length - 1;
			double startDepth = (x - _path[0].X) * _startTangent.X + (y - _path[0].Y) * _startTangent.Y;
			double endDepth = (_path[last].X - x) * _endTangent.X + (_path[last].Y - y) * _endTangent.Y;
			if (!_flat)
			{
				return startDepth < 0.0 ? Face.Front : endDepth < 0.0 ? Face.Back : Face.Sides;
			}
			double side = _outerRadius - PathDistance(x, y);
			return startDepth <= endDepth && startDepth <= side ? Face.Front : endDepth <= side ? Face.Back : Face.Sides;
		}

		/// <summary>The core's edge steps inside the outline, so the painter samples the pixels near it (without this the core edge came out unsmoothed, 2026-10-22; every pixel of the tube until 2026-11-03).</summary>
		public override bool HasInnerEdges { get; }

		/// <summary>The core boundary's distance, |r − core radius| by the distance from the path; none without a cladding.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double InnerEdgeDistance(double x, double y) => HasInnerEdges ? Math.Abs(PathDistance(x, y) - _coreRadius) : double.PositiveInfinity;
		protected override double Extent => _extent;
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double BodyIndex(double x, double y) => PathDistance(x, y) <= _coreRadius ? _coreIndex : _claddingIndex;

		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double Depth(double x, double y)
		{
			(int from, int to) = ChordsNear(x, y);
			if (from == to)
			{
				return -Margin;
			}
			int last = _path.Length - 1;
			// depth from the end faces: how far past the start, and how far short of the end, along the tangents there
			double startDepth = _flat ? (x - _path[0].X) * _startTangent.X + (y - _path[0].Y) * _startTangent.Y : double.PositiveInfinity;
			double endDepth = _flat ? (_path[last].X - x) * _endTangent.X + (_path[last].Y - y) * _endTangent.Y : double.PositiveInfinity;
			double best = double.NegativeInfinity;
			for (int k = from; k < to; k++)
			{
				int i = _cellChords[k];
				(double ax, double ay) = _path[i - 1];
				(double bx, double by) = _path[i];
				double dx = bx - ax;
				double dy = by - ay;
				double length = Math.Sqrt(dx * dx + dy * dy);
				double along = ((x - ax) * dx + (y - ay) * dy) / length;   // from a, in pixels
				double depth;
				if (along >= 0.0 && along <= length || i == 1 && startDepth < 0.0 || i == last && endDepth < 0.0)
				{
					// across the chord's span, or before a flat start or beyond a flat end: the distance to the line
					double perpendicular = Math.Abs((x - ax) * dy - (y - ay) * dx) / length;
					depth = _outerRadius - perpendicular;
				}
				else
				{
					// beyond a chord's end inside the path: the round joint to the next chord, or a round end
					depth = _outerRadius - SegmentDistance(ax, ay, bx, by, x, y);
				}
				if (i <= _startZone)
				{
					depth = Math.Min(depth, startDepth);
				}
				if (i >= _endZone)
				{
					depth = Math.Min(depth, endDepth);
				}
				best = Math.Max(best, depth);
			}
			// a chord the cell does not list is more than the margin outside, so it never sets the maximum
			return Math.Max(best, -Margin);
		}

		/// <summary>Distance to the nearest chord of the path among those listed for the point's cell (exact within the tube, where the nearest chord is always listed).</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		private double PathDistance(double x, double y)
		{
			(int from, int to) = ChordsNear(x, y);
			double nearest = double.PositiveInfinity;
			for (int k = from; k < to; k++)
			{
				int i = _cellChords[k];
				nearest = Math.Min(nearest, SegmentDistance(_path[i - 1].X, _path[i - 1].Y, _path[i].X, _path[i].Y, x, y));
			}
			return nearest;
		}

		/// <summary>The range in the chord lists for the cell of (x', y'): empty outside the grid.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		private (int From, int To) ChordsNear(double x, double y)
		{
			int column = (int)Math.Floor((x - _gridX) / CellSize);
			int row = (int)Math.Floor((y - _gridY) / CellSize);
			if (column < 0 || column >= _columns || row < 0 || row >= _rows)
			{
				return (0, 0);
			}
			int cell = row * _columns + column;
			return (_cellStart[cell], _cellStart[cell + 1]);
		}

		/// <summary>The cell holding (x', y'), clamped onto the grid.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		private (int Column, int Row) CellOf(double x, double y) =>
			(Math.Clamp((int)Math.Floor((x - _gridX) / CellSize), 0, _columns - 1), Math.Clamp((int)Math.Floor((y - _gridY) / CellSize), 0, _rows - 1));

		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		private static double Square(double v) => v * v;

		/// <summary>The direction (x, y) as a unit vector, or null for a zero vector.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		private static (double X, double Y)? Unit(double x, double y)
		{
			double length = Math.Sqrt(x * x + y * y);
			return length > 0.0 ? (x / length, y / length) : null;
		}
	}

	private sealed class BraggShape : PartShape
	{
		private readonly double _halfLength;
		private readonly double _halfWidth;
		private readonly double _period;
		private readonly double _thickness1;
		private readonly double _index1;
		private readonly double _index2;

		public BraggShape(BraggReflectorPart bragg) : base(bragg)
		{
			_thickness1 = Math.Max(1, bragg.Thickness1);
			_period = _thickness1 + Math.Max(1, bragg.Thickness2);
			_halfLength = Math.Clamp(bragg.Pairs, 1, BraggReflectorPart.MaxPairs) * _period / 2.0;
			_halfWidth = Math.Max(1, bragg.Width) / 2.0;
			_index1 = GuardIndex(bragg.Index1);
			_index2 = GuardIndex(bragg.Index2);
		}

		public override bool IsMirror => false;
		protected override int Coating => 0;
		public override bool HasInnerEdges => true;
		protected override double Extent => Math.Sqrt(_halfLength * _halfLength + _halfWidth * _halfWidth);

		/// <summary>The nearest layer boundary along x': a pair's edges at 0, its first thickness and the period.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double InnerEdgeDistance(double x, double y)
		{
			double along = (x + _halfLength) % _period;
			return Math.Min(along, Math.Min(Math.Abs(along - _thickness1), _period - along));
		}
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double Depth(double x, double y) => Math.Min(_halfLength - Math.Abs(x), _halfWidth - Math.Abs(y));

		/// <summary>The first layer of a pair starts at -L/2 and the pairs repeat along +x'.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double BodyIndex(double x, double y)
		{
			double along = (x + _halfLength) % _period;
			return along < _thickness1 ? _index1 : _index2;
		}
	}

	private sealed class GrinShape : PartShape
	{
		private readonly double _halfLength;
		private readonly double _halfDiameter;
		private readonly double _centreIndex;
		private readonly double _edgeIndex;

		public GrinShape(GrinLensPart grin) : base(grin)
		{
			_halfLength = Math.Max(1, grin.Length) / 2.0;
			_halfDiameter = Math.Max(1, grin.Diameter) / 2.0;
			_centreIndex = GuardIndex(grin.CentreIndex);
			_edgeIndex = GuardIndex(grin.EdgeIndex);
			Coating = GuardCoating(grin.Coating);
		}

		public override bool IsMirror => false;
		protected override int Coating { get; }
		protected override double Extent => Math.Sqrt(_halfLength * _halfLength + _halfDiameter * _halfDiameter);
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double Depth(double x, double y) => Math.Min(_halfLength - Math.Abs(x), _halfDiameter - Math.Abs(y));

		/// <summary>The end faces across the axis are the front (−x') and the back, the long sides the sides.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override Face FaceAt(double x, double y) =>
			_halfLength - Math.Abs(x) <= _halfDiameter - Math.Abs(y) ? (x < 0.0 ? Face.Front : Face.Back) : Face.Sides;

		/// <summary>n(y') = n_c − (n_c − n_e)·(y' / (D/2))².</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double BodyIndex(double x, double y)
		{
			double t = y / _halfDiameter;
			return _centreIndex - (_centreIndex - _edgeIndex) * t * t;
		}
	}

	/// <summary>
	/// A zone plate (<see cref="ZonePlatePart"/>): the plate's rectangle, its blocks absorbing and nothing painted into the
	/// red channel. A block's absorption rises from nothing at the faces across the axis (x' = ±thickness / 2) over the
	/// anti-reflection depth, shortened to half the thickness; its sides toward the open zones are sharp. The mount, when on,
	/// is a block beyond the diameter out to the outer half-width.
	/// </summary>
	private sealed class ZonePlateShape : PartShape
	{
		private readonly ZonePlatePart _plate;
		private readonly double _halfThickness;
		private readonly double _halfDiameter;
		private readonly double _outerHalfWidth;
		private readonly double _ramp;

		public ZonePlateShape(ZonePlatePart plate) : base(plate)
		{
			// a copy, so the painter's threads read values that do not change under them
			_plate = (ZonePlatePart)plate.Clone();
			_halfThickness = Math.Max(1, plate.Thickness) / 2.0;
			_halfDiameter = Math.Max(2, plate.Diameter) / 2.0;
			_outerHalfWidth = plate.OuterHalfWidth;
			_ramp = Math.Min(Math.Max(0, plate.Coating), _halfThickness);
		}

		public override bool IsMirror => false;
		protected override int Coating => 0;
		public override bool PaintsIndex => false;
		public override int Absorption => ZonePlatePart.BlockAbsorption;
		public override int AbsorptionRamp => Math.Max(1, (int)Math.Ceiling(_ramp));
		protected override double Extent => Math.Sqrt(_halfThickness * _halfThickness + _outerHalfWidth * _outerHalfWidth);
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double Depth(double x, double y) => Math.Min(_halfThickness - Math.Abs(x), _outerHalfWidth - Math.Abs(y));
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double BodyIndex(double x, double y) => 1.0;

		/// <summary>Nothing in an open zone; in a block or the mount, rising quadratically from the faces across the axis over the ramp.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		public override double AbsorptionShareAt(double canvasX, double canvasY, double depth, double ramp)
		{
			(double x, double y) = ToFrame(canvasX, canvasY);
			double across = Math.Abs(y);
			if (across > _outerHalfWidth || across <= _halfDiameter && !_plate.IsBlockAt(y))
			{
				return 0.0;
			}
			double inside = _halfThickness - Math.Abs(x);
			return _ramp > 0.0 ? AbsorptionProfile(inside / _ramp) : 1.0;
		}
	}

	/// <summary>
	/// A logic gate (<see cref="LogicGateDesign"/>): two arms mirrored about the axis, each a core in its cladding, cut square at
	/// both ends; the distance from an arm is the offset across the axis times the cosine of the arm's slope, the first-order
	/// distance to the curve (exact on the straight stretches, within a few percent of a pixel in the gentle bends). The
	/// cladding's outer band absorbs, rising toward the rim (<see cref="LogicGateDesign.AbsorberWidth"/>), so light the
	/// sources and the bends send into the cladding is not guided along it by the step to the scene outside. Each square
	/// end the part selects carries its anti-reflection band (√n of the core and of the cladding, as a lens's coating), and
	/// a switched-off output's arm absorbs over its last stretch.
	/// </summary>
	private sealed class LogicGateShape : PartShape
	{
		private readonly LogicGateDesign _design;
		private readonly double _halfLength;
		private readonly double _core;
		private readonly double _outer;
		private readonly double _coreIndex;
		private readonly double _phaseIndex;
		private readonly double _claddingIndex;
		private readonly (double Start, double End) _phase;
		private readonly int _endCoating;
		private readonly bool _inputCoated;
		private readonly bool _outputCoated;
		private readonly bool _xorActive;
		private readonly bool _sumActive;

		public LogicGateShape(LogicGatePart gate, LogicGateDesign design) : base(gate)
		{
			_design = design;
			_halfLength = design.Length / 2.0;
			_core = design.CoreWidth / 2.0;
			_outer = _core + design.CladdingThickness + design.AbsorberWidth;
			_coreIndex = design.CoreIndex;
			_phaseIndex = design.PhaseIndex;
			_claddingIndex = design.CladdingIndex;
			_phase = design.PhaseSection;
			_endCoating = GuardCoating(gate.Coating);
			_inputCoated = gate.InputCoated;
			_outputCoated = gate.OutputCoated;
			_xorActive = gate.UpperOutputActive;
			_sumActive = gate.LowerOutputActive;
		}

		public override bool IsMirror => false;
		protected override int Coating => 0;
		public override int Absorption => LogicGateDesign.Absorption;
		public override int AbsorptionRamp => Math.Max(1, (int)Math.Ceiling(_design.AbsorberWidth));
		public override bool HasInnerEdges => true;
		protected override double Extent => Math.Sqrt(_halfLength * _halfLength + _design.HalfWidth * _design.HalfWidth);

		/// <summary>
		/// Full at the cladding's side rim, falling quadratically to nothing <see cref="AbsorptionRamp"/> pixels in; and in the
		/// arm of a switched-off output, rising quadratically from nothing over the output ramp to full at the right end. The
		/// input ends and an active output do not absorb, so light comes in and goes out.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		public override double AbsorptionShareAt(double canvasX, double canvasY, double depth, double ramp)
		{
			(double x, double y) = ToFrame(canvasX, canvasY);
			(double distance, bool armB) = Nearest(x, y);
			double inside = _outer - distance;
			if (inside < 0.0 || Math.Abs(x) > _halfLength)
			{
				return 0.0;
			}
			double side = AbsorptionProfile(1.0 - inside / _design.AbsorberWidth);
			double output = (armB ? _sumActive : _xorActive) ? 0.0 : AbsorptionProfile((x - _design.OutputRampStart) / _design.OutputRampLength);
			return Math.Max(side, output);
		}

		/// <summary>The distance from the nearer arm's centre line, and whether that arm is B (below the axis).</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		private (double Distance, bool ArmB) Nearest(double x, double y)
		{
			(double offset, double slope) = _design.ArmOffsetAndSlope(x);
			double cos = 1.0 / Math.Sqrt(1.0 + slope * slope);
			return (Math.Abs(Math.Abs(y) - offset) * cos, y > 0.0);
		}

		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double Depth(double x, double y) => Math.Min(_halfLength - Math.Abs(x), _outer - Nearest(x, y).Distance);

		/// <summary>The core, the phase section on its arm, or the cladding; within the coating's thickness of a coated end (the input at −x, the output at +x), √n of it rounded to 0.01.</summary>
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		protected override double BodyIndex(double x, double y)
		{
			(double distance, bool armB) = Nearest(x, y);
			double index = distance > _core ? _claddingIndex
				: armB != _design.PhaseOnArmA && x >= _phase.Start && x < _phase.End ? _phaseIndex
				: _coreIndex;
			bool coated = x < 0.0 ? _inputCoated : _outputCoated;
			return coated && _endCoating > 0 && _halfLength - Math.Abs(x) < _endCoating ? Math.Round(Math.Sqrt(index), 2) : index;
		}
	}
}
