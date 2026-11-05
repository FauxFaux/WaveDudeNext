// Schema snapshot from the HTTP frontend; replace generation alongside the Wasm project contract.
import { z } from 'zod'

export const SceneModeSchema = z.enum(["Canvas", "Image"])
export type SceneMode = z.infer<typeof SceneModeSchema>

export const EdgeModeSchema = z.enum(["Open", "Reflective"])
export type EdgeMode = z.infer<typeof EdgeModeSchema>

export const SceneSchema = z.strictObject({
  mode: SceneModeSchema.meta({ default: "Canvas", description: "Blank canvas of the given size, or a canvas built from the scene image." }),
  image: z.string().meta({ default: "", description: "Image the canvas is built from: red = refractive index (red 0 = a mirror), green = sources, blue = damping. The name of the embedded image, or a path relative to the project folder." }),
  width: z.number().int().min(-2147483648).max(2147483647).min(8).max(16384).meta({ default: 1920, description: "Width of a blank canvas in pixels." }),
  height: z.number().int().min(-2147483648).max(2147483647).min(8).max(16384).meta({ default: 1080, description: "Height of a blank canvas in pixels." }),
  dampingEdge: z.number().int().min(-2147483648).max(2147483647).min(-8192).max(8192).meta({ default: -32, description: "Thickness in pixels of the absorbing frame: positive inside the canvas, where it is visible, negative outside it, where it is not. 0 turns it off." }),
  edges: EdgeModeSchema.meta({ default: "Open", description: "Open lets a wave leave the grid; Reflective is a hard wall, as in Wave Dude 2D." }),
  refractiveIndex: z.number().min(1).max(2.55).meta({ default: 1, description: "Refractive index of a blank canvas, 1.0 to 2.55. The wave speed is 1/n cells per step." }),
  imageSources: z.boolean().meta({ default: false, description: "Let the green channel of the scene image emit waves; the pixel value is the frequency level." }),
  imageSourceScale: z.number().min(0).max(1000).meta({ default: 1, description: "Peak amplitude of the image sources." }),
  objectSources: z.boolean().meta({ default: true, description: "Use the sources defined in the project." }),
  imageAssetId: z.uuid().nullable(),
})
export type Scene = z.infer<typeof SceneSchema>

export const SimulationSchema = z.strictObject({
  steps: z.number().int().min(-2147483648).max(2147483647).min(1).max(10000000).meta({ default: 2500, description: "Total number of simulation steps." }),
  visualizeEvery: z.number().int().min(-2147483648).max(2147483647).min(1).max(1000).meta({ default: 2, description: "Render a frame, export it and sample the probes every this many steps." }),
  fourthOrderLaplacian: z.boolean().meta({ default: true, description: "Use the 4th-order stencil with sub-stepping instead of the 9-point stencil. More accurate, slower." }),
  waveSpeed: z.number().min(1E-06).max(1000000).meta({ default: 1, description: "Nominal wave speed in pixels per step; only used to estimate wavelengths in the editor." }),
  timeDecay: z.number().min(1).max(100).meta({ default: 10, description: "Averaging window factor of the intensity: the engine averages over this \u00D7 100 / lowest source level steps." }),
  sourceFrequencyScale: z.number().min(0).max(1000).meta({ default: 2, description: "Multiplier of the level-to-frequency mapping: \u03C9 = level / 255 \u00D7 0.5 \u00D7 scale, in radians per step." }),
  intensityScale: z.number().min(1E-07).max(1000000).meta({ default: 1, description: "Multiplier applied to the intensity values the engine produces." }),
})
export type Simulation = z.infer<typeof SimulationSchema>

export const DisplayLayerSchema = z.strictObject({
  colorScheme: z.string().meta({ description: "Name of the colour scheme mapped onto the values." }),
  brightness: z.number().min(0.0001).max(100000).meta({ default: 30, description: "Gain applied to the values before the colour lookup." }),
  blackLevel: z.number().min(-10).max(1).meta({ default: 0, description: "Offset applied before the colour lookup; values at or below it map to the first colour." }),
  preview: z.boolean().meta({ default: true, description: "Show this output in the live preview." }),
  overlay: z.boolean().meta({ default: true, description: "Lay the scene image\u0027s glowing-edges outline over this output in the preview." }),
  overlayGain: z.number().min(0).max(5).meta({ default: 3, description: "Multiplier of the outline: 1 as computed, higher lifts faint edges, 0 hides it." }),
  overlayInExport: z.boolean().meta({ default: false, description: "Bake the outline into exported frames and videos of this output." }),
})
export type DisplayLayer = z.infer<typeof DisplayLayerSchema>

export const VideoSchema = z.strictObject({
  fps: z.number().int().min(-2147483648).max(2147483647).min(1).max(240).meta({ default: 60, description: "Frames per second of the exported video." }),
  quality: z.number().int().min(-2147483648).max(2147483647).min(1).max(100).meta({ default: 85, description: "Encoder quality, 1 to 100." }),
})
export type Video = z.infer<typeof VideoSchema>

export const DisplaySchema = z.strictObject({
  field: DisplayLayerSchema.meta({ description: "Rendering of the wave field." }),
  intensity: DisplayLayerSchema.meta({ description: "Rendering of the accumulated intensity." }),
  video: VideoSchema.meta({ description: "Encoder settings of exported videos." }),
  exportFrom: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ default: 0, description: "First simulation step of the exported range; 0 is the start of the run." }),
  exportTo: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ default: 0, description: "Last simulation step of the exported range; 0 means the end of the run." }),
  probeGraphsInExport: z.boolean().meta({ default: true, description: "Draw the probes\u0027 graph boxes over exported frames and videos." }),
  probeMarkersInExport: z.boolean().meta({ default: true, description: "Draw the probes\u0027 positions (crosses, the line of a line probe, the number) over exported frames and videos." }),
  sourceMarkersInExport: z.boolean().meta({ default: false, description: "Draw the enabled sources (the marker, a composite source\u0027s emitter dots, the name) over exported frames and videos." }),
  handlesInPreview: z.boolean().meta({ default: true, description: "Show the handles, lines, emitter dots and levers of every source, part and probe over the preview pictures. A selected object shows its own either way." }),
  labelsInPreview: z.boolean().meta({ default: true, description: "Show the names beside the handles over the preview pictures. A selected object shows its own either way." }),
})
export type Display = z.infer<typeof DisplaySchema>

export const SourceModeSchema = z.enum(["Continuous", "Pulsed"])
export type SourceMode = z.infer<typeof SourceModeSchema>

export const PulseShapeSchema = z.enum(["Gaussian", "SmoothedSquare"])
export type PulseShape = z.infer<typeof PulseShapeSchema>

export const SourceArraySchema = z.strictObject({
  sizeX: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 1, description: "Number of emitters across. Odd counts keep the centre emitter on the source position." }),
  sizeY: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 1, description: "Number of emitters down." }),
  spacingX: z.number().int().min(-2147483648).max(2147483647).meta({ default: 10, description: "Distance between neighbouring columns in canvas pixels." }),
  spacingY: z.number().int().min(-2147483648).max(2147483647).meta({ default: 10, description: "Distance between neighbouring rows in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation of the grid about the source position in degrees, counter-clockwise on the screen. 0 keeps the rows horizontal." }),
  randomFrequency: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ default: 0, description: "Each emitter\u0027s level is offset by up to \u00B1 this amount. 0 turns it off." }),
  randomPhase: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ default: 0, description: "Each emitter\u0027s phase is offset by up to \u00B1 this many degrees. 0 turns it off." }),
  randomOffWidth: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ default: 0, description: "Each gap between bursts is offset by up to \u00B1 this many cycles, re-drawn every period. 0 turns it off." }),
  randomPosition: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ default: 0, description: "Each emitter is moved by up to \u00B1 this many pixels in X and in Y. 0 turns it off." }),
})
export type SourceArray = z.infer<typeof SourceArraySchema>

export const SourceSchema = z.strictObject({
  id: z.uuid(),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Include the source in simulation runs." }),
  x: z.number().int().min(-2147483648).max(2147483647).meta({ description: "X of the emitter, or of the array centre, in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).meta({ description: "Y of the emitter, or of the array centre, in canvas pixels." }),
  frequency: z.number().min(0.01).max(255).meta({ default: 100, description: "Frequency as a level up to 255, decimals allowed, the same scale as the green channel of image sources. Above 255 the grid cannot sample the wave." }),
  amplitude: z.number().min(0).max(100).meta({ default: 100, description: "Amplitude in percent, 0 to 100, decimals allowed." }),
  phase: z.number().min(-180).max(180).meta({ default: 0, description: "Phase offset in degrees, -180 to 180." }),
  mode: SourceModeSchema.meta({ default: "Continuous", description: "Emit continuously or in bursts." }),
  pulseShape: PulseShapeSchema.meta({ default: "Gaussian", description: "Envelope of each burst; pulsed mode only." }),
  pulseWidth: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 10, description: "Length of each burst in cycles of the source frequency; pulsed mode only." }),
  offWidth: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 10, description: "Length of the gap between bursts in cycles; pulsed mode only." }),
  detachAfter: z.number().int().min(-2147483648).max(2147483647).min(-1).max(2147483647).meta({ default: -1, description: "Simulation step at which the source fades out; -1 keeps it on for the whole run." }),
  array: SourceArraySchema.nullable().meta({ description: "Grid of emitters around the position; empty for a single emitter." }),
})
export type Source = z.infer<typeof SourceSchema>

export const CoatingProfileSchema = z.enum(["Layer", "Graded"])
export type CoatingProfile = z.infer<typeof CoatingProfileSchema>

export const LensPartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("Lens"),
  mirror: z.boolean().meta({ default: false, description: "Make the part a mirror instead of a lens. The refractive index, the coating and the absorption are ignored then." }),
  refractiveIndex: z.number().min(1).max(2.55).meta({ default: 1.5, description: "Refractive index of the glass, 1.00 to 2.55 in steps of 0.01." }),
  absorption: z.number().int().min(-2147483648).max(2147483647).min(0).max(255).meta({ default: 0, description: "How strongly the part absorbs waves, 0 (not at all) to 255, as the scene image\u0027s blue channel. It rises from nothing at the outline over the absorption ramp, so a wave enters the part instead of being reflected. A mirror ignores it." }),
  absorptionRamp: z.number().int().min(-2147483648).max(2147483647).min(1).max(400).meta({ default: 40, description: "Depth in pixels over which the absorption rises from nothing at the outline to its full value; the longer, the less of a wave is reflected. A part thinner than twice the ramp reaches its full absorption in its middle." }),
  diameter: z.number().int().min(-2147483648).max(2147483647).min(2).max(2147483647).meta({ default: 200, description: "Extent across the axis in pixels." }),
  r1: z.number().meta({ default: 300, description: "Radius of the first surface in pixels: positive bulges outward, negative is concave, 0 is flat. A radius too curved for the diameter and the centre thickness is raised to the smallest that fits." }),
  r2: z.number().meta({ default: -300, description: "Radius of the second surface in pixels: negative bulges outward, positive is concave, 0 is flat. A radius too curved for the diameter and the centre thickness is raised to the smallest that fits." }),
  thickness: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 40, description: "Distance between the two surfaces on the axis in pixels." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 0, description: "Thickness in pixels of the anti-reflection band just inside the outline; 0 for none. A quarter-wave layer is \u03BB / (4 \u221An) pixels thick, \u03BB being the wavelength the source form shows; a graded band is the better the thicker, from a wavelength on." }),
  coatingProfile: CoatingProfileSchema.meta({ default: "Layer", description: "How the index runs through the band: a layer at \u221An, the quarter-wave idea, or a graded rise from the medium outside to the glass. A graded band reflects less than a layer once it is a wavelength thick or more, at any wavelength; a layer only at the wavelength it is cut for." }),
  frontCoated: z.boolean().meta({ default: true, description: "Put the band on the front: the faces turned toward a source on the left, a lens\u0027s first surface. Clear it to leave them bare, as the inner faces of a cavity are." }),
  backCoated: z.boolean().meta({ default: true, description: "Put the band on the back: the faces turned away from a source on the left, a lens\u0027s second surface. Clear it to leave them bare." }),
  sidesCoated: z.boolean().meta({ default: true, description: "Put the band on the sides: the faces that are neither front nor back, a lens\u0027s rim, a slab\u0027s narrow ends. Clear it to leave them bare." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type LensPart = z.infer<typeof LensPartSchema>

export const PolygonPartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("Polygon"),
  sides: z.number().int().min(-2147483648).max(2147483647).min(3).max(64).meta({ default: 6, description: "Number of sides, 3 to 64." }),
  side: z.number().int().min(-2147483648).max(2147483647).min(2).max(2147483647).meta({ default: 60, description: "Length of each side in pixels." }),
  mirror: z.boolean().meta({ default: false, description: "Make the part a mirror instead of glass. The refractive index, the coating and the absorption are ignored then." }),
  refractiveIndex: z.number().min(1).max(2.55).meta({ default: 1.5, description: "Refractive index of the glass, 1.00 to 2.55 in steps of 0.01." }),
  absorption: z.number().int().min(-2147483648).max(2147483647).min(0).max(255).meta({ default: 0, description: "How strongly the part absorbs waves, 0 (not at all) to 255, as the scene image\u0027s blue channel. It rises from nothing at the outline over the absorption ramp, so a wave enters the part instead of being reflected. A mirror ignores it." }),
  absorptionRamp: z.number().int().min(-2147483648).max(2147483647).min(1).max(400).meta({ default: 40, description: "Depth in pixels over which the absorption rises from nothing at the outline to its full value; the longer, the less of a wave is reflected. A part thinner than twice the ramp reaches its full absorption in its middle." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 0, description: "Thickness in pixels of the anti-reflection band just inside the outline; 0 for none. A quarter-wave layer is \u03BB / (4 \u221An) pixels thick, \u03BB being the wavelength the source form shows; a graded band is the better the thicker, from a wavelength on." }),
  coatingProfile: CoatingProfileSchema.meta({ default: "Layer", description: "How the index runs through the band: a layer at \u221An, the quarter-wave idea, or a graded rise from the medium outside to the glass. A graded band reflects less than a layer once it is a wavelength thick or more, at any wavelength; a layer only at the wavelength it is cut for." }),
  frontCoated: z.boolean().meta({ default: true, description: "Put the band on the front: the faces turned toward a source on the left, a lens\u0027s first surface. Clear it to leave them bare, as the inner faces of a cavity are." }),
  backCoated: z.boolean().meta({ default: true, description: "Put the band on the back: the faces turned away from a source on the left, a lens\u0027s second surface. Clear it to leave them bare." }),
  sidesCoated: z.boolean().meta({ default: true, description: "Put the band on the sides: the faces that are neither front nor back, a lens\u0027s rim, a slab\u0027s narrow ends. Clear it to leave them bare." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type PolygonPart = z.infer<typeof PolygonPartSchema>

export const RectanglePartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("Rectangle"),
  length: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 60, description: "Extent along the axis in pixels." }),
  width: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 60, description: "Extent across the axis in pixels." }),
  linked: z.boolean().meta({ default: false, description: "Keep the width equal to the length." }),
  mirror: z.boolean().meta({ default: false, description: "Make the part a mirror instead of glass. The refractive index, the coating and the absorption are ignored then." }),
  refractiveIndex: z.number().min(1).max(2.55).meta({ default: 1.5, description: "Refractive index of the glass, 1.00 to 2.55 in steps of 0.01." }),
  absorption: z.number().int().min(-2147483648).max(2147483647).min(0).max(255).meta({ default: 0, description: "How strongly the part absorbs waves, 0 (not at all) to 255, as the scene image\u0027s blue channel. It rises from nothing at the outline over the absorption ramp, so a wave enters the part instead of being reflected. A mirror ignores it." }),
  absorptionRamp: z.number().int().min(-2147483648).max(2147483647).min(1).max(400).meta({ default: 40, description: "Depth in pixels over which the absorption rises from nothing at the outline to its full value; the longer, the less of a wave is reflected. A part thinner than twice the ramp reaches its full absorption in its middle." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 0, description: "Thickness in pixels of the anti-reflection band just inside the outline; 0 for none. A quarter-wave layer is \u03BB / (4 \u221An) pixels thick, \u03BB being the wavelength the source form shows; a graded band is the better the thicker, from a wavelength on." }),
  coatingProfile: CoatingProfileSchema.meta({ default: "Layer", description: "How the index runs through the band: a layer at \u221An, the quarter-wave idea, or a graded rise from the medium outside to the glass. A graded band reflects less than a layer once it is a wavelength thick or more, at any wavelength; a layer only at the wavelength it is cut for." }),
  frontCoated: z.boolean().meta({ default: true, description: "Put the band on the front: the faces turned toward a source on the left, a lens\u0027s first surface. Clear it to leave them bare, as the inner faces of a cavity are." }),
  backCoated: z.boolean().meta({ default: true, description: "Put the band on the back: the faces turned away from a source on the left, a lens\u0027s second surface. Clear it to leave them bare." }),
  sidesCoated: z.boolean().meta({ default: true, description: "Put the band on the sides: the faces that are neither front nor back, a lens\u0027s rim, a slab\u0027s narrow ends. Clear it to leave them bare." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type RectanglePart = z.infer<typeof RectanglePartSchema>

export const EllipsePartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("Ellipse"),
  length: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 60, description: "Diameter along the axis in pixels." }),
  width: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 60, description: "Diameter across the axis in pixels." }),
  linked: z.boolean().meta({ default: false, description: "Keep the width equal to the length." }),
  mirror: z.boolean().meta({ default: false, description: "Make the part a mirror instead of glass. The refractive index, the coating and the absorption are ignored then." }),
  refractiveIndex: z.number().min(1).max(2.55).meta({ default: 1.5, description: "Refractive index of the glass, 1.00 to 2.55 in steps of 0.01." }),
  absorption: z.number().int().min(-2147483648).max(2147483647).min(0).max(255).meta({ default: 0, description: "How strongly the part absorbs waves, 0 (not at all) to 255, as the scene image\u0027s blue channel. It rises from nothing at the outline over the absorption ramp, so a wave enters the part instead of being reflected. A mirror ignores it." }),
  absorptionRamp: z.number().int().min(-2147483648).max(2147483647).min(1).max(400).meta({ default: 40, description: "Depth in pixels over which the absorption rises from nothing at the outline to its full value; the longer, the less of a wave is reflected. A part thinner than twice the ramp reaches its full absorption in its middle." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 0, description: "Thickness in pixels of the anti-reflection band just inside the outline; 0 for none. A quarter-wave layer is \u03BB / (4 \u221An) pixels thick, \u03BB being the wavelength the source form shows; a graded band is the better the thicker, from a wavelength on." }),
  coatingProfile: CoatingProfileSchema.meta({ default: "Layer", description: "How the index runs through the band: a layer at \u221An, the quarter-wave idea, or a graded rise from the medium outside to the glass. A graded band reflects less than a layer once it is a wavelength thick or more, at any wavelength; a layer only at the wavelength it is cut for." }),
  frontCoated: z.boolean().meta({ default: true, description: "Put the band on the front: the faces turned toward a source on the left, a lens\u0027s first surface. Clear it to leave them bare, as the inner faces of a cavity are." }),
  backCoated: z.boolean().meta({ default: true, description: "Put the band on the back: the faces turned away from a source on the left, a lens\u0027s second surface. Clear it to leave them bare." }),
  sidesCoated: z.boolean().meta({ default: true, description: "Put the band on the sides: the faces that are neither front nor back, a lens\u0027s rim, a slab\u0027s narrow ends. Clear it to leave them bare." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type EllipsePart = z.infer<typeof EllipsePartSchema>

export const TriangleTypeSchema = z.enum(["Equilateral", "RightIsosceles", "ThirtySixtyNinety", "Isosceles", "Custom"])
export type TriangleType = z.infer<typeof TriangleTypeSchema>

export const TrianglePartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("Triangle"),
  base: z.number().int().min(-2147483648).max(2147483647).min(2).max(2147483647).meta({ default: 60, description: "Length of the base in pixels; the base lies across the axis, toward a source on the left." }),
  type: TriangleTypeSchema.meta({ default: "Equilateral", description: "Equilateral, right isosceles (the 45\u00B0 prism), 30-60-90, isosceles with one typed angle, or custom with both typed." }),
  angleA: z.number().min(1).max(178).meta({ default: 60, description: "Base angle at the first end of the base, in degrees. Typed for the isosceles and custom types; the presets fix it." }),
  angleB: z.number().min(1).max(178).meta({ default: 60, description: "Base angle at the second end of the base, in degrees. Typed for the custom type; the others fix it." }),
  mirror: z.boolean().meta({ default: false, description: "Make the part a mirror instead of glass. The refractive index, the coating and the absorption are ignored then." }),
  refractiveIndex: z.number().min(1).max(2.55).meta({ default: 1.5, description: "Refractive index of the glass, 1.00 to 2.55 in steps of 0.01." }),
  absorption: z.number().int().min(-2147483648).max(2147483647).min(0).max(255).meta({ default: 0, description: "How strongly the part absorbs waves, 0 (not at all) to 255, as the scene image\u0027s blue channel. It rises from nothing at the outline over the absorption ramp, so a wave enters the part instead of being reflected. A mirror ignores it." }),
  absorptionRamp: z.number().int().min(-2147483648).max(2147483647).min(1).max(400).meta({ default: 40, description: "Depth in pixels over which the absorption rises from nothing at the outline to its full value; the longer, the less of a wave is reflected. A part thinner than twice the ramp reaches its full absorption in its middle." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 0, description: "Thickness in pixels of the anti-reflection band just inside the outline; 0 for none. A quarter-wave layer is \u03BB / (4 \u221An) pixels thick, \u03BB being the wavelength the source form shows; a graded band is the better the thicker, from a wavelength on." }),
  coatingProfile: CoatingProfileSchema.meta({ default: "Layer", description: "How the index runs through the band: a layer at \u221An, the quarter-wave idea, or a graded rise from the medium outside to the glass. A graded band reflects less than a layer once it is a wavelength thick or more, at any wavelength; a layer only at the wavelength it is cut for." }),
  frontCoated: z.boolean().meta({ default: true, description: "Put the band on the front: the faces turned toward a source on the left, a lens\u0027s first surface. Clear it to leave them bare, as the inner faces of a cavity are." }),
  backCoated: z.boolean().meta({ default: true, description: "Put the band on the back: the faces turned away from a source on the left, a lens\u0027s second surface. Clear it to leave them bare." }),
  sidesCoated: z.boolean().meta({ default: true, description: "Put the band on the sides: the faces that are neither front nor back, a lens\u0027s rim, a slab\u0027s narrow ends. Clear it to leave them bare." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type TrianglePart = z.infer<typeof TrianglePartSchema>

export const PartPointSchema = z.strictObject({
  x: z.number().int().min(-2147483648).max(2147483647).meta({ description: "X relative to the part\u0027s centre in canvas pixels, before rotation." }),
  y: z.number().int().min(-2147483648).max(2147483647).meta({ description: "Y relative to the part\u0027s centre in canvas pixels, before rotation." }),
})
export type PartPoint = z.infer<typeof PartPointSchema>

export const OutlinePartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("Outline"),
  points: z.array(PartPointSchema).meta({ description: "The outline\u0027s vertices relative to the centre, before rotation. Fewer than three points paint nothing." }),
  mirror: z.boolean().meta({ default: false, description: "Make the part a mirror instead of glass. The refractive index, the coating and the absorption are ignored then." }),
  refractiveIndex: z.number().min(1).max(2.55).meta({ default: 1.5, description: "Refractive index of the glass, 1.00 to 2.55 in steps of 0.01." }),
  absorption: z.number().int().min(-2147483648).max(2147483647).min(0).max(255).meta({ default: 0, description: "How strongly the part absorbs waves, 0 (not at all) to 255, as the scene image\u0027s blue channel. It rises from nothing at the outline over the absorption ramp, so a wave enters the part instead of being reflected. A mirror ignores it." }),
  absorptionRamp: z.number().int().min(-2147483648).max(2147483647).min(1).max(400).meta({ default: 40, description: "Depth in pixels over which the absorption rises from nothing at the outline to its full value; the longer, the less of a wave is reflected. A part thinner than twice the ramp reaches its full absorption in its middle." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 0, description: "Thickness in pixels of the anti-reflection band just inside the outline; 0 for none. A quarter-wave layer is \u03BB / (4 \u221An) pixels thick, \u03BB being the wavelength the source form shows; a graded band is the better the thicker, from a wavelength on." }),
  coatingProfile: CoatingProfileSchema.meta({ default: "Layer", description: "How the index runs through the band: a layer at \u221An, the quarter-wave idea, or a graded rise from the medium outside to the glass. A graded band reflects less than a layer once it is a wavelength thick or more, at any wavelength; a layer only at the wavelength it is cut for." }),
  frontCoated: z.boolean().meta({ default: true, description: "Put the band on the front: the faces turned toward a source on the left, a lens\u0027s first surface. Clear it to leave them bare, as the inner faces of a cavity are." }),
  backCoated: z.boolean().meta({ default: true, description: "Put the band on the back: the faces turned away from a source on the left, a lens\u0027s second surface. Clear it to leave them bare." }),
  sidesCoated: z.boolean().meta({ default: true, description: "Put the band on the sides: the faces that are neither front nor back, a lens\u0027s rim, a slab\u0027s narrow ends. Clear it to leave them bare." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type OutlinePart = z.infer<typeof OutlinePartSchema>

export const FiberEndsSchema = z.enum(["Flat", "Round"])
export type FiberEnds = z.infer<typeof FiberEndsSchema>

export const FiberNodeSchema = z.strictObject({
  x: z.number().int().min(-2147483648).max(2147483647).meta({ description: "X relative to the part\u0027s centre in canvas pixels, before rotation." }),
  y: z.number().int().min(-2147483648).max(2147483647).meta({ description: "Y relative to the part\u0027s centre in canvas pixels, before rotation." }),
  inX: z.number().int().min(-2147483648).max(2147483647).meta({ default: 0, description: "X of the in-handle relative to the node, in canvas pixels before rotation. It shapes the segment arriving from the previous node; zero makes that segment straight at this end." }),
  inY: z.number().int().min(-2147483648).max(2147483647).meta({ default: 0, description: "Y of the in-handle relative to the node, in canvas pixels before rotation." }),
  outX: z.number().int().min(-2147483648).max(2147483647).meta({ default: 0, description: "X of the out-handle relative to the node, in canvas pixels before rotation. It shapes the segment leaving to the next node; zero makes that segment straight at this end." }),
  outY: z.number().int().min(-2147483648).max(2147483647).meta({ default: 0, description: "Y of the out-handle relative to the node, in canvas pixels before rotation." }),
  smooth: z.boolean().meta({ default: true, description: "Keep the two handles opposite each other through the node, so the path runs smoothly through it. Switching it on lines them up at once; afterwards moving or typing one handle turns the other with it, keeping its length. Off, the node may be a corner." }),
})
export type FiberNode = z.infer<typeof FiberNodeSchema>

export const FiberPartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("Fiber"),
  coreIndex: z.number().min(1).max(2.55).meta({ default: 1.5, description: "Refractive index of the core, 1.00 to 2.55 in steps of 0.01." }),
  coreDiameter: z.number().int().min(-2147483648).max(2147483647).min(2).max(2147483647).meta({ default: 20, description: "Diameter of the core in pixels." }),
  claddingIndex: z.number().min(1).max(2.55).meta({ default: 1.45, description: "Refractive index of the cladding, 1.00 to 2.55 in steps of 0.01. Below the core\u0027s index, the fibre guides." }),
  claddingThickness: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ default: 10, description: "Thickness of the cladding around the core in pixels; 0 for a bare core." }),
  absorption: z.number().int().min(-2147483648).max(2147483647).min(0).max(255).meta({ default: 0, description: "Peak absorption of the cladding\u0027s absorbing band, 0 (not at all) to 255, as the scene image\u0027s blue channel: a hump in the outer part of the cladding, rising over the band width from nothing, peaking, and falling to nothing at the outer edge, so the guided wave\u0027s tail stays untouched and stray light is absorbed from either side. Nothing on a bare core." }),
  absorptionRamp: z.number().int().min(-2147483648).max(2147483647).min(1).max(400).meta({ default: 10, description: "Half the absorbing band\u0027s width in pixels: the absorption rises over this many pixels to its peak and falls over as many to the outer edge. A band wider than the cladding is squeezed into it." }),
  ends: FiberEndsSchema.meta({ default: "Flat", description: "How the two ends are cut: flat, straight across the path like a cleaved fibre, or rounded with the outer radius. The AR coating follows a flat end face." }),
  nodes: z.array(FiberNodeSchema).meta({ description: "The path\u0027s nodes relative to the centre, before rotation, each with the handles of the cubic segments meeting there. Fewer than two nodes paint nothing." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 0, description: "Thickness in pixels of the anti-reflection band just inside the outline; 0 for none. A quarter-wave layer is \u03BB / (4 \u221An) pixels thick, \u03BB being the wavelength the source form shows; a graded band is the better the thicker, from a wavelength on." }),
  coatingProfile: CoatingProfileSchema.meta({ default: "Layer", description: "How the index runs through the band: a layer at \u221An, the quarter-wave idea, or a graded rise from the medium outside to the glass. A graded band reflects less than a layer once it is a wavelength thick or more, at any wavelength; a layer only at the wavelength it is cut for." }),
  frontCoated: z.boolean().meta({ default: true, description: "Put the band on the front: the faces turned toward a source on the left, a lens\u0027s first surface. Clear it to leave them bare, as the inner faces of a cavity are." }),
  backCoated: z.boolean().meta({ default: true, description: "Put the band on the back: the faces turned away from a source on the left, a lens\u0027s second surface. Clear it to leave them bare." }),
  sidesCoated: z.boolean().meta({ default: true, description: "Put the band on the sides: the faces that are neither front nor back, a lens\u0027s rim, a slab\u0027s narrow ends. Clear it to leave them bare." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type FiberPart = z.infer<typeof FiberPartSchema>

export const BraggReflectorPartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("BraggReflector"),
  pairs: z.number().int().min(-2147483648).max(2147483647).min(1).max(200).meta({ default: 10, description: "Number of layer pairs, 1 to 200." }),
  thickness1: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 10, description: "Thickness of the first layer of each pair in pixels. A quarter-wave layer is \u03BB / (4 n) pixels thick." }),
  thickness2: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 10, description: "Thickness of the second layer of each pair in pixels. A quarter-wave layer is \u03BB / (4 n) pixels thick." }),
  index1: z.number().min(1).max(2.55).meta({ default: 1.5, description: "Refractive index of the first layer of each pair, 1.00 to 2.55 in steps of 0.01." }),
  index2: z.number().min(1).max(2.55).meta({ default: 2, description: "Refractive index of the second layer of each pair, 1.00 to 2.55 in steps of 0.01." }),
  width: z.number().int().min(-2147483648).max(2147483647).min(2).max(2147483647).meta({ default: 200, description: "Extent of the stack across the axis in pixels." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type BraggReflectorPart = z.infer<typeof BraggReflectorPartSchema>

export const GrinLensPartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("GrinLens"),
  length: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 100, description: "Extent along the axis in pixels." }),
  diameter: z.number().int().min(-2147483648).max(2147483647).min(2).max(2147483647).meta({ default: 100, description: "Extent across the axis in pixels." }),
  centreIndex: z.number().min(1).max(2.55).meta({ default: 1.8, description: "Refractive index on the axis, 1.00 to 2.55 in steps of 0.01." }),
  edgeIndex: z.number().min(1).max(2.55).meta({ default: 1.5, description: "Refractive index at the edge, 1.00 to 2.55 in steps of 0.01. The profile between the axis and the edge is parabolic." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 0, description: "Thickness in pixels of the anti-reflection band just inside the outline; 0 for none. A quarter-wave layer is \u03BB / (4 \u221An) pixels thick, \u03BB being the wavelength the source form shows; a graded band is the better the thicker, from a wavelength on." }),
  coatingProfile: CoatingProfileSchema.meta({ default: "Layer", description: "How the index runs through the band: a layer at \u221An, the quarter-wave idea, or a graded rise from the medium outside to the glass. A graded band reflects less than a layer once it is a wavelength thick or more, at any wavelength; a layer only at the wavelength it is cut for." }),
  frontCoated: z.boolean().meta({ default: true, description: "Put the band on the front: the faces turned toward a source on the left, a lens\u0027s first surface. Clear it to leave them bare, as the inner faces of a cavity are." }),
  backCoated: z.boolean().meta({ default: true, description: "Put the band on the back: the faces turned away from a source on the left, a lens\u0027s second surface. Clear it to leave them bare." }),
  sidesCoated: z.boolean().meta({ default: true, description: "Put the band on the sides: the faces that are neither front nor back, a lens\u0027s rim, a slab\u0027s narrow ends. Clear it to leave them bare." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type GrinLensPart = z.infer<typeof GrinLensPartSchema>

export const XorGatePartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("XorGate"),
  designFrequency: z.number().min(1).max(255).meta({ default: 50, description: "The source frequency the gate is designed for, on the same scale as a source\u0027s frequency: the frequency of the sources that drive it. The wavelength, and from it the core width, the coupler and the phase section, follow from it, so the gate works at this frequency only." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 6, description: "Thickness in pixels of the anti-reflection band at the ends ticked below, with the index \u221An of the core and of the cladding; 0 for none. A quarter-wave band, the least reflecting, is \u03BB / (4 \u221An) pixels thick; the form shows it for the core." }),
  inputCoated: z.boolean().meta({ default: true, description: "Put the anti-reflection band on the input end. Switch it off where the gate takes its light from a gate before it, so the wave passes through the junction." }),
  outputCoated: z.boolean().meta({ default: true, description: "Put the anti-reflection band on the output end. Switch it off where the gate hands its light to a gate after it, so the wave passes through the junction." }),
  upperOutputActive: z.boolean().meta({ default: true, description: "Let the upper output\u0027s light leave the gate. Switched off, an absorber with a soft rise takes it in the last stretch of the arm." }),
  lowerOutputActive: z.boolean().meta({ default: true, description: "Let the lower output\u0027s light leave the gate. Switched off, an absorber with a soft rise takes it in the last stretch of the arm." }),
  coreIndex: z.number().min(1).max(2.55).meta({ default: 2.55, description: "Refractive index of the waveguide cores, 1.00 to 2.55 in steps of 0.01. It must be above the cladding\u0027s; the larger the difference, the narrower the cores and the tighter the bends." }),
  claddingIndex: z.number().min(1).max(2.55).meta({ default: 1.45, description: "Refractive index of the cladding around the cores, 1.00 to 2.55 in steps of 0.01." }),
  portSpacing: z.number().int().min(-2147483648).max(2147483647).min(2).max(2000).meta({ default: 2, description: "Distance between the two inputs, and between the two outputs, in pixels. A value below what the design needs to keep the arms apart is raised to it." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type XorGatePart = z.infer<typeof XorGatePartSchema>

export const AndGatePartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("AndGate"),
  designFrequency: z.number().min(1).max(255).meta({ default: 50, description: "The source frequency the gate is designed for, on the same scale as a source\u0027s frequency: the frequency of the sources that drive it. The wavelength, and from it the core width, the coupler and the phase section, follow from it, so the gate works at this frequency only." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 6, description: "Thickness in pixels of the anti-reflection band at the ends ticked below, with the index \u221An of the core and of the cladding; 0 for none. A quarter-wave band, the least reflecting, is \u03BB / (4 \u221An) pixels thick; the form shows it for the core." }),
  inputCoated: z.boolean().meta({ default: true, description: "Put the anti-reflection band on the input end. Switch it off where the gate takes its light from a gate before it, so the wave passes through the junction." }),
  outputCoated: z.boolean().meta({ default: true, description: "Put the anti-reflection band on the output end. Switch it off where the gate hands its light to a gate after it, so the wave passes through the junction." }),
  upperOutputActive: z.boolean().meta({ default: true, description: "Let the upper output\u0027s light leave the gate. Switched off, an absorber with a soft rise takes it in the last stretch of the arm." }),
  lowerOutputActive: z.boolean().meta({ default: true, description: "Let the lower output\u0027s light leave the gate. Switched off, an absorber with a soft rise takes it in the last stretch of the arm." }),
  coreIndex: z.number().min(1).max(2.55).meta({ default: 2.55, description: "Refractive index of the waveguide cores, 1.00 to 2.55 in steps of 0.01. It must be above the cladding\u0027s; the larger the difference, the narrower the cores and the tighter the bends." }),
  claddingIndex: z.number().min(1).max(2.55).meta({ default: 1.45, description: "Refractive index of the cladding around the cores, 1.00 to 2.55 in steps of 0.01." }),
  portSpacing: z.number().int().min(-2147483648).max(2147483647).min(2).max(2000).meta({ default: 2, description: "Distance between the two inputs, and between the two outputs, in pixels. A value below what the design needs to keep the arms apart is raised to it." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type AndGatePart = z.infer<typeof AndGatePartSchema>

export const OrGatePartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("OrGate"),
  designFrequency: z.number().min(1).max(255).meta({ default: 50, description: "The source frequency the gate is designed for, on the same scale as a source\u0027s frequency: the frequency of the sources that drive it. The wavelength, and from it the core width, the coupler and the phase section, follow from it, so the gate works at this frequency only." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(50).meta({ default: 6, description: "Thickness in pixels of the anti-reflection band at the ends ticked below, with the index \u221An of the core and of the cladding; 0 for none. A quarter-wave band, the least reflecting, is \u03BB / (4 \u221An) pixels thick; the form shows it for the core." }),
  inputCoated: z.boolean().meta({ default: true, description: "Put the anti-reflection band on the input end. Switch it off where the gate takes its light from a gate before it, so the wave passes through the junction." }),
  outputCoated: z.boolean().meta({ default: true, description: "Put the anti-reflection band on the output end. Switch it off where the gate hands its light to a gate after it, so the wave passes through the junction." }),
  upperOutputActive: z.boolean().meta({ default: true, description: "Let the upper output\u0027s light leave the gate. Switched off, an absorber with a soft rise takes it in the last stretch of the arm." }),
  lowerOutputActive: z.boolean().meta({ default: true, description: "Let the lower output\u0027s light leave the gate. Switched off, an absorber with a soft rise takes it in the last stretch of the arm." }),
  coreIndex: z.number().min(1).max(2.55).meta({ default: 2.55, description: "Refractive index of the waveguide cores, 1.00 to 2.55 in steps of 0.01. It must be above the cladding\u0027s; the larger the difference, the narrower the cores and the tighter the bends." }),
  claddingIndex: z.number().min(1).max(2.55).meta({ default: 1.45, description: "Refractive index of the cladding around the cores, 1.00 to 2.55 in steps of 0.01." }),
  portSpacing: z.number().int().min(-2147483648).max(2147483647).min(2).max(2000).meta({ default: 2, description: "Distance between the two inputs, and between the two outputs, in pixels. A value below what the design needs to keep the arms apart is raised to it." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type OrGatePart = z.infer<typeof OrGatePartSchema>

export const ZonePlatePartSchema = z.strictObject({
  id: z.uuid(),
  kind: z.literal("ZonePlate"),
  mount: z.boolean().meta({ default: false, description: "Surround the plate with an absorbing mount, so no light passes outside its diameter. Without it, that light meets the focused light on the axis and moves the focus further out, the more so the fewer zones the plate has." }),
  mountWidth: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 200, description: "Width in pixels of the absorbing mount on either side, beyond the diameter." }),
  diameter: z.number().int().min(-2147483648).max(2147483647).min(2).max(2147483647).meta({ default: 800, description: "Extent across the axis in pixels; the zones fill it." }),
  thickness: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 64, description: "Extent along the axis in pixels: the depth of the blocks. It should leave room for the anti-reflection rise on both faces." }),
  coating: z.number().int().min(-2147483648).max(2147483647).min(0).max(400).meta({ default: 32, description: "Depth in pixels over which a block\u0027s absorption rises from nothing at its faces to its full strength, so the blocks reflect little; at most half the thickness takes effect." }),
  designFrequency: z.number().min(1).max(255).meta({ default: 50, description: "The source frequency the plate is designed for, on the same scale as a source\u0027s frequency. The zones follow from the wavelength it gives, so the plate focuses this frequency at the focal length; other frequencies focus elsewhere." }),
  focalLength: z.number().int().min(-2147483648).max(2147483647).min(1).max(2147483647).meta({ default: 600, description: "Distance in pixels from the plate to its focus, for a plane wave arriving along the axis." }),
  inverse: z.boolean().meta({ default: false, description: "Exchange the zones: the central zone becomes a block, the first ring open, and so on. The focus stays where it is." }),
  name: z.string().meta({ description: "Name shown in the project tree and on the canvas." }),
  enabled: z.boolean().meta({ default: true, description: "Paint the part into the scene; a disabled part has no effect on a run." }),
  x: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "X of the part\u0027s centre in canvas pixels." }),
  y: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ description: "Y of the part\u0027s centre in canvas pixels." }),
  angle: z.number().gt(-180).max(180).meta({ default: 0, description: "Rotation about the centre in degrees, counter-clockwise on the screen. 0 points the part\u0027s axis to the right." }),
})
export type ZonePlatePart = z.infer<typeof ZonePlatePartSchema>

export const OpticalPartSchema = z.discriminatedUnion('kind', [LensPartSchema, PolygonPartSchema, RectanglePartSchema, EllipsePartSchema, TrianglePartSchema, OutlinePartSchema, FiberPartSchema, BraggReflectorPartSchema, GrinLensPartSchema, XorGatePartSchema, AndGatePartSchema, OrGatePartSchema, ZonePlatePartSchema])
export type OpticalPart = z.infer<typeof OpticalPartSchema>

export const ProbeLayerSchema = z.enum(["Field", "Intensity"])
export type ProbeLayer = z.infer<typeof ProbeLayerSchema>

export const ProbeMeasurementSchema = z.enum(["Amplitude", "Value", "IntegratedValue"])
export type ProbeMeasurement = z.infer<typeof ProbeMeasurementSchema>

export const ProbeKindSchema = z.enum(["Point", "Line"])
export type ProbeKind = z.infer<typeof ProbeKindSchema>

export const ProbeBoxSizeSchema = z.enum(["Tiny", "Small", "Medium", "Large", "ExtraLarge", "Custom"])
export type ProbeBoxSize = z.infer<typeof ProbeBoxSizeSchema>

export const ProbeScaleModeSchema = z.enum(["Running", "Full"])
export type ProbeScaleMode = z.infer<typeof ProbeScaleModeSchema>

export const ProbeValueScaleSchema = z.enum(["Linear", "Logarithmic"])
export type ProbeValueScale = z.infer<typeof ProbeValueScaleSchema>

export const ProbeSchema = z.strictObject({
  id: z.uuid(),
  name: z.string().meta({ description: "Name shown in the project tree and in the graph title." }),
  active: z.boolean().meta({ default: true, description: "Sample and draw this probe." }),
  layer: ProbeLayerSchema.meta({ default: "Field", description: "Output the probe samples and is drawn on." }),
  measurement: ProbeMeasurementSchema.meta({ default: "Amplitude", description: "Recorded quantity. The field offers its amplitude only; the intensity layer its value, or the value integrated over the run." }),
  kind: ProbeKindSchema.meta({ default: "Point", description: "A single point, or a line from A to B." }),
  ax: z.number().int().min(-2147483648).max(2147483647).meta({ description: "X of the probe pixel, or of the start of the line, in canvas pixels." }),
  ay: z.number().int().min(-2147483648).max(2147483647).meta({ description: "Y of the probe pixel, or of the start of the line, in canvas pixels." }),
  bx: z.number().int().min(-2147483648).max(2147483647).meta({ default: 0, description: "X of the end of the line in canvas pixels; line probes only." }),
  by: z.number().int().min(-2147483648).max(2147483647).meta({ default: 0, description: "Y of the end of the line in canvas pixels; line probes only." }),
  boxSize: ProbeBoxSizeSchema.meta({ default: "Medium", description: "Nominal size of the graph box drawn over the output; a box larger than the canvas is shrunk to fit it, aspect kept." }),
  boxX: z.number().int().min(-2147483648).max(2147483647).meta({ default: 0, description: "Left edge of the graph box in canvas pixels. A box placed on the probe\u0027s point A moves with the probe; dragging it close snaps it on." }),
  boxY: z.number().int().min(-2147483648).max(2147483647).meta({ default: 0, description: "Top edge of the graph box in canvas pixels. A box placed on the probe\u0027s point A moves with the probe; dragging it close snaps it on." }),
  boxWidth: z.number().int().min(-2147483648).max(2147483647).min(60).max(4096).meta({ default: 400, description: "Width of the graph box in canvas pixels; custom box size only. A box larger than the canvas is shrunk to fit it, aspect kept." }),
  boxHeight: z.number().int().min(-2147483648).max(2147483647).min(30).max(4096).meta({ default: 200, description: "Height of the graph box in canvas pixels; custom box size only. A box larger than the canvas is shrunk to fit it, aspect kept." }),
  scaleMode: ProbeScaleModeSchema.meta({ default: "Running", description: "Sliding window of the last steps, or the whole run; point probes only." }),
  autoScaleX: z.boolean().meta({ default: true, description: "Let the step axis follow the samples recorded so far; point probes only." }),
  autoScaleY: z.boolean().meta({ default: true, description: "Fit the value axis to the data instead of the fixed minimum and maximum." }),
  yMin: z.number().meta({ default: -1, description: "Lower bound of the value axis when it is not auto-scaled." }),
  yMax: z.number().meta({ default: 1, description: "Upper bound of the value axis when it is not auto-scaled." }),
  yScale: ProbeValueScaleSchema.meta({ default: "Linear", description: "A linear or a logarithmic value axis; intensity probes only, the field is always linear." }),
  decades: z.number().int().min(-2147483648).max(2147483647).min(1).max(12).meta({ default: 6, description: "Decades a logarithmic value axis spans below its top; smaller values, zero included, sit on the bottom line." }),
  window: z.number().int().min(-2147483648).max(2147483647).min(10).max(1000000).meta({ default: 500, description: "Width of the sliding step window, in steps." }),
  startAfter: z.number().int().min(-2147483648).max(2147483647).min(0).max(2147483647).meta({ default: 0, description: "Step at which recording starts; 0 records from the first step." }),
})
export type Probe = z.infer<typeof ProbeSchema>

export const ColorSchemeKindSchema = z.enum(["Unspecified", "Field", "Intensity"])
export type ColorSchemeKind = z.infer<typeof ColorSchemeKindSchema>

export const ColorStopSchema = z.strictObject({
  position: z.number().min(0).max(1).meta({ description: "Position along the gradient, 0 at the lowest value to 1 at the highest." }),
  colorText: z.string().regex(new RegExp("^#[0-9A-Fa-f]{6}$")),
})
export type ColorStop = z.infer<typeof ColorStopSchema>

export const ColorSchemeSchema = z.strictObject({
  id: z.uuid(),
  name: z.string().meta({ description: "Name the display settings refer to." }),
  kind: ColorSchemeKindSchema.meta({ default: "Unspecified", description: "Output the scheme is meant for; the pickers list it for that output only." }),
  description: z.string().meta({ default: "", description: "Free text: what the scheme is, where it comes from." }),
  stops: z.array(ColorStopSchema),
})
export type ColorScheme = z.infer<typeof ColorSchemeSchema>

export const ProjectSchema = z.strictObject({
  version: z.literal(3).meta({ description: "Version of the project file format." }),
  seed: z.number().int().min(-2147483648).max(2147483647).meta({ description: "Seed of all random decisions (emitter jitter, random gaps). The same seed reproduces the same frames." }),
  description: z.string().meta({ default: "", description: "Free-text notes about the project." }),
  scene: SceneSchema.meta({ description: "The canvas: its size or image, damping border and image sources." }),
  simulation: SimulationSchema.meta({ description: "Run length and solver parameters." }),
  display: DisplaySchema.meta({ description: "How the field and intensity outputs are rendered, previewed and exported." }),
  sources: z.array(SourceSchema),
  parts: z.array(OpticalPartSchema),
  probes: z.array(ProbeSchema),
  colorSchemes: z.array(ColorSchemeSchema),
})
export type Project = z.infer<typeof ProjectSchema>

