# Optical parts

The reference for the optical parts: how they reach the simulation, what each kind is, and why it was built so, with
the measurements behind the choices. The code's `///` docs carry the details of each member; `CLAUDE.md` holds a summary
and the working rules, which apply here too. The plan this file began as (2026-10-18, stages 1 and 2) is in git history.

Jeroen Vleggaar proposed parts as project objects, added like sources, to demonstrate optics without an external image
editor ("a cavity of mirrors keeps a wave for ever"); the user decided their form. The gates and the zone plate came
from Jeroen's sessions (2026-10-21/22), merged from `jv/optical-logic`.

## Adding a part kind

The class is the kind (2026-10-22: the `PartKind` enum was removed).

1. A concrete subclass of `OpticalPart` (or `ShapePart`, `LogicGatePart`) in `Model/OpticalPart.cs` with `NameStem`
   (the English stem of new names, "Zone plate"), `PrepareNew()` if a new part needs something a loaded one must not
   get (a fibre's default path: `XmlSerializer` appends a file's items to the list it finds), and its properties under
   `CLAUDE.md`'s model conventions (`[Range]` beside every clamp in `Normalize()`).
2. Its shape in `Parts/PartShape.cs` (`PartShape.For`) and, if it has one, its size grip in `SceneOverlay.GripOf` and
   `SetGrip`.
3. An `[XmlElement("Name", typeof(…))]` line on `Project.Parts`: one element name per kind.
4. Its entry in `PartMenuEntries` (`Panes/ProjectTree.xaml`), where the app decides which kinds it offers and in which
   order; both menus take their entries from there.
5. A form in `Views/` with its `DataTemplate` in `PropertiesPane.xaml` (a template for a base class serves its
   subclasses: one for `LogicGatePart`), and its strings (`MenuAdd…`, `Label.`, `Hint.`, `Enum.` for its enums) in all
   four tables.
6. A 16 px drawing in `Themes/Controls.xaml` (2026-10-23): `Icon<Name>` stroked and, for solid parts, `Icon<Name>Fill`,
   `<Name>` the class name without "Part", 1 px lines on half pixels, the ink in rows and columns 0 to 14; the menu
   entry names it (`controls:MenuIcon.Drawing`) and the tree row finds it by the class name (`PartIconConverter`).

`library_check` finds every part class by reflection: it fails one without its file element, one that `AddPart`
cannot add as "<stem> 1", and a clamped number without its `[Range]`. The menu entry, the form and the drawing are not
checked.

## How parts reach the simulation

- **Painted into the scene pixels.** The scene reaches the engines only as BGRA pixels (`SceneData.FromBgra`: red is
  the index, green the image sources, blue the damping). `PartPainter.Paint` paints the enabled parts, in tree order,
  later over earlier, into a copy of the scene image or a blank canvas; the engines, the glowing outline, the export
  outline and the scratch frames see them as part of the scene. `MainWindow.Live`'s `LoadSceneImage` caches the result
  by (image, parts version, size, background), so an unchanged scene stays the same reference and the scratch runner
  keeps its engine. A part edit is a simulation edit.
- **Red is the index**, 1.00 to 2.55 in steps of 0.01 (red = index × 100, Jeroen's range); **red 0 is a mirror**
  (index 1000, Jeroen's V0.30 rule); red 1..99 is index 1. **Blue is damping** (each step a cell's velocity times
  1 − blue / 255), painted by absorbing parts keeping the larger of their value and the blue already there. **Green is
  never touched**: its shades are source frequencies (WaveSimulator2D's warning: never anti-alias green).
- **Rasterisation**: 4 × 4 supersampling per pixel for refractive parts, each sample classified (outside, coating,
  body, a fibre's or GRIN lens's zone) and the indices averaged (an existing red 0 counting as index 1), rounded to red.
  A mirror uses the pixel-centre test alone and writes red 0: a half-covered mirror pixel would otherwise round to an
  index and be transparent. A part's centre is the corner of pixel (X, Y), so even sizes, every default, cover whole
  pixels. A part whose index is not painted (`PaintsIndex` false, the zone plate) writes blue alone.
- **The angle** is in degrees, counter-clockwise on the screen, 0 pointing the part's axis x' along +x (the composite
  source's convention); y' is across. Shapes face a source on the left (-x').
- **The AR coating** (`CoatedPart`: lens, shapes, GRIN lens) is a band of `Coating` pixels just inside the outline: with
  `CoatingProfile` `Layer` at the index √n (rounded to 0.01), the quarter-wave idea without a tie to a wavelength, a
  quarter-wave band being λ / (4 √n) pixels thick, λ the wavelength the source form shows; with `Graded` (2026-10-29, the
  user's request, after Jeroen's drawn scenes, whose glass faces ramp) the index rises from the medium outside, the index
  the pixel had before the part, to the body as a raised cosine of the depth over the thickness
  (`PartShape.GradedProfile`, measured under Coatings). `FrontCoated`, `BackCoated` and `SidesCoated` choose the faces,
  classed by their outward normal in the part frame: within 45° of −x' the front, of +x' the back, else a side (a lens's
  first surface, second surface and rim; a rectangle's four faces when its length runs along the beam); inside the band
  the nearest face decides, so a cavity's inner faces can stay bare. The fibre is a `CoatedPart` too (its start face the
  front, its end face the back, the tube the sides). Mirrors and Bragg reflectors ignore it; the gates' bands are their
  own measured design, a layer on the ends they coat.

## Absorption

A lens or shape absorbs with `Absorption` (0..255 as blue) rising over `AbsorptionRamp` pixels from 0 at the outline as
blue = round(Absorption × (δ / ramp)²), δ the depth of the pixel centre (no supersampling: the profile is continuous
and 0 at the outline); a part thinner than twice the ramp reaches its full value in its middle (Jeroen's point 11,
2026-10-20).

A jump in damping reflects like a jump in index, so the profile and strength were measured (2026-10-20): a pulse from
an open grid onto a deep absorbing slab, its reflected peak against a mirror slab's, for waves of level 120 and 60. At
absorption 255 every profile reflects a lot; over a 40 px ramp: linear 9 / 25 %, quadratic 2 / 11 %, cubic
1.7 / 9 %, a smooth step 4.7 / 20 %, an error-function step 4.2 / 18 %, Jeroen's √ band 4.5 / 16 %. At 64 a quadratic
80 px ramp reflects 0.5 / 1.5 % and lets nothing through 160 px; at 16 the reflection is lower still, but up to 6 %
passes through 160 px. Quadratic was chosen (`PartShape.AbsorptionProfile`); 64 is the strength of the gates' absorbing
cladding rim (`LogicGateDesign.Absorption`).

## Coatings

The graded band was measured the same way (2026-10-29): a plane pulse of six cycles, a column of emitters, on a deep
slab whose face carries the band, the reflected pulse's peak amplitude in a box before the face against a mirror face's,
for indices 2.55 and 1.50 at wavelengths of 10, 20 and 32 px with the 4th-order stencil. Amplitude as a share of the
mirror's (the reflectance is its square); the band profiles in the order linear, smooth step, quintic, raised cosine:

| face, n = 2.55 | λ 10 px | λ 20 px | λ 32 px |
|---|---|---|---|
| bare (theory 43.7 %) | 54 % | 46 % | 45 % |
| quarter-wave layer at √n, rounded to whole pixels (2, 3, 5) | 25 % | 8.8 % | 7.4 % |
| graded, ½ λ thick | 10.4 / 12.5 / 13.9 / 12.2 % | 11.2 / 11.8 / 13.5 / 11.6 % | 10.9 / 11.2 / 12.8 / 11.3 % |
| graded, 1 λ | 5.6 / 4.3 / 4.0 / 4.1 % | 6.1 / 4.1 / 4.4 / 4.1 % | 6.1 / 4.1 / 4.1 / 3.6 % |
| graded, 2 λ | 2.8 / 1.5 / 0.6 / 1.0 % | 3.2 / 0.9 / 0.8 / 1.0 % | 3.2 / 1.1 / 0.5 / 0.7 % |

For n = 1.50 (bare 20 to 22 %) the layer reflects 3.6 to 4.1 %, a ½ λ band 3.4 to 7.3 %, a 1 λ band 0.9 to 2.2 % and a
2 λ band 0.3 to 1.2 %, the profiles ordered as above. The raised cosine, (1 − cos πt) / 2, was chosen
(`PartShape.GradedProfile`): best or tied at one wavelength and within a few tenths of a percent of the quintic at two,
where the linear ramp reflects three to four times as much; the linear ramp wins only at half a wavelength, by a percent.
A layer beats every band only when the band is thinner than half a wavelength, and at λ = 10 px the rounding of its
thickness to 2 px (1.6 is the quarter wave) costs it most. Jeroen's drawn scenes ramp linearly over 20 to 22 px.

## The kinds

Every part has `Name`, `Enabled` (a disabled part is not painted), `X`, `Y` (its centre, ≥ 0) and `Angle` (−180, 180].
Defaults and ranges are in the attributes beside each property; the figures below are the defaults.

**Lens** (`LensPart`, `<Lens>`): `Diameter` 200 across the axis, `Thickness` 40 on the axis, signed radii `R1` 300 (the
left surface) and `R2` −300 (R1 > 0 and R2 < 0 bulge outward, 0 is flat), `RefractiveIndex` 1.50, the coating of
`CoatedPart`, `Absorption`, `AbsorptionRamp` 40, `Mirror`. The strip |y'| ≤ D/2 between the surfaces x'₁ = −t/2 + s₁(y') and
x'₂ = t/2 + s₂(y'), the sag s(y') = R − sign(R)·√(R² − y'²). The diameter and the centre thickness are the bulk: a radius
too curved for them is raised to the smallest that fits (`FitRadii`), so the lens fills its diameter and the edge is
never negative (a diameter of 2000 with radii of 300 had given a tiny lens). The form shows the edge thickness, and a
"Concave" box flips both radii. A curved mirror is the same shape with `Mirror` on; ticking the box renames the part, Lens to Mirror and back
(`LensPart.MirrorName`, applied by the box's command, 2026-10-29, the user's design), and the tree row's icon is the
part's own profile, each surface convex, flat or concave by its sag at the rim (`ShapeOf`: flat under half a pixel, so
Example 1's mirror of radii 14401 on 104 px reads flat, the user's report of 2026-10-29). On the pictures the size grip
stretches the lens as a rectangle's grip does: across, the diameter with the profile held; along, the whole profile
with its sags and edge, so a thinner lens is the same lens squashed; diagonally, the lens as a whole; the arcs are
refitted as circles through the stretched rim and vertex, and nothing stops the grip (`SetExtents`; 2026-10-26, Jeroen's
report: a grip dragged a little inward had the radii fitted flat and the lens was a rectangle, and held sags made a
stretched lens a slab). A handle on each surface's vertex sets its radius
(`SetVertex`, Jeroen's proposal): dragged along the axis with the rim and the other surface held, the sag at the rim
gives the radius by the sagitta, outward bulging, through the rim plane flat, then concave, a hemisphere at most; the
centre thickness follows and the position moves to the new centre. A drag holds the rim and the other vertex as it
found them (`MarkerThumb.DragAnchors`), so the position's rounding never creeps into them. A radius a grip or a vertex
handle sets is rounded to a hundredth of a pixel (`RadiusOfSag`; 2026-10-28, Jeroen's report: sixteen decimals in the
box and the file), R1 towards +∞ and R2 towards −∞, so a convex surface gets a hair flatter and a concave one a hair
deeper, never the other way, and the rounding never brings on the refit of a lens whose surfaces meet at the rim.

**Shapes** (`ShapePart`, a `CoatedPart`: `Mirror`, `RefractiveIndex` 1.50, the coating, `Absorption`, `AbsorptionRamp`
40, over an outline each kind generates). One class per shape, not a shape mode inside one class (the user's point, 2026-10-18: a
side length alone made every rectangle a square):
- `PolygonPart` (`<Polygon>`): regular, `Sides` 6 (3..64) of `Side` 60 px, a face centred on -x'; for an odd count the
  first vertex lies on +x'.
- `RectanglePart` (`<Rectangle>`): `Length` 60 along, `Width` 60 across. A flat mirror is a long, narrow one.
- `EllipsePart` (`<Ellipse>`, 2026-10-20): `Length` and `Width` 60; outlined as a polygon of a vertex per 3 px of
  circumference, 24 to 180. Rectangle and ellipse carry `Linked`, the image editors' chain between width and length,
  which keeps them equal through the boxes, the grip and the file (a square, a circle).
- `TrianglePart` (`<Triangle>`): a `Base` of 60 across the axis toward -x', the apex along +x', base angles `AngleA` (at
  -y') and `AngleB`; `Type` presets (equilateral, right isosceles, 30-60-90, isosceles with one typed angle, custom); the
  form shows the apex angle.
- `OutlinePart` (`<Outline>`): free `Points` relative to the centre before rotation; fewer than three paint nothing.

**Fibre** (`FiberPart`, `<Fiber>`): `CoreIndex` 1.50, `CoreDiameter` 20, `CladdingIndex` 1.45, `CladdingThickness` 10
(0 for a bare core), the coating of `CoatedPart` (2026-10-29: the start face is the front, the end face the back, the
tube the sides; a graded band on the back alone is the author's output taper), `Absorption` and `AbsorptionRamp`
(2026-10-29, the user's request, after the author's fibre scene, whose fibres run between absorbing strips: the
cladding's absorbing band, a hump in its outer part 2 × ramp wide that rises and falls by the quadratic profile, so the
guided mode's tail in the inner cladding is untouched and stray light is eaten from either side without a damping step;
nothing on a bare core; a mode stripper), `Ends` Flat or Round. The path (2026-10-22, the user's request, replacing a polyline whose
joints were sharp bends) is cubic Bézier segments through `Nodes` (`<Node X Y InX InY OutX OutY Smooth>`, the handles
offsets from the node, sparse); a new fibre gets two nodes at (−100, 0) and (100, 0) with handles a third of the way
along. A smooth node (the default) lines its handles up when switched on (`AlignNode`; a node without handles is seeded
from its neighbours) and then turns the other handle opposite the one dragged or typed, keeping its length as captured
at the drag's start, so rounding does not creep in; a node added or loaded is taken as it is, so a file never changes on
loading. A flat end is cut across the curve's tangent at the endpoint, the coating following the face; a round end is
the distance test. The painter flattens the curve into chords within 0.05 px, bucketed on a 32 px cell grid (an S-bent
fibre across a Full HD canvas: 51 ms against 1.3 s without the grid), and supersamples the core edge (`HasInnerEdges`).

**Bragg reflector** (`BraggReflectorPart`, `<BraggReflector>`): `Pairs` 10 of layers `Thickness1` and `Thickness2`
10 px with `Index1` 1.50 and `Index2` 2.00, stacked along +x' and centred, `Width` 200 across. Thicknesses are pixels as
typed; a quarter-wave layer is λ / (4 n).

**GRIN lens** (`GrinLensPart`, `<GrinLens>`): a rectangle of `Length` 100 and `Diameter` 100 whose index falls from
`CentreIndex` 1.80 to `EdgeIndex` 1.50 as n(y') = n_c − (n_c − n_e)·(2y'/D)²; the coating of `CoatedPart`, its end
faces the front and back, its long sides the sides.

**Logic gates** (`LogicGatePart`: `XorGatePart`, `AndGatePart`, `OrGatePart`; `<XorGate>`, `<AndGate>`, `<OrGate>`;
under "+ Part" > Optical logic). One 2 × 2 directional coupler, the combining half of a Mach-Zehnder (the user's
choice, 2026-10-21): input A above the axis, B below, a phase section putting B `PhaseShift` ahead of A, and a 3 dB
coupler whose upper output carries (a + i·b) / √2 and lower (i·a + b) / √2 of two coherent inputs. The phase shift
alone makes the kind: +90° gives the upper output a − b (XOR), −90° a + b (AND, 4 : 1 at best; a passive linear gate
has no truer AND), +30° the waves at 120° (OR: ½, ½, ½). The phase section sits in a quarter-period stretch in all
three, so they share dimensions and ports and tile together (measured at 120 × 544 px: XOR 0.01; AND 1.88 against
0.50; OR 0.49 against 0.55).
- **Design**: entered as a source frequency (`DesignFrequency` 50); the wavelength follows from
  `Simulation.SourceFrequencyScale`, which `GatePortFollower` hands each gate (`FrequencyScale`), and the form shows it.
  `LogicGateDesign` lays the gate out with `WaveguideModes`, a mode solver on the engine's own discrete equations
  (4th-order stencil; its β matched a simulated guide to 1e-5 when measured, `library_check` holds it within 2e-3): smooth S-bends whose radius follows the mode's
  confinement, a measured coupling excess of 0.07 rad taken off. Defaults `CoreIndex` 2.55 in `CladdingIndex` 1.45,
  the phase section then lowered on arm A: port spacing 60, 120 × 544 px; both inputs leave about 1 % at the XOR output,
  the sum 1.9× (the user found 1600 px unacceptable).
- **Ends**: an AR band like a lens's (`Coating` 6 px, √n of core and cladding, the measured least reflection: 3.4 %
  against 24 % bare), which replaced an output taper; switchable per end (`InputCoated`, `OutputCoated`): a band in a
  junction keeps the wave from passing from one unit into the next. Both ends are open (the user's choice, 2026-10-22:
  sources are to be coupled in from outside by a fibre later), so staggered gates cascade; a source placed in the unit
  sits half a wavelength in, its backward wave leaving through the coated input end.
- **Outputs**: each can be switched off (`UpperOutputActive`, `LowerOutputActive`): an absorber rising over 2 λ in its
  arm's last stretch (0.03 % reflected, nothing out). The cladding's rim absorbs (`PartShape.AbsorptionShareAt`).
- **Tiling**: the unit is twice the port spacing wide, the ports at ¼ and ¾ (the user's request, for staggered tiling);
  the side absorber fills out to there, at least λ/2 (without it 7.6 % reached the XOR output). `PortSpacing` is raised
  to the design's minimum; the form's box edits `PortSpacingUsed`, so it shows the spacing used and every step moves the
  ports.
- **Inputs** are ordinary sources on the ports the form lists. `GatePortFollower` (one per project in the window) moves
  a source within 1 px of an input onto the input's new position after any gate edit; `Movable.MoveAll` moves parts
  last, so a group move does not move such a source twice.

**Zone plate** (`ZonePlatePart`, `<ZonePlate>`, 2026-10-22, among the main parts beside the lens): Fresnel zones at
rₙ = √(nλf + (nλ/2)²) from `DesignFrequency` 50 (λ through the frequency scale, as for the gates; `IFrequencyDesigned`)
and `FocalLength` 600, across `Diameter` 800, `Thickness` 64 deep. Every second zone is an absorbing block (the user's
choice over mirror or phase blocks), the central zone open, or a block when `Inverse`; blue only. A block absorbs 96 in
its middle, rising quadratically over `Coating` 32 from the faces the wave meets and leaves, sharp at its sides, as the
outer zones are narrow (measured: 0.4 % through, 1.0 % back at 64 px thick with a 32 px rise). In 2D a zone plate is
cylindrical: the central zone dominates (the Cornu spiral), so the inverse plate focuses weakly (about 1 × the free
wave), the default plate with its 8 zones 4.6 ×. Light passing outside the diameter pushes the focus on (5 zones +30 %,
8 zones +3 %); the optional absorbing `Mount` (`MountWidth` 200 either side, off by default) stops it (5 zones: +12 % in
the check's small canvas, +6 % in a wide one).

## In the app

- **Tree**: a "Parts" folder between Sources and Probes; rows like a source's (enabled box, name, move, remove).
- **Menus**: the "+ Part" button and the Parts folder's plus open the menu of kinds (Space opens it on the menu itself,
  the arrows walk it, Enter picks); a tree row's context menu has the same entries under "Add part". Each entry's tooltip is the kind's description
  (the `Kind.<Type>` string, 2026-10-29, the user's choice), shown again at the top of the part's form under a header
  whose chevron turns while it is open; open or closed is a user setting. The entries are
  written once (`PartMenuEntries`, an unshared `x:Array`) and added to each menu's items by `ProjectTree.MenuEntries`,
  as real children: as an `ItemsSource` they made a submenu opened by keyboard start on its last entry the first time
  (2026-10-22). Each entry passes its class to `ProjectCommands.AddPart`; `Project.AddPart(Type)` adds it centred on
  the canvas, named "<stem> n".
- **Pictures**: a handle at the centre and a rotation lever; while selected, the outline as painted in the accent
  colour, a size grip (a rectangle's or ellipse's corner, the lens's thickness and diameter, a GRIN lens, Bragg stack or
  zone plate corner, a polygon's first vertex, the second end of a triangle's base), a lens's two vertex handles for
  the radii (2026-10-26), a handle per outline point (2026-10-19) and per fibre node with its two Bézier handles on
  dotted lines (2026-10-22); otherwise the shape shows through the glowing outline. Arrows nudge, Escape during a drag
  puts things back.
- **Examples**: `Examples/Example18_XOR_AND_OR_logic.wdproj`, the curated form of Jeroen's `logical_elements_example.wdproj`
  (2026-10-22: XOR and AND gates cascaded with pulsed sources and probes; the original is in git history up to 02d0e2d).

## Open points

- A `PartCheck.ps1` harness (add a part by keys, edit a value, the run goes stale and re-runs, the handle moves with
  the arrows) and example projects for Jeroen (a lens, a cavity, a bent fibre); the planned stage 3.
- Bragg thicknesses in pixels as typed, without an automatic quarter-wave from a source's wavelength.
- Where example projects live: settled (2026-10-29/31): the curated set in `Examples/`, embedded in the app; Jeroen's
  originals are out of the repository (git history up to 02d0e2d), the four the import checks read sit in
  `_tools/legacy_examples/`.
