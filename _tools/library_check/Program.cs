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
using System.Globalization;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Serialization;
using NLog;
using WaveDudeNext.Engine;
using WaveDudeNext.Export;
using WaveDudeNext.Model;
using WaveDudeNext.Parts;
using WaveDudeNext.Probes;
using WaveDudeNext.Rendering;
using WaveDudeNext.Runs;

namespace LibraryCheck;

// Drives the library end to end: CPU and (if available) GPU engines over the same scene and compares their
// fields, seed determinism, the model-to-emitter mapping, the frame store, the renderer, the colour scheme
// files, the optical part painter and a headless run. Exit code 0 = all checks passed.
internal static class Program
{
	private static int _failures;

	private static void Check(bool ok, string what)
	{
		Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
		if (!ok)
		{
			_failures++;
		}
	}

	private static Emitter Make(int x, int y, int level, double amplitude, SourceMode mode = SourceMode.Continuous, PulseShape shape = PulseShape.Gaussian,
		double phase = 0, int onWidth = 10, int offWidth = 10, int detach = -1, int randomize = 0, uint seed = 1) => new()
	{
		X = x, Y = y, Level = level, Amplitude = amplitude, PhaseRadians = phase, Mode = mode, PulseShape = shape,
		OnWidthCycles = onWidth, OffWidthCycles = offWidth, OffWidthBase = offWidth, RandomizeOffWidthAmount = randomize, DetachAfterStep = detach, RngSeed = seed,
	};

	private static List<Emitter> Sources() =>
	[
		Make(20, 24, 120, 1.0),
		Make(44, 12, 200, 0.7, SourceMode.Pulsed, PulseShape.Gaussian, phase: 0.5, onWidth: 4, offWidth: 6),
		Make(44, 36, 90, 0.5, SourceMode.Pulsed, PulseShape.SmoothedSquare, onWidth: 5, offWidth: 3, detach: 120),
		Make(99, 99, 50, 9.0), // outside: must be dropped
	];

	private static float MaxAbs(ReadOnlySpan<float> a)
	{
		float m = 0f;
		foreach (float v in a)
		{
			m = Math.Max(m, Math.Abs(v));
		}
		return m;
	}

	private static float MaxAbs(ReadOnlySpan<Half> a)
	{
		float m = 0f;
		foreach (Half v in a)
		{
			m = Math.Max(m, Math.Abs((float)v));
		}
		return m;
	}

	private static float MaxDiff(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
	{
		float m = 0f;
		for (int i = 0; i < a.Length; i++)
		{
			m = Math.Max(m, Math.Abs(a[i] - b[i]));
		}
		return m;
	}

	private static float MaxDiff(float[]?[] a, float[]?[] b)
	{
		float m = 0f;
		for (int i = 0; i < a.Length; i++)
		{
			if (a[i] == null || b[i] == null)
			{
				if (a[i] != b[i])
				{
					return float.PositiveInfinity;
				}
				continue;
			}
			if (a[i]!.Length != b[i]!.Length)
			{
				return float.PositiveInfinity;
			}
			m = Math.Max(m, MaxDiff(a[i], b[i]));
		}
		return m;
	}

	private static ProbeSampler Probes(int w, int h) => ProbeSampler.Build(
	[
		new ProbeSpec("A", ProbeKind.Point, ProbeMeasurement.Amplitude, 20, 24, 0, 0, 0),
		new ProbeSpec("B", ProbeKind.Line, ProbeMeasurement.IntegratedValue, 5, 5, 40, 30, 50),
		new ProbeSpec("C", ProbeKind.Point, ProbeMeasurement.Value, 30, 20, 0, 0, 0),
	], w, h);

	private static void Run(IWaveEngine engine, int steps)
	{
		for (int i = 0; i < steps; i++)
		{
			engine.AdvanceOneStep();
			engine.SampleProbes();
		}
		engine.PrepareFrame(true, true);
	}

	/// <summary>A 64×48 BGRA scene: refractive index 1.00 everywhere, one green source pixel at (10, 10).</summary>
	private static (byte[] Bgra, int Stride) ScenePixels(int w, int h)
	{
		int stride = w * 4 + 8; // deliberately padded rows
		byte[] bgra = new byte[stride * h];
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				bgra[y * stride + x * 4 + 2] = 100; // R
				bgra[y * stride + x * 4 + 3] = 255; // A
			}
		}
		bgra[10 * stride + 10 * 4 + 1] = 160; // G at (10, 10)
		return (bgra, stride);
	}

	private static int Main(string[] args)
	{
		LogManager.Setup().LoadConfiguration(c => c.ForLogger().FilterMinLevel(LogLevel.Info).WriteToConsole(layout: "  log  ${level}: ${message}"));
		if (args.Length > 0 && args[0] == "bench")
		{
			Bench(args.Length > 1 ? int.Parse(args[1]) : 1920, args.Length > 2 ? int.Parse(args[2]) : 1080);
			return 0;
		}
		if (args.Length > 2 && args[0] == "simplify-schemes")
		{
			// one-off: slims every .wdscheme of a folder with more than the editor's 9 stops to its bends (2026-10-14)
			double tolerance = double.Parse(args[2], CultureInfo.InvariantCulture);
			foreach (string file in Directory.GetFiles(args[1], "*" + ColorSchemeStore.XmlExtension))
			{
				ColorScheme scheme = ColorSchemeStore.LoadXml(file);
				int before = scheme.Stops.Count;
				if (before <= ColorScheme.StopCount)
				{
					continue;
				}
				scheme.Simplify(tolerance);
				ColorSchemeStore.SaveXml(scheme, file);
				Console.WriteLine($"{Path.GetFileName(file)}: {before} -> {scheme.Stops.Count} stops");
			}
			return 0;
		}
		if (args.Length > 1 && args[0] == "convert-schemes")
		{
			// one-off: the author's JSON files of a folder become .wdscheme files beside them (2026-10-13)
			foreach (string file in Directory.GetFiles(args[1], "*.json"))
			{
				ColorScheme scheme = ColorSchemeStore.Load(file);
				string target = ColorSchemeStore.PathFor(args[1], Path.GetFileNameWithoutExtension(file));
				ColorSchemeStore.SaveXml(scheme, target);
				Console.WriteLine($"{Path.GetFileName(file)} -> {Path.GetFileName(target)} ({scheme.Stops.Count} stops)");
			}
			return 0;
		}
		if (args.Length > 0 && args[0] == "bench-paint")
		{
			// how long the part painter takes on a Full HD canvas: an outline, a lens, a polygon (2026-10-19, a drag was sluggish)
			var project = new Project();
			project.Scene.Width = 1920;
			project.Scene.Height = 1080;
			var outline = project.AddPart<OutlinePart>();
			foreach ((int px, int py) in new[] { (-200, -150), (200, -120), (150, 180), (-180, 120) })
			{
				outline.Points.Add(new PartPoint { X = px, Y = py });
			}
			var lens = project.AddPart<LensPart>();
			lens.Diameter = 400;
			var polygon = project.AddPart<PolygonPart>();
			polygon.Side = 150;
			// a fibre in an S-bend across the whole canvas: its bounding box is the canvas (2026-10-22)
			var fiber = project.AddPart<FiberPart>();
			fiber.Nodes[0].X = -900;
			fiber.Nodes[0].SetHandle(true, 400, -500);
			fiber.Nodes[1].X = 900;
			fiber.Nodes[1].SetHandle(false, -400, 500);
			foreach (OpticalPart part in project.Parts)
			{
				var single = new List<OpticalPart> { part };
				PartPainter.Paint(null, 1920, 1080, 1.0, single);
				var timer = Stopwatch.StartNew();
				for (int i = 0; i < 10; i++)
				{
					PartPainter.Paint(null, 1920, 1080, 1.0, single);
				}
				Console.WriteLine($"  {part.NameStem}: {timer.Elapsed.TotalMilliseconds / 10:0.0} ms per paint");
			}
			var blank = Stopwatch.StartNew();
			for (int i = 0; i < 10; i++)
			{
				PartPainter.Paint(null, 1920, 1080, 1.0, [new OutlinePart { Name = "empty" }]);
			}
			Console.WriteLine($"  blank canvas alone: {blank.Elapsed.TotalMilliseconds / 10:0.0} ms");
			var edges = Stopwatch.StartNew();
			ScenePixels painted = PartPainter.Paint(null, 1920, 1080, 1.0, project.Parts)!;
			for (int i = 0; i < 10; i++)
			{
				GlowingEdges.Compute(painted.Bgra, painted.Width, painted.Height, painted.StrideBytes);
			}
			Console.WriteLine($"  glowing edges: {edges.Elapsed.TotalMilliseconds / 10:0.0} ms");
			return 0;
		}
		if (args.Length > 0 && args[0] == "bench-render")
		{
			BenchRender(args.Length > 1 ? int.Parse(args[1]) : 1920, args.Length > 2 ? int.Parse(args[2]) : 1080, args.Length > 3 && args[3] == "load");
			return 0;
		}
		if (args.Length > 0 && args[0] == "bench-export")
		{
			ExportChecks.Bench(args.Length > 1 ? int.Parse(args[1]) : 1920, args.Length > 2 ? int.Parse(args[2]) : 1080);
			return 0;
		}
		if (args.Length > 0 && args[0] == "export")
		{
			// just the export checks (they take a few seconds and need no engine)
			ExportChecks.Run(Check);
			Console.WriteLine(_failures == 0 ? "EXPORT OK" : _failures + " check(s) failed");
			return _failures == 0 ? 0 : 1;
		}
		const int W = 64, H = 48, Steps = 200;
		(byte[] bgra, int stride) = ScenePixels(W, H);
		SceneData scene = SceneData.FromBgra(bgra, W, H, stride, true, 6);
		Check(scene.SourceCount == 1 && scene.SourceX[0] == 10 && scene.SourceLevel[0] == 160, "scene from BGRA has 1 image source at (10, 10) level 160");
		Check(SceneData.CountSources(bgra, W, H, stride) == 1, "CountSources agrees");
		Check(Math.Abs(scene.C2[0] - 1f) < 1e-6f && scene.D[0] == 0f && scene.D[7 * W + 7] == 1f, "c² = 1, damping edge of 6 (corner 0, inside 1)");

		CheckEngines(scene, W, H, Steps);
		CheckCaptureResetEmitters(scene, W, H);
		CheckSceneReplacement();
		CheckDeterminism(scene);
		CheckEmitterMapping();
		CheckMirror(W, H);
		CheckDecimalLevels(W, H);
		CheckMovable();
		CheckRanges();
		CheckHistory();
		CheckClipboard();
		CheckProjectSchema();
		CheckUnreadableFile();
		CheckOpenEdges(W, H);
		CheckWorkGroups();
		CheckFrameStore(scene);
		CheckFirstStart();
		CheckPreviewQuality(scene, W, H);
		CheckStoreBytes();
		CheckRenderer();
		CheckColorSchemes();
		CheckProbeGraph();
		CheckImport();
		CheckParts();
		CheckPainterRuns();
		CheckAbsorbingParts();
		CheckXorGate();
		CheckGatePortFollower();
		CheckZonePlate();
		CheckBrightnessFit();
		CheckRun();
		ExportChecks.Run(Check);

		Console.WriteLine(_failures == 0 ? "LIBRARY OK" : _failures + " check(s) failed");
		return _failures == 0 ? 0 : 1;
	}

	/// <summary>
	/// The first start's choice of the preview quality (2026-10-31): the facts of this machine can be read, the cache
	/// drive's kind among them, and the tier table holds at its edges on the facts of machines this one is not.
	/// </summary>
	private static void CheckFirstStart()
	{
		MachineFacts facts = MachineFacts.Read();
		// the drive's kind may be unknown (the workstation's C: is a RAID controller's virtual disk, which answers neither
		// on the volume nor on the disk): the query must answer the same twice and refuse paths without a volume
		Check(facts.PhysicalBytes > 1L << 30 && facts.Threads >= 1 && facts.CacheDrive.Length > 0
			&& StorageDevice.IncursSeekPenalty(FrameCache.Directory) == facts.CacheDriveSeekPenalty
			&& StorageDevice.IncursSeekPenalty("relative" + Path.DirectorySeparatorChar + "path") == null && StorageDevice.IncursSeekPenalty(@"\\server\share\cache") == null,
			"first start: this machine's facts: " + FrameMemory.Gigabytes(facts.PhysicalBytes) + ", " + facts.Threads + " threads, GPU '" + facts.Gpu + "', cache drive " + facts.CacheDrive
			+ " seek penalty " + (facts.CacheDriveSeekPenalty?.ToString() ?? "unknown"));
		static MachineFacts Machine(double gigabytes, int threads, bool gpu, bool? seekPenalty) => new((long)(gigabytes * (1L << 30)), threads, gpu ? "GPU" : "", "C:", seekPenalty);
		Check(PreviewDefaults.TierOf(Machine(15.9, 8, true, false)) == PreviewTier.Middle
			&& PreviewDefaults.TierOf(Machine(15.9, 4, true, false)) == PreviewTier.Low
			&& PreviewDefaults.TierOf(Machine(15.9, 8, true, true)) == PreviewTier.Low
			&& PreviewDefaults.TierOf(Machine(15.9, 8, false, false)) == PreviewTier.Low
			&& PreviewDefaults.TierOf(Machine(11.9, 16, true, false)) == PreviewTier.Low
			&& PreviewDefaults.TierOf(Machine(23.9, 8, true, null)) == PreviewTier.Middle
			&& PreviewDefaults.TierOf(Machine(31.9, 2, false, true)) == PreviewTier.High
			&& PreviewDefaults.TierOf(Machine(63.9, 20, true, false)) == PreviewTier.High,
			"first start: 32 GB is High whatever else; below it under 16 GB, a seek penalty, no GPU or 4 threads is Low, an unknown drive is not");
		Check(PreviewDefaults.QualityOf(PreviewTier.Low) == new PreviewQuality(2, 2, FrameFormat.Index) && PreviewDefaults.DitherBitsOf(PreviewTier.Low) == 0
			&& PreviewDefaults.QualityOf(PreviewTier.Middle) == new PreviewQuality(1, 1, FrameFormat.Index) && PreviewDefaults.DitherBitsOf(PreviewTier.Middle) == 0
			&& PreviewDefaults.QualityOf(PreviewTier.High) == new PreviewQuality(1, 1, FrameFormat.Half) && PreviewDefaults.DitherBitsOf(PreviewTier.High) == 16,
			"first start: Low is half resolution, every second frame, 8-bit; Middle the fixed defaults; High 16-bit dithered to 16 bits");
	}

	/// <summary>
	/// Every <see cref="RangeAttribute"/> on the model agrees with its owner's Normalize(): a value below or above the
	/// range lands inside it, and a value at an inclusive bound stays as it is. Each leg runs on a fresh instance, so
	/// dependent clamps (a triangle's second base angle) see the state the case prepares. Every part kind is found by
	/// reflection, and a persisted number that Normalize() clamps without a [Range] fails its owner (<see cref="CheckUnranged"/>).
	/// </summary>
	private static void CheckRanges()
	{
		Console.WriteLine("--- [Range] attributes against Normalize()");
		static (Action Normalize, object[] Targets) Part(OpticalPart part) => (part.Normalize, [part]);
		(string Name, Func<(Action Normalize, object[] Targets)> Make)[] cases =
		[
			("Scene", () => { var s = new Scene(); return (s.Normalize, [s]); }),
			("Simulation", () => { var s = new Simulation(); return (s.Normalize, [s]); }),
			("Display", () => { var d = new Display(); return (d.Normalize, [d, d.Field, d.Intensity, d.Video]); }),
			("Source", () => { var s = new Source { Array = new SourceArray() }; return (s.Normalize, [s, s.Array]); }),
			("Probe", () => { var p = new Probe(); return (p.Normalize, [p]); }),
			("ColorStop", () =>
			{
				var scheme = new ColorScheme();
				var stop = new ColorStop(0.5, new Rgb(1, 2, 3));
				scheme.Stops.Add(stop);
				return (scheme.Normalize, [stop]);
			}),
			// every part kind, found rather than listed, so a new one cannot be left out; a custom triangle with the first
			// angle at its minimum leaves the second its full range
			.. typeof(OpticalPart).Assembly.GetTypes()
				.Where(type => type.IsSubclassOf(typeof(OpticalPart)) && !type.IsAbstract)
				.OrderBy(type => type.Name, StringComparer.Ordinal)
				.Select(type => (type.Name, (Func<(Action, object[])>)(() => Part(type == typeof(TrianglePart)
					? new TrianglePart { Type = TriangleType.Custom, AngleA = 1.0 }
					: (OpticalPart)Activator.CreateInstance(type)!)))),
		];
		// clamped by Normalize() against a bound that depends on another value, which stays in code alone (CLAUDE.md)
		// (a probe's value axis falls back to -1..1 when its top is not above its bottom)
		HashSet<string> dependentBounds = ["Probe.YMin", "Probe.YMax"];
		int total = 0;
		foreach ((string name, Func<(Action Normalize, object[] Targets)> make) in cases)
		{
			var failures = new List<string>();
			int count = 0;
			object[] shape = make().Targets;
			for (int t = 0; t < shape.Length; t++)
			{
				foreach (PropertyInfo property in shape[t].GetType().GetProperties())
				{
					if (property.GetCustomAttribute<RangeAttribute>() is not { } range)
					{
						CheckUnranged(make, t, property, dependentBounds, failures);
						continue;
					}
					count++;
					bool integer = property.PropertyType == typeof(int);
					double min = Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture);
					double max = Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture);
					// the value to set and what Normalize() must leave: the bound itself, or NaN for "inside the range"
					var legs = new List<(string Leg, double Set, double Expect)>();
					if (min > (integer ? int.MinValue : double.MinValue))
					{
						legs.Add(("below", integer ? min - 1 : min - Math.Max(1.0, Math.Abs(min)), double.NaN));
					}
					if (max < (integer ? int.MaxValue : double.MaxValue))
					{
						legs.Add(("above", integer ? max + 1 : max + Math.Max(1.0, Math.Abs(max)), double.NaN));
					}
					if (!range.MinimumIsExclusive)
					{
						legs.Add(("at min", min, min));
					}
					if (!range.MaximumIsExclusive)
					{
						legs.Add(("at max", max, max));
					}
					foreach ((string leg, double set, double expect) in legs)
					{
						string where = $"{shape[t].GetType().Name}.{property.Name} {leg} ({range.Minimum}..{range.Maximum}): set {set}";
						try
						{
							(Action normalize, object[] targets) = make();
							object target = targets[t];
							property.SetValue(target, integer ? (object)(int)set : set);
							normalize();
							double got = Convert.ToDouble(property.GetValue(target), CultureInfo.InvariantCulture);
							bool inside = (range.MinimumIsExclusive ? got > min : got >= min) && (range.MaximumIsExclusive ? got < max : got <= max);
							if (!(double.IsNaN(expect) ? inside : got == expect))
							{
								failures.Add($"{where}, got {got}");
							}
						}
						catch (Exception e)
						{
							failures.Add($"{where}, threw {e.GetType().Name}: {e.Message}");
						}
					}
				}
			}
			total += count;
			Check(count > 0 && failures.Count == 0, $"{name}: {count} [Range] attribute(s) agree with Normalize(), and no persisted number is clamped without one");
			foreach (string failure in failures)
			{
				Console.WriteLine("         " + failure);
			}
		}
		Console.WriteLine($"  {total} ranged properties");
	}

	/// <summary>
	/// A persisted number without a <see cref="RangeAttribute"/> must come out of Normalize() as it went in, far below and
	/// far above any sensible value; one that is clamped lacks its attribute, unless its bound depends on another value
	/// (<paramref name="dependentBounds"/>, "Type.Property"). Failures are added to <paramref name="failures"/>.
	/// </summary>
	private static void CheckUnranged(Func<(Action Normalize, object[] Targets)> make, int t, PropertyInfo property, HashSet<string> dependentBounds, List<string> failures)
	{
		bool integer = property.PropertyType == typeof(int);
		if (!(integer || property.PropertyType == typeof(double)) || !property.CanWrite || property.GetCustomAttribute<XmlAttributeAttribute>() is null
			|| dependentBounds.Contains($"{property.DeclaringType!.Name}.{property.Name}") || dependentBounds.Contains($"{property.ReflectedType!.Name}.{property.Name}"))
		{
			return;
		}
		foreach (double set in (double[])[-1_000_000, 1_000_000])
		{
			string where = $"{property.ReflectedType!.Name}.{property.Name} has no [Range]: set {set}";
			try
			{
				(Action normalize, object[] targets) = make();
				property.SetValue(targets[t], integer ? (object)(int)set : set);
				normalize();
				double got = Convert.ToDouble(property.GetValue(targets[t]), CultureInfo.InvariantCulture);
				if (got != set)
				{
					failures.Add($"{where}, Normalize() clamped it to {got}");
				}
			}
			catch (Exception e)
			{
				failures.Add($"{where}, threw {e.GetType().Name}: {e.Message}");
			}
		}
	}

	/// <summary>
	/// The project file's DTD (<see cref="ProjectSchema"/>, 2026-10-31): built from the serializer attributes, it accepts what
	/// Save writes for a project with every kind of object and for every example, and such a file loads back; it reports a
	/// misspelt attribute, a value outside an enumeration and an element out of the written order, which the loader takes
	/// anyway. Validated by .NET's own DTD validator (DtdProcessing.Parse with ValidationType.DTD), which the app never runs.
	/// </summary>
	private static void CheckProjectSchema()
	{
		string subset = ProjectSchema.InternalSubset;
		Check(subset.Contains("<!ELEMENT Project (Description?, Scene?, Simulation?, Display?, Source*, (Lens|") && subset.Contains("<!ATTLIST Project Version CDATA #REQUIRED>"),
			"the DTD declares the root's children in the written order and its version as required");
		Check(subset.Contains("<!ATTLIST Source Mode (Continuous|Pulsed) #IMPLIED>") && subset.Contains("<!ATTLIST Source Enabled (true|false) #IMPLIED>"), "an enum attribute lists its members, a bool its two values");
		Check(subset.Contains("<!ELEMENT Fiber (Node*)>") && subset.Contains("<!ELEMENT Outline (Point*)>") && subset.Contains("<!ELEMENT ImageData (#PCDATA)>"), "child elements and text elements are declared");
		Check(subset.Contains("<!-- ") == ProjectSchema.Comments, "comments in the DTD follow the Comments constant");
		Check(!ProjectSchema.Comments || System.Text.RegularExpressions.Regex.IsMatch(subset, @"<!-- [^\r\n]*Default 1920\. Range \d+ to \d+\. -->\r?\n\t<!ATTLIST Scene Width CDATA #IMPLIED>"),
			"an attribute's comment names its description, default and range on the line before it");

		var project = new Project { Description = "Every kind of object" };
		project.Scene.ImageData = [1, 2, 3];
		Source source = project.AddSource();
		source.Array = new SourceArray();
		Probe probe = project.AddProbe();
		probe.Kind = ProbeKind.Line;
		probe.BoxSize = ProbeBoxSize.Custom;
		project.AddColorScheme();
		foreach (Type kind in typeof(OpticalPart).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(OpticalPart)) && !t.IsAbstract).OrderBy(t => t.Name))
		{
			project.AddPart(kind);
		}
		string xml = SaveToText(project);
		string newLine = Environment.NewLine;
		Check(xml.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + newLine + "<!DOCTYPE Project [" + newLine + "\t<!") && xml.Contains(newLine + "]>" + newLine + "<Project Version=\"3\""),
			"the file holds the declaration, the DTD and then the root");
		List<string> errors = ValidateAgainstDtd(xml);
		Check(errors.Count == 0, "a project with every kind of object validates against its DTD" + (errors.Count == 0 ? "" : ": " + errors[0]));
		Project back = LoadText(xml);
		Check(back.Parts.Count == project.Parts.Count && back.Sources.Count == 1 && back.Sources[0].Array != null && back.Probes.Count == 1 && back.ColorSchemes.Count == 1
			&& back.Scene.ImageData is { Length: 3 } && back.Description == "Every kind of object", "the file with its DTD loads back with everything");
		errors = ValidateAgainstDtd(xml.Replace("<Source Name=", "<Source Nmae="));
		Check(errors.Count == 1 && errors[0].Contains("Nmae"), "a misspelt attribute is reported" + (errors.Count == 0 ? "" : ": " + errors[0]));
		errors = ValidateAgainstDtd(xml.Replace("Kind=\"Line\"", "Kind=\"Curve\""));
		Check(errors.Count == 1 && errors[0].Contains("Curve"), "a value outside the enumeration is reported" + (errors.Count == 0 ? "" : ": " + errors[0]));
		int probeAt = xml.IndexOf("\t<Probe ");
		string probeLine = xml.Substring(probeAt, xml.IndexOf(newLine, probeAt) + newLine.Length - probeAt);
		string reordered = xml.Remove(probeAt, probeLine.Length).Replace("\t<Source ", probeLine + "\t<Source ");
		errors = ValidateAgainstDtd(reordered);
		Check(errors.Count > 0 && LoadText(reordered).Probes.Count == 1, "a probe before the sources is reported, and the loader takes it anyway" + (errors.Count == 0 ? "" : ": " + errors[0]));

		string? root = FindRepoRoot();
		if (root == null)
		{
			Console.WriteLine("  skip examples: the repository was not found above the harness");
			return;
		}
		int files = 0, valid = 0;
		foreach (string file in Directory.GetFiles(Path.Combine(root, "Examples"), "*.wdproj").Order())
		{
			files++;
			errors = ValidateAgainstDtd(SaveToText(Project.Load(file)));
			if (errors.Count == 0)
			{
				valid++;
			}
			else
			{
				Console.WriteLine("  " + Path.GetFileName(file) + ": " + errors[0]);
			}
		}
		Check(files >= 24 && valid == files, $"every example as saved now validates against the DTD ({valid} of {files})");
	}

	/// <summary>The project as its file: Save's bytes as text.</summary>
	private static string SaveToText(Project project)
	{
		using var stream = new MemoryStream();
		project.Save(stream);
		return System.Text.Encoding.UTF8.GetString(stream.ToArray());
	}

	/// <summary>A project read from file text.</summary>
	private static Project LoadText(string xml)
	{
		using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
		return Project.Load(stream);
	}

	/// <summary>The errors .NET's DTD validator reports for <paramref name="xml"/>, a document carrying its DTD; none for a valid one.</summary>
	private static List<string> ValidateAgainstDtd(string xml)
	{
		var errors = new List<string>();
		var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, ValidationType = ValidationType.DTD };
		settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
		using var reader = XmlReader.Create(new StringReader(xml), settings);
		while (reader.Read())
		{
		}
		return errors;
	}

	/// <summary>
	/// Undo and redo (<see cref="ProjectHistory"/>): a step written back gives the file it was recorded from, image
	/// included, into the same project object and lists; an edit that leaves the file as it was is no step; equal image
	/// bytes are shared between steps; a new edit after an undo drops the redo; the saved mark follows the steps.
	/// </summary>
	private static void CheckHistory()
	{
		Console.WriteLine("--- undo and redo (ProjectHistory)");
		static string FileOf(Project project)
		{
			using var stream = new MemoryStream();
			project.Save(stream);
			return System.Text.Encoding.UTF8.GetString(stream.ToArray());
		}
		var project = new Project { Seed = 7 };
		byte[] image = [1, 2, 3, 4, 5];
		project.Scene.ImageData = image;
		var history = new ProjectHistory(project);
		string first = FileOf(project);
		Check(history.Count == 1 && !history.CanUndo && !history.CanRedo && history.IsAtSaved, "a new history: one step, the saved one, nothing to undo or redo");
		Check(!history.Record(), "an unchanged project records no step");

		var sources = project.Sources;
		var parts = project.Parts;
		project.Seed = 8;
		project.AddSource().X = 40;
		project.AddPart<FiberPart>().Nodes[0].OutY = 30;
		project.AddProbe();
		project.Display.Field.Brightness = 2.5;
		project.Scene.ImageData = [9, 9, 9];
		Check(history.Record() && history.Count == 2 && !history.IsAtSaved, "an edit (seed, a source, a fibre node, a probe, a layer, the image) records a step");
		string second = FileOf(project);
		Scene secondScene = project.Scene;

		project.Scene.ImageData = [9, 9, 9];
		byte[] shared = project.Scene.ImageData!;
		Check(!history.Record(), "an image with equal bytes is no step");

		Check(history.Undo() && FileOf(project) == first && ReferenceEquals(project.Scene.ImageData, image) && history.IsAtSaved,
			"Undo writes the first step back: the same file, the first image's own bytes, the saved step");
		Check(ReferenceEquals(project.Sources, sources) && ReferenceEquals(project.Parts, parts) && project.Sources.Count == 0 && project.Parts.Count == 0,
			"the lists are the project's own, emptied: what is bound to them stays bound");
		Check(!ReferenceEquals(project.Scene, secondScene), "the settings objects are replaced by the step's");

		Check(history.Redo() && FileOf(project) == second && project.Sources.Count == 1 && project.Sources[0].Elements.Count > 0,
			"Redo writes the second step back, the source's emitters generated again");
		Check(project.Parts[0] is FiberPart { Nodes: [{ OutY: 30 }, _] }, "a fibre node's handle comes back with its part");
		Check(project.Scene.ImageData != null && !ReferenceEquals(project.Scene.ImageData, image) && project.Scene.ImageData.SequenceEqual(shared),
			"the second step's image comes back");

		history.Undo();
		project.Description = "another way";
		Check(history.Record() && !history.CanRedo && history.Count == 2 && !history.IsAtSaved,
			"an edit after an undo drops the undone step, and the saved state is no longer reachable by redo");
		history.MarkSaved();
		Check(history.IsAtSaved && history.Undo() && !history.IsAtSaved && history.Redo() && history.IsAtSaved, "the saved mark moves to the step saved");

		// 100 steps of a few kilobytes: unlimited is affordable
		for (int i = 0; i < 100; i++)
		{
			project.Seed = 100 + i;
			history.Record();
		}
		int undone = 0;
		while (history.Undo())
		{
			undone++;
		}
		Check(undone == 101 && history.Count == 102 && FileOf(project) == first, $"{undone} undos walk back through {history.Count} steps to the first");

		// what the design rests on: a step written back reaches the watcher as an edit, and the restored objects are watched like any
		project.AddSource().X = 12;
		history.Record();
		List<object> changed = [];
		List<ProjectChange> changes = [];
		using (var watcher = new ProjectWatcher(project))
		{
			watcher.Changed += (_, change) => changed.Add(change.Item);
			watcher.Changed += (_, change) => changes.Add(change);
			history.Undo();
			int seen = changed.Count;
			history.Redo();
			int afterRedo = changed.Count;
			project.Sources[0].X = 13;
			project.Scene.Width += 1;
			Check(seen > 0 && changed.Skip(afterRedo).Contains(project.Sources[0]) && changed.Skip(afterRedo).Contains(project.Scene),
				$"a step written back reaches the watcher ({seen} changes), and the restored source and scene are watched");
			// a change carries the value before and after (2026-10-27), for the log
			ProjectChange width = changes.Last(c => c.Item == project.Scene && c.Property == nameof(Scene.Width));
			Check(width.Values is { OldValue: int before, NewValue: int after } && after == before + 1 && width.NewValueText == after.ToString() && width.OldValueText == before.ToString()
				&& ProjectChange.Format(1.5) == "1.5" && ProjectChange.Format("a") == "'a'" && ProjectChange.Format(SourceMode.Pulsed) == "Pulsed" && ProjectChange.Format(null) == "null" && ProjectChange.Format(new byte[3]) == "byte[3]"
				&& changes.Last(c => c.Property == nameof(Source.X)).Values is { OldValue: int, NewValue: 13 },
				"a change carries the value before and after (" + width.OldValueText + " → " + width.NewValueText + "), formatted invariant");
		}
	}

	/// <summary>
	/// Copy and paste (<see cref="ProjectClipboard"/>): the objects travel as clipboard text into another project, behind
	/// its current item, renamed only on a clash, their emitters from the new project's seed; a settings object replaces on its
	/// own row or the project root only; text that is no clip is refused.
	/// </summary>
	private static void CheckClipboard()
	{
		Console.WriteLine("--- copy and paste (ProjectClipboard)");
		var from = new Project { Seed = 3 };
		Source one = from.AddSource();
		one.X = 40;
		from.AddSource();
		var fiber = from.AddPart<FiberPart>();
		fiber.Nodes[0].OutY = 30;
		Probe probe = from.AddProbe();
		ColorScheme scheme = from.AddColorScheme();
		from.Scene.Width = 333;
		string text = ProjectClipboard.Write(from, [probe, fiber, one, from.Sources[1], scheme, new object()]);
		Check(text.StartsWith("<Source", StringComparison.Ordinal) && text.Contains("<Fiber") && text.Contains("<Probe") && text.Contains("<ColorScheme")
			&& !text.Contains("Project") && !text.Contains("Seed") && !text.Contains("Version") && !text.Contains("<Scene"),
			"a copy is a fragment of the objects' own elements, in tree order: no wrapper, seed, version or settings not chosen; unknown objects left out");

		var to = new Project { Seed = 9 };
		Source existing = to.AddSource();
		Source last = to.AddSource();
		ProjectClipboard.Clip? clip = ProjectClipboard.Read(text);
		Check(clip is { HasItems: true } && clip.CanPaste(to, null), "the text reads back as a clip with items");
		IReadOnlyList<object> pasted = ProjectClipboard.Paste(to, clip!, existing);
		Check(pasted.Count == 5 && to.Sources.Select(s => s.Name).SequenceEqual(["Source 1", "Source 1 copy", "Source 2 copy", "Source 2"]),
			"sources paste behind the current one, in order, renamed only where the name is taken: " + string.Join(", ", to.Sources.Select(s => s.Name)));
		Check(to.Sources[1] is { X: 40 } pastedOne && !ReferenceEquals(pastedOne, one) && pastedOne.Elements.Count > 0,
			"a pasted source keeps its position, is a copy, and has its emitters");
		Check(to.Parts is [FiberPart { Name: "Fiber 1", Nodes: [{ OutY: 30 }, _] }] && to.Probes is [{ Name: "Probe 1" }] && to.ColorSchemes.Count == 1,
			"a part (with its nodes), a probe and a scheme paste at the end of their lists, their free names kept");

		string scene = ProjectClipboard.Write(from, [from.Scene]);
		ProjectClipboard.Clip? sceneClip = ProjectClipboard.Read(scene);
		Check(sceneClip is { HasItems: false } && !sceneClip.CanPaste(to, to.Sources[0]) && sceneClip.CanPaste(to, to.Scene),
			"a copied scene can be pasted only onto the scene row");
		ProjectClipboard.Paste(to, sceneClip!, to.Scene);
		Check(to.Scene.Width == 333, "pasted onto the scene row, it replaces the scene");
		from.Simulation.Steps = 777;
		from.Display.Field.Brightness = 4.5;
		ProjectClipboard.Clip? settingsClip = ProjectClipboard.Read(ProjectClipboard.Write(from, [from.Simulation, from.Display]));
		Check(settingsClip != null && settingsClip.CanPaste(to, to) && !settingsClip.CanPaste(to, to.Scene), "settings can be pasted onto the project root, not onto another settings row");
		ProjectClipboard.Paste(to, settingsClip!, to);
		Check(to.Simulation.Steps == 777 && to.Display.Field.Brightness == 4.5 && to.Scene.Width == 333,
			"pasted onto the project root, each copied settings object replaces the project's, the others stay");

		ProjectClipboard.Paste(to, ProjectClipboard.Read(ProjectClipboard.Write(from, [from.Sources[1]]))!, to.Sources[0], before: true);
		Check(to.Sources[0].Name == "Source 2 copy 2" && to.Sources[1].Name == "Source 1",
			"a drop on the upper half of a row puts the item in front of it: " + string.Join(", ", to.Sources.Select(s => s.Name)));
		// a drop onto the picture: the first object's anchor on the drop pixel, the group's formation kept, inside the canvas
		var group = new Project();
		Source left = group.AddSource();
		(left.X, left.Y) = (100, 100);
		Probe right = group.AddProbe();
		(right.AX, right.AY) = (150, 120);
		var onto = new Project();
		IReadOnlyList<object> dropped = ProjectClipboard.Paste(onto, ProjectClipboard.Read(ProjectClipboard.Write(group, [left, right]))!, null);
		ProjectClipboard.PlaceAt(dropped, 300, 200, 1920, 1080);
		Check(onto.Sources[0] is { X: 300, Y: 200 } && onto.Probes[0] is { AX: 350, AY: 220 }, "dropped onto the picture, the first object lands on the drop pixel and the rest keep their offsets");
		ProjectClipboard.PlaceAt(dropped, 1910, 5, 1920, 1080);
		Check(onto.Sources[0] is { X: 1869, Y: 5 } && onto.Probes[0] is { AX: 1919, AY: 25 }, "near the edge the group stops as a whole inside the canvas, as a group drag does");
		Check(ProjectClipboard.Read("<Project />") == null && ProjectClipboard.Read("just text") == null, "other text is no clip");
		Check(ProjectClipboard.Read("<Source Name=\"A\" /><Bogus />") == null, "a fragment with an element the file does not know is refused as a whole");
		var twins = new Project();
		string twin = ProjectClipboard.Write(from, [from.Sources[0]]);
		ProjectClipboard.Paste(twins, ProjectClipboard.Read(twin + twin)!, null);
		Check(twins.Sources.Select(s => s.Name).SequenceEqual(["Source 1", "Source 1 copy"]), "the same object twice in one text pastes as the name and the name with copy");
	}

	/// <summary>"bench-render [w h]": what colouring a stored frame costs, per layer, at that size (the presenter's cost per picture).</summary>
	private static void BenchRender(int w, int h, bool load = false)
	{
		Console.WriteLine($"--- bench-render {w}x{h} (Vector128 accelerated: {System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}){(load ? ", under a frame store's load" : "")}");
		using var stop = new CancellationTokenSource();
		SimulationRun? run = null;
		FrameStore? runStore = null;
		if (load)
		{
			// the real thing: a GPU run of this size with its store, in this process, while the renderer is timed
			var project = new Project();
			project.Scene.Width = w;
			project.Scene.Height = h;
			project.Simulation.Steps = 4000;
			project.Simulation.VisualizeEvery = 2;
			Source source = project.AddSource();
			source.X = w / 3;
			source.Y = h / 2;
			source.Frequency = 120;
			RunSpec spec = RunSpec.Create(project, null, preferGpu: true);
			string path = Path.Combine(Path.GetTempPath(), "library_check-bench-run-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
			runStore = FrameStore.Create(path, spec.Shape, spec.Layers, spec.StoredFrameCount, spec.CaptureEvery);
			run = new SimulationRun(spec, runStore, null);
			run.Start();
			Thread.Sleep(1500);
			Console.WriteLine($"  run: {run.State}, {run.EngineDescription}");
		}
		var random = new Random(7);
		var field = new Half[w * h];
		var intensity = new Half[w * h];
		for (int i = 0; i < field.Length; i++)
		{
			field[i] = (Half)(random.NextDouble() * 2 - 1);
			intensity[i] = (Half)(random.NextDouble() * 1.5);
		}
		ColorLut lut = ColorLut.Build(ColorSchemeStore.LoadBuiltIn(null).First(s => s.Name == Display.DefaultFieldColorScheme));
		var pixels = new int[w * h];
		// the 8-bit preview format: the index the GPU's capture_index kernel computed, the CPU only looks the colour up
		var indices = new byte[w * h];
		for (int i = 0; i < indices.Length; i++)
		{
			indices[i] = (byte)ColorLut.FieldIndex((float)field[i], 1f);
		}
		foreach ((string name, Action render) in new (string, Action)[]
		{
			("8-bit index", () => FrameRenderer.RenderIndexed(indices, w, h, lut, pixels)),
			("intensity", () => FrameRenderer.Render(intensity, w, h, lut, SampleKind.IntensityRoot, 1f, pixels)),
			("field", () => FrameRenderer.Render(field, w, h, lut, SampleKind.Field, 1f, pixels)),
			("field, grey", () => FrameRenderer.Render(field, w, h, ColorLut.Grey, SampleKind.Field, 1f, pixels)),
			("intensity again", () => FrameRenderer.Render(intensity, w, h, lut, SampleKind.IntensityRoot, 1f, pixels)),
		})
		{
			for (int i = 0; i < 60; i++)
			{
				render();
			}
			Thread.Sleep(300); // the tiered JIT's background recompilation
			var clock = Stopwatch.StartNew();
			const int Runs = 100;
			for (int i = 0; i < Runs; i++)
			{
				render();
			}
			double ms = clock.Elapsed.TotalMilliseconds / Runs;
			Console.WriteLine($"  {name,-12} {ms,6:0.00} ms per layer  ({ms * 1e6 / (w * (double)h):0.00} ns per pixel)");
		}
		// a half table's build, the cost of a brightness the colours have not seen (a drag on the brightness box, Auto)
		{
			ColorLut fresh = ColorLut.Build(ColorSchemeStore.LoadBuiltIn(null).First(s => s.Name == "Inferno"));
			float brightness = 1f;
			for (int i = 0; i < 20; i++)
			{
				fresh.HalfTable(SampleKind.IntensityRoot, brightness += 0.01f);
			}
			Thread.Sleep(300);
			var clock = Stopwatch.StartNew();
			const int Builds = 100;
			for (int i = 0; i < Builds; i++)
			{
				fresh.HalfTable(i % 2 == 0 ? SampleKind.Field : SampleKind.IntensityRoot, brightness += 0.01f);
			}
			Console.WriteLine($"  half table build {clock.Elapsed.TotalMilliseconds / Builds,6:0.00} ms (65,536 colours from Inferno's {ColorSchemeStore.LoadBuiltIn(null).First(s => s.Name == "Inferno").Stops.Count} stops)");
		}
		// the preview's 16-bit path: resampled to the picture's size on screen, coloured, dithered or not
		foreach ((int tw, int th, int bits) in new[] { (w, h, 0), (w, h, 12), (w * 10 / 21, h * 10 / 21, 0), (w * 10 / 21, h * 10 / 21, 16), (w * 3 / 2, h * 3 / 2, 16) })
		{
			var screen = new ScreenRenderer(w, h, tw, th);
			var shown = new int[tw * th];
			for (int i = 0; i < 30; i++)
			{
				screen.Render(intensity, lut, SampleKind.IntensityRoot, 1f, bits, shown);
			}
			Thread.Sleep(300);
			var clock = Stopwatch.StartNew();
			const int Runs = 50;
			for (int i = 0; i < Runs; i++)
			{
				screen.Render(intensity, lut, SampleKind.IntensityRoot, 1f, bits, shown);
			}
			Console.WriteLine($"  screen {tw}x{th}{(bits == 0 ? "" : $", dithered from {bits} bits"),-26} {clock.Elapsed.TotalMilliseconds / Runs,6:0.00} ms per layer");
		}
		stop.Cancel();
		if (run != null)
		{
			Console.WriteLine($"  run while measuring: {run.State}, {run.StepsDone} steps done");
			run.Stop();
			run.Completion.Wait(10000);
			runStore?.Dispose();
		}
	}

	/// <summary>"bench [w h]": what a step costs on each engine at that size, pure stepping and with a frame readback every 2 steps.</summary>
	private static void Bench(int w, int h)
	{
		Console.WriteLine($"--- bench {w}x{h}");
		SceneData scene = SceneData.Empty(w, h, 64);
		List<Emitter> emitters = [Make(w / 3, h / 2, 120, 1.0), Make(2 * w / 3, h / 3, 200, 0.7, SourceMode.Pulsed, onWidth: 4, offWidth: 6)];
		foreach (bool fourth in new[] { false, true })
		{
			var options = new EngineOptions(2.0, 1.0, true, fourth, 10.0, 1.0);
			var clock = Stopwatch.StartNew();
			GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(scene, options, emitters, out string problems);
			Console.WriteLine($"  GPU engine build (4th order {fourth}): {clock.ElapsedMilliseconds} ms{(gpu == null ? ", " + problems : "")}");
			if (gpu != null)
			{
				using (gpu)
				{
					Time(gpu, "GPU", 300, fourth);
				}
			}
			if (fourth)
			{
				continue;
			}
			int cpuSteps = (long)w * h > 500_000 ? 40 : 300;
			clock.Restart();
			var cpu = new WaveSimulator(scene, options, emitters);
			Console.WriteLine($"  CPU engine build: {clock.ElapsedMilliseconds} ms");
			Time(cpu, "CPU", cpuSteps, fourth);
		}
	}

	private static void Time(IWaveEngine engine, string name, int steps, bool fourth)
	{
		for (int i = 0; i < 5; i++)
		{
			engine.AdvanceOneStep();
		}
		engine.PrepareFrame(true, true);
		var clock = Stopwatch.StartNew();
		for (int i = 0; i < steps; i++)
		{
			engine.AdvanceOneStep();
		}
		engine.PrepareFrame(true, false);
		double pure = clock.Elapsed.TotalMilliseconds / steps;
		clock.Restart();
		for (int i = 0; i < steps; i++)
		{
			engine.AdvanceOneStep();
			if (i % 2 == 0)
			{
				engine.PrepareFrame(true, true);
			}
		}
		double withReadback = clock.Elapsed.TotalMilliseconds / steps;
		// the run's pattern: capture into a pool of snapshots, await one frame late
		FrameShape full = FrameShape.Full(engine.Width, engine.Height);
		var snapshots = new[] { engine.CreateSnapshot(full, true), engine.CreateSnapshot(full, true), engine.CreateSnapshot(full, true) };
		int next = 0;
		clock.Restart();
		for (int i = 0; i < steps; i++)
		{
			engine.AdvanceOneStep();
			if (i % 2 == 0)
			{
				FrameSnapshot s = snapshots[next];
				next = (next + 1) % snapshots.Length;
				engine.AwaitFrame(s);
				engine.CaptureFrame(s, true);
			}
		}
		foreach (FrameSnapshot s in snapshots)
		{
			engine.AwaitFrame(s);
		}
		double withCapture = clock.Elapsed.TotalMilliseconds / steps;
		Console.WriteLine($"  {name} 4th order {fourth}: {pure:0.00} ms/step pure, {withReadback:0.00} ms/step with both planes read back every 2 steps, {withCapture:0.00} ms/step with pipelined half captures every 2 steps ({steps} steps)");
	}

	private static void CheckEngines(SceneData scene, int W, int H, int Steps)
	{
		foreach (bool fourth in new[] { false, true })
		{
			Console.WriteLine("--- engines, 4th order: " + fourth);
			var options = new EngineOptions(1.0, 1.0, true, fourth, 10.0, 1.0);
			var cpu = new WaveSimulator(scene, options, Sources());
			cpu.SetProbes(Probes(W, H));
			Run(cpu, Steps);
			float[] cpuField = (float[])cpu.FieldData.Clone();
			float[] cpuInten = (float[])cpu.IntensityData!.Clone();
			float[][] cpuSamples = cpu.TakeProbeSamples();
			float[]?[] cpuProfiles = cpu.ReadProbeProfiles();
			Check(cpuSamples[0].Length == Steps && cpuSamples[2].Length == Steps && cpuSamples[1].Length == 0, "CPU point probes have one sample per step");
			Check(cpuProfiles[1] != null && cpuProfiles[1]!.Length > 30 && MaxAbs(cpuProfiles[1]) > 0f, "CPU integrated line profile populated");
			Check(cpu.TakeProbeSamples()[0].Length == 0, "CPU TakeProbeSamples clears");
			Check(MaxAbs(cpuField) > 0.01f, "CPU field is non-trivial (max |u| = " + MaxAbs(cpuField) + ")");
			Check(MaxAbs(cpuInten) > 0f, "CPU intensity accumulated");
			Check(Math.Abs(cpu.Time - Steps) < 1e-9, "CPU time counts steps");
			bool borderZero = true;
			for (int x = 0; x < cpu.Stride; x++)
			{
				borderZero &= cpuField[x] == 0f && cpuField[(H + 1) * cpu.Stride + x] == 0f;
			}
			Check(borderZero, "CPU padding rows untouched");

			GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(scene, options, Sources(), out string problems);
			if (gpu == null)
			{
				Console.WriteLine("  skip GPU: " + problems);
				continue;
			}
			using (gpu)
			{
				Console.WriteLine("  " + gpu.Description);
				gpu.SetProbes(Probes(W, H));
				Run(gpu, Steps);
				float diffField = MaxDiff(cpuField, gpu.FieldData);
				float diffInten = MaxDiff(cpuInten, gpu.IntensityData!);
				Check(diffField < 1e-3f * Math.Max(1f, MaxAbs(cpuField)), "GPU field matches CPU (max diff " + diffField + ")");
				Check(diffInten < 1e-3f * Math.Max(1f, MaxAbs(cpuInten)), "GPU intensity matches CPU (max diff " + diffInten + ")");
				float[][] gpuSamples = gpu.TakeProbeSamples();
				float[]?[] gpuProfiles = gpu.ReadProbeProfiles();
				float diffSamples = MaxDiff(cpuSamples, gpuSamples);
				float diffProfiles = MaxDiff(cpuProfiles, gpuProfiles);
				Check(diffSamples < 1e-3f, "GPU point-probe samples match CPU (max diff " + diffSamples + ")");
				Check(diffProfiles < 1e-3f * Math.Max(1f, MaxAbs(cpuProfiles[1])), "GPU line profile matches CPU (max diff " + diffProfiles + ")");
				gpu.SetProbes(Probes(W, H));
				Run(gpu, 10);
				Check(gpu.TakeProbeSamples()[0].Length == 10, "GPU probes re-installed and sampling again");
				gpu.SetProbes(null);
				Check(gpu.TakeProbeSamples().Length == 0 && gpu.ReadProbeProfiles().Length == 0, "GPU: probes cleared -> empty results");
				gpu.Dispose();
				gpu.Dispose();
				Check(true, "GPU double dispose is harmless");
			}
		}
		Console.WriteLine("--- factory");
		IWaveEngine fallback = WaveEngineFactory.Create(scene, new EngineOptions(1.0, 1.0, false, false, 10.0, 1.0), false, null, out string note);
		Check(fallback is WaveSimulator && note == "" && fallback.IntensityData == null, "factory gives the CPU engine without intensity when asked");
		(fallback as IDisposable)?.Dispose();
	}

	private static float MaxDiff(ReadOnlySpan<float> padded, int stride, int w, int h, ReadOnlySpan<Half> plane)
	{
		float m = 0f;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				m = Math.Max(m, Math.Abs(padded[(y + 1) * stride + x + 1] - (float)plane[y * w + x]));
			}
		}
		return m;
	}

	/// <summary>CaptureFrame/AwaitFrame, Reset and SetEmitters on both engines, and a deferred await (the pipeline's pattern).</summary>
	private static void CheckCaptureResetEmitters(SceneData scene, int W, int H)
	{
		Console.WriteLine("--- capture, reset, emitters");
		var options = new EngineOptions(1.0, 1.0, true, false, 10.0, 1.0);
		List<Emitter> moved = [Make(30, 30, 120, 1.0), Make(44, 12, 200, 0.7, SourceMode.Pulsed, PulseShape.Gaussian, phase: 0.5, onWidth: 4, offWidth: 6)];
		var reference = new WaveSimulator(scene, options, Sources());
		Run(reference, 100);
		float[] at100 = (float[])reference.FieldData.Clone();
		float[] inten100 = (float[])reference.IntensityData!.Clone();
		Run(reference, 50);
		float[] at150 = (float[])reference.FieldData.Clone();
		var movedReference = new WaveSimulator(scene, options, moved);
		Run(movedReference, 100);
		float[] movedAt100 = (float[])movedReference.FieldData.Clone();
		Check(MaxDiff(at100, movedAt100) > 1e-3f, "moving a source changes the field (reference)");

		GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(scene, options, Sources(), out string problems);
		if (gpu == null)
		{
			Console.WriteLine("  skip GPU: " + problems);
		}
		foreach ((IWaveEngine engine, string name) in new (IWaveEngine, string)[] { (new WaveSimulator(scene, options, Sources()), "CPU"), (gpu!, "GPU") })
		{
			if (engine == null)
			{
				continue;
			}
			var a = engine.CreateSnapshot(FrameShape.Full(W, H), true);
			var b = engine.CreateSnapshot(FrameShape.Full(W, H), true);
			Run(engine, 100);
			engine.CaptureFrame(a, true);
			// keep stepping before awaiting: the capture must still show step 100
			Run(engine, 50);
			engine.CaptureFrame(b, false);
			engine.AwaitFrame(a);
			engine.AwaitFrame(b);
			float tolerance = 2e-3f * Math.Max(1f, MaxAbs(at100));
			Check(a.HasIntensity && !b.HasIntensity, name + ": intensity captured only when asked");
			Check(MaxDiff(at100, engine.Stride, W, H, a.Field) < tolerance, name + ": deferred capture holds the field of its step (max diff " + MaxDiff(at100, engine.Stride, W, H, a.Field) + ")");
			// a 16-bit capture holds the intensity's square root (2026-10-23)
			float[] root100 = [.. inten100.Select(MathF.Sqrt)];
			Check(MaxDiff(root100, engine.Stride, W, H, a.Intensity!) < 2e-3f * Math.Max(1f, MaxAbs(root100)), name + ": captured intensity matches (its square root)");
			Check(MaxDiff(at150, engine.Stride, W, H, b.Field) < tolerance, name + ": second capture holds the later step");
			engine.Reset();
			engine.CaptureFrame(a, true);
			engine.AwaitFrame(a);
			Check(MaxAbs(a.Field) == 0f && MaxAbs(a.Intensity) == 0f, name + ": reset clears field and intensity");
			Run(engine, 100);
			engine.CaptureFrame(a, false);
			engine.AwaitFrame(a);
			Check(MaxDiff(at100, engine.Stride, W, H, a.Field) < tolerance, name + ": after reset the run reproduces (max diff " + MaxDiff(at100, engine.Stride, W, H, a.Field) + ")");
			engine.Reset();
			engine.SetEmitters(moved);
			Run(engine, 100);
			engine.CaptureFrame(a, false);
			engine.AwaitFrame(a);
			Check(MaxDiff(movedAt100, engine.Stride, W, H, a.Field) < tolerance, name + ": replaced emitters give the moved reference's field (max diff " + MaxDiff(movedAt100, engine.Stride, W, H, a.Field) + ")");
			engine.Reset();
			engine.SetEmitters(Sources());
			Run(engine, 100);
			engine.CaptureFrame(a, false);
			engine.AwaitFrame(a);
			Check(MaxDiff(at100, engine.Stride, W, H, a.Field) < tolerance, name + ": emitters restored, original field again");
			// the engine's own snapshot (page-locked host memory on the GPU, 2026-11-03) and an ordinary one capture the same
			// bytes; the engine's planes go with the engine, an ordinary one's stay
			var plain = new FrameSnapshot(FrameShape.Full(W, H), true);
			engine.CaptureFrame(a, true);
			engine.CaptureFrame(plain, true);
			engine.AwaitFrame(a);
			engine.AwaitFrame(plain);
			Check(a.HasIntensity && a.FieldBytes.SequenceEqual(plain.FieldBytes) && a.IntensityBytes.SequenceEqual(plain.IntensityBytes),
				name + ": the engine's snapshot and an ordinary one capture the same bytes");
			(engine as IDisposable)?.Dispose();
			bool gone = false;
			try
			{
				_ = a.FieldBytes.Length;
			}
			catch (ObjectDisposedException)
			{
				gone = true;
			}
			Check(gone == (engine is GpuWaveSimulator) && plain.FieldBytes.Length == FrameShape.Full(W, H).PlaneBytes,
				name + ": " + (gone ? "the engine's snapshot is released with the engine" : "the snapshot outlives the engine") + ", an ordinary one stays readable");
		}
	}

	/// <summary>Jeroen's V0.30 rule: a red value of 0 is a refractive index of 1000, a wall no wave gets through.</summary>
	private static void CheckMirror(int W, int H)
	{
		Console.WriteLine("--- mirror pixels (red 0)");
		(byte[] bgra, int stride) = ScenePixels(W, H);
		for (int y = 0; y < H; y++)
		{
			bgra[y * stride + 32 * 4 + 2] = 0; // a one-pixel wall at x = 32
		}
		SceneData wall = SceneData.FromBgra(bgra, W, H, stride, false, 0);
		SceneData open = SceneData.FromBgra(ScenePixels(W, H).Bgra, W, H, stride, false, 0);
		Check(Math.Abs(wall.C2[32] - 1e-6f) < 1e-9f && Math.Abs(wall.C2[31] - 1f) < 1e-6f, "red 0 is a refractive index of 1000 (c\u00b2 = 1e-6), red 100 stays 1");
		bgra[33 * 4 + 2] = 50; // red 50 at (33, 0): below 100 and not 0
		Check(Math.Abs(SceneData.FromBgra(bgra, W, H, stride, false, 0).C2[33] - 1f) < 1e-6f, "red 1..99 is a refractive index of 1, as in Wave Dude 2D (the Python allowed 0.9)");
		var emitters = new List<Emitter> { Make(16, 24, 120, 1.0) };
		const int steps = 150;
		float[] withWall = RunCpu(wall, emitters, steps);
		float[] without = RunCpu(open, emitters, steps);
		float leftWall = MaxAbsColumns(withWall, W, H, 0, 30);
		float rightWall = MaxAbsColumns(withWall, W, H, 34, W - 1);
		float rightOpen = MaxAbsColumns(without, W, H, 34, W - 1);
		Check(rightOpen > 0.01f && leftWall > 0.01f && rightWall < rightOpen * 1e-2f, $"a wall of red 0 blocks the wave: max |u| right of it {rightWall} against {rightOpen} without the wall, left of it {leftWall}");
		GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(wall, new EngineOptions(1.0, 1.0, false, false, 10.0, 1.0), emitters, out string problems);
		if (gpu == null)
		{
			Console.WriteLine("  skip GPU: " + problems);
			return;
		}
		using (gpu)
		{
			Run(gpu, steps);
			float diff = MaxDiff(withWall, gpu.FieldData);
			Check(diff < 1e-3f * Math.Max(1f, MaxAbs(withWall)), "GPU agrees on the wall scene (max diff " + diff + ")");
		}
	}

	private static void CheckDecimalLevels(int W, int H)
	{
		Console.WriteLine("--- decimal frequency, amplitude and phase");
		var project = new Project();
		project.Scene.Width = W;
		project.Scene.Height = H;
		Source source = project.AddSource();
		source.X = 20;
		source.Y = 24;
		source.Frequency = 120.5;
		source.Amplitude = 33.3;
		source.Phase = 12.5;
		RunSpec spec = RunSpec.Create(project, null, preferGpu: false);
		Check(spec.Emitters.Length == 1 && spec.Emitters[0].Level == 120.5 && Math.Abs(spec.Emitters[0].Amplitude - 0.333) < 1e-12 && Math.Abs(spec.Emitters[0].PhaseRadians - 12.5 * Math.PI / 180.0) < 1e-12, "a decimal frequency, amplitude and phase reach the emitter as typed");
		source.Frequency = 300;
		source.Amplitude = 100.5;
		source.Phase = -200;
		source.Normalize();
		Check(source.Frequency == 255 && source.Amplitude == 100 && source.Phase == -180, "the level is capped at 255 (the grid cannot sample above it), the amplitude at 100, the phase at -180");
		source.Frequency = 0;
		source.Normalize();
		Check(source.Frequency == Source.MinLevel, "a level of 0 becomes the least level");
		source.Frequency = 120.5;
		source.Amplitude = 33.3;
		source.Phase = 12.5;
		using (var stream = new MemoryStream())
		{
			project.Save(stream);
			string xml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
			stream.Position = 0;
			Project back = Project.Load(stream);
			Check(xml.Contains("Frequency=\"120.5\"") && xml.Contains("Amplitude=\"33.3\"") && xml.Contains("Phase=\"12.5\"") && back.Sources[0].Frequency == 120.5 && back.Sources[0].Amplitude == 33.3 && back.Sources[0].Phase == 12.5, "decimals round-trip through the file");
		}
		SceneData scene = SceneData.Empty(W, H, 0);
		var emitters = new List<Emitter> { Make(20, 24, 120, 1.0) with { Level = 120.5, PhaseRadians = 12.5 * Math.PI / 180.0 } };
		float[] cpu = RunCpu(scene, emitters, 150);
		float[] whole = RunCpu(scene, [Make(20, 24, 120, 1.0)], 150);
		Check(MaxAbs(cpu) > 0.01f && MaxDiff(cpu, whole) > 1e-3f, "a level of 120.5 runs and differs from 120");
		GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(scene, new EngineOptions(1.0, 1.0, false, false, 10.0, 1.0), emitters, out string problems);
		if (gpu == null)
		{
			Console.WriteLine("  skip GPU: " + problems);
			return;
		}
		using (gpu)
		{
			Run(gpu, 150);
			float diff = MaxDiff(cpu, gpu.FieldData);
			Check(diff < 1e-3f * Math.Max(1f, MaxAbs(cpu)), "GPU agrees on a decimal level and phase (max diff " + diff + ")");
		}
	}

	private static void CheckMovable()
	{
		Console.WriteLine("--- movable items (group move)");
		var project = new Project();
		project.Scene.Width = 64;
		project.Scene.Height = 48;
		Source source = project.AddSource();
		source.X = 10;
		source.Y = 10;
		OpticalPart part = project.AddPart<RectanglePart>();
		part.X = 50;
		part.Y = 40;
		Probe probe = project.AddProbe();
		probe.Kind = ProbeKind.Line;
		probe.AX = 5;
		probe.AY = 5;
		probe.BX = 20;
		probe.BY = 30;
		probe.BoxX = probe.AX;
		probe.BoxY = probe.AY;
		IMovable[] items = [source, part, probe];
		Check(source.Anchors.SequenceEqual([(10, 10)]) && part.Anchors.SequenceEqual([(50, 40)]) && probe.Anchors.SequenceEqual([(5, 5), (20, 30)]), "anchors: a source's and a part's position, both ends of a line probe");
		Check(Movable.Limit(items, -10, -10, 64, 48) == (-5, -5), "a move up and left stops when the first anchor reaches the edge");
		Check(Movable.Limit(items, 100, 100, 64, 48) == (13, 7), "a move down and right stops when the last anchor reaches the edge");
		Check(Movable.Limit(items, 3, -2, 64, 48) == (3, -2), "a move that fits passes unchanged");
		Check(Movable.Limit([], 9, 9, 64, 48) == (9, 9), "no anchors, no limit");
		Movable.MoveAll(items, 3, 2);
		Check(source.X == 13 && source.Y == 12 && part.X == 53 && part.Y == 42 && probe.AX == 8 && probe.AY == 7 && probe.BX == 23 && probe.BY == 32, "every item moved by the same delta");
		Check(probe.BoxX == 8 && probe.BoxY == 7 && probe.IsBoxAttached, "the attached graph box followed point A");
		probe.Kind = ProbeKind.Point;
		Check(probe.Anchors.SequenceEqual([(8, 7)]), "a point probe anchors at point A alone");
	}

	private static void CheckOpenEdges(int W, int H)
	{
		Console.WriteLine("--- open edges and the damping rim");
		// a scene image with a green pixel: a negative damping edge grows the grid, moves the source into grid coordinates
		// and continues the canvas's edge cells into the rim
		var bgra = new byte[8 * 8 * 4];
		for (int i = 0; i < 64; i++)
		{
			bgra[i * 4 + 2] = 100;
			bgra[i * 4 + 3] = 255;
		}
		bgra[(3 * 8 + 2) * 4 + 1] = 120;   // a source at (2, 3)
		bgra[2] = 200;                     // index 2 at the corner (0, 0)
		SceneData rimmed = SceneData.FromBgra(bgra, 8, 8, 32, true, -4, true);
		Check(rimmed.Width == 16 && rimmed.Height == 16 && rimmed.Rim == 4 && rimmed.CanvasWidth == 8 && rimmed.CanvasHeight == 8 && rimmed.OpenEdges,
			"a damping edge of -4 grows an 8x8 scene to a 16x16 grid with a rim of 4, open");
		Check(rimmed.SourceCount == 1 && rimmed.SourceX[0] == 6 && rimmed.SourceY[0] == 7, "the image source moved into grid coordinates");
		Check(rimmed.D[0] == 0.5f && Math.Abs(rimmed.D[2 * 16 + 2] - (1f - 0.5f * 0.25f)) < 1e-6f && rimmed.D[4 * 16 + 4] == 1f && rimmed.C2[0] == rimmed.C2[4 * 16 + 4] && rimmed.C2[0] == 0.25f,
			"the rim loses half per step at its outer ring, an eighth halfway in (the square of the depth), the canvas nothing, and the rim repeats the canvas's corner cell");
		SceneData plain = SceneData.Empty(W, H, 8);
		Check(plain.Rim == 0 && plain.Width == W && !plain.OpenEdges && plain.D[0] == 0f && plain.D[8 * W + 8] == 1f, "a positive damping edge stays inside the canvas, reflective by default");
		// the default (2026-10-22, the user's choice): open with a 32 px rim outside, format version 3; older files keep what they meant
		var fresh = new Project();
		Check(fresh.Scene.DampingEdge == -32 && fresh.Scene.Edges == EdgeMode.Open && fresh.Version == 3 && fresh.Scene.IsDefault, "a new project is open with a 32 px rim outside, format version 3, its scene element left out of the file");
		static Project LoadXml(string xml)
		{
			using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
			return Project.Load(stream);
		}
		Check(LoadXml("<Project Version=\"2\"><Scene Width=\"64\" Height=\"48\" /></Project>").Scene is { DampingEdge: 0, Edges: EdgeMode.Open }, "a version-2 file without a damping edge meant none: it loads as 0, not as the new default");
		Check(LoadXml("<Project Version=\"2\"><Scene DampingEdge=\"-32\" /></Project>").Scene.DampingEdge == -32 && LoadXml("<Project Version=\"3\"><Scene /></Project>").Scene.DampingEdge == -32,
			"an explicit -32 in a version-2 file stays, and a version-3 file without the attribute is -32");
		Check(LoadXml("<Project Version=\"1\"><Scene /></Project>").Scene is { DampingEdge: 0, Edges: EdgeMode.Reflective }, "a version-1 file is reflective without a rim");
		Project saved = LoadXml("<Project Version=\"2\"><Scene /></Project>");
		string savedXml;
		using (var stream = new MemoryStream())
		{
			saved.Save(stream);
			savedXml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
		}
		Check(savedXml.Contains("Version=\"3\"") && savedXml.Contains("DampingEdge=\"0\""), "saved again it is version 3 and says DampingEdge=\"0\" explicitly");
		// what comes back: a Gaussian pulse from the centre, its peak amplitude on the way out against the largest
		// amplitude found in the canvas once it has had time to leave (steps 300 to 700)
		// both stencils: the 4th-order one runs at the true Laplacian's speed, and its outermost canvas cells, which fall
		// back to the 9-point stencil, ran at 0.56 of it until 2026-10-22, a speed step that sent a fifth of a pulse back
		(string Name, int Edge, bool Open, bool Fourth)[] configs =
		[
			("reflective, no damping", 0, false, false), ("reflective, a 24 px band inside", 24, false, false), ("open, no damping", 0, true, false), ("open, a 32 px rim outside", -32, true, false),
			("open, no damping, 4th order", 0, true, true), ("open, a 32 px rim outside, 4th order", -32, true, true),
		];
		double[] back = new double[configs.Length];
		for (int c = 0; c < configs.Length; c++)
		{
			SceneData scene = SceneData.Empty(W, H, configs[c].Edge, 1.0, configs[c].Open);
			var pulse = new List<Emitter> { Make(W / 2, H / 2, 120, 1.0) with { Mode = SourceMode.Pulsed, PulseShape = PulseShape.Gaussian, OnWidthCycles = 3, OffWidthCycles = 100000, OffWidthBase = 100000 } };
			var cpu = new WaveSimulator(scene, new EngineOptions(1.0, 1.0, false, configs[c].Fourth, 10.0, 1.0), pulse);
			double peak = 0, returned = 0;
			for (int step = 1; step <= 700; step++)
			{
				cpu.AdvanceOneStep();
				if (step % 5 == 0)
				{
					double amplitude = CanvasPeak(cpu);
					if (step <= 150)
					{
						peak = Math.Max(peak, amplitude);
					}
					else if (step >= 300)
					{
						returned = Math.Max(returned, amplitude);
					}
				}
			}
			back[c] = returned / peak;
			Console.WriteLine($"  {configs[c].Name}: {back[c] * 100:0.000} % of the pulse's amplitude is back in the canvas between steps 300 and 700");
		}
		Check(back[2] < 0.002, "open edges alone: under 0.2 % of the amplitude comes back");
		Check(back[3] < 0.005, "open edges with a 32 px rim outside: under half a percent comes back");
		Check(back[0] > 0.5, "a reflective box without damping sends it all back");
		Check(back[4] < 0.005, "open edges alone with the 4th-order stencil: under half a percent comes back");
		Check(back[5] < 0.005, "open edges with a 32 px rim outside, 4th-order stencil: under half a percent comes back");
		// a wave under three cells long (level 160 at scale 2, aliased, as Jeroen's Example 3 runs) with the 4th-order
		// stencil: the open ring reflects it almost whole, the rim must absorb on its own (2026-10-22: the linear ramp to a
		// tenth let 1 % of Example 3's beam back, the quadratic ramp to a half under 0.1 %). Measured against a reference
		// canvas grown by a margin no reflection crosses within the steps run, from a point source 60 px from the left edge
		const int shortW = 256, shortH = 160, margin = 220, shortSteps = 400;
		var shortOptions = new EngineOptions(2.0, 1.0, false, true, 10.0, 1.0);
		static List<Emitter> ShortPulse(int x, int y) => [Make(x, y, 160, 1.0) with { Mode = SourceMode.Pulsed, PulseShape = PulseShape.Gaussian, OnWidthCycles = 10, OffWidthCycles = 100000, OffWidthBase = 100000 }];
		var referenceEngine = new WaveSimulator(SceneData.Empty(shortW + 2 * margin, shortH + 2 * margin, 0, 1.0, false), shortOptions, ShortPulse(60 + margin, 80 + margin));
		var references = new List<float[]>();
		double edgeAmplitude = 0;
		for (int step = 1; step <= shortSteps; step++)
		{
			referenceEngine.AdvanceOneStep();
			if (step % 20 == 0)
			{
				float[] canvas = CanvasRegion(referenceEngine, margin, shortW, shortH);
				references.Add(canvas);
				for (int y = 0; y < shortH; y++)
				{
					for (int x = 0; x < 40; x++)
					{
						edgeAmplitude = Math.Max(edgeAmplitude, Math.Abs(canvas[y * shortW + x]));
					}
				}
			}
		}
		double[] shortBack = new double[2];
		for (int t = 0; t < 2; t++)
		{
			var engine = new WaveSimulator(SceneData.Empty(shortW, shortH, t == 0 ? 0 : -64, 1.0, true), shortOptions, ShortPulse(60, 80));
			for (int step = 1; step <= shortSteps; step++)
			{
				engine.AdvanceOneStep();
				if (step % 20 == 0)
				{
					float[] canvas = CanvasRegion(engine, 0, shortW, shortH);
					float[] expected = references[step / 20 - 1];
					for (int i = 0; i < canvas.Length; i++)
					{
						shortBack[t] = Math.Max(shortBack[t], Math.Abs(canvas[i] - expected[i]));
					}
				}
			}
			shortBack[t] /= edgeAmplitude;
		}
		Console.WriteLine($"  a wave under three cells long, 4th order, against a wide reference: open ring alone {shortBack[0] * 100:0.00} %, with a 64 px rim {shortBack[1] * 100:0.000} % of the edge amplitude comes back");
		Check(shortBack[0] > 0.1, "the open ring alone reflects a wave under three cells long: over a tenth comes back");
		Check(shortBack[1] < 0.003, "a 64 px rim with the quadratic ramp lets under 0.3 % of it back");
		// CPU = GPU with the rim and the open edge
		SceneData openScene = SceneData.Empty(W, H, -32, 1.0, true);
		var wave = new List<Emitter> { Make(W / 2, H / 2, 120, 1.0) };
		float[] cpuField = RunCpu(openScene, wave, 300);
		Check(cpuField.Length == (W + 66) * (H + 66), "the engine's buffer holds the grid with its rim and padding");
		GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(openScene, new EngineOptions(1.0, 1.0, false, false, 10.0, 1.0), wave, out string problems);
		if (gpu == null)
		{
			Console.WriteLine("  skip GPU: " + problems);
			return;
		}
		using (gpu)
		{
			Run(gpu, 300);
			float diff = MaxDiff(cpuField, gpu.FieldData);
			Check(gpu.Rim == 32 && gpu.Width == W && gpu.Stride == W + 66 && diff < 1e-3f * Math.Max(1f, MaxAbs(cpuField)), "GPU agrees with the CPU on the rim and the open edge (max diff " + diff + ")");
		}
	}

	/// <summary>A <paramref name="w"/> × <paramref name="h"/> region of an engine's field at offset (<paramref name="m"/>, <paramref name="m"/>) of its canvas (the rim and padding skipped), row-major.</summary>
	private static float[] CanvasRegion(IWaveEngine engine, int m, int w, int h)
	{
		var region = new float[w * h];
		for (int y = 0; y < h; y++)
		{
			Array.Copy(engine.FieldData, (y + m + 1 + engine.Rim) * engine.Stride + m + 1 + engine.Rim, region, y * w, w);
		}
		return region;
	}

	/// <summary>Max |u| over the canvas cells of an engine's field (the rim and padding left out).</summary>
	private static double CanvasPeak(IWaveEngine engine)
	{
		float[] u = engine.FieldData;
		double peak = 0;
		for (int y = 0; y < engine.Height; y++)
		{
			int row = (y + 1 + engine.Rim) * engine.Stride + 1 + engine.Rim;
			for (int x = 0; x < engine.Width; x++)
			{
				peak = Math.Max(peak, Math.Abs(u[row + x]));
			}
		}
		return peak;
	}

	/// <summary>Max |u| over the canvas columns <paramref name="x0"/>..<paramref name="x1"/> of a padded field.</summary>
	/// <summary>
	/// The GPU on a grid prime in both dimensions (a 103 × 67 canvas with a rim of 32: 167 × 131). With the work-group
	/// size left to the driver it had to divide the global size, so it was one cell wide there (2026-10-26, Jeroen's
	/// 1725 px canvas: 13 to 19 times slower); the kernels now run in explicit groups over a global size rounded up to
	/// whole groups, and the surplus work items must change nothing: the field with the open ring, and both capture
	/// formats, agree with the CPU.
	/// </summary>
	/// <summary>TryReplaceScene (2026-11-03): an engine given another scene of its grid steps like a fresh one on it, on both engines; another grid is refused.</summary>
	private static void CheckSceneReplacement()
	{
		Console.WriteLine("--- scene replacement");
		const int W = 96, H = 64;
		// 4th order, so the sub-steps follow the scene's highest c²: index 1 needs 3, index 1.6 fewer
		var options = new EngineOptions(1.0, 1.0, true, true, 10.0, 1.0);
		SceneData first = SceneData.Empty(W, H, -8, 1.0, true);
		SceneData second = SceneData.Empty(W, H, -8, 1.6, true);
		SceneData otherGrid = SceneData.Empty(W + 1, H, -8, 1.0, true);
		List<Emitter> emitters = [Make(W / 2, H / 2, 120, 1.0)];
		var reference = new WaveSimulator(second, options, emitters);
		Run(reference, 60);
		GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(first, options, emitters, out string problems);
		if (gpu == null)
		{
			Console.WriteLine("  skip GPU: " + problems);
		}
		foreach ((IWaveEngine engine, string name) in new (IWaveEngine, string)[] { (new WaveSimulator(first, options, emitters), "CPU"), (gpu!, "GPU") })
		{
			if (engine == null)
			{
				continue;
			}
			Run(engine, 30);
			Check(!engine.TryReplaceScene(otherGrid), name + ": a scene of another grid is refused");
			Check(engine.TryReplaceScene(second), name + ": a scene of the same grid is taken");
			Run(engine, 60);
			engine.PrepareFrame(true, true);
			float tolerance = name == "CPU" ? 0f : 2e-3f * Math.Max(1f, MaxAbs(reference.FieldData));
			float diff = MaxDiff(reference.FieldData, engine.FieldData);
			Check(diff <= tolerance && MaxAbs(engine.FieldData) > 0f, name + ": after the replacement the engine steps like a fresh one on the new scene (max diff " + diff + ")");
			(engine as IDisposable)?.Dispose();
		}
	}

	private static void CheckWorkGroups()
	{
		Console.WriteLine("--- work groups on a prime grid");
		const int cw = 103, ch = 67, rim = 32;
		SceneData scene = SceneData.Empty(cw, ch, -rim, 1.0, true);
		Check(scene.Width == 167 && scene.Height == 131 && scene.Rim == rim && scene.OpenEdges, "a 103 × 67 canvas with a rim of 32 is a 167 × 131 grid, prime in both dimensions, open");
		// a second source at the canvas's last cell: the work item at the corner, beside the rounded-up surplus
		List<Emitter> emitters = [Make(cw / 2, ch / 2, 120, 1.0), Make(cw - 1, ch - 1, 200, 0.7)];
		var options = new EngineOptions(1.0, 1.0, true, false, 10.0, 1.0);
		var cpu = new WaveSimulator(scene, options, emitters);
		Run(cpu, 200);
		GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(scene, options, emitters, out string problems);
		if (gpu == null)
		{
			Console.WriteLine("  skip GPU: " + problems);
			return;
		}
		using (gpu)
		{
			Check(gpu.WorkGroup.Width >= 32 && gpu.WorkGroup.Height >= 1, $"wave_step runs in groups of {gpu.WorkGroup.Width} × {gpu.WorkGroup.Height}, not in the driver's divisors of 167");
			Run(gpu, 200);
			float tolerance = 2e-3f * Math.Max(1f, MaxAbs(cpu.FieldData));
			float diff = MaxDiff(cpu.FieldData, gpu.FieldData);
			Check(diff < tolerance, "the GPU agrees with the CPU on the prime grid, rim and open ring included (max diff " + diff + ")");
			// the captures at the canvas's own prime size, in both formats, against the CPU's conversion of the same field
			FrameShape full = FrameShape.Full(cw, ch);
			FrameShape index = full with { Format = FrameFormat.Index, FieldBrightness = 30f, IntensityBrightness = 5f };
			int origin = (rim + 1) * cpu.Stride + rim + 1;
			var halves = gpu.CreateSnapshot(full, true);
			var indices = gpu.CreateSnapshot(index, true);
			gpu.CaptureFrame(halves, true);
			gpu.CaptureFrame(indices, true);
			gpu.AwaitFrame(halves);
			gpu.AwaitFrame(indices);
			var halfBytes = new byte[full.PlaneBytes];
			FrameConversion.Convert(cpu.FieldData, origin, cpu.Stride, full, false, halfBytes);
			float worst = 0f;
			ReadOnlySpan<Half> expected = MemoryMarshal.Cast<byte, Half>(halfBytes);
			for (int i = 0; i < cw * ch; i++)
			{
				worst = Math.Max(worst, Math.Abs((float)expected[i] - (float)halves.Field[i]));
			}
			Check(worst < tolerance, "the 16-bit capture of the prime canvas matches the CPU's conversion (worst " + worst + ")");
			var indexField = new byte[index.PlaneBytes];
			var indexInten = new byte[index.PlaneBytes];
			FrameConversion.Convert(cpu.FieldData, origin, cpu.Stride, index, false, indexField);
			FrameConversion.Convert(cpu.IntensityData!, origin, cpu.Stride, index, true, indexInten);
			int fieldOff = 0, intenOff = 0;
			for (int i = 0; i < cw * ch; i++)
			{
				fieldOff += Math.Abs(indexField[i] - indices.FieldBytes[i]) > 1 ? 1 : 0;
				intenOff += Math.Abs(indexInten[i] - indices.IntensityBytes[i]) > 1 ? 1 : 0;
			}
			Check(fieldOff == 0 && intenOff == 0, "the 8-bit capture of the prime canvas is within a palette step of the CPU's, field and intensity (" + fieldOff + " / " + intenOff + " cells off)");
		}
	}

	private static float MaxAbsColumns(float[] padded, int W, int H, int x0, int x1)
	{
		int stride = W + 2;
		float max = 0f;
		for (int y = 0; y < H; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				max = Math.Max(max, Math.Abs(padded[(y + 1) * stride + x + 1]));
			}
		}
		return max;
	}

	private static float[] RunCpu(SceneData scene, List<Emitter> emitters, int steps)
	{
		var cpu = new WaveSimulator(scene, new EngineOptions(1.0, 1.0, false, false, 10.0, 1.0), emitters);
		Run(cpu, steps);
		return (float[])cpu.FieldData.Clone();
	}

	private static void CheckDeterminism(SceneData scene)
	{
		Console.WriteLine("--- determinism");
		Source Randomized(int seed)
		{
			var source = new Source
			{
				Name = "Jitter", X = 32, Y = 24, Frequency = 180, Amplitude = 80, Mode = SourceMode.Pulsed, PulseShape = PulseShape.SmoothedSquare,
				PulseWidth = 2, OffWidth = 2, Array = new SourceArray { SizeX = 3, SizeY = 1, SpacingX = 6, RandomOffWidth = 3 },
			};
			source.GenerateElements(seed);
			return source;
		}
		List<Emitter> a = Emitter.FromSource(Randomized(123), 123);
		List<Emitter> b = Emitter.FromSource(Randomized(123), 123);
		List<Emitter> c = Emitter.FromSource(Randomized(124), 124);
		Check(a.Count == 3 && a.All(e => e.RandomizeOffWidthAmount == 3 && e.RngSeed != 0), "randomized composite gives 3 emitters with non-zero seeds");
		Check(a.Select(e => e.RngSeed).SequenceEqual(b.Select(e => e.RngSeed)), "same project seed -> same emitter seeds");
		Check(!a.Select(e => e.RngSeed).SequenceEqual(c.Select(e => e.RngSeed)), "different project seed -> different emitter seeds");
		float[] fieldA = RunCpu(scene, a, 300);
		float[] fieldB = RunCpu(scene, b, 300);
		float[] fieldC = RunCpu(scene, c, 300);
		Check(fieldA.AsSpan().SequenceEqual(fieldB), "same seed -> bit-identical field after 300 steps");
		Check(MaxDiff(fieldA, fieldC) > 1e-4f, "different seed -> different field (max diff " + MaxDiff(fieldA, fieldC) + ")");
		var options = new EngineOptions(1.0, 1.0, false, false, 10.0, 1.0);
		GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(scene, options, a, out string problems);
		if (gpu == null)
		{
			Console.WriteLine("  skip GPU: " + problems);
			return;
		}
		using (gpu)
		{
			Run(gpu, 300);
			float diff = MaxDiff(fieldA, gpu.FieldData);
			Check(diff < 1e-3f * Math.Max(1f, MaxAbs(fieldA)), "GPU agrees with CPU on the randomized gaps (max diff " + diff + ")");
		}
	}

	private static void CheckEmitterMapping()
	{
		Console.WriteLine("--- emitter mapping");
		var project = new Project();
		Source source = project.AddSource();
		source.X = 12;
		source.Y = 34;
		source.Frequency = 77;
		source.Amplitude = 50;
		source.Phase = 90;
		source.Mode = SourceMode.Pulsed;
		source.PulseShape = PulseShape.SmoothedSquare;
		source.PulseWidth = 7;
		source.OffWidth = 9;
		source.DetachAfter = 500;
		List<Emitter> emitters = Emitter.FromProject(project);
		Check(emitters.Count == 1, "one emitter for a single source");
		Emitter e = emitters[0];
		Check(e.X == 12 && e.Y == 34 && e.Level == 77, "position and level copied");
		Check(Math.Abs(e.Amplitude - 0.5) < 1e-12 && Math.Abs(e.PhaseRadians - Math.PI / 2) < 1e-12, "amplitude / 100 and phase in radians");
		Check(e.Mode == SourceMode.Pulsed && e.PulseShape == PulseShape.SmoothedSquare && e.OnWidthCycles == 7 && e.OffWidthCycles == 9 && e.OffWidthBase == 9 && e.RandomizeOffWidthAmount == 0 && e.DetachAfterStep == 500, "mode, shape, widths, detach copied");
		source.Enabled = false;
		Check(Emitter.FromProject(project).Count == 0, "disabled source gives no emitters");
		source.Enabled = true;
		project.Scene.ObjectSources = false;
		Check(Emitter.FromProject(project).Count == 0, "scene without object sources gives no emitters");
	}

	private static void CheckFrameStore(SceneData scene)
	{
		Console.WriteLine("--- frame store");
		string path = Path.Combine(Path.GetTempPath(), "library_check-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
		var cpu = new WaveSimulator(scene, new EngineOptions(1.0, 1.0, true, false, 10.0, 1.0), Sources());
		using (var store = FrameStore.Create(path, scene.Width, scene.Height, FrameLayers.Field | FrameLayers.Intensity, 3, 2, ramBudget: 0))
		{
			Check(store.RamFrames == 0 && store.HasFile && File.Exists(path) && new FileInfo(path).Length == store.FileBytes && store.FileBytes == FrameStore.HeaderSize + 3 * store.FrameBytes, "no budget: the file is created at once and pre-sized (" + store.FileBytes + " bytes)");
			Check(store.Count == 0 && store.LayerCount == 2 && store.Has(FrameLayer.Intensity) && store.StepOf(2) == 4, "empty store reports its layout");
			int written = 0;
			store.FrameWritten += n => written = n;
			float[][] fields = new float[3][];
			for (int frame = 0; frame < 3; frame++)
			{
				Run(cpu, 2);
				fields[frame] = (float[])cpu.FieldData.Clone();
				store.Write(frame, cpu.FieldData, cpu.IntensityData, cpu.Stride);
			}
			Check(store.Count == 3 && written == 3, "three frames written, event saw the count");
			bool same = true;
			var copy = new Half[scene.Width * scene.Height];
			for (int frame = 0; frame < 3 && same; frame++)
			{
				Check(store.CopyLayer(frame, FrameLayer.Field, copy), "CopyLayer frame " + frame);
				for (int y = 0; y < scene.Height && same; y++)
				{
					for (int x = 0; x < scene.Width; x++)
					{
						Half expected = (Half)fields[frame][(y + 1) * cpu.Stride + x + 1];
						if (copy[y * scene.Width + x] != expected)
						{
							same = false;
							break;
						}
					}
				}
			}
			Check(same, "CopyLayer returns the Half of every interior cell");
			Check(store.CopyLayer(2, FrameLayer.Intensity, copy) && MaxAbs(fields[2]) > 0 && (float)copy[24 * scene.Width + 20] > 0, "intensity plane populated");
			Check(!store.CopyLayer(2, FrameLayer.Intensity, copy) == false, "a read while alive succeeds");
			bool threw = false;
			try
			{
				store.Write(5, cpu.FieldData, cpu.IntensityData, cpu.Stride);
			}
			catch (ArgumentOutOfRangeException)
			{
				threw = true;
			}
			Check(threw, "out-of-order write is rejected");
			try
			{
				store.Write(3, cpu.FieldData, cpu.IntensityData, cpu.Stride);
				threw = false;
			}
			catch (ArgumentOutOfRangeException)
			{
				threw = true;
			}
			Check(threw, "write beyond the capacity is rejected");
		}
		Check(!File.Exists(path), "file deleted on dispose");
		FrameShape fullHd = FrameShape.Full(1920, 1080);
		Check(FrameStore.SizeOf(fullHd, FrameLayers.Field | FrameLayers.Intensity, 1250) == 64 + 1250L * 2 * 1920 * 1080 * 2, "SizeOf matches the layout");
		Check(FrameStore.FramesInBudget(fullHd, FrameLayers.Field | FrameLayers.Intensity, 1L << 30) == 129 && FrameStore.FramesInBudget(FrameShape.Full(64, 48), FrameLayers.Field, 0) == 0, "FramesInBudget: 129 full-HD frames of both planes per GB");
		Check(FrameStore.FramesInBudget(fullHd with { Divisor = 2, Format = FrameFormat.Index }, FrameLayers.Field | FrameLayers.Intensity, 1L << 30) == 1035, "FramesInBudget: eight times as many half-size index frames (1035 per GB)");

		// the two tiers: a budget for two of five frames; the first two stay in memory, the other three go to the file
		path = Path.Combine(Path.GetTempPath(), "library_check-tiers-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
		using (var store = FrameStore.Create(path, scene.Width, scene.Height, FrameLayers.Field | FrameLayers.Intensity, 5, 2, ramBudget: 2L * 2 * scene.Width * scene.Height * 2))
		{
			Check(store.RamFrames == 2 && store.FirstSpilledFrame == 2 && store.HasFile && new FileInfo(path).Length == FrameStore.HeaderSize + 3 * store.FrameBytes, "budget for two frames: the file is sized for the other three");
			float[][] fields = new float[5][];
			for (int frame = 0; frame < 5; frame++)
			{
				Run(cpu, 2);
				fields[frame] = (float[])cpu.FieldData.Clone();
				store.Write(frame, cpu.FieldData, cpu.IntensityData, cpu.Stride);
			}
			Check(store.Count == 5 && store.FramesInMemory == 2 && store.FramesOnDisk == 3 && store.RamBytes == 2 * store.FrameBytes, "five frames: two in memory, three on disk");
			bool same = true;
			var copy = new Half[scene.Width * scene.Height];
			for (int frame = 0; frame < 5 && same; frame++)
			{
				same = store.CopyLayer(frame, FrameLayer.Field, copy);
				for (int y = 0; y < scene.Height && same; y++)
				{
					for (int x = 0; x < scene.Width; x++)
					{
						if (copy[y * scene.Width + x] != (Half)fields[frame][(y + 1) * cpu.Stride + x + 1])
						{
							same = false;
							break;
						}
					}
				}
			}
			Check(same, "every frame reads back from its tier");
		}
		Check(!File.Exists(path), "tiered store: file deleted on dispose");
		// a run that fits never touches the disk
		path = Path.Combine(Path.GetTempPath(), "library_check-ram-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
		using (var store = FrameStore.Create(path, scene.Width, scene.Height, FrameLayers.Field, 3, 1, ramBudget: 1L << 30))
		{
			store.Write(0, cpu.FieldData, null, cpu.Stride);
			Check(store.RamFrames == 3 && !store.HasFile && !File.Exists(path) && store.FileBytes == 0 && store.FirstSpilledFrame == 3, "a run within the budget creates no file");
		}
		// Auto (0) is what is free minus the margin; a percentage caps that
		long automatic = FrameMemory.Budget(FrameMemory.Automatic);
		Check(FrameMemory.PhysicalBytes() > 1L << 30 && FrameMemory.AvailableBytes() <= FrameMemory.PhysicalBytes() && FrameMemory.Budget(50) <= FrameMemory.PhysicalBytes() / 2
			&& Math.Abs(automatic - Math.Max(0, FrameMemory.AvailableBytes() - FrameMemory.Margin)) < 512L << 20 && FrameMemory.Budget(50) <= automatic + (512L << 20) && FrameMemory.Budget(1) <= FrameMemory.PhysicalBytes() / 100,
			"memory: " + FrameMemory.Gigabytes(FrameMemory.PhysicalBytes()) + " physical, " + FrameMemory.Gigabytes(FrameMemory.AvailableBytes()) + " available, automatic " + FrameMemory.Gigabytes(automatic) + ", at most 50 % " + FrameMemory.Gigabytes(FrameMemory.Budget(50)));
		// the outgoing run's frames count as available (reclaimed; the laptop finding of 2026-10-16): exact on the
		// pure overload — 3 GB free + 5 GB reclaimed - 2 GB margin = 6 GB, capped by a 25 % share of 16 GB, never negative
		const long gb = 1L << 30;
		Check(FrameMemory.Budget(3 * gb, 16 * gb, FrameMemory.Automatic, 5 * gb) == 6 * gb
			&& FrameMemory.Budget(3 * gb, 16 * gb, 25, 5 * gb) == 4 * gb
			&& FrameMemory.Budget(3 * gb, 16 * gb, FrameMemory.Automatic, 0) == 1 * gb
			&& FrameMemory.Budget(1 * gb, 16 * gb, FrameMemory.Automatic, 0) == 0
			&& FrameMemory.Budget(1 * gb, 16 * gb, 100, 512L << 20) == 0,
			"budget: reclaimed memory of the outgoing run counts as available, the share still caps it");
		// the memory tier waits for that reclaimed memory (2026-10-16): a store made with a release task allocates
		// nothing and a write to it waits until the task completes, then the frame lands in memory, not in a file
		path = Path.Combine(Path.GetTempPath(), "library_check-wait-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
		var release = new TaskCompletionSource();
		using (var store = FrameStore.Create(path, scene.Width, scene.Height, FrameLayers.Field, 3, 1, ramBudget: 1L << 30, memoryReleased: release.Task))
		{
			Task write = Task.Run(() => store.Write(0, cpu.FieldData, null, cpu.Stride));
			bool waited = !write.Wait(300) && store.Count == 0;
			release.SetResult();
			bool landed = write.Wait(5000) && store.Count == 1 && store.FramesInMemory == 1 && !store.HasFile;
			Check(waited && landed, "the memory tier waits for the outgoing run's memory before its first allocation, then the frame lands in memory");
		}
		// a store whose release never comes is still disposed at once (the preparer's wait is cancelled)
		path = Path.Combine(Path.GetTempPath(), "library_check-never-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
		var never = FrameStore.Create(path, scene.Width, scene.Height, FrameLayers.Field, 3, 1, ramBudget: 1L << 30, memoryReleased: new TaskCompletionSource().Task);
		var disposeClock = Stopwatch.StartNew();
		never.Dispose();
		Check(disposeClock.ElapsedMilliseconds < 1000, "a store still waiting for the released memory disposes at once (" + disposeClock.ElapsedMilliseconds + " ms)");

		// a full-HD store of 1 GB: the writes must run at memory speed and the dispose must not flush the
		// dirty pages to disk (the file is deleted anyway; this used to take a minute for a 10 GB run)
		const int bigW = 1920, bigH = 1080, bigFrames = 64;
		path = Path.Combine(Path.GetTempPath(), "library_check-big-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
		float[] bigField = new float[(bigW + 2) * (bigH + 2)];
		float[] bigIntensity = new float[(bigW + 2) * (bigH + 2)];
		for (int i = 0; i < bigField.Length; i++)
		{
			bigField[i] = (i % 97) / 97f - 0.5f;
			bigIntensity[i] = (i % 89) / 89f;
		}
		// written the way the run does it: Half planes already converted (the engine's captures), no conversion in the store
		var bigFieldHalf = new Half[bigW * bigH];
		var bigIntensityHalf = new Half[bigW * bigH];
		for (int y = 0; y < bigH; y++)
		{
			TensorPrimitives.ConvertToHalf(bigField.AsSpan((y + 1) * (bigW + 2) + 1, bigW), bigFieldHalf.AsSpan(y * bigW, bigW));
			TensorPrimitives.ConvertToHalf(bigIntensity.AsSpan((y + 1) * (bigW + 2) + 1, bigW), bigIntensityHalf.AsSpan(y * bigW, bigW));
		}
		var clock = Stopwatch.StartNew();
		var big = FrameStore.Create(path, bigW, bigH, FrameLayers.Field | FrameLayers.Intensity, bigFrames, 1);
		for (int frame = 0; frame < bigFrames; frame++)
		{
			big.Write(frame, bigFieldHalf, bigIntensityHalf);
		}
		double writeMs = clock.Elapsed.TotalMilliseconds;
		clock.Restart();
		var plane = new Half[bigW * bigH];
		for (int frame = 0; frame < bigFrames; frame++)
		{
			big.CopyLayer(frame, FrameLayer.Field, plane);
			big.CopyLayer(frame, FrameLayer.Intensity, plane);
		}
		double readMs = clock.Elapsed.TotalMilliseconds;
		// every sample of both planes of the last frame: the copier's chunks (2026-11-03) must join up exactly
		big.CopyLayer(bigFrames - 1, FrameLayer.Field, plane);
		bool fieldSame = plane.AsSpan().SequenceEqual(bigFieldHalf);
		big.CopyLayer(bigFrames - 1, FrameLayer.Intensity, plane);
		Check(fieldSame && plane.AsSpan().SequenceEqual(bigIntensityHalf), "big store reads back what was written, every sample of both planes");
		clock.Restart();
		big.Dispose();
		double disposeMs = clock.Elapsed.TotalMilliseconds;
		Console.WriteLine($"  1 GB store in memory ({big.FramesInMemory} of {bigFrames} frames): {writeMs:0} ms writing ({big.TotalBytes / writeMs / 1000:0} MB/s, {writeMs / bigFrames:0.0} ms/frame), {readMs:0} ms reading ({readMs / bigFrames:0.0} ms/frame), {disposeMs:0} ms dispose");
		Check(!File.Exists(path), "big store deleted on dispose");
		Check(disposeMs < 1500, "disposing a 1 GB store does not flush it to disk (" + disposeMs.ToString("0") + " ms)");
	}

	/// <summary>
	/// The reduced preview (2026-10-15): FrameConversion against the renderer's index mapping and the block means,
	/// the CPU captures of every shape equal to it and the GPU's within the card's arithmetic, a store of index
	/// bytes, the export's refusal of it, a spec with a quality, and a headless run plus a scratch frame in that shape.
	/// </summary>
	/// <summary>
	/// The exception the log carries for an unreadable file (2026-11-03): its text quotes the file under the error, the
	/// whole of a small file, the first 64 K characters of a large one and then the line an XML error names, and says why
	/// when the file itself cannot be read.
	/// </summary>
	private static void CheckUnreadableFile()
	{
		Console.WriteLine("--- unreadable file: the log's quotation");
		string folder = Path.Combine(Path.GetTempPath(), "library_check-unreadable-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			// a duplicate attribute: the reader's error, with a line and a position
			string small = Path.Combine(folder, "small.wdproj");
			File.WriteAllText(small, "<?xml version=\"1.0\"?>\n<Project Version=\"3\" Version=\"3\" />\n");
			Exception? error = null;
			try
			{
				Project.Load(small);
			}
			catch (Exception ex)
			{
				error = ex;
			}
			UnreadableFileException? wrapped = error == null ? null : new UnreadableFileException(small, error);
			string text = wrapped?.ToString() ?? "";
			Check(wrapped != null && text.Contains("duplicate attribute", StringComparison.Ordinal) && text.Contains("--- the text of '", StringComparison.Ordinal) && wrapped.Text == File.ReadAllText(small),
				"a small malformed project: the error names the duplicate attribute, the quotation is the whole file");
			// a large file, the error on a line beyond the quoted head: a comment of 100,000 characters, then the duplicate
			string large = Path.Combine(folder, "large.wdproj");
			File.WriteAllText(large, "<?xml version=\"1.0\"?>\n<!-- " + new string('x', 100_000) + " -->\n<Project Version=\"3\" Version=\"3\" />\n");
			error = null;
			try
			{
				Project.Load(large);
			}
			catch (Exception ex)
			{
				error = ex;
			}
			wrapped = error == null ? null : new UnreadableFileException(large, error);
			Check(wrapped != null && wrapped.Text.Length < 70_000 && wrapped.Text.Contains(" more characters", StringComparison.Ordinal)
				&& wrapped.Text.Contains("--- line 3, position", StringComparison.Ordinal) && wrapped.Text.Contains("<Project Version=\"3\" Version=\"3\" />", StringComparison.Ordinal),
				"a large malformed project: the first 64 K characters, then the line the XML error names");
			Check(UnreadableFileException.Excerpt(Path.Combine(folder, "missing.wdscheme"), null).StartsWith("(the file could not be read:", StringComparison.Ordinal),
				"a missing file: the quotation says why");
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	/// <summary>
	/// A run's store bytes at another preview quality (the memory warning's arithmetic, 2026-11-03): ⌈frames / frame
	/// divisor⌉ frames of the canvas at the resolution divisor in the sample format, against the sizes written out, and a
	/// spec made at that quality needing the same.
	/// </summary>
	private static void CheckStoreBytes()
	{
		Console.WriteLine("--- store bytes at a preview quality (the memory warning)");
		var project = new Project();
		project.Scene.Width = 64;
		project.Scene.Height = 48;
		project.Simulation.Steps = 18;
		project.Simulation.VisualizeEvery = 2;
		RunSpec full = RunSpec.Create(project, null, preferGpu: false);
		int planes = full.TrackIntensity ? 2 : 1;
		long plane = 64L * 48;
		long halfPlane = 32L * 24;
		Check(full.FrameCount == 9 && full.StoreBytes == 9 * 2 * plane * planes
			&& full.StoreBytesAt(new PreviewQuality(2, 1, FrameFormat.Half)) == 9 * 2 * halfPlane * planes
			&& full.StoreBytesAt(new PreviewQuality(1, 2, FrameFormat.Half)) == 5 * 2 * plane * planes
			&& full.StoreBytesAt(new PreviewQuality(1, 4, FrameFormat.Half)) == 3 * 2 * plane * planes
			&& full.StoreBytesAt(new PreviewQuality(1, 1, FrameFormat.Index)) == 9 * plane * planes
			&& full.StoreBytesAt(new PreviewQuality(2, 2, FrameFormat.Index)) == 5 * halfPlane * planes,
			"store bytes: 9 frames of 64x48 as halves; half resolution a quarter, every second frame 5 frames, every fourth 3, 8-bit half, all three together");
		RunSpec reduced = RunSpec.Create(project, null, preferGpu: false, quality: new PreviewQuality(2, 2, FrameFormat.Index));
		Check(reduced.StoreBytes == full.StoreBytesAt(reduced.Quality) && reduced.StoreBytes == FrameStore.MemoryOf(reduced.Shape, reduced.Layers, reduced.StoredFrameCount),
			"a spec made at a quality needs what the full spec computes for it: the store's frame bytes times its capacity");
	}

	private static void CheckPreviewQuality(SceneData scene, int W, int H)
	{
		Console.WriteLine("--- preview quality (shrunk and 8-bit frames)");
		var options = new EngineOptions(1.0, 1.0, true, false, 10.0, 1.0);
		var cpu = new WaveSimulator(scene, options, Sources());
		Run(cpu, 100);
		float[] field = (float[])cpu.FieldData.Clone();
		float[] inten = (float[])cpu.IntensityData!.Clone();
		int stride = cpu.Stride;
		FrameShape full = FrameShape.Full(W, H);
		FrameShape half = full with { Divisor = 2 };
		FrameShape third = full with { Divisor = 3 };
		FrameShape index = full with { Format = FrameFormat.Index, FieldBrightness = 30f, IntensityBrightness = 5f };
		FrameShape halfIndex = half with { Format = FrameFormat.Index, FieldBrightness = 30f, IntensityBrightness = 5f };
		Check(half.Width == 32 && half.Height == 24 && third.Width == 22 && third.Height == 16 && index.SampleBytes == 1 && halfIndex.PlaneBytes == 32 * 24 && full.IsFull && !half.IsFull,
			"shapes: 64x48 halves to 32x24, thirds to 22x16, an index sample is one byte");
		float tolerance = 2e-3f * Math.Max(1f, MaxAbs(field));

		// the reference conversion: the full shape is the plain float-to-half conversion
		var fullBytes = new byte[full.PlaneBytes];
		FrameConversion.Convert(field, stride + 1, stride, full, false, fullBytes);
		Check(MaxDiff(field, stride, W, H, MemoryMarshal.Cast<byte, Half>(fullBytes)) < tolerance, "full shape: the plain half conversion");
		// the intensity as its square root, the preview's, or as itself for an OpenEXR export of I (2026-10-23): each the
		// half of the float, the root correctly rounded first; the field the same either way
		FrameShape linear = full with { IntensityRoot = false };
		var rootBytes = new byte[full.PlaneBytes];
		var linearBytes = new byte[full.PlaneBytes];
		var fieldLinearBytes = new byte[full.PlaneBytes];
		FrameConversion.Convert(inten, stride + 1, stride, full, true, rootBytes);
		FrameConversion.Convert(inten, stride + 1, stride, linear, true, linearBytes);
		FrameConversion.Convert(field, stride + 1, stride, linear, false, fieldLinearBytes);
		bool samples = true;
		for (int y = 0; y < H; y++)
		{
			for (int x = 0; x < W; x++)
			{
				float i = inten[(y + 1) * stride + x + 1];
				samples &= MemoryMarshal.Cast<byte, Half>(rootBytes)[y * W + x] == (Half)MathF.Sqrt(i) && MemoryMarshal.Cast<byte, Half>(linearBytes)[y * W + x] == (Half)i;
			}
		}
		Check(samples && fieldLinearBytes.AsSpan().SequenceEqual(fullBytes) && full.IntensityRoot && !full.SameLayoutAs(linear),
			"the 16-bit intensity: the half of its square root by default, of itself when the shape says so; the field alike; the two layouts differ");
		var rootSource = new SnapshotFrameSource(new FrameSnapshot(full, true));
		var linearSource = new SnapshotFrameSource(new FrameSnapshot(linear, true));
		Check(rootSource.KindOf(FrameLayer.Intensity) == SampleKind.IntensityRoot && linearSource.KindOf(FrameLayer.Intensity) == SampleKind.Intensity
			&& linearSource.KindOf(FrameLayer.Field) == SampleKind.Field && RunSpec.Create(new Project(), null, false, quality: PreviewQuality.Full with { IntensityRoot = false }).Shape.IntensityRoot == false,
			"a frame source says what its intensity holds; a run spec takes it from its quality");
		// a half-size frame holds the 2 x 2 block means
		var halfBytes = new byte[half.PlaneBytes];
		FrameConversion.Convert(field, stride + 1, stride, half, false, halfBytes);
		ReadOnlySpan<Half> halves = MemoryMarshal.Cast<byte, Half>(halfBytes);
		float worst = 0f;
		for (int y = 0; y < 24; y++)
		{
			for (int x = 0; x < 32; x++)
			{
				float mean = (field[(2 * y + 1) * stride + 2 * x + 1] + field[(2 * y + 1) * stride + 2 * x + 2] + field[(2 * y + 2) * stride + 2 * x + 1] + field[(2 * y + 2) * stride + 2 * x + 2]) / 4f;
				worst = Math.Max(worst, Math.Abs(mean - (float)halves[y * 32 + x]));
			}
		}
		Check(worst < tolerance, "half shape: every sample is the mean of its 2 x 2 block (worst " + worst + ")");
		// a divisor that does not divide the canvas: the last column is one cell wide and averaged over the cells it has
		var thirdBytes = new byte[third.PlaneBytes];
		FrameConversion.Convert(field, stride + 1, stride, third, false, thirdBytes);
		ReadOnlySpan<Half> thirds = MemoryMarshal.Cast<byte, Half>(thirdBytes);
		worst = 0f;
		for (int y = 0; y < 16; y++)
		{
			float mean = (field[(3 * y + 1) * stride + 64] + field[(3 * y + 2) * stride + 64] + field[(3 * y + 3) * stride + 64]) / 3f;
			worst = Math.Max(worst, Math.Abs(mean - (float)thirds[y * 22 + 21]));
		}
		Check(worst < tolerance, "third shape: the clipped last column is the mean of its 1 x 3 block (worst " + worst + ")");
		// an index frame is the renderer's palette index of every cell, bit for bit
		var indexField = new byte[index.PlaneBytes];
		var indexInten = new byte[index.PlaneBytes];
		FrameConversion.Convert(field, stride + 1, stride, index, false, indexField);
		FrameConversion.Convert(inten, stride + 1, stride, index, true, indexInten);
		bool same = true;
		for (int y = 0; y < H && same; y++)
		{
			for (int x = 0; x < W; x++)
			{
				int at = (y + 1) * stride + x + 1;
				if (indexField[y * W + x] != ColorLut.FieldIndex((float)(Half)field[at], 30f) || indexInten[y * W + x] != ColorLut.IntensityRootIndex((float)(Half)MathF.Sqrt(inten[at]), 5f))
				{
					same = false;
					break;
				}
			}
		}
		Check(same && indexField.Max() > 127 && indexField.Min() < 127 && indexInten.Max() > 0, "index shape: the renderer's palette index of every cell's half-rounded field and intensity root at brightness 30 and 5, both sides of the centre used");
		// an index frame holds the palette index of the very half a 16-bit frame stores, both planes (one rule since
		// 2026-10-23): the 8-bit preview shows the stored samples, at the palette's 255 colours
		var halfField = new byte[full.PlaneBytes];
		var halfRoot = new byte[full.PlaneBytes];
		FrameConversion.Convert(field, stride + 1, stride, full, false, halfField);
		FrameConversion.Convert(inten, stride + 1, stride, full, true, halfRoot);
		ReadOnlySpan<Half> storedHalves = MemoryMarshal.Cast<byte, Half>(halfField);
		ReadOnlySpan<Half> storedRoots = MemoryMarshal.Cast<byte, Half>(halfRoot);
		bool rendersAlike = true;
		for (int i = 0; i < W * H && rendersAlike; i++)
		{
			rendersAlike = indexField[i] == ColorLut.FieldIndex((float)storedHalves[i], 30f) && indexInten[i] == ColorLut.IntensityRootIndex((float)storedRoots[i], 5f);
		}
		Check(rendersAlike, "index shape: every index is the palette index of the half a 16-bit frame of the same floats stores, field and intensity root");
		// the darkest indices at a high brightness (2026-10-23): an intensity of 2e-8 at brightness 30 is index 1.08, so 1;
		// the intensity rounded to a half first had made it 0 (below half's smallest step, 6e-8), the root keeps it
		{
			FrameShape bright = new(4, 1, 1, FrameFormat.Index, 1f, 30f);
			float[] dim = [2e-8f, 5e-8f, 1e-7f, 1e-6f];
			var dimIndices = new byte[4];
			FrameConversion.Convert(dim, 0, 4, bright, true, dimIndices);
			var oldIndices = dim.Select(i => ColorLut.IntensityIndex((float)(Half)i, 30f)).ToArray();
			var exact = dim.Select(i => (int)(Math.Sqrt(i) * 30 * 254)).ToArray();
			Check(dimIndices.Select(b => (int)b).SequenceEqual(exact) && oldIndices[0] == 0 && exact[0] == 1,
				$"index of dim intensities at brightness 30: {string.Join(" ", dimIndices)}, the exact ones; rounding I to a half first gave {string.Join(" ", oldIndices)}");
		}

		// the CPU engine's captures of every shape equal the reference conversion of its own floats
		foreach (FrameShape shape in new[] { linear, half, third, index, halfIndex, half with { IntensityRoot = false } })
		{
			var expectedField = new byte[shape.PlaneBytes];
			var expectedInten = new byte[shape.PlaneBytes];
			FrameConversion.Convert(field, stride + 1, stride, shape, false, expectedField);
			FrameConversion.Convert(inten, stride + 1, stride, shape, true, expectedInten);
			var snapshot = cpu.CreateSnapshot(shape, true);
			cpu.CaptureFrame(snapshot, true);
			cpu.AwaitFrame(snapshot);
			Check(snapshot.HasIntensity && snapshot.FieldBytes.SequenceEqual(expectedField) && snapshot.IntensityBytes.SequenceEqual(expectedInten),
				$"CPU capture of 1/{shape.Divisor} {shape.Format}{(shape.IntensityRoot ? "" : ", the intensity as I")} equals the reference conversion");
		}
		// the GPU's: halves within the tolerance, indices exact where the card's square root rounds correctly (within one elsewhere)
		GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(scene, options, Sources(), out string problems);
		if (gpu == null)
		{
			Console.WriteLine("  skip GPU: " + problems);
		}
		else
		{
			using (gpu)
			{
				Run(gpu, 100);
				gpu.PrepareFrame(true, true);
				float[] gpuField = (float[])gpu.FieldData.Clone();
				float[] gpuInten = (float[])gpu.IntensityData!.Clone();
				foreach (FrameShape shape in new[] { linear, half, third, index, halfIndex, half with { IntensityRoot = false } })
				{
					var expectedField = new byte[shape.PlaneBytes];
					var expectedInten = new byte[shape.PlaneBytes];
					FrameConversion.Convert(gpuField, stride + 1, stride, shape, false, expectedField);
					FrameConversion.Convert(gpuInten, stride + 1, stride, shape, true, expectedInten);
					var snapshot = gpu.CreateSnapshot(shape, true);
					gpu.CaptureFrame(snapshot, true);
					gpu.AwaitFrame(snapshot);
					int worstIndex = 0;
					float worstHalf = 0f;
					if (shape.Format == FrameFormat.Index)
					{
						for (int i = 0; i < expectedField.Length; i++)
						{
							worstIndex = Math.Max(worstIndex, Math.Max(Math.Abs(snapshot.FieldBytes[i] - expectedField[i]), Math.Abs(snapshot.IntensityBytes[i] - expectedInten[i])));
						}
					}
					else
					{
						ReadOnlySpan<Half> gotField = snapshot.Field;
						ReadOnlySpan<Half> wantField = MemoryMarshal.Cast<byte, Half>(expectedField);
						ReadOnlySpan<Half> gotInten = snapshot.Intensity;
						ReadOnlySpan<Half> wantInten = MemoryMarshal.Cast<byte, Half>(expectedInten);
						for (int i = 0; i < gotField.Length; i++)
						{
							worstHalf = Math.Max(worstHalf, Math.Max(Math.Abs((float)gotField[i] - (float)wantField[i]), Math.Abs((float)gotInten[i] - (float)wantInten[i])));
						}
					}
					int allowed = gpu.Device.CorrectlyRoundedSqrt ? 0 : 1;
					Check(snapshot.HasIntensity && worstIndex <= allowed && worstHalf < tolerance,
						$"GPU capture of 1/{shape.Divisor} {shape.Format}{(shape.IntensityRoot ? "" : ", the intensity as I")} matches the reference conversion (index off by at most {worstIndex} of {allowed} allowed, halves by {worstHalf})");
				}
			}
		}

		// a store of half-size index bytes: the float, snapshot and byte writes agree, the half overloads refuse it, the export refuses it
		string path = Path.Combine(Path.GetTempPath(), "library_check-index-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
		using (var store = FrameStore.Create(path, halfIndex, FrameLayers.Field | FrameLayers.Intensity, 3, 2, ramBudget: 0))
		{
			Check(store.Format == FrameFormat.Index && store.Width == 32 && store.Height == 24 && store.CanvasWidth == 64 && store.CanvasHeight == 48 && store.PlaneBytes == 768 && store.FileBytes == FrameStore.HeaderSize + 3 * 2 * 768,
				"index store: 32x24 bytes per plane of a 64x48 canvas, the file sized for them");
			var expectedField = new byte[halfIndex.PlaneBytes];
			var expectedInten = new byte[halfIndex.PlaneBytes];
			FrameConversion.Convert(field, stride + 1, stride, halfIndex, false, expectedField);
			FrameConversion.Convert(inten, stride + 1, stride, halfIndex, true, expectedInten);
			store.Write(0, cpu.FieldData, cpu.IntensityData, stride);
			var snapshot = new FrameSnapshot(halfIndex, true);
			cpu.CaptureFrame(snapshot, true);
			cpu.AwaitFrame(snapshot);
			store.Write(1, snapshot);
			store.Write(2, expectedField, expectedInten);
			var read = new byte[halfIndex.PlaneBytes];
			bool allSame = true;
			for (int frame = 0; frame < 3; frame++)
			{
				allSame &= store.CopyLayer(frame, FrameLayer.Field, read.AsSpan()) && read.AsSpan().SequenceEqual(expectedField);
				allSame &= store.CopyLayer(frame, FrameLayer.Intensity, read.AsSpan()) && read.AsSpan().SequenceEqual(expectedInten);
			}
			Check(store.Count == 3 && allSame, "index store: the float, snapshot and byte writes read back as the same bytes");
			bool refused = false;
			try
			{
				store.CopyLayer(0, FrameLayer.Field, new Half[32 * 24]);
			}
			catch (InvalidOperationException)
			{
				refused = true;
			}
			Check(refused, "index store: the Half read is refused");
			// every export needs the 16-bit samples (2026-10-23): a picture takes every level of the gradient from them, a
			// data export (OpenEXR) writes them; both fail on a store of palette indices, and the application runs again first
			ColorLut indexLut = ColorLut.Build(ColorSchemeStore.LoadBuiltIn(null).First(s => s.Name == Display.DefaultFieldColorScheme));
			var collected = new CollectingSink(32, 24);
			var indexJob = new ExportJob(store, 0, 2, [new ExportTarget(new ExportLayer(FrameLayer.Field, indexLut, 1f, null, 0, null), "memory", () => collected)], null);
			indexJob.Start();
			Check(indexJob.Completion.Wait(30000) && indexJob.State == ExportState.Failed && indexJob.Error?.Message.Contains("16-bit") == true && collected.Frames.Count == 0,
				"an export of pictures from a store of palette indices fails: it needs the 16-bit samples");
			string exrOnIndex = Path.Combine(Path.GetTempPath(), "library_check-exr-" + Guid.NewGuid().ToString("N") + ".exr");
			var dataJob = new ExportJob(store, 0, 0, [new ExportTarget(new ExportLayer(FrameLayer.Field, ColorLut.Grey, 1f, null, 0, null), exrOnIndex, () => ExportFormats.OpenStill(ExportFormat.Exr, exrOnIndex, 32, 24, 70))], null);
			dataJob.Start();
			Check(dataJob.Completion.Wait(30000) && dataJob.State == ExportState.Failed && dataJob.Error?.Message.Contains("16-bit") == true,
				"a data export (OpenEXR) of a store of palette indices fails: it needs the 16-bit samples");
			File.Delete(exrOnIndex);
			refused = false;
			try
			{
				store.Write(3, new FrameSnapshot(half, true));
			}
			catch (ArgumentException)
			{
				refused = true;
			}
			Check(refused, "a snapshot of another format is refused");
		}
		Check(!File.Exists(path), "index store file deleted on dispose");

		// a spec with a quality: the interval multiplied, the frames fewer, the shape from the display brightness; a run and a scratch frame in it
		var project = new Project();
		project.Scene.Width = W;
		project.Scene.Height = H;
		project.Scene.DampingEdge = 4;
		project.Simulation.Steps = 200;
		project.Simulation.VisualizeEvery = 2;
		project.Display.Field.Brightness = 25;
		project.Display.Intensity.Brightness = 4;
		Source source = project.AddSource();
		source.X = 20;
		source.Y = 24;
		source.Frequency = 120;
		Probe lineProbe = project.AddProbe();
		lineProbe.Kind = ProbeKind.Line;
		lineProbe.AX = 5;
		lineProbe.AY = 5;
		lineProbe.BX = 40;
		lineProbe.BY = 30;
		var quality = new PreviewQuality(2, 2, FrameFormat.Index);
		RunSpec spec = RunSpec.Create(project, null, preferGpu: false, quality: quality);
		RunSpec fullSpec = RunSpec.Create(project, null, preferGpu: false);
		Check(spec.VisualizeEvery == 2 && spec.FrameCount == 100 && spec.FrameStride == 2 && spec.CaptureEvery == 4 && spec.StoredFrameCount == 50 && spec.StoredIndexOf(99) == 49
			&& spec.Quality == quality && !spec.Quality.IsFull && fullSpec.Quality.IsFull && fullSpec.FrameStride == 1 && fullSpec.StoredFrameCount == 100,
			"spec with a quality of 1/2, every 2nd frame, index: the timeline keeps 100 frames every 2 steps, 50 of them stored every 4 steps; the full spec stores all 100");
		Check(spec.Shape == new FrameShape(W, H, 2, FrameFormat.Index, 25f, 4f) && spec.Shape.Width == 32 && fullSpec.Shape.IsFull, "the spec's shape: half the canvas, index bytes, the display brightness of both layers");
		Check(spec.SameSceneAs(fullSpec), "the quality does not change the scene, so an engine serves both");
		path = Path.Combine(Path.GetTempPath(), "library_check-quality-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
		using (var store = FrameStore.Create(path, spec.Shape, spec.Layers, spec.StoredFrameCount, spec.CaptureEvery))
		{
			var run = new SimulationRun(spec, store, null);
			run.Start();
			Check(run.Completion.Wait(30000) && run.State == RunState.Completed && store.Count == 50 && store.StepOf(49) == 196 && run.FrameCount == 100 && run.Frames.Count == 100,
				"a run in that quality completes with 50 stored frames of 32x24 index bytes covering all 100 timeline frames");
			// stored frame 49 is timeline frame 98, step 196: an engine stepped 197 times and captured in the shape gives the same bytes,
			// and timeline frame 99 (not stored) reads the same stored frame
			IWaveEngine reference = WaveEngineFactory.Create(SceneData.Empty(W, H, spec.DampingEdge, spec.RefractiveIndex, spec.Edges == EdgeMode.Open), spec.Engine, false, spec.Emitters, out _);
			Run(reference, 197);
			var expected = new FrameSnapshot(spec.Shape, spec.TrackIntensity);
			reference.CaptureFrame(expected, spec.TrackIntensity);
			reference.AwaitFrame(expected);
			var stored = new byte[spec.Shape.PlaneBytes];
			var shown = new byte[spec.Shape.PlaneBytes];
			Check(store.CopyLayer(49, FrameLayer.Field, stored.AsSpan()) && stored.AsSpan().SequenceEqual(expected.FieldBytes) && stored.Max() > 127 && stored.Min() < 127,
				"stored frame 49 equals a fresh engine's capture of step 196 in that shape");
			Check(run.Frames is StridedFrameSource && run.Frames.CopyLayer(98, FrameLayer.Field, shown.AsSpan()) && shown.AsSpan().SequenceEqual(stored)
				&& run.Frames.CopyLayer(99, FrameLayer.Field, shown.AsSpan()) && shown.AsSpan().SequenceEqual(stored),
				"timeline frames 98 and 99 both show stored frame 49 (a stored frame stands in for the next one)");
			Check(run.Traces.Length == 1 && run.Traces[0].Profiles.Count == 100,
				$"the line probe's profiles are collected per timeline frame (100), not per stored one ({run.Traces.Length} traces, {(run.Traces.Length > 0 ? run.Traces[0].Profiles.Count : 0)} profiles)");
			using (var scratch = new ScratchRunner(null))
			using (var arrived = new ManualResetEventSlim())
			{
				ScratchResult? result = null;
				scratch.Completed += (_, r) =>
				{
					result = r;
					arrived.Set();
				};
				scratch.Request(spec, 98);
				Check(arrived.Wait(30000) && result is { Snapshot: not null, Frame: 98 } && result.Snapshot.Shape == spec.Shape && result.Snapshot.FieldBytes.SequenceEqual(expected.FieldBytes),
					"a scratch frame 98 in that quality has the spec's shape and the stored bytes (the scratch always computes the exact timeline frame)");
			}
			run.Dispose();
		}
	}

	/// <summary>A sink that keeps every frame's pixels in memory, for checks on what a job renders.</summary>
	private sealed class CollectingSink(int width, int height) : IFrameSink
	{
		public List<int[]> Frames { get; } = [];
		public int Width => width;
		public int Height => height;
		public int FrameCount => Frames.Count;
		public string Output => "memory";
		public void Add(ReadOnlySpan<int> pixels) => Frames.Add(pixels[..(width * height)].ToArray());
		public void Close() { }
		public void Dispose() { }
	}

	private static void CheckRenderer()
	{
		Console.WriteLine("--- renderer");
		static int Grey(int level) => unchecked((int)0xFF000000) | (level << 16) | (level << 8) | level;
		// 16-bit samples (2026-10-23): the gradient at the sample's own position, rounded; grey is the Python code's 0..254 ramp
		var values = new Half[4] { (Half)0f, (Half)0.5f, (Half)(-2f), (Half)1f };
		var pixels = new int[4];
		FrameRenderer.Render(values, 2, 2, ColorLut.Grey, SampleKind.Field, 1f, pixels);
		Check(pixels[0] == Grey(127) && pixels[1] == Grey(191) && pixels[2] == Grey(0) && pixels[3] == Grey(254),
			"field, grey: 0 -> 127, 0.5 -> 190.5 rounded to 191 (the palette truncates to 190), the clamp -> 0 and 254");
		// the intensity plane holds the square root (2026-10-23)
		var intensities = new Half[4] { (Half)0f, (Half)(-1f), (Half)0.5f, (Half)3f };
		FrameRenderer.Render(intensities, 2, 2, ColorLut.Grey, SampleKind.IntensityRoot, 1f, pixels);
		Check(pixels[0] == Grey(0) && pixels[1] == Grey(0) && pixels[2] == Grey(127) && pixels[3] == Grey(254), "intensity, grey: <= 0 -> 0, a stored root of 0.5 -> 127, the clamp -> 254");
		// a run for an OpenEXR export of I stores the intensity itself, coloured through its square root
		var linearIntensities = new Half[4] { (Half)0f, (Half)(-1f), (Half)0.25f, (Half)9f };
		FrameRenderer.Render(linearIntensities, 2, 2, ColorLut.Grey, SampleKind.Intensity, 1f, pixels);
		Check(pixels[0] == Grey(0) && pixels[1] == Grey(0) && pixels[2] == Grey(127) && pixels[3] == Grey(254), "intensity stored as I, grey: <= 0 -> 0, sqrt(0.25) -> 127, the clamp -> 254");
		// every value a half can hold, against the gradient worked out here: a ramp from black to blue 250 gets the nearest
		// level to 250 × position, NaN in the field's middle, and every one of the 251 levels is used
		{
			ColorLut ramp = ColorLut.Build(new ColorScheme("ramp", [new Rgb(0, 0, 0), new Rgb(0, 0, 250)]));
			int[] fieldTable = ramp.HalfTable(SampleKind.Field, 3.5f);
			int[] intensityTable = ramp.HalfTable(SampleKind.IntensityRoot, 3.5f);
			int[] linearTable = ramp.HalfTable(SampleKind.Intensity, 3.5f);
			bool nearest = true;
			for (int bits = 0; bits < ColorLut.HalfValues && nearest; bits++)
			{
				double value = (double)BitConverter.UInt16BitsToHalf((ushort)bits);
				double fieldAt = double.IsNaN(value) ? 0.5 : (Math.Clamp(value * 3.5, -1.0, 1.0) + 1.0) / 2.0;
				double intensityAt = value > 0.0 ? Math.Min(value * 3.5, 1.0) : 0.0;
				double linearAt = value > 0.0 ? Math.Min(Math.Sqrt(value) * 3.5, 1.0) : 0.0;
				nearest = (fieldTable[bits] & ~0xFF) == unchecked((int)0xFF000000) && Math.Abs((fieldTable[bits] & 0xFF) - 250 * fieldAt) <= 0.5 + 1e-9
					&& (intensityTable[bits] & ~0xFF) == unchecked((int)0xFF000000) && Math.Abs((intensityTable[bits] & 0xFF) - 250 * intensityAt) <= 0.5 + 1e-9
					&& (linearTable[bits] & ~0xFF) == unchecked((int)0xFF000000) && Math.Abs((linearTable[bits] & 0xFF) - 250 * linearAt) <= 0.5 + 1e-9;
			}
			Check(nearest && fieldTable.Distinct().Count() == 251 && intensityTable.Distinct().Count() == 251 && linearTable.Distinct().Count() == 251,
				"half tables: each of the 65,536 values gets the level nearest the gradient at its position (field, the intensity's root, the intensity; NaN, infinities, zeros), every level used");
			// the frame: a lookup per sample over several row blocks and an odd width
			var rng = new Random(11);
			var frame = new Half[37 * 70];
			for (int i = 0; i < frame.Length; i++)
			{
				frame[i] = BitConverter.UInt16BitsToHalf((ushort)rng.Next(ColorLut.HalfValues));
			}
			var framePixels = new int[frame.Length];
			FrameRenderer.Render(frame, 37, 70, ramp, SampleKind.Field, 3.5f, framePixels);
			Check(Enumerable.Range(0, frame.Length).All(i => framePixels[i] == fieldTable[BitConverter.HalfToUInt16Bits(frame[i])]), "Render looks every sample up in the half table (37 × 70, three row blocks)");
		}
		// the case that started it (2026-10-23): Inferno's dark start, blue 3 to 33 over its first 15 palette entries,
		// showed as patches two levels apart; the half table has every level between
		ColorScheme infernoScheme = ColorSchemeStore.LoadBuiltIn(null).First(s => s.Name == "Inferno");
		ColorLut inferno = ColorLut.Build(infernoScheme);
		int paletteBlues = Enumerable.Range(0, 16).Select(i => Rgb.FromArgb(inferno.Argb[i]).B).Distinct().Count();
		int[] dark = inferno.HalfTable(SampleKind.IntensityRoot, 1f);
		int halfBlues = Enumerable.Range(0, ColorLut.HalfValues)
			.Where(bits => (double)BitConverter.UInt16BitsToHalf((ushort)bits) is var v && v > 0.0 && v <= 15 / 254.0)
			.Select(bits => dark[bits] & 0xFF).Distinct().Count();
		Check(paletteBlues == 16 && halfBlues == 31, $"Inferno's dark start: the palette's first 16 entries have {paletteBlues} levels of blue, the half table every one from 3 to 33 ({halfBlues})");
		Check(ReferenceEquals(inferno.HalfTable(SampleKind.IntensityRoot, 1f), dark) && !ReferenceEquals(inferno.HalfTable(SampleKind.Field, 1f), dark) && !ReferenceEquals(inferno.HalfTable(SampleKind.IntensityRoot, 2f), dark),
			"a half table is kept per layer until another brightness asks");
		// the 8-bit palette, as the author's table
		var stops = new Rgb[ColorScheme.StopCount];
		for (int i = 0; i < stops.Length; i++)
		{
			stops[i] = new Rgb((byte)(i * 30), 0, (byte)(255 - i * 30));
		}
		ColorScheme scheme = new("test", stops);
		ColorLut built = ColorLut.Build(scheme);
		Check(Rgb.FromArgb(built.Argb[0]) == stops[0] && Rgb.FromArgb(built.Argb[254]) == stops[^1] && Rgb.FromArgb(built.Argb[127]) == stops[4], "the palette of a stop scheme hits the stops at 0, 127 and 254");
		var indexed = new int[4];
		FrameRenderer.RenderIndexed(new byte[] { 0, 127, 254, 255 }, 2, 2, built, indexed);
		Check(indexed.SequenceEqual([built.Argb[0], built.Argb[127], built.Argb[254], built.Argb[254]]), "RenderIndexed looks each byte up in the palette, 255 as 254");
		ColorLut lifted = ColorLut.Build(scheme, blackLevel: 0.5);
		Check(Rgb.FromArgb(lifted.Argb[0]).G == 127 && ((lifted.HalfTable(SampleKind.IntensityRoot, 1f)[0] >> 8) & 0xFF) == 128,
			"black level 0.5 lifts a 0 channel to 127 in the palette (truncated, as in Python) and to 128 in the half table (rounded)");
		CheckScreenRenderer(inferno);
	}

	/// <summary>The preview's 16-bit path (2026-10-23): resampling in value space to the screen's size, the 16-bit colours, the Bayer dither.</summary>
	private static void CheckScreenRenderer(ColorLut lut)
	{
		// the axes: every target pixel's weights sum to 1, shrinking covers each sample once in all, the same size copies
		bool sums = true;
		bool covered = true;
		foreach ((int source, int target) in new[] { (1920, 960), (1920, 1000), (37, 5), (64, 640), (64, 97), (5, 5), (1, 7), (7, 1) })
		{
			var axis = new ResampleAxis(source, target);
			var coverage = new double[source];
			for (int t = 0; t < target; t++)
			{
				ReadOnlySpan<float> weights = axis.WeightsOf(t, out int first);
				double sum = 0;
				for (int k = 0; k < weights.Length; k++)
				{
					sum += weights[k];
					coverage[first + k] += weights[k];
				}
				sums &= Math.Abs(sum - 1) < 1e-5 && first >= 0 && first + weights.Length <= source;
			}
			double scale = source / (double)target;
			covered &= target > source || coverage.All(c => Math.Abs(c - 1 / scale) < 1e-4);
		}
		Check(sums && covered, "resampling axes: each pixel's weights sum to 1 within the samples; shrinking weighs every sample alike (shrink, enlarge, odd ratios, 1:1, one sample)");
		var copy = new ResampleAxis(9, 9);
		var half = new ResampleAxis(8, 4);
		var twice = new ResampleAxis(2, 4);
		Check(copy.WeightsOf(4, out int c4).SequenceEqual([1f]) && c4 == 4 && half.WeightsOf(1, out int h1).SequenceEqual([0.5f, 0.5f]) && h1 == 2
			&& twice.WeightsOf(1, out int t1).SequenceEqual([0.75f, 0.25f]) && t1 == 0 && twice.WeightsOf(0, out int t0).SequenceEqual([1f]) && t0 == 0,
			"resampling axes: 1:1 copies, halving averages pairs, doubling blends a quarter of the neighbour at the pixel's centre and holds the edge");
		// the frames: at the samples' size without dithering, the screen picture is the half table's, bit for bit
		var rng = new Random(5);
		int w = 61;
		int h = 45;
		var plane = new Half[w * h];
		for (int i = 0; i < plane.Length; i++)
		{
			plane[i] = (Half)(rng.NextDouble() * 0.2);
		}
		var direct = new int[w * h];
		FrameRenderer.Render(plane, w, h, lut, SampleKind.IntensityRoot, 3f, direct);
		var screen = new int[w * h];
		new ScreenRenderer(w, h, w, h).Render(plane, lut, SampleKind.IntensityRoot, 3f, 0, screen);
		Check(screen.SequenceEqual(direct), "screen renderer at the samples' size, no dithering: the half table's picture, bit for bit");
		// halved: each pixel the colour of its 2 x 2 block's mean (in value space, before any colour; the intensity, stored as
		// its root, as energy: the root of the mean square)
		var shrunk = new int[30 * 22];
		new ScreenRenderer(60, 44, 30, 22).Render(plane.AsMemory(0, 60 * 44), lut, SampleKind.IntensityRoot, 3f, 0, shrunk);
		int[] table = lut.HalfTable(SampleKind.IntensityRoot, 3f);
		bool means = true;
		for (int y = 0; y < 22; y++)
		{
			for (int x = 0; x < 30; x++)
			{
				double a = (double)plane[2 * y * 60 + 2 * x], b = (double)plane[2 * y * 60 + 2 * x + 1], c = (double)plane[(2 * y + 1) * 60 + 2 * x], d = (double)plane[(2 * y + 1) * 60 + 2 * x + 1];
				double mean = Math.Sqrt((a * a + b * b + c * c + d * d) / 4);
				// the sums run in single precision (fused or not, by the machine): the half they round to may be the neighbour
				int bits = BitConverter.HalfToUInt16Bits((Half)mean);
				means &= shrunk[y * 30 + x] == table[bits] || shrunk[y * 30 + x] == table[bits - 1] || shrunk[y * 30 + x] == table[bits + 1];
			}
		}
		Check(means, "screen renderer halving: every pixel the colour of its 2 x 2 samples' mean (the intensity's mean energy)");
		// the field's crest and trough average to zero, the gradient's middle, not to a mix of their colours
		var wave = new Half[4 * 2];
		for (int i = 0; i < wave.Length; i++)
		{
			wave[i] = (Half)(i % 2 == 0 ? 0.5f : -0.5f);
		}
		var flat = new int[2];
		new ScreenRenderer(4, 2, 2, 1).Render(wave, lut, SampleKind.Field, 1f, 0, flat);
		Check(flat.All(p => p == lut.HalfTable(SampleKind.Field, 1f)[BitConverter.HalfToUInt16Bits((Half)0f)]), "screen renderer: a crest beside a trough shrinks to the colour of zero");
		// enlarging through the whole path: a 2 x 2 field to 4 x 4, each pixel the bilinear blend of the two samples nearest
		// its centre on each axis (weights 3/4 and 1/4), the outer pixels holding the outer samples; exact in float and half
		Half[] small = [(Half)0.5f, (Half)(-0.5f), (Half)1.0f, (Half)0.0f];
		var large = new int[16];
		new ScreenRenderer(2, 2, 4, 4).Render(small, lut, SampleKind.Field, 1f, 0, large);
		float[] blended = [0.5f, 0.25f, -0.25f, -0.5f, 0.625f, 0.375f, -0.125f, -0.375f, 0.875f, 0.625f, 0.125f, -0.125f, 1.0f, 0.75f, 0.25f, 0.0f];
		int[] fieldTable = lut.HalfTable(SampleKind.Field, 1f);
		Check(large.SequenceEqual(blended.Select(v => fieldTable[BitConverter.HalfToUInt16Bits((Half)v)])), "enlarged 2 x 2 to 4 x 4, every pixel is the bilinear blend of the nearest samples, the ends held");
		// the dither: a flat plane comes out at the colour's two neighbouring 8-bit levels, their mix over one matrix the
		// 16-bit colour to the depth's bits; without dithering the level rounded
		ulong[] deep = lut.DeepHalfTable(SampleKind.IntensityRoot, 1f);
		bool deepRounds = Enumerable.Range(0, ColorLut.HalfValues).All(bits =>
		{
			int argb = lut.HalfTable(SampleKind.IntensityRoot, 1f)[bits];
			ulong d = deep[bits];
			return Math.Abs(((argb >> 16) & 0xFF) - ((d >> 32) & 0xFFFF) / 256.0) <= 0.5 + 1 / 256.0
				&& Math.Abs(((argb >> 8) & 0xFF) - ((d >> 16) & 0xFFFF) / 256.0) <= 0.5 + 1 / 256.0
				&& Math.Abs((argb & 0xFF) - (d & 0xFFFF) / 256.0) <= 0.5 + 1 / 256.0;
		});
		Check(deepRounds, "the 16-bit table rounds to the 8-bit one: each channel within half a level of it");
		Half dark = (Half)0.0007f; // Inferno's dark start: blue between two levels
		ulong colour = deep[BitConverter.HalfToUInt16Bits(dark)];
		int blue16 = (int)(colour & 0xFFFF);
		bool dithers = true;
		var detail = new List<string>();
		foreach (int bits in ScreenRenderer.DitherDepths)
		{
			int side = 1 << ((bits - 8) / 2);
			int[] thresholds = ScreenRenderer.BayerThresholds(bits);
			bool permutation = thresholds.Length == side * side && thresholds.Distinct().Count() == thresholds.Length
				&& thresholds.All(t => t > 0 && t < 512 && (t >> (8 - (bits - 8))) % 2 == 1);
			var flatPlane = new Half[32 * 32];
			Array.Fill(flatPlane, dark);
			var dithered = new int[32 * 32];
			new ScreenRenderer(32, 32, 32, 32).Render(flatPlane, lut, SampleKind.IntensityRoot, 1f, bits, dithered);
			int[] blues = [.. dithered.Select(p => p & 0xFF)];
			double mean = Enumerable.Range(0, side).SelectMany(y => Enumerable.Range(0, side).Select(x => blues[y * 32 + x])).Average();
			// the fraction to the depth's bits, a half going up (the threshold's tie)
			double expected = (blue16 >> 8) + Math.Floor((blue16 & 0xFF) / (double)(1 << (16 - bits)) + 0.5) / (1 << (bits - 8));
			bool twoLevels = blues.All(b => b == blue16 >> 8 || b == (blue16 >> 8) + 1);
			bool tiles = Enumerable.Range(0, 32 * 32).All(i => blues[i] == blues[(i / 32 % side) * 32 + i % 32 % side]);
			dithers &= permutation && twoLevels && tiles && Math.Abs(mean - expected) < 1e-9;
			detail.Add($"{bits}: mean {mean:0.####} for {blue16 / 256.0:0.####}");
		}
		var plain = new int[32 * 32];
		var plainPlane = new Half[32 * 32];
		Array.Fill(plainPlane, dark);
		new ScreenRenderer(32, 32, 32, 32).Render(plainPlane, lut, SampleKind.IntensityRoot, 1f, 0, plain);
		Check(dithers && plain.All(p => p == lut.HalfTable(SampleKind.IntensityRoot, 1f)[BitConverter.HalfToUInt16Bits(dark)]),
			"Bayer dither at 10, 12, 14 and 16 bits: a permutation of the matrix's thresholds, only the two neighbouring levels, one tile repeated, its mean the 16-bit colour to the depth's bits; off rounds (" + string.Join(", ", detail) + ")");
		bool refused = false;
		try
		{
			new ScreenRenderer(2, 2, 2, 2).Render(new Half[4], lut, SampleKind.Field, 1f, 11, new int[4]);
		}
		catch (ArgumentOutOfRangeException)
		{
			refused = true;
		}
		Check(refused, "a dithering depth other than 0, 10, 12, 14 or 16 is refused");
	}

	private static void CheckColorSchemes()
	{
		Console.WriteLine("--- colour schemes");
		string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "WaveDudeNext", "ColorSchemes"));
		var errors = new List<string>();
		List<ColorScheme> schemes = ColorSchemeStore.LoadBuiltIn(errors);
		foreach (string error in errors)
		{
			Console.WriteLine("  " + error);
		}
		Check(errors.Count == 0, "all built-in schemes load from the assembly (" + schemes.Count + ")");
		Check(schemes.Count >= 20 && schemes.All(s => s.IsBuiltIn && s.FilePath == ""), "at least 20 schemes embedded, marked built-in");
		List<ColorScheme> fromFiles = ColorSchemeStore.LoadFolder(folder, null);
		Check(fromFiles.Count == schemes.Count && fromFiles.Select(s => s.Name).SequenceEqual(schemes.Select(s => s.Name)), "the embedded set equals the library's ColorSchemes folder");
		Check(fromFiles.Count > 0 && fromFiles.All(s => s.FilePath.EndsWith(ColorSchemeStore.XmlExtension, StringComparison.OrdinalIgnoreCase)), "the shipped schemes are XML (.wdscheme)");
		ColorScheme? field = ColorSchemeStore.Find(schemes, Display.DefaultFieldColorScheme);
		ColorScheme? intensity = ColorSchemeStore.Find(schemes, Display.DefaultIntensityColorScheme);
		Check(field != null && intensity != null, "default field and intensity schemes found (case-insensitive)");
		Check(field != null && field.IsStopScheme, "'Default field' is a 9-stop scheme (its table consolidated into the stops)");
		Check(intensity != null && intensity.Kind == ColorSchemeKind.Intensity, "'Default intensity' is typed intensity");
		Check(schemes.All(s => !s.Name.Contains('_') && char.IsUpper(s.Name[0])) && schemes.Select(s => s.Name).SequenceEqual(fromFiles.Select(s => Path.GetFileNameWithoutExtension(s.FilePath))),
			"the shipped names have no underscores, start with a capital, and name their files");
		Check(ReferenceEquals(ColorSchemeStore.Find(schemes, "Default_intensity"), intensity) && ReferenceEquals(ColorSchemeStore.Find(schemes, "Default_Field"), field)
			&& ColorSchemeStore.Find(schemes, "Flare_inv")?.Name == "Flare inverted" && ColorSchemeStore.Find(schemes, "Blue_to_yellow_GNU")?.Name == "Blue to yellow"
			&& ColorSchemeStore.Find(schemes, "Gray_scales_inv")?.Name == "Gray scales inverted" && ColorSchemeStore.Find(schemes, "Turky_brown")?.Name == "Turkey brown"
			&& ColorSchemeStore.Find(schemes, "inferno")?.Name == "Inferno" && ColorSchemeStore.Find(schemes, "No_such_scheme") == null,
			"the old underscore names (the author's files, projects from before) still find the renamed schemes");
		Check(schemes.Count(s => s.IsStopScheme && s.IsEvenlySpaced) == 11 && schemes.All(s => s.Stops.Count <= 24), "11 shipped schemes have the author's 9 evenly spaced stops, the 12 sampled colormaps are slimmed to at most 24");
		// simplification: a ramp with one bend, sampled at 255 points, keeps its two ends and the bend only
		var ramp = new ColorScheme("ramp", Enumerable.Range(0, 255).Select(i => i <= 127 ? new Rgb((byte)(i * 2), 0, 0) : new Rgb(254, (byte)((i - 127) * 2), 0)));
		int sampled = ramp.Stops.Count;
		ramp.Simplify(1.0);
		Check(sampled == 255 && ramp.Stops.Count == 3 && Math.Abs(ramp.Stops[1].Position - 127 / 254.0) < 1e-9 && ramp.ColorAt(127 / 254.0) == new Rgb(254, 0, 0) && ramp.ColorAt(1.0) == new Rgb(254, 254, 0),
			"Simplify keeps only the bend of a two-segment ramp and the gradient still hits it");
		Check(Rgb.Parse("#FF8000") == new Rgb(255, 128, 0) && Rgb.Parse("[10, 20, 30]") == new Rgb(10, 20, 30) && new Rgb(1, 2, 3).ToString() == "#010203", "Rgb parses hex and triplets and prints #RRGGBB");
		if (field != null)
		{
			// the author's JSON layout still comes out of a 9-stop scheme (what his program reads)
			string temp = Path.Combine(Path.GetTempPath(), "library_check-scheme.json");
			ColorSchemeStore.Save(field.Clone(field.Name), temp);
			string json = File.ReadAllText(temp);
			File.Delete(temp);
			Check(json.Contains("\"stops\": [[253,253,253]") && json.Contains("\"scheme_type\": \"field\""), "JSON export writes the author's stops and type");
			// the program's own format: the shipped XML round-trips byte for byte, standalone and inside a project
			string xmlPath = Path.Combine(Path.GetTempPath(), "library_check-scheme" + ColorSchemeStore.XmlExtension);
			ColorSchemeStore.SaveXml(field.Clone(field.Name), xmlPath);
			string xml = File.ReadAllText(xmlPath);
			ColorScheme back = ColorSchemeStore.LoadXml(xmlPath);
			File.Delete(xmlPath);
			string shipped = File.ReadAllText(Path.Combine(folder, Display.DefaultFieldColorScheme + ColorSchemeStore.XmlExtension));
			Check(xml.ReplaceLineEndings() == shipped.ReplaceLineEndings(), "SaveXml reproduces the shipped .wdscheme file");
			Check(xml.Contains("<Stop At=\"0.125\" Color=\"#") && !xml.Contains("xmlns"), "XML writes one Stop element per stop with its position and #RRGGBB, no namespaces");
			Check(back.Name == field.Name && back.Kind == field.Kind && back.Stops.Select(s => (s.Position, s.Color)).SequenceEqual(field.Stops.Select(s => (s.Position, s.Color))), "XML round trip keeps name, kind and the 9 stops with positions");
			var uneven = new ColorScheme("uneven", [new Rgb(0, 0, 0)]);
			uneven.SetStops([new ColorStop(0.0, new Rgb(0, 0, 0)), new ColorStop(0.25, new Rgb(255, 255, 255)), new ColorStop(1.0, new Rgb(0, 0, 255))]);
			Check(uneven.ColorAt(0.0) == new Rgb(0, 0, 0) && uneven.ColorAt(127 / 254.0) == new Rgb(170, 170, 255) && uneven.ColorAt(1.0) == new Rgb(0, 0, 255) && uneven.ColorAt(32 / 254.0) == new Rgb(129, 129, 129)
				&& ColorScheme.Interpolate(uneven.Stops, 255)[32] == new Rgb(129, 129, 129) && uneven.ColorAt(0.25) == new Rgb(255, 255, 255) && uneven.ColorAt(double.NaN) == new Rgb(0, 0, 0),
				"stops at any position interpolate between their neighbours (0, 0.25, 1), a stop's own position gives its colour, the 255-entry sampling the same");
			ColorStop added = uneven.AddStop();
			Check(uneven.Stops.Count == 4 && Math.Abs(added.Position - 0.625) < 1e-9 && uneven.Stops.IndexOf(added) == 2, "AddStop fills the widest gap, in position order");
			bool hsvRoundTrip = true;
			for (int r = 0; r < 256; r += 5)
			{
				for (int g = 0; g < 256; g += 5)
				{
					for (int b = 0; b < 256; b += 5)
					{
						var c = new Rgb((byte)r, (byte)g, (byte)b);
						(double h, double s, double v) = c.ToHsv();
						hsvRoundTrip &= Rgb.FromHsv(h, s, v) == c;
					}
				}
			}
			Check(hsvRoundTrip && Rgb.FromHsv(0, 1, 1) == new Rgb(255, 0, 0) && Rgb.FromHsv(120, 1, 1) == new Rgb(0, 255, 0) && Rgb.FromHsv(240, 1, 0.5) == new Rgb(0, 0, 128)
				&& new Rgb(64, 128, 192).ToHsv().Hue == 210 && Rgb.FromHsv(210, 0.5667, 0.7529) == new Rgb(83, 138, 192) && Rgb.FromHsv(-150, 2, 1) == Rgb.FromHsv(210, 1, 1),
				"HSV: every sampled colour round-trips, the primaries, #4080C0's hue, a lowered saturation and the wrapping come out right");
			var project = new Project();
			project.ColorSchemes.Add(field.Clone("Mine"));
			project.Scene.ImageData = [1, 2, 3, 4, 250, 0, 255];
			project.Scene.Image = "scene.png";
			project.Scene.Mode = SceneMode.Image;
			using var stream = new MemoryStream();
			project.Save(stream);
			stream.Position = 0;
			Project loaded = Project.Load(stream);
			Check(loaded.ColorSchemes.Count == 1 && loaded.ColorSchemes[0].Name == "Mine" && loaded.ColorSchemes[0].Colors.SequenceEqual(field.Colors), "a project file embeds its own schemes");
			// a renamed project scheme takes the layers that refer to it along (2026-10-15), in a fresh and in a loaded project
			project.Display.Field.ColorScheme = "mine";
			project.Display.Intensity.ColorScheme = "Other";
			project.ColorSchemes[0].Name = "Yours";
			loaded.Display.Field.ColorScheme = "Mine";
			loaded.ColorSchemes[0].Name = "Theirs";
			ColorScheme later = project.AddColorScheme();
			project.Display.Intensity.ColorScheme = later.Name;
			later.Name = "Later";
			ColorScheme gone = project.AddColorScheme();
			project.Remove(gone);
			gone.Name = "Yours";
			Check(project.Display.Field.ColorScheme == "Yours" && project.Display.Intensity.ColorScheme == "Later" && loaded.Display.Field.ColorScheme == "Theirs" && loaded.Display.Intensity.ColorScheme == Display.DefaultIntensityColorScheme,
				"a renamed project scheme takes the layers naming it along (case-insensitively; a scheme added later too, a removed one no more; a loaded project as well)");
			Check(loaded.Scene.ImageData != null && loaded.Scene.ImageData.SequenceEqual(project.Scene.ImageData) && loaded.Scene.Image == "scene.png", "a project file embeds the scene image bytes (base64)");
			// the image's source path (2026-10-15): relative inside the project folder, absolute elsewhere, resolved back, not a simulation change
			string projectFolder = Path.Combine(Path.GetTempPath(), "wd-project");
			Check(Scene.StoredImagePath(projectFolder, Path.Combine(projectFolder, "scenes", "a.png")) == Path.Combine("scenes", "a.png") && Scene.StoredImagePath(projectFolder, Path.Combine(Path.GetTempPath(), "b.png")) == Path.Combine(Path.GetTempPath(), "b.png") && Scene.StoredImagePath(null, Path.Combine(projectFolder, "a.png")) == Path.Combine(projectFolder, "a.png"),
				"StoredImagePath: relative inside the project folder, absolute outside or without a folder");
			project.Scene.ImagePath = Path.Combine("scenes", "a.png");
			Check(project.Scene.ResolveImagePath(projectFolder) == Path.Combine(projectFolder, "scenes", "a.png") && project.Scene.ResolveImagePath(null) == "" && new Scene { ImagePath = Path.Combine(projectFolder, "a.png") }.ResolveImagePath(null) == Path.Combine(projectFolder, "a.png"),
				"ResolveImagePath: relative needs the folder, absolute does not");
			using var stream2 = new MemoryStream();
			project.Save(stream2);
			stream2.Position = 0;
			Check(Project.Load(stream2).Scene.ImagePath == Path.Combine("scenes", "a.png"), "the image path round-trips through the file");
			Check(!new ProjectChange(project.Scene, nameof(Scene.ImagePath)).AffectsSimulation && new ProjectChange(project.Scene, nameof(Scene.ImageData)).AffectsSimulation, "the path is no simulation change, the bytes are");
			var abs = new Scene { ImagePath = Path.Combine(projectFolder, "a.png") };
			abs.RelativizeImagePath(projectFolder);
			Check(abs.ImagePath == "a.png", "RelativizeImagePath on save");
			var pictured = new Scene { Mode = SceneMode.Image, Image = "a.png", ImagePath = "a.png", ImageData = [1, 2, 3] };
			pictured.RemoveImage();
			Check(pictured.Mode == SceneMode.Canvas && pictured.Image.Length == 0 && pictured.ImagePath.Length == 0 && pictured.ImageData == null && pictured.IsDefault,
				"RemoveImage drops the name, the path and the bytes and leaves a blank canvas, the default scene");
			var edited = loaded.ColorSchemes[0];
			int changes = 0;
			edited.PropertyChanged += (_, args) => changes += args.PropertyName == nameof(ColorScheme.Stops) ? 1 : 0;
			edited.Stops[4].Color = new Rgb(1, 2, 3);
			Check(changes == 1 && edited.ColorAt(0.5) == new Rgb(1, 2, 3), "editing one stop notifies once and the gradient follows");
		}
		// the author's glowing edges: a vertical black|white step at x = 2 of a 5 x 5 image
		var image = new byte[5 * 5 * 4];
		for (int y = 0; y < 5; y++)
		{
			for (int x = 2; x < 5; x++)
			{
				int o = (y * 5 + x) * 4;
				image[o] = image[o + 1] = image[o + 2] = 255;
				image[o + 3] = 255;
			}
		}
		byte[] edges = GlowingEdges.Compute(image, 5, 5, 20);
		int At(int x, int y) => edges[(y * 5 + x) * 4];
		Check(At(1, 1) == 255 && At(2, 2) == 255 && At(3, 2) == 0 && At(0, 2) == 0 && At(2, 0) == 0 && edges[(2 * 5 + 2) * 4 + 3] == 255,
			"glowing edges: the two columns beside the step light up (Sobel x 0.25 clamps at 255), the flat area and the outer ring stay black");
		int[] frame = new int[25];
		Array.Fill(frame, unchecked((int)0xFF404040));
		GlowingEdges.BlendAdditive(frame, 5, 5, edges, 0.5);
		Check(frame[2 * 5 + 2] == unchecked((int)0xFFBFBFBF) && frame[2 * 5 + 3] == unchecked((int)0xFF404040), "additive blend adds edge x gain per channel and leaves flat pixels alone");
	}

	private static void CheckProbeGraph()
	{
		Console.WriteLine("--- probe graph geometry");
		var inv = CultureInfo.InvariantCulture;
		var probe = new Probe { Name = "P", BoxX = 1700, BoxY = 900 };
		Check(ProbeGraph.NominalBoxSize(ProbeBoxSize.Medium) == new BoxSize(400, 200) && ProbeGraph.NominalBoxSize(ProbeBoxSize.Tiny) == new BoxSize(200, 100), "nominal box sizes");
		var turned = new Source { Name = "Turned", X = 100, Y = 100, Array = new SourceArray { SizeX = 3, SizeY = 1, SpacingX = 10, Angle = 90 } };
		turned.GenerateElements(1);
		Check(turned.Elements.Select(e => (e.X, e.Y)).SequenceEqual([(100, 110), (100, 100), (100, 90)]), "a 3 x 1 array turned by 90 degrees stands vertical, counter-clockwise on the screen");
		turned.Array.Angle = 45;
		turned.GenerateElements(1);
		Check(turned.Elements.Select(e => (e.X, e.Y)).SequenceEqual([(93, 107), (100, 100), (107, 93)]), "45 degrees: the ends at (-7, +7) and (+7, -7)");
		turned.Array.Angle = 540;
		turned.Array.Normalize();
		bool wraps = turned.Array.Angle == 180;
		turned.Array.Angle = -180;
		turned.Array.Normalize();
		wraps &= turned.Array.Angle == 180;
		turned.Array.Angle = -190;
		turned.Array.Normalize();
		wraps &= turned.Array.Angle == 170;
		turned.Array.Angle = 190;
		turned.Array.Normalize();
		Check(wraps && turned.Array.Angle == -170, "an angle is normalised into (-180, 180]: 540 -> 180, -180 -> 180, -190 -> 170, 190 -> -170");
		var follower = new Probe { Name = "F" };
		follower.AX = 50;
		follower.AY = 60;
		Check(follower.IsBoxAttached && follower.BoxX == 50 && follower.BoxY == 60, "an attached graph box moves with the probe");
		follower.BoxX = 10;
		follower.AX = 70;
		follower.AY = 61;
		Check(!follower.IsBoxAttached && follower.BoxX == 10 && follower.BoxY == 60, "a box moved away is detached on both axes and stays put");
		follower.BoxX = 70;
		follower.BoxY = 61;
		follower.AX = 75;
		Check(follower.IsBoxAttached && follower.BoxX == 75 && follower.BoxY == 61, "a box put back on the probe is attached again");
		Check(ProbeGraph.BoxInCanvas(probe, 1920, 1080) == new BoxRect(1520, 880, 400, 200), "box clamped into a full-HD canvas");
		Check(ProbeGraph.BoxInCanvas(probe, 64, 48) == new BoxRect(0, 16, 64, 32), "box shrunk to the width of a 64 x 48 canvas, aspect kept");
		probe.BoxSize = ProbeBoxSize.Custom;
		probe.BoxWidth = 500;
		probe.BoxHeight = 100;
		Check(ProbeGraph.BoxSizeOf(probe) == new BoxSize(500, 100) && ProbeGraph.BoxInCanvas(probe, 1920, 1080) == new BoxRect(1420, 900, 500, 100), "custom box size used as it is");
		probe.BoxWidth = 1200;
		probe.BoxHeight = 300;
		Check(ProbeGraph.BoxInCanvas(probe, 1920, 1080) == new BoxRect(720, 780, 1200, 300), "a custom box wider than half the canvas is drawn at its size (it was held to half the width until 2026-10-18)");
		probe.BoxWidth = 3000;
		probe.BoxHeight = 600;
		Check(ProbeGraph.BoxInCanvas(probe, 1920, 1080) == new BoxRect(0, 696, 1920, 384), "a custom box wider than the canvas is shrunk to its width, aspect kept");
		Check(ProbeGraph.Snap(405, 195) == (ProbeBoxSize.Medium, new BoxSize(400, 200)) && ProbeGraph.Snap(690, 360) == (ProbeBoxSize.ExtraLarge, new BoxSize(700, 350)), "a dragged size near a preset snaps to it");
		Check(ProbeGraph.Snap(450, 230) == (ProbeBoxSize.Custom, new BoxSize(450, 230)) && ProbeGraph.Snap(10, 10) == (ProbeBoxSize.Custom, new BoxSize(60, 30)), "other sizes stay custom, clamped to the minimum");
		Check(ProbeGraph.NiceTicks(-1, 1, 4).SequenceEqual([-1.0, -0.5, 0.0, 0.5, 1.0]) && ProbeGraph.NiceTicks(0, 2500, 5).SequenceEqual([0.0, 500.0, 1000.0, 1500.0, 2000.0, 2500.0]), "nice ticks at 1/2/5 steps, zero exact");
		Check(ProbeGraph.NiceTicks(0.3, 0.3, 4).Count == 0 && ProbeGraph.NiceTicks(double.NaN, 1, 4).Count == 0, "no ticks for an empty or NaN range");
		Check(ProbeGraph.FormatTick(0.123456, inv) == "0.123" && ProbeGraph.FormatTick(0, inv) == "0" && ProbeGraph.FormatTick(0.5, new CultureInfo("de-DE")) == "0,5", "tick labels: 3 significant digits, locale decimal separator");
		Check(ProbeGraph.FormatPosition(1234.6, 2500, inv) == "1235" && ProbeGraph.FormatPosition(2.25, 5, inv) == "2.3", "position labels: integers over wide spans, one decimal otherwise");

		// a point probe's series: 120 samples, a sine between -0.5 and 0.5
		float[] values = new float[1024];
		for (int i = 0; i < 120; i++)
		{
			values[i] = 0.5f * MathF.Sin(i * 0.2f);
		}
		var point = new Probe { Name = "Point", Window = 500 };
		var data = new ProbeGraphData(values, 120, null, 0, 120);
		ProbeGraph.ComputeRanges(point, data, 2500, out double xMin, out double xMax, out double yMin, out double yMax);
		Check(xMin == 0 && xMax == 120, "running window, auto x: 0..samples shown (" + xMin + ".." + xMax + ")");
		Check(Math.Abs(yMin + 0.54) < 0.001 && Math.Abs(yMax - 0.54) < 0.001, "auto y: symmetric about zero, largest magnitude plus 8 % (" + yMin.ToString("0.000", inv) + ".." + yMax.ToString("0.000", inv) + ")");
		float[] lopsided = new float[16];
		lopsided[3] = -0.2f;
		lopsided[7] = 0.5f;
		ProbeGraph.ComputeRanges(point, new ProbeGraphData(lopsided, 16, null, 0, 16), 2500, out _, out _, out double lopMin, out double lopMax);
		Check(Math.Abs(lopMin + 0.54) < 0.001 && Math.Abs(lopMax - 0.54) < 0.001, "lopsided data still gets a symmetric axis (" + lopMin.ToString("0.00", inv) + ".." + lopMax.ToString("0.00", inv) + ")");
		ProbeGraph.ComputeRanges(point, new ProbeGraphData(new float[16], 16, null, 0, 16), 2500, out _, out _, out double zeroMin, out double zeroMax);
		Check(zeroMin == -1 && zeroMax == 1, "all-zero data keeps the fixed range instead of a 1e-6 fit");
		var intensityProbe = new Probe { Name = "I", Layer = ProbeLayer.Intensity, Measurement = ProbeMeasurement.Value };
		float[] positive = new float[16];
		positive[5] = 0.3f;
		ProbeGraph.ComputeRanges(intensityProbe, new ProbeGraphData(positive, 16, null, 0, 16), 2500, out _, out _, out double intMin, out double intMax);
		Check(intMin == 0 && Math.Abs(intMax - 0.324) < 0.001, "an intensity axis runs from zero to the largest value plus 8 %");
		point.AutoScaleX = false;
		ProbeGraph.ComputeRanges(point, data, 2500, out xMin, out xMax, out _, out _);
		Check(xMin == 0 && xMax == 500, "running window, fixed x: full window from the start");
		point.ScaleMode = ProbeScaleMode.Full;
		ProbeGraph.ComputeRanges(point, data, 2500, out xMin, out xMax, out _, out _);
		Check(xMin == 0 && xMax == 2500, "full scale, fixed x: the whole run");
		point.StartAfter = 100;
		point.AutoScaleX = true;
		ProbeGraph.ComputeRanges(point, data, 2500, out xMin, out xMax, out _, out _);
		Check(xMin == 100 && xMax == 120, "start after 100: x starts there");
		point.AutoScaleY = false;
		point.YMin = 2;
		point.YMax = 1;
		ProbeGraph.ComputeRanges(point, data, 2500, out _, out _, out yMin, out yMax);
		Check(yMin == -1 && yMax == 1, "inverted fixed y range falls back to -1..1");
		point.StartAfter = 0;
		point.AutoScaleY = true;
		point.ScaleMode = ProbeScaleMode.Running;
		// the text keeps the renderer's size (11 px, 14.3 px lines, 6.6 px per character here); the margins come from the labels
		ProbeGraphStyle style = ProbeGraphStyle.Estimate(11);
		ProbeGraphLayout layout = ProbeGraph.Layout(point, data, 2500, 400, 200, style, inv);
		float widest = layout.YTicks.Max(t => style.Measure(t.Label));
		Check(layout.IsVisible && layout.HasPlot && layout.ShowLabels && layout.FontSize == 11 && Math.Abs(layout.PlotX - (3 + widest + 4)) < 0.01 && Math.Abs(layout.PlotY - (14.3 + 6)) < 0.01
			&& Math.Abs(layout.PlotWidth - (400 - layout.PlotX - 8)) < 0.01 && Math.Abs(layout.PlotHeight - (200 - layout.PlotY - 14.3 - 4)) < 0.01,
			"400 x 200 layout: font 11, plot at (" + layout.PlotX.ToString("0.0", inv) + ", " + layout.PlotY.ToString("0.0", inv) + ") " + layout.PlotWidth.ToString("0.0", inv) + " x " + layout.PlotHeight.ToString("0.0", inv) + " from the measured labels");
		Check(layout.Curve.Length == 120 && layout.Curve[0].X > layout.PlotX && layout.Curve[^1].X <= layout.PlotX + layout.PlotWidth + 0.01 && layout.Title == "Point" && layout.Readout == "= " + ProbeGraph.FormatTick(values[119], inv),
			"one point per sample inside the plot, title and readout");
		Check(layout.YTicks.Any(t => t.IsZero && Math.Abs(t.Position - (layout.PlotY + layout.PlotHeight / 2)) < 2) && layout.XTicks.Count >= 4 && layout.XTicks[0].Label == "0", "zero line in the middle, step ticks from 0");
		Check(ProbeGraph.Layout(point, data, 2500, 30, 20, style, inv).IsVisible == false && ProbeGraph.Layout(point, data, 2500, 80, 40, style, inv) is { IsVisible: true, HasPlot: false }, "too small: nothing, or the box alone");
		ProbeGraphLayout bare = ProbeGraph.Layout(point, data, 2500, 120, 45, style, inv);
		Check(bare is { HasPlot: true, ShowLabels: false } && bare.YTicks.All(t => t.Label == "") && bare.PlotX == 6 && Math.Abs(bare.PlotHeight - (45 - 20.3 - 6)) < 0.01, "a box too low for labels keeps the plot and drops the labels");
		// the UI harness's 172 x 86 box at frame 49: three value labels 23 px apart fit a 47 px plot (asking for two ticks gave only the zero)
		float[] lowValues = new float[1024];
		for (int i = 40; i < 99; i++)
		{
			lowValues[i] = (i % 7 == 3 ? 0.0135f : 0.0126f) * MathF.Sin((i - 40) * 0.5f);
		}
		ProbeGraphLayout low = ProbeGraph.Layout(point, new ProbeGraphData(lowValues, 99, null, 0, 99), 100, 172, 86, new ProbeGraphStyle(11, 14.65f, t => t.Length * 6.2f), inv);
		Check(low.ShowLabels && low.YTicks.Count == 3 && low.YTicks.All(t => t.Label.Length > 0), "a low plot keeps three value labels while they are a line apart (" + string.Join(" ", low.YTicks.Select(t => t.Label)) + ")");
		ProbeGraphLayout narrow = ProbeGraph.Layout(point, data, 2500, 110, 80, style, inv);
		Check(narrow.ShowLabels && narrow.XTicks.Count <= 3 && narrow.XTicks.Sum(t => style.Measure(t.Label) + 8) - 8 <= narrow.PlotWidth, "a narrow plot gets fewer step labels so they do not collide (" + narrow.XTicks.Count + ")");
		var dense = new ProbeGraphData(Enumerable.Range(0, 1024).Select(i => (float)Math.Sin(i)).ToArray(), 1024, null, 0, 1024);
		point.ScaleMode = ProbeScaleMode.Full;
		ProbeGraphLayout denseLayout = ProbeGraph.Layout(point, dense, 1024, 200, 100, style, inv);
		Check(denseLayout.Curve.Length <= 2 * (int)denseLayout.PlotWidth && denseLayout.Curve.Length > 100, "dense series binned to two points per pixel column (" + denseLayout.Curve.Length + ")");

		// a line probe: 50 values along a 49-pixel line, gated by "start after step"
		float[] profile = Enumerable.Range(0, 50).Select(i => 0.1f * i).ToArray();
		var line = new Probe { Name = "Line", Kind = ProbeKind.Line, StartAfter = 100, Layer = ProbeLayer.Intensity };
		ProbeGraphLayout before = ProbeGraph.Layout(line, new ProbeGraphData(null, 0, profile, 49, 50), 2500, 400, 200, style, inv);
		ProbeGraphLayout after = ProbeGraph.Layout(line, new ProbeGraphData(null, 0, profile, 49, 100), 2500, 400, 200, style, inv);
		Check(before.Curve.Length == 0 && before.Readout == "" && before.XTicks[^1].Label == "1", "line probe blank before its start step");
		Check(after.Curve.Length == 50 && after.Readout == "max 4.9" && after.XTicks[^1].Label == "40" && after.YTicks[0].Label == "0", "line probe after its start step: profile over 0..49 pixels, intensity fitted from 0");

		// a logarithmic value axis: intensity only, the decades below a fitted top, zeros on the bottom line
		var logProbe = new Probe { Name = "Log", Layer = ProbeLayer.Intensity, YScale = ProbeValueScale.Logarithmic };
		float[] spread = new float[16];
		spread[2] = 1e-5f;
		spread[9] = 0.01f;
		ProbeGraph.ComputeRanges(logProbe, new ProbeGraphData(spread, 16, null, 0, 16), 2500, out _, out _, out double logMin, out double logMax);
		Check(logProbe.IsLogarithmic && Math.Abs(Math.Log10(logMax) - (-2 + 0.48)) < 1e-6 && Math.Abs(Math.Log10(logMax / logMin) - 6) < 1e-9,
			"log axis: six decades below the largest value raised by 8 % of the axis (" + logMin.ToString("G3", inv) + ".." + logMax.ToString("G3", inv) + ")");
		ProbeGraph.ComputeRanges(logProbe, new ProbeGraphData(new float[16], 16, null, 0, 16), 2500, out _, out _, out double logZeroMin, out double logZeroMax);
		Check(logZeroMax == 1 && Math.Abs(logZeroMin - 1e-6) < 1e-18, "log axis on all-zero data: the fixed top (1 for -1..1), six decades below it");
		logProbe.AutoScaleY = false;
		logProbe.YMin = 1e-4;
		logProbe.YMax = 10;
		ProbeGraph.ComputeRanges(logProbe, default, 2500, out _, out _, out double logFixedMin, out double logFixedMax);
		Check(logFixedMin == 1e-4 && logFixedMax == 10, "log axis, fixed: a positive minimum is the bottom");
		logProbe.AutoScaleY = true;
		ProbeGraphLayout logLayout = ProbeGraph.Layout(logProbe, new ProbeGraphData(spread, 16, null, 0, 16), 2500, 400, 200, style, inv);
		float bottom = logLayout.PlotY + logLayout.PlotHeight;
		Check(logLayout.YTicks.All(t => !t.IsZero) && string.Join(" ", logLayout.YTicks.Select(t => t.Label)) == "1e-6 1e-4 0.01",
			"log axis ticks: every second power of ten over 1e-7.5..1e-1.5 (" + string.Join(" ", logLayout.YTicks.Select(t => t.Label)) + ")");
		Check(logLayout.Curve.Length == 16 && logLayout.Curve.Count(p => Math.Abs(p.Y - bottom) < 0.01) == 14 && logLayout.Curve[9].Y < logLayout.Curve[2].Y && logLayout.Curve[9].Y > logLayout.PlotY,
			"log axis: zeros on the bottom line, 0.01 above 1e-5, both inside the plot");
		Check(ProbeGraph.LogTicks(1e-6, 1, 4) is [1e-6, 1e-4, 1e-2, 1] && ProbeGraph.LogTicks(1e-12, 1, 4) is [1e-12, 1e-9, 1e-6, 1e-3, 1] && ProbeGraph.LogTicks(2, 8, 4).All(t => t is > 0 and <= 8),
			"log ticks: every second or third decade, aligned; nice ticks within a decade");
		Check(ProbeGraph.FormatLogTick(1e-6, inv) == "1e-6" && ProbeGraph.FormatLogTick(0.01, inv) == "0.01" && ProbeGraph.FormatLogTick(1e3, inv) == "1e3" && ProbeGraph.FormatLogTick(5, inv) == "5", "log tick labels: plain from 0.01 to 100, 1e-n beyond");
		logProbe.Layer = ProbeLayer.Field;
		var normalised = new Probe { Layer = ProbeLayer.Intensity, YScale = ProbeValueScale.Logarithmic, Decades = 40 };
		normalised.Normalize();
		Check(logProbe is { YScale: ProbeValueScale.Linear, IsLogarithmic: false } && normalised.Decades == 12 && normalised.IsLogarithmic, "a field probe's axis is linear; the decades are clamped to 12");
	}

	/// <summary>
	/// The repository root, found by walking up from the harness binary to the folder holding <c>_tools/legacy_examples</c>,
	/// the Wave Dude 2D projects the import checks read (2026-10-31: the root folder of Jeroen's original examples is gone,
	/// the four the checks need moved there); null when not found.
	/// </summary>
	private static string? FindRepoRoot()
	{
		for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
		{
			if (Directory.Exists(Path.Combine(dir.FullName, "_tools", "legacy_examples")))
			{
				return dir.FullName;
			}
		}
		return null;
	}

	/// <summary>The path of a Wave Dude 2D project among the import fixtures.</summary>
	private static string LegacyExample(string root, string folder) => Path.Combine(root, "_tools", "legacy_examples", folder, "configuration.json");

	private static void CheckImport()
	{
		Console.WriteLine("--- legacy project import");
		string? root = FindRepoRoot();
		if (root == null)
		{
			Console.WriteLine("  skip import: the _tools/legacy_examples folder was not found above the harness");
			return;
		}
		var notes = new List<string>();
		Project p = LegacyProject.Import(LegacyExample(root, "Example12_Dispersion"), notes, out string name);
		Check(name == "Example12_Dispersion" && p.Description.StartsWith("This example shows the difference", StringComparison.Ordinal) && notes.Count == 0, "Example 12: name and description, no notes");
		Check(p.Scene.Mode == SceneMode.Image && p.Scene.Image == "frequency_race_track (2).png" && p.Scene.ImageData is { Length: 58074 } && p.Scene.Width == 1920 && p.Scene.Height == 1080, "scene: image mode, the image embedded (58074 bytes) under its file name, 1920 x 1080");
		Check(Path.IsPathRooted(p.Scene.ImagePath) && p.Scene.ImagePath.EndsWith(Path.Combine("scene_images", "frequency_race_track (2).png"), StringComparison.OrdinalIgnoreCase), "the import keeps the image's source path (absolute) for the watch");
		Check(p.Scene.DampingEdge == 64 && !p.Scene.ImageSources && p.Scene.ObjectSources && p.Scene.RefractiveIndex == 1.0 && p.Scene.ImageSourceScale == 1.0, "scene: damping 64, object sources on, image sources off");
		Check(p.Simulation.Steps == 15000 && p.Simulation.VisualizeEvery == 2 && !p.Simulation.FourthOrderLaplacian && p.Simulation.WaveSpeed == 1.0 && p.Simulation.TimeDecay == 10.0 && p.Simulation.SourceFrequencyScale == 2.0 && p.Simulation.IntensityScale == 1.0, "simulation: 15000 steps every 2, 9-point stencil, c 1, decay 10");
		Check(p.Display.Field.Brightness == 80 && p.Display.Intensity.Brightness == 30 && p.Display.Field.BlackLevel == -0.05 && p.Display.Intensity.BlackLevel == 0 && p.Display.Field.ColorScheme == "Default_Field" && p.Display.Intensity.ColorScheme == "Default_intensity", "display: brightness 80 / 30, black levels, schemes");
		Check(p.Display.Field.Preview && p.Display.Intensity.Preview && p.Display.ProbeGraphsInExport && p.Display.Video.Fps == 60 && p.Display.Video.Quality == 70, "display: previews on, probe graphs in the export, 60 fps at the file's quality 70");
		Check(p.Sources.Count == 4 && p.Sources.Select(s => s.Name).SequenceEqual(["Source 1", "Source 2", "Source 3", "Source 4"]) && p.Sources.Select(s => s.Frequency).SequenceEqual([40, 100, 150, 240]) && p.Sources.Select(s => s.Amplitude).SequenceEqual([70, 80, 90, 100]), "4 single sources with their names, levels and amplitudes");
		Check(p.Sources.All(s => s.Enabled && s.Array == null && s.DetachAfter == Source.NeverDetach && s.Mode == SourceMode.Continuous && s.Elements.Count == 1) && p.Sources[0].X == 150 && p.Sources[0].Y == 140 && p.Sources[3].Y == 924, "sources enabled, single, never detaching, placed, with one emitter each");
		Check(p.Probes.Count == 2 && p.Probes.All(pr => !pr.Active && pr.Layer == ProbeLayer.Field && pr.Measurement == ProbeMeasurement.Amplitude && pr.BoxSize == ProbeBoxSize.Medium && pr.Window == 500 && pr.StartAfter == 0), "2 inactive field probes, medium boxes, window 500, no start step");
		Check(p.Probes[0].Kind == ProbeKind.Line && p.Probes[0].AX == 1713 && p.Probes[0].AY == 2 && p.Probes[0].BX == 1719 && p.Probes[0].BY == 1073 && p.Probes[0].BoxX == 1 && p.Probes[0].BoxY == 426 && p.Probes[0].Name == "Probe 1", "probe 1: a line from (1713, 2) to (1719, 1073), box at (1, 426)");
		Check(p.Probes[1].Kind == ProbeKind.Point && p.Probes[1].AX == 100 && p.Probes[1].BoxX == 450 && p.Probes[1].BoxY == 20 && p.Probes[1].Name == "Probe 2", "probe 2: the author's default point probe");
		using (var stream = new MemoryStream())
		{
			p.Save(stream);
			stream.Position = 0;
			Project back = Project.Load(stream);
			Check(back.Sources.Count == 4 && back.Probes.Count == 2 && back.Scene.ImageData!.SequenceEqual(p.Scene.ImageData!) && back.Simulation.Steps == 15000 && !back.Simulation.FourthOrderLaplacian, "the imported project round-trips through the XML format");
		}
		Check(LegacyProject.SuggestedFileName(LegacyExample(root, "Example12_Dispersion"), name) == Path.Combine(root, "_tools", "legacy_examples", "Example12_Dispersion", "Example12_Dispersion.wdproj"), "Save As proposes <name>.wdproj beside the original");

		// a composite, randomised, detaching, pulsed source; a missing image in canvas mode; a global probe switch
		notes.Clear();
		p = LegacyProject.Import(LegacyExample(root, "Example5_Small_incoherent_source"), notes, out _);
		Check(p.Scene.Mode == SceneMode.Canvas && p.Scene.ImageData == null && p.Scene.Image == "scene_images/spherrical_mirror_small_central_aperture.png" && notes.Any(n => n.Contains("not found")), "Example 5: canvas mode, the missing image's path kept and noted");
		Source composite = p.Sources[0];
		Check(composite.Array is { SizeX: 5, SizeY: 5, SpacingX: 3, SpacingY: 3, RandomFrequency: 50, RandomPhase: 90, RandomOffWidth: 19, RandomPosition: 1 } && composite.Elements.Count == 25, "a 5 x 5 array with its random amounts, 25 emitters regenerated");
		Check(composite.Mode == SourceMode.Pulsed && composite.PulseWidth == 5 && composite.OffWidth == 20 && composite.DetachAfter == 2000 && composite.Enabled && notes.Any(n => n.Contains("random offsets")), "pulsed 5 / 20 cycles, detaching after step 2000, the regeneration noted");
		Check(p.Simulation.WaveSpeed == 0.563 && p.Display.Field.ColorScheme == "Orange_symmetry" && p.Display.Intensity.ColorScheme == "Purple_rain" && p.Display.Field.Brightness == 50, "c 0.563 and the chosen schemes");
		Check(p.Probes[0] is { Active: true, Kind: ProbeKind.Point, AutoScaleY: false, YMin: -0.02, YMax: 0.02, BoxSize: ProbeBoxSize.ExtraLarge, BoxX: 14, BoxY: 14 } && p.Probes[1] is { Layer: ProbeLayer.Intensity, Measurement: ProbeMeasurement.Value, Kind: ProbeKind.Line, StartAfter: 0, YMax: 0.0001 }, "probes: fixed value ranges, the unused start step dropped, use_probes honoured");

		// start-after steps in use, an intensity probe, a detaching single source, the fallback decay
		notes.Clear();
		p = LegacyProject.Import(LegacyExample(root, "Example3_Spherical_mirror"), notes, out _);
		Check(p.Probes[0] is { StartAfter: 600, Window: 3000, Layer: ProbeLayer.Intensity, Measurement: ProbeMeasurement.Value } && p.Probes[1] is { StartAfter: 600, Measurement: ProbeMeasurement.IntegratedValue, Kind: ProbeKind.Line }, "Example 3: start-after steps kept when their flag is on, intensity measurements");
		Check(p.Sources[0] is { Array: null, DetachAfter: 670, PulseWidth: 70, OffWidth: 100, Frequency: 200 } && p.Simulation.TimeDecay == 8 && p.Display.Intensity.ColorScheme == "Copper", "a single detaching source (its unused array settings ignored), decay 8, Copper");
		Check(p.Scene.Mode == SceneMode.Image && p.Scene.ImageData == null && notes.Count == 1, "image mode with the image missing: one note");
		// Example 11's only source is a composite lattice whose position was never picked (has_position false) and
		// which the author's program simulated at its default (100, 100) all the same: it must come in enabled, or
		// the run is black (2026-10-16)
		notes.Clear();
		p = LegacyProject.Import(LegacyExample(root, "Example11_Fluorescent_emitter"), notes, out _);
		Check(p.Sources.Count == 1 && p.Sources[0] is { Enabled: true, X: 100, Y: 100, Array: { SizeX: 41, SizeY: 21 } } && notes.Any(n => n.Contains("no picked position")),
			"Example 11: the lattice without a picked position comes in enabled at (100, 100), noted");

		bool refused = false;
		string newer = Path.Combine(Path.GetTempPath(), "library_check-newer.json");
		File.WriteAllText(newer, "{ \"format_version\": 2 }");
		try
		{
			LegacyProject.Import(newer, null, out _);
		}
		catch (InvalidDataException)
		{
			refused = true;
		}
		File.Delete(newer);
		Check(refused, "a newer format version is refused");
	}

	/// <summary>
	/// The painter's runs (2026-11-04: where a shape's depths are distances, one pixel centre settles the pixels after it
	/// along the row) against the pixel-by-pixel painting they replaced, kept here as <see cref="PaintPixelByPixel"/>: every
	/// example's parts, and every kind at several angles with coatings of both profiles, bare faces, mirrors, absorption,
	/// both fibre ends and a fibre bent sharply at its flat start face (the wedge the allowance covers), painted both ways
	/// onto a canvas whose red and blue vary and that holds mirror pixels, must come out byte for byte the same.
	/// </summary>
	private static void CheckPainterRuns()
	{
		var cases = new List<(string Name, int Width, int Height, IReadOnlyList<OpticalPart> Parts)>();
		string? root = FindRepoRoot();
		if (root != null)
		{
			foreach (string file in Directory.GetFiles(Path.Combine(root, "Examples"), "*.wdproj").Order())
			{
				Project example = Project.Load(file);
				if (example.Parts.Any(part => part.Enabled))
				{
					cases.Add((Path.GetFileNameWithoutExtension(file), Math.Max(64, example.Scene.Width), Math.Max(64, example.Scene.Height), example.Parts));
				}
			}
		}
		foreach (double angle in new[] { 0.0, 17.0, 90.0, -123.0 })
		{
			var project = new Project();
			project.Scene.Width = 520;
			project.Scene.Height = 360;
			var lens = project.AddPart<LensPart>();
			lens.X = 110; lens.Y = 90; lens.Angle = angle; lens.Diameter = 120; lens.Thickness = 30;
			lens.Coating = 5; lens.CoatingProfile = CoatingProfile.Graded; lens.BackCoated = false; lens.Absorption = 40;
			var mirror = project.AddPart<LensPart>();
			mirror.X = 260; mirror.Y = 90; mirror.Angle = angle; mirror.Diameter = 100; mirror.Thickness = 10; mirror.Mirror = true;
			var slab = project.AddPart<RectanglePart>();
			slab.X = 410; slab.Y = 90; slab.Angle = angle; slab.Length = 140; slab.Width = 50;
			slab.Coating = 4; slab.SidesCoated = false; slab.RefractiveIndex = 2.25; slab.Absorption = 120; slab.AbsorptionRamp = 12;
			var wall = project.AddPart<RectanglePart>();
			wall.X = 60; wall.Y = 230; wall.Angle = angle; wall.Length = 90; wall.Width = 6; wall.Mirror = true;
			var polygon = project.AddPart<PolygonPart>();
			polygon.X = 170; polygon.Y = 230; polygon.Angle = angle; polygon.Side = 40;
			polygon.Coating = 3; polygon.CoatingProfile = CoatingProfile.Layer; polygon.FrontCoated = false;
			var disc = project.AddPart<EllipsePart>();
			disc.X = 290; disc.Y = 230; disc.Angle = angle; disc.Length = 80; disc.Width = 50;
			disc.Absorption = 200; disc.AbsorptionRamp = 60; disc.Coating = 6; disc.CoatingProfile = CoatingProfile.Graded;
			var triangle = project.AddPart<TrianglePart>();
			triangle.X = 400; triangle.Y = 230; triangle.Angle = angle; triangle.Base = 70;
			var outline = project.AddPart<OutlinePart>();
			outline.X = 470; outline.Y = 300; outline.Angle = angle;
			foreach ((int px, int py) in new[] { (-40, -30), (30, -40), (45, 10), (0, 45), (-35, 20), (-10, 0) })
			{
				outline.Points.Add(new PartPoint { X = px, Y = py });
			}
			var fiber = project.AddPart<FiberPart>();
			fiber.X = 130; fiber.Y = 320; fiber.Angle = angle; fiber.CoreDiameter = 12; fiber.CladdingThickness = 8;
			fiber.Coating = 3; fiber.FrontCoated = false; fiber.Absorption = 90; fiber.AbsorptionRamp = 3; fiber.Ends = FiberEnds.Flat;
			fiber.Nodes[0].X = -100; fiber.Nodes[0].Y = 0; fiber.Nodes[0].SetHandle(true, 8, 40);
			fiber.Nodes[1].X = 100; fiber.Nodes[1].Y = 10; fiber.Nodes[1].SetHandle(false, -60, 30);
			var bare = project.AddPart<FiberPart>();
			bare.X = 380; bare.Y = 320; bare.Angle = angle; bare.CoreDiameter = 16; bare.CladdingThickness = 0;
			bare.Ends = FiberEnds.Round; bare.Coating = 4; bare.CoatingProfile = CoatingProfile.Graded;
			bare.Nodes[0].X = -70; bare.Nodes[1].X = 70; bare.Nodes[1].Y = -20;
			var bragg = project.AddPart<BraggReflectorPart>();
			bragg.X = 60; bragg.Y = 90; bragg.Angle = angle; bragg.Pairs = 4; bragg.Thickness1 = 5; bragg.Thickness2 = 7; bragg.Width = 60;
			var grin = project.AddPart<GrinLensPart>();
			grin.X = 190; grin.Y = 160; grin.Angle = angle; grin.Length = 60; grin.Diameter = 50;
			grin.Coating = 6; grin.CoatingProfile = CoatingProfile.Graded; grin.SidesCoated = false;
			var plate = project.AddPart<ZonePlatePart>();
			plate.X = 330; plate.Y = 160; plate.Angle = angle; plate.Diameter = 120; plate.Thickness = 20; plate.Coating = 6; plate.Mount = true; plate.MountWidth = 20;
			var gate = project.AddPart<XorGatePart>();
			gate.X = 450; gate.Y = 160; gate.Angle = angle;
			cases.Add(($"every kind at {angle}°", project.Scene.Width, project.Scene.Height, project.Parts));
		}
		int compared = 0;
		foreach ((string name, int width, int height, IReadOnlyList<OpticalPart> parts) in cases)
		{
			// red and blue vary over the canvas, with a column of mirror pixels every 50, so graded bands rise from every
			// medium and absorption meets blue already there
			var bgra = new byte[width * height * 4];
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					int i = (y * width + x) * 4;
					bgra[i] = (byte)(y % 90);
					bgra[i + 2] = (byte)(x % 50 == 0 ? 0 : 100 + (x / 7 + y / 5) % 150);
					bgra[i + 3] = 255;
				}
			}
			var image = new ScenePixels(width, height, width * 4, bgra);
			ScenePixels expected = PaintPixelByPixel(image, parts);
			ScenePixels actual = PartPainter.Paint(image, width, height, 1.0, parts)!;
			int first = expected.Bgra.AsSpan().CommonPrefixLength(actual.Bgra);
			compared++;
			Check(first == expected.Bgra.Length, first == expected.Bgra.Length
				? $"painter runs: {name} comes out byte for byte as pixel by pixel ({parts.Count} parts, {width}×{height})"
				: $"painter runs: {name} differs at ({first / 4 % width}, {first / 4 / width}) channel {first % 4}: {expected.Bgra[first]} pixel by pixel, {actual.Bgra[first]} with runs");
		}
		Check(compared >= 14, $"the runs were compared on {compared} projects");

		// the glowing edges in lanes (2026-11-04) against the pixel-by-pixel Sobel they replaced: a painted scene and noise
		var noise = new Random(7);
		foreach ((string name, ScenePixels pixels) in new[]
		{
			("Example 16 painted", PartPainter.Paint(null, 1500, 900, 1.0, cases.First(c => c.Name.StartsWith("Example16")).Parts)!),
			("noise 1920 × 1080", new ScenePixels(1920, 1080, 1920 * 4, Enumerable.Range(0, 1920 * 1080 * 4).Select(_ => (byte)noise.Next(256)).ToArray())),
			("noise 5 × 3", new ScenePixels(5, 3, 20, Enumerable.Range(0, 60).Select(_ => (byte)noise.Next(256)).ToArray())),
		})
		{
			byte[] expected = GlowingEdgesPixelByPixel(pixels.Bgra, pixels.Width, pixels.Height, pixels.StrideBytes);
			byte[] actual = GlowingEdges.Compute(pixels.Bgra, pixels.Width, pixels.Height, pixels.StrideBytes);
			int first = expected.AsSpan().CommonPrefixLength(actual);
			Check(first == expected.Length, first == expected.Length
				? $"glowing edges in lanes: {name} comes out byte for byte as pixel by pixel"
				: $"glowing edges in lanes: {name} differs at byte {first}: {expected[first]} pixel by pixel, {actual[first]} in lanes");
		}
	}

	/// <summary>The glowing edges as they stood before the lanes (2026-11-04): the Sobel magnitude per pixel and channel in double.</summary>
	private static byte[] GlowingEdgesPixelByPixel(byte[] bgra, int width, int height, int strideBytes)
	{
		var result = new byte[width * height * 4];
		for (int i = 3; i < result.Length; i += 4)
		{
			result[i] = 255;
		}
		if (width < 3 || height < 3)
		{
			return result;
		}
		for (int y = 1; y < height - 1; y++)
		{
			int rowUp = (y - 1) * strideBytes;
			int rowMid = y * strideBytes;
			int rowDown = (y + 1) * strideBytes;
			int dstRow = y * width * 4;
			for (int x = 1; x < width - 1; x++)
			{
				int cUp = rowUp + (x - 1) * 4;
				int cMid = rowMid + (x - 1) * 4;
				int cDown = rowDown + (x - 1) * 4;
				int dst = dstRow + x * 4;
				for (int channel = 0; channel < 3; channel++)
				{
					int p00 = bgra[cUp + channel];
					int p01 = bgra[cUp + 4 + channel];
					int p02 = bgra[cUp + 8 + channel];
					int p10 = bgra[cMid + channel];
					int p12 = bgra[cMid + 8 + channel];
					int p20 = bgra[cDown + channel];
					int p21 = bgra[cDown + 4 + channel];
					int p22 = bgra[cDown + 8 + channel];
					int gx = (p02 + 2 * p12 + p22) - (p00 + 2 * p10 + p20);
					int gy = (p20 + 2 * p21 + p22) - (p00 + 2 * p01 + p02);
					double magnitude = Math.Sqrt((double)gx * gx + (double)gy * gy) * GlowingEdges.Scale;
					result[dst + channel] = (byte)Math.Min(255.0, magnitude);
				}
			}
		}
		return result;
	}

	/// <summary>The painting as it stood before the runs (2026-11-03): every pixel of a part's rectangle evaluated on its own.</summary>
	private static ScenePixels PaintPixelByPixel(ScenePixels image, IReadOnlyList<OpticalPart> parts)
	{
		int width = image.Width;
		int height = image.Height;
		int stride = width * 4;
		var bgra = new byte[stride * height];
		for (int y = 0; y < height; y++)
		{
			Buffer.BlockCopy(image.Bgra, y * image.StrideBytes, bgra, y * stride, stride);
		}
		const double reach = 0.6;
		const double step = 1.0 / PartPainter.Supersample;
		const double half = step / 2.0;
		foreach (OpticalPart part in parts)
		{
			if (!part.Enabled || PartShape.For(part) is not { } shape)
			{
				continue;
			}
			(int x0, int y0, int x1, int y1) = shape.Bounds(width, height);
			if (x0 > x1 || y0 > y1)
			{
				continue;
			}
			if (shape.Absorption > 0)
			{
				int columns = x1 - x0 + 1;
				var depths = new float[columns * (y1 - y0 + 1)];
				double deepest = 0.0;
				for (int y = y0; y <= y1; y++)
				{
					for (int x = x0; x <= x1; x++)
					{
						double depth = shape.DepthAt(x + 0.5, y + 0.5);
						depths[(y - y0) * columns + x - x0] = (float)depth;
						deepest = Math.Max(deepest, depth);
					}
				}
				double ramp = Math.Min(shape.AbsorptionRamp, deepest);
				int full = shape.Absorption;
				for (int y = y0; y <= y1; y++)
				{
					for (int x = x0; x <= x1; x++)
					{
						float depth = depths[(y - y0) * columns + x - x0];
						if (depth >= 0f)
						{
							int offset = y * stride + x * 4;
							int blue = (int)Math.Round(full * shape.AbsorptionShareAt(x + 0.5, y + 0.5, depth, ramp));
							bgra[offset] = (byte)Math.Max(bgra[offset], blue);
						}
					}
				}
			}
			if (!shape.PaintsIndex)
			{
				continue;
			}
			if (shape.IsMirror)
			{
				for (int y = y0; y <= y1; y++)
				{
					for (int x = x0; x <= x1; x++)
					{
						if (shape.IndexAt(x + 0.5, y + 0.5) is not null)
						{
							bgra[y * stride + x * 4 + 2] = 0;
						}
					}
				}
				continue;
			}
			int coating = shape.CoatingWidth;
			for (int y = y0; y <= y1; y++)
			{
				for (int x = x0; x <= x1; x++)
				{
					int offset = y * stride + x * 4 + 2;
					(double depth, double inner) = shape.DepthsAt(x + 0.5, y + 0.5);
					if (depth < -reach)
					{
						continue;
					}
					int red = bgra[offset];
					double existing = red == 0 ? 1.0 : red / 100.0;
					if (depth > reach && inner > reach && (coating == 0 || Math.Abs(depth - coating) > reach))
					{
						if (shape.IndexAt(x + 0.5, y + 0.5, existing) is { } whole)
						{
							bgra[offset] = (byte)Math.Clamp((int)Math.Round(whole * 100.0), 1, 255);
						}
						continue;
					}
					double sum = 0.0;
					int inside = 0;
					for (int sy = 0; sy < PartPainter.Supersample; sy++)
					{
						for (int sx = 0; sx < PartPainter.Supersample; sx++)
						{
							double? index = shape.IndexAt(x + half + sx * step, y + half + sy * step, existing);
							if (index is { } n)
							{
								sum += n;
								inside++;
							}
							else
							{
								sum += existing;
							}
						}
					}
					if (inside > 0)
					{
						bgra[offset] = (byte)Math.Clamp((int)Math.Round(sum / (PartPainter.Supersample * PartPainter.Supersample) * 100.0), 1, 255);
					}
				}
			}
		}
		return new ScenePixels(width, height, stride, bgra);
	}

	/// <summary>Red at canvas pixel (x, y) of a painted scene.</summary>
	private static int RedAt(ScenePixels pixels, int x, int y) => pixels.Bgra[y * pixels.StrideBytes + x * 4 + 2];

	/// <summary>Sum over the canvas of the coverage a painted glass part of <paramref name="index"/> left on a background of index 1: the painted area in pixels.</summary>
	private static double PaintedArea(ScenePixels pixels, double index)
	{
		double area = 0.0;
		for (int y = 0; y < pixels.Height; y++)
		{
			for (int x = 0; x < pixels.Width; x++)
			{
				area += (RedAt(pixels, x, y) - 100) / ((index - 1.0) * 100.0);
			}
		}
		return area;
	}

	private static void CheckParts()
	{
		Console.WriteLine("--- optical parts");
		const int W = 200, H = 120;
		(byte[] bgra, int stride) = ScenePixels(W, H);
		bgra[5 * stride + 5 * 4] = 77;      // blue at (5, 5), under the lens
		bgra[60 * stride + 100 * 4 + 1] = 160; // green at the lens centre
		bgra[60 * stride + 100 * 4] = 33;      // blue at the lens centre
		var image = new ScenePixels(W, H, stride, bgra);
		var project = new Project();
		project.Scene.Width = W;
		project.Scene.Height = H;
		Check(ReferenceEquals(PartPainter.Paint(image, W, H, 1.0, project.Parts), image) && PartPainter.Paint(null, W, H, 1.0, project.Parts) == null, "no enabled part: the painter returns the image itself (null for a canvas)");
		var lens = project.AddPart<LensPart>();
		lens.Enabled = false;
		Check(ReferenceEquals(PartPainter.Paint(image, W, H, 1.0, project.Parts), image), "a disabled part leaves the image untouched");
		lens.Enabled = true;
		lens.X = 100;
		lens.Y = 60;
		lens.Diameter = 100;
		lens.R1 = 300;
		lens.R2 = -300;
		lens.Thickness = 40;
		Check(lens.Name == "Lens 1" && lens.RefractiveIndex == 1.5, "a new lens: 'Lens 1', index 1.5");
		ScenePixels painted = PartPainter.Paint(image, W, H, 1.0, project.Parts)!;
		Check(!ReferenceEquals(painted, image) && painted.Width == W && painted.Height == H && image.Bgra[60 * stride + 100 * 4 + 2] == 100, "an enabled part paints a copy; the image is not modified");
		Check(painted.Bgra[60 * painted.StrideBytes + 100 * 4 + 1] == 160 && painted.Bgra[60 * painted.StrideBytes + 100 * 4] == 33 && painted.Bgra[5 * painted.StrideBytes + 5 * 4] == 77, "green and blue are untouched under the lens");
		int centreRow = Enumerable.Range(0, W).Count(x => RedAt(painted, x, 60) == 150);
		int edgeRow = Enumerable.Range(0, W).Count(x => RedAt(painted, x, 60 + 49) >= 125);
		int rows = Enumerable.Range(0, H).Count(y => Enumerable.Range(0, W).Any(x => RedAt(painted, x, y) > 100));
		Check(centreRow == 40 && rows == 100 && edgeRow >= 30 && edgeRow <= 33, $"a biconvex lens D 100, R ±300, t 40: 40 px thick on the axis (red 150), 100 rows, {edgeRow} px at the edge (sag 4 each side)");
		Check(RedAt(painted, 100 - 20, 60) == 150 && RedAt(painted, 100 - 21, 60) == 100 && RedAt(painted, 100 + 19, 60) == 150 && RedAt(painted, 100 + 20, 60) == 100, "the lens spans x = 80..119 on the axis, hard edges on whole pixels");
		lens.Angle = 90;
		painted = PartPainter.Paint(image, W, H, 1.0, project.Parts)!;
		int columnOnAxis = Enumerable.Range(0, H).Count(y => RedAt(painted, 100, y) == 150);
		int columns = Enumerable.Range(0, W).Count(x => Enumerable.Range(0, H).Any(y => RedAt(painted, x, y) > 100));
		Check(columnOnAxis == 40 && columns == 100, $"turned by 90°, the lens is 40 px thick along y and 100 px wide along x ({columnOnAxis}, {columns})");
		lens.Angle = 0;
		lens.Coating = 5;
		painted = PartPainter.Paint(image, W, H, 1.0, project.Parts)!;
		Check(RedAt(painted, 100, 60) == 150 && RedAt(painted, 100, 60 + 47) == 122 && RedAt(painted, 100, 60 - 47) == 122 && RedAt(painted, 100 - 18, 60) == 122 && RedAt(painted, 100 - 14, 60) == 150,
			"a 5 px coating: red 122 = round(100 √1.5) just inside the flat edges and the curved surfaces, 150 in the body");
		// the graded profile (2026-10-29): along the axis the index rises from the canvas's 1.00 at the first surface (x = 80)
		// to 1.50 as a raised cosine over the 5 px band: 1.25 exactly at mid-depth (pixel 82), the body from pixel 85 on
		lens.CoatingProfile = CoatingProfile.Graded;
		painted = PartPainter.Paint(image, W, H, 1.0, project.Parts)!;
		int[] ramp = Enumerable.Range(80, 8).Select(x => RedAt(painted, x, 60)).ToArray();
		Check(ramp[0] <= 103 && ramp[2] == 125 && ramp[5] == 150 && ramp[7] == 150 && ramp.Zip(ramp.Skip(1)).All(pair => pair.First <= pair.Second),
			$"a graded 5 px band rises from red 100 to 150 as a raised cosine: 125 at mid-depth, the body from the band's end, never falling ({string.Join(" ", ramp)})");
		Check(RedAt(painted, 100 + 17, 60) == 125 && RedAt(painted, 100, 60 + 47) == 125, "the graded band is the same on the second surface and the rim");
		lens.CoatingProfile = CoatingProfile.Layer;
		// the faces: the first surface is the front, the second the back, the rim the sides
		lens.FrontCoated = false;
		painted = PartPainter.Paint(image, W, H, 1.0, project.Parts)!;
		Check(RedAt(painted, 100 - 18, 60) == 150 && RedAt(painted, 100 + 17, 60) == 122 && RedAt(painted, 100, 60 + 47) == 122 && RedAt(painted, 100, 60 - 47) == 122,
			"the front left bare: 150 just inside the first surface, the band on the second surface and both rims");
		lens.FrontCoated = true;
		lens.SidesCoated = false;
		painted = PartPainter.Paint(image, W, H, 1.0, project.Parts)!;
		Check(RedAt(painted, 100 - 18, 60) == 122 && RedAt(painted, 100 + 17, 60) == 122 && RedAt(painted, 100, 60 + 47) == 150 && RedAt(painted, 100, 60 - 47) == 150,
			"the sides left bare: the band on both surfaces, 150 just inside both rims");
		lens.SidesCoated = true;
		{
			// a slab along the beam, 40 along and 60 across at (100, 60), index 2.25 so the layer is 1.50 (red 150) against the body's 225;
			// it spans x 80..119 and y 30..89 and the 4 px band lies just inside each face
			var slabProject = new Project();
			slabProject.Scene.Width = W;
			slabProject.Scene.Height = H;
			var slab = slabProject.AddPart<RectanglePart>();
			slab.X = 100;
			slab.Y = 60;
			slab.Length = 40;
			slab.Width = 60;
			slab.RefractiveIndex = 2.25;
			slab.Coating = 4;
			slab.FrontCoated = false;
			slab.SidesCoated = false;
			ScenePixels slabPainted = PartPainter.Paint(null, W, H, 1.0, slabProject.Parts)!;
			Check(RedAt(slabPainted, 82, 60) == 225 && RedAt(slabPainted, 117, 60) == 150 && RedAt(slabPainted, 100, 32) == 225 && RedAt(slabPainted, 100, 87) == 225,
				"a slab with only its back coated: bare at the front and the long sides, √2.25 = 1.50 inside the back face");
			slab.FrontCoated = true;
			slab.BackCoated = false;
			slab.SidesCoated = true;
			slabPainted = PartPainter.Paint(null, W, H, 1.0, slabProject.Parts)!;
			Check(RedAt(slabPainted, 82, 60) == 150 && RedAt(slabPainted, 117, 60) == 225 && RedAt(slabPainted, 100, 32) == 150 && RedAt(slabPainted, 100, 87) == 150,
				"front and sides coated, the back bare");
			// the faces turn with the part: at 90° (counter-clockwise on the screen) the front lies at y = 80, facing down, the back at y = 40
			slab.Angle = 90;
			slabPainted = PartPainter.Paint(null, W, H, 1.0, slabProject.Parts)!;
			Check(RedAt(slabPainted, 100, 78) == 150 && RedAt(slabPainted, 100, 42) == 225 && RedAt(slabPainted, 72, 60) == 150 && RedAt(slabPainted, 127, 60) == 150,
				"turned by 90° the bare back faces up: 225 just inside y = 40, the band inside y = 80 and both long sides");
			slab.Angle = 0;
			// a circle: the arcs within 45° of the axis are front and back, the top and bottom arcs the sides
			var disc = slabProject.AddPart<EllipsePart>();
			disc.X = 100;
			disc.Y = 60;
			disc.Length = 60;
			disc.Width = 60;
			disc.RefractiveIndex = 2.25;
			disc.Coating = 4;
			disc.SidesCoated = false;
			slab.Enabled = false;
			slabPainted = PartPainter.Paint(null, W, H, 1.0, slabProject.Parts)!;
			Check(RedAt(slabPainted, 72, 60) == 150 && RedAt(slabPainted, 127, 60) == 150 && RedAt(slabPainted, 100, 32) == 225 && RedAt(slabPainted, 100, 87) == 225,
				"a circle with its sides bare: the band at the leftmost and rightmost points, none at the top and bottom");
			// the profile and the bare faces persist as attributes, coated faces and the layer as the defaults
			slab.CoatingProfile = CoatingProfile.Graded;
			using var stream = new MemoryStream();
			slabProject.Save(stream);
			stream.Position = 0;
			Project loadedProject = Project.Load(stream);
			string coatingXml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
			var loadedSlab = (RectanglePart)loadedProject.Parts[0];
			var loadedDisc = (EllipsePart)loadedProject.Parts[1];
			Check(loadedSlab.CoatingProfile == CoatingProfile.Graded && !loadedSlab.BackCoated && loadedSlab.FrontCoated && loadedSlab.SidesCoated && !loadedDisc.SidesCoated
				&& loadedDisc.CoatingProfile == CoatingProfile.Layer && coatingXml.Contains("CoatingProfile=\"Graded\"") && coatingXml.Contains("BackCoated=\"false\"") && !coatingXml.Contains("FrontCoated=") && !coatingXml.Contains("CoatingProfile=\"Layer\""),
				"the coating profile and a bare face round-trip through the file; the defaults are not written");
		}
		lens.Coating = 0;
		lens.Mirror = true;
		painted = PartPainter.Paint(image, W, H, 1.0, project.Parts)!;
		int mirrorRow = Enumerable.Range(0, W).Count(x => RedAt(painted, x, 60) == 0);
		Check(mirrorRow == 40 && painted.Bgra.Where((_, i) => i % 4 == 2).All(red => red is 0 or 100), $"as a mirror the lens is red 0 by the pixel-centre test ({mirrorRow} px on the axis), nothing in between");
		lens.Mirror = false;
		lens.R1 = -10;
		lens.Normalize();
		Check(lens.R1 == -50 && lens.R2 == -300, "Normalize: a concave radius below half the diameter becomes half the diameter, the sign kept");
		// the lens's bulk is its diameter and centre thickness; radii too curved for them are raised so the surfaces meet
		// at the rim, not before it (the user's point: a diameter of 2000 with radii of 300 gave a tiny lens)
		lens.Diameter = 100;
		lens.Thickness = 20;
		lens.R1 = 50;
		lens.R2 = -50;
		lens.Normalize();
		Check(lens.R1 == 130 && lens.R2 == -130 && Math.Abs(lens.EdgeThickness) < 1e-9, $"two semicircular surfaces on a 100 × 20 lens are flattened to R ±130, where their sags of 10 just fit ({lens.R1}, {lens.R2})");
		painted = PartPainter.Paint(image, W, H, 1.0, project.Parts)!;
		rows = Enumerable.Range(0, H).Count(y => Enumerable.Range(0, W).Any(x => RedAt(painted, x, y) > 100));
		centreRow = Enumerable.Range(0, W).Count(x => RedAt(painted, x, 60) == 150);
		Check(rows >= 98 && rows <= 100 && centreRow == 20, $"the fitted lens fills its diameter ({rows} rows) and keeps its centre thickness ({centreRow} px)");
		lens.R1 = 300;
		lens.R2 = 0;
		lens.Normalize();
		Check(lens.R1 == 300 && lens.R2 == 0 && Math.Abs(lens.EdgeThickness - (20 - LensPart.Sag(300, 50))) < 1e-9, "a plano-convex lens whose sag fits is left alone; the edge thickness is what the one surface leaves");
		lens.R1 = 60;
		lens.R2 = 60;
		lens.Normalize();
		Check(lens.R1 == 60 && lens.R2 == 60 && Math.Abs(lens.EdgeThickness - 20) < 1e-9, "a meniscus of equal radii: the convex surface fits since the concave one adds what it takes, the edge as thick as the centre");
		lens.Diameter = 2000;
		lens.Thickness = 40;
		lens.R1 = 300;
		lens.R2 = -300;
		lens.Normalize();
		Check(Math.Abs(lens.R1 - 25010) < 1e-9 && Math.Abs(lens.R2 + 25010) < 1e-9 && Math.Abs(lens.EdgeThickness) < 1e-9, "the user's case: a 2000 × 40 lens gets radii of 25010 and fills its diameter");
		// the size grip stretches the lens (2026-10-26): across with the profile held, along with the whole profile scaled, so a
		// diagonal pull scales the lens as a whole; nothing stops it
		var gripped = new LensPart { Diameter = 100, Thickness = 20, R1 = 130, R2 = -130 };
		gripped.SetExtents(400, 20);
		Check(Math.Abs(gripped.R1 - 2005) < 1e-9 && Math.Abs(gripped.R2 + 2005) < 1e-9 && gripped.Diameter == 400 && gripped.Thickness == 20 && Math.Abs(gripped.EdgeThickness) < 1e-9,
			$"dragged wider alone the sags of 10 stay and the radii follow the diameter ({gripped.R1}), the thickness and the edge as they were");
		gripped = new LensPart { Diameter = 100, Thickness = 20, R1 = 130, R2 = -130 };
		gripped.SetExtents(100, 60);
		Check(gripped.R1 == 56.67 && gripped.R2 == -56.67 && gripped.Thickness == 60 && gripped.Diameter == 100 && gripped.EdgeThickness >= 0.0 && gripped.EdgeThickness < 0.01,
			$"dragged thicker alone the whole profile scales: sags of 30, R 3400 / 60 to a hundredth, {gripped.R1}, the edge still zero within a hundredth ({gripped.EdgeThickness})");
		gripped = new LensPart { Diameter = 100, Thickness = 20, R1 = 130, R2 = -130 };
		gripped.SetExtents(200, 40);
		Check(Math.Abs(gripped.R1 - 260) < 1e-9 && Math.Abs(gripped.R2 + 260) < 1e-9, $"pulled out diagonally the lens scales as a whole: the radii doubled to {gripped.R1}");
		gripped.SetExtents(200, 4);
		Check(gripped.Thickness == 4 && Math.Abs(gripped.R1 - (10000 + 4) / 4.0) < 1e-9 && Math.Abs(gripped.EdgeThickness) < 1e-9, $"squashed to 4 thick it is the same lens squashed, sags of 2, R {gripped.R1}, never a slab");
		gripped.SetExtents(100, 400);
		Check(gripped.R1 == 50 && gripped.R2 == -50 && gripped.Thickness == 400, "stretched thicker than its surfaces can take at that diameter, they are hemispheres on a slab");
		// a dragged radius is rounded to a hundredth of a pixel away from the lens (2026-10-28, Jeroen's report: sixteen decimals
		// in the box): 3725 / 70 = 53.214... becomes 53.22, not 53.21, whose deeper sags would have had the surfaces refitted
		gripped = new LensPart { Diameter = 100, Thickness = 20, R1 = 130, R2 = -130 };
		gripped.SetExtents(100, 70);
		gripped.Normalize();
		Check(gripped.R1 == 53.22 && gripped.R2 == -53.22 && gripped.EdgeThickness >= 0.0 && gripped.EdgeThickness < 0.05,
			$"a dragged radius is rounded to a hundredth of a pixel on the flatter side, 3725 / 70 giving {gripped.R1}, which Normalize keeps since the surfaces still fit (the edge a few hundredths, {gripped.EdgeThickness}: so curved a surface moves its rim by twice the rounding)");
		// a surface handle: the vertex moves on the axis, the rim and the other surface stay; the position follows the centre
		var shaped = new LensPart { X = 500, Y = 300, Diameter = 100, Thickness = 20, R1 = 0, R2 = 0 };
		shaped.SetVertex(1, -30.0);
		Check(Math.Abs(shaped.R1 - 72.5) < 1e-9 && shaped.R2 == 0 && shaped.Thickness == 40 && shaped.X == 490 && Math.Abs(shaped.ToCanvas(shaped.RimX(1), 0).X - 490) < 1e-9 && Math.Abs(shaped.ToCanvas(shaped.VertexX(2), 0).X - 510) < 1e-9,
			$"the left vertex pulled 20 px out of a flat slab: R1 {shaped.R1} (the sagitta of 20 at 50), 40 thick, the centre at {shaped.X}, the rim and the other vertex where they were");
		shaped.SetVertex(1, 0.0);
		Check(shaped.R1 == 0 && shaped.Thickness == 20 && shaped.X == 500, "pushed back to the rim plane it is flat again, the slab as before");
		shaped.SetVertex(1, 5.0);
		Check(shaped.R1 == -90.83 && shaped.Thickness == 5 && shaped.X == 508 && shaped.EdgeThickness >= 20.0 && shaped.EdgeThickness < 20.01, $"pushed 15 px past the rim plane it is concave, R1 {shaped.R1} (the sagitta of 15, 2725 / 30, to a hundredth towards the deeper side), 5 thick at the centre, the edge still 20 within a hundredth");
		shaped.SetVertex(2, 1000.0);
		Check(shaped.R2 == -50 && shaped.Thickness > 500, $"the right vertex pulled far out stops at a hemisphere, R2 {shaped.R2}");
		shaped.SetVertex(2, -2000.0);
		Check(shaped.Thickness == 1, "pushed through the other surface it stops a pixel short of it");
		// the rim a drag captured lies where the rotation and the position's rounding left it, a fraction of a pixel off, and the
		// radius through it has sixteen decimals before the rounding (29.7 of sag at 50 gives 56.9375...)
		shaped = new LensPart { X = 500, Y = 300, Diameter = 100, Thickness = 20, R1 = 0, R2 = 0 };
		shaped.SetVertex(1, -30.0, -0.3, 10.0);
		Check(shaped.R1 == 56.94 && shaped.Thickness == 40, $"a vertex dragged against a rim a fraction of a pixel off gets a radius of two decimals, {shaped.R1}, rounded up");
		// the name follows the Mirror box (2026-10-29, the user's design): the word Lens becomes Mirror and back, whole words only, other names untouched
		Check(LensPart.MirrorName("Lens 1", true) == "Mirror 1" && LensPart.MirrorName("Mirror 1", false) == "Lens 1", "MirrorName: Lens 1 ticked is Mirror 1, unticked Lens 1 again");
		Check(LensPart.MirrorName("Lens 1", false) == "Lens 1" && LensPart.MirrorName("Mirror 1", true) == "Mirror 1", "MirrorName: a name that already matches the flag is as given");
		Check(LensPart.MirrorName("Lensing stage", true) == "Lensing stage" && LensPart.MirrorName("Objective", true) == "Objective" && LensPart.MirrorName("lens 2", true) == "lens 2",
			"MirrorName: whole words in the user's case only; other names are as given");
		// a surface's shape for the row icon (2026-10-29, the user's report: Example 1's mirror of radii 14401 on 104 px showed as
		// biconvex): flat for 0 or a sag at the rim under half a pixel, else by the sign; the threshold on 104 px lies at R = 2704.25
		Check(LensPart.ShapeOf(14401, 104) == SurfaceShape.Flat && LensPart.ShapeOf(-14401, 104) == SurfaceShape.Flat && LensPart.ShapeOf(0, 200) == SurfaceShape.Flat,
			"ShapeOf: a sag under half a pixel is flat, as is a radius of 0");
		Check(LensPart.ShapeOf(300, 200) == SurfaceShape.Convex && LensPart.ShapeOf(-300, 200) == SurfaceShape.Concave && LensPart.ShapeOf(10, 200) == SurfaceShape.Convex,
			"ShapeOf: a sag of a pixel or more goes by the sign, a radius under the half diameter too");
		Check(LensPart.ShapeOf(2704, 104) == SurfaceShape.Convex && LensPart.ShapeOf(2705, 104) == SurfaceShape.Flat,
			"ShapeOf: the half-pixel threshold on 104 px lies between R 2704 and 2705");
		lens.Diameter = 100;
		lens.Thickness = 40;
		lens.R1 = 300;
		lens.R2 = -300;
		Check(!lens.IsConcave, "a biconvex lens is not concave");
		lens.IsConcave = true;
		Check(lens.IsConcave && lens.R1 == -300 && lens.R2 == 300, "ticking Concave flips both radii: biconcave");
		lens.IsConcave = true;
		Check(lens.R1 == -300, "setting the state it already has changes nothing");
		lens.R1 = 300;
		lens.R2 = 0;
		Check(!lens.IsConcave, "plano-convex is not concave");
		lens.IsConcave = true;
		Check(lens.R1 == -300 && lens.R2 == 0 && lens.IsConcave, "plano-convex turned around is plano-concave, the flat side still flat");
		lens.R1 = 100;
		lens.R2 = 60;
		Check(lens.IsConcave, "a meniscus whose concave side curves more is concave (diverging)");
		project.Parts.Clear();

		// the shapes: a regular hexagon's area, a rectangle, triangles, an outline; then a GRIN lens, a Bragg stack, a fibre
		var hexagon = project.AddPart<PolygonPart>();
		hexagon.X = 100;
		hexagon.Y = 60;
		hexagon.Sides = 6;
		hexagon.Side = 30;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		double area = PaintedArea(painted, 1.5);
		double expected = 3.0 * Math.Sqrt(3.0) / 2.0 * 30 * 30;
		Check(Math.Abs(area - expected) < 0.01 * expected, $"a regular hexagon of side 30 covers {area:F1} px, the formula gives {expected:F1} (within 1 %)");
		Check(RedAt(painted, 100 + 24, 60) == 150 && RedAt(painted, 100 + 26, 60) == 100 && RedAt(painted, 100, 60 + 14) == 150 && RedAt(painted, 100, 60 + 30) == 100, "the hexagon has flat faces at ±x' (26 px out) and vertices at ±y' (30 px out)");
		Check(painted.Bgra[(60 * W + 100) * 4 + 3] == 255 && painted.Bgra[(60 * W + 100) * 4 + 1] == 0 && painted.Bgra[(0 * W + 0) * 4 + 2] == 100, "a blank canvas: opaque, no green, background red 100");
		project.Parts.Clear();
		var rectangle = project.AddPart<RectanglePart>();
		rectangle.X = 100;
		rectangle.Y = 60;
		rectangle.Length = 60;
		rectangle.Width = 20;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		area = PaintedArea(painted, 1.5);
		Check(Math.Abs(area - 1200.0) < 2.0 && RedAt(painted, 100 - 30, 60) == 150 && RedAt(painted, 100 - 31, 60) == 100 && RedAt(painted, 100 + 29, 60) == 150 && RedAt(painted, 100, 60 + 9) == 150 && RedAt(painted, 100, 60 + 10) == 100,
			$"a rectangle of length 60 and width 20 lies square to the axis, x = 70..129, y = 50..69 ({area:F1} px)");
		project.Parts.Clear();
		var ellipse = project.AddPart<EllipsePart>();
		ellipse.X = 100;
		ellipse.Y = 60;
		ellipse.Length = 60;
		ellipse.Width = 40;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		area = PaintedArea(painted, 1.5);
		expected = Math.PI * 30 * 20;
		Check(Math.Abs(area - expected) < 0.01 * expected && RedAt(painted, 100 + 28, 60) == 150 && RedAt(painted, 100 + 31, 60) == 100 && RedAt(painted, 100, 60 + 18) == 150 && RedAt(painted, 100, 60 + 21) == 100,
			$"an ellipse of 60 by 40 covers {area:F1} px against π·30·20 = {expected:F1}, reaching 30 px along the axis and 20 across");
		ellipse.Width = 60;
		(double X, double Y)[] round = ellipse.Vertices();
		Check(round.Length == ellipse.VertexCount && round.Length == 63 && round.All(v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y) - 30) < 1e-9) && Math.Abs(ellipse.AxisReach - 30) < 1e-9,
			"a circle of 60: one vertex per three pixels of circumference (63), all 30 px from the centre");
		Check(new EllipsePart { Length = 4, Width = 4 }.VertexCount == EllipsePart.MinVertices && new EllipsePart { Length = 2000, Width = 2000 }.VertexCount == EllipsePart.MaxVertices, "the vertex count stays within its limits");
		// the chain: linking takes the length into the width, then either side moves the other
		var linked = new RectanglePart { Length = 50, Width = 20 };
		linked.Linked = true;
		bool follows = linked.Width == 50;
		linked.Length = 70;
		follows &= linked.Width == 70;
		linked.Width = 30;
		follows &= linked.Length == 30;
		linked.Linked = false;
		linked.Length = 80;
		Check(follows && linked.Width == 30, "a linked rectangle keeps width and length equal, an unlinked one not");
		ellipse.Width = 10;
		ellipse.Linked = true;
		Check(ellipse.Width == 60 && ellipse.Vertices().All(v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y) - 30) < 1e-9), "a linked ellipse is a circle");
		project.Parts.Clear();
		var triangle = project.AddPart<TrianglePart>();
		triangle.X = 100;
		triangle.Y = 60;
		triangle.Base = 60;
		(double X, double Y)[] tri = triangle.Vertices();
		Check(triangle.Type == TriangleType.Equilateral && tri.Length == 3 && Math.Abs(tri[2].X - tri[0].X - 60 * Math.Sqrt(3) / 2) < 1e-9 && Math.Abs(tri[2].Y) < 1e-9 && Math.Abs(triangle.ApexAngle - 60) < 1e-9 && Math.Abs(triangle.AxisReach - tri[2].X) < 1e-9,
			"an equilateral triangle of base 60: height 52, the apex on +x', the reach along the axis is the apex");
		triangle.Type = TriangleType.RightIsosceles;
		tri = triangle.Vertices();
		Check(triangle.AngleA == 45 && triangle.AngleB == 45 && Math.Abs(tri[2].X - tri[0].X - 30) < 1e-9 && Math.Abs(triangle.ApexAngle - 90) < 1e-9, "the right isosceles preset writes 45° / 45°: height 30, the right angle at the apex");
		triangle.Type = TriangleType.ThirtySixtyNinety;
		tri = triangle.Vertices();
		Check(triangle.AngleA == 90 && triangle.AngleB == 30 && Math.Abs(tri[2].Y - tri[0].Y) < 1e-9 && Math.Abs(tri[2].X - tri[0].X - 60 * Math.Tan(Math.PI / 6)) < 1e-9, "the 30-60-90 preset: the right angle at the first end of the base, height 34.6");
		triangle.Type = TriangleType.Isosceles;
		triangle.AngleA = 70;
		Check(triangle.AngleB == 70 && Math.Abs(triangle.ApexAngle - 40) < 1e-9, "isosceles: angle A is mirrored into B, the apex angle follows");
		triangle.Type = TriangleType.Custom;
		triangle.AngleA = 100;
		triangle.AngleB = 100;
		triangle.Normalize();
		Check(triangle.AngleA == 100 && triangle.AngleB == 79 && triangle.ApexAngle == 1, "custom angles leave at least a degree for the apex (100 + 100 becomes 100 + 79)");
		triangle.AngleA = 60;
		triangle.AngleB = 60;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		area = PaintedArea(painted, 1.5);
		Check(Math.Abs(area - 1558.8) < 0.01 * 1558.8 && RedAt(painted, 100 - 25, 60) == 150 && RedAt(painted, 100 - 27, 60) == 100 && RedAt(painted, 100 + 20, 60) == 150 && RedAt(painted, 100 + 27, 60) == 100,
			$"the painted equilateral triangle covers {area:F1} px of 1558.8, the base at x' = -26, the apex at +26");
		project.Parts.Clear();
		var outline = project.AddPart<OutlinePart>();
		outline.X = 100;
		outline.Y = 60;
		Check(PartShape.For(outline) == null && ReferenceEquals(PartPainter.Paint(image, W, H, 1.0, project.Parts), image) == false && RedAt(PartPainter.Paint(null, W, H, 1.0, project.Parts)!, 100, 60) == 100, "an outline without points paints nothing");
		outline.Points.Add(new PartPoint { X = -10, Y = -5 });
		outline.Points.Add(new PartPoint { X = 10, Y = -5 });
		outline.Points.Add(new PartPoint { X = 10, Y = 5 });
		outline.Points.Add(new PartPoint { X = -10, Y = 5 });
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		area = PaintedArea(painted, 1.5);
		Check(Math.Abs(area - 200.0) < 2.0 && RedAt(painted, 100, 60) == 150 && RedAt(painted, 100, 60 + 6) == 100, $"four points make a 20 × 10 outline ({area:F1} px)");
		project.Parts.Clear();
		var grin = project.AddPart<GrinLensPart>();
		grin.X = 100;
		grin.Y = 60;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		// pixel (100, 85) is sampled around y' = 25.5: n = 1.8 - 0.3 (25.5 / 50)² = 1.722; (100, 108) around 48.5: 1.518
		Check(RedAt(painted, 100, 60) == 180 && RedAt(painted, 100, 60 + 25) == 172 && RedAt(painted, 100, 60 + 48) == 152 && RedAt(painted, 100, 60 + 50) == 100 && RedAt(painted, 100 + 49, 60) == 180 && RedAt(painted, 100 + 50, 60) == 100,
			"a GRIN lens 100 × 100: index 1.8 on the axis falling parabolically to 1.5 at the edge, 1 outside");
		project.Parts.Clear();
		var bragg = project.AddPart<BraggReflectorPart>();
		bragg.X = 100;
		bragg.Y = 60;
		bragg.Pairs = 3;
		bragg.Thickness1 = 10;
		bragg.Thickness2 = 6;
		bragg.Width = 20;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		string layers = string.Concat(Enumerable.Range(100 - 25, 50).Select(x => RedAt(painted, x, 60) switch { 150 => '1', 200 => '2', 100 => '.', _ => '?' }));
		Check(layers == "." + string.Concat(Enumerable.Repeat("1111111111222222", 3)) + "." && RedAt(painted, 100, 60 + 9) == 150 && RedAt(painted, 100, 60 + 10) == 100,
			"a Bragg stack of 3 pairs 10 + 6 px, indices 1.5 / 2.0, 20 wide, centred: " + layers);
		project.Parts.Clear();
		var fiber = project.AddPart<FiberPart>();
		fiber.X = 80;
		fiber.Y = 60;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(fiber.Nodes.Count == 2 && fiber.Nodes[0] is { X: -100, Y: 0, OutX: 67, OutY: 0, InX: 0, Smooth: true } && fiber.Nodes[1] is { X: 100, Y: 0, InX: -67, InY: 0, OutX: 0 } && fiber.Ends == FiberEnds.Flat,
			"a new fibre runs straight from (-100, 0) to (100, 0), the handles a third of the way along it, the ends flat");
		Check(FiberPart.Flatten(fiber.Nodes).Length == 2, "the straight path flattens to one chord");
		Check(RedAt(painted, 80, 60) == 150 && RedAt(painted, 80, 60 + 9) == 150 && RedAt(painted, 80, 60 + 12) == 145 && RedAt(painted, 80, 60 + 19) == 145 && RedAt(painted, 80, 60 + 21) == 100 && RedAt(painted, 0, 60) == 150,
			"the fibre: core 1.5 within 10 px of the path, cladding 1.45 to 20 px, the canvas edge reached");
		Check(RedAt(painted, 179, 60) == 150 && RedAt(painted, 180, 60) == 100 && RedAt(painted, 179, 60 + 18) == 145 && RedAt(painted, 180, 60 + 18) == 100 && RedAt(painted, 185, 60) == 100,
			"a flat end: the last pixel before the endpoint is core, the first beyond it background, across the whole width");
		fiber.Coating = 3;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(RedAt(painted, 177, 60) == 122 && RedAt(painted, 176, 60) == 150 && RedAt(painted, 80, 60 + 18) == 120 && RedAt(painted, 80, 60 + 16) == 145,
			"the AR coating lies along the sides (√1.45 = 1.20) and on the flat end face (√1.5 = 1.22)");
		// the fibre's faces (2026-10-29): the start face is the front, the end face the back, the tube the sides; a shorter
		// fibre at x = 100 so both faces lie on the canvas, 40..160
		static int BlueAt(ScenePixels pixels, int x, int y) => pixels.Bgra[y * pixels.StrideBytes + x * 4];
		fiber.X = 100;
		fiber.Nodes[0].X = -60;
		fiber.Nodes[1].X = 60;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(RedAt(painted, 42, 60) == 122 && RedAt(painted, 157, 60) == 122 && RedAt(painted, 100, 60 + 18) == 120, "the 3 px layer on both end faces and along the tube");
		fiber.FrontCoated = false;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(RedAt(painted, 42, 60) == 150 && RedAt(painted, 157, 60) == 122 && RedAt(painted, 100, 60 + 18) == 120, "the start face left bare, the end face and the tube coated");
		fiber.FrontCoated = true;
		fiber.BackCoated = false;
		fiber.SidesCoated = false;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(RedAt(painted, 42, 60) == 122 && RedAt(painted, 157, 60) == 150 && RedAt(painted, 100, 60 + 18) == 145, "the end face and the tube left bare, the start face coated");
		// a graded 10 px band on the end face alone: the core's index falls to the canvas's 1.00 at the face as a raised cosine
		fiber.FrontCoated = false;
		fiber.BackCoated = true;
		fiber.Coating = 10;
		fiber.CoatingProfile = CoatingProfile.Graded;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(RedAt(painted, 145, 60) == 150 && RedAt(painted, 155, 60) == 121 && RedAt(painted, 159, 60) <= 103 && RedAt(painted, 42, 60) == 150,
			$"a graded end taper: 150 before the band, 121 at mid-depth of the band, near 100 at the face, the start face bare ({RedAt(painted, 155, 60)}, {RedAt(painted, 159, 60)})");
		fiber.Coating = 0;
		fiber.CoatingProfile = CoatingProfile.Layer;
		fiber.FrontCoated = true;
		fiber.SidesCoated = true;
		// the cladding's absorbing band (2026-10-29): with a half-width of 5 in a 10 px cladding the hump fills the cladding, nothing in
		// the core, the peak 15 px from the path, nothing at the outer edge, the band running up to the end face and not beyond it;
		// pixel centres lie at r = 12.5, 14.5, 15.5 and 18.5: shares 0.25, 0.81, 0.81 and 0.09 of 200
		fiber.Absorption = 200;
		fiber.AbsorptionRamp = 5;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(BlueAt(painted, 100, 69) == 0 && BlueAt(painted, 100, 72) == 50 && BlueAt(painted, 100, 74) == 162 && BlueAt(painted, 100, 75) == 162 && BlueAt(painted, 100, 78) == 18 && BlueAt(painted, 100, 80) == 0,
			$"the cladding's absorbing hump: 0 in the core, 50 half-way up, 162 either side of the peak 15 px out, 18 near the outer edge, 0 beyond it ({BlueAt(painted, 100, 72)}, {BlueAt(painted, 100, 75)}, {BlueAt(painted, 100, 78)})");
		Check(BlueAt(painted, 158, 75) == 162 && BlueAt(painted, 162, 75) == 0 && BlueAt(painted, 158, 60) == 0, "the band runs to the end face and stops there; the core stays clear");
		fiber.CladdingThickness = 0;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(BlueAt(painted, 100, 75) == 0 && BlueAt(painted, 100, 65) == 0, "a bare core absorbs nothing");
		fiber.CladdingThickness = 10;
		fiber.Absorption = 0;
		fiber.X = 80;
		fiber.Nodes[0].X = -100;
		fiber.Nodes[1].X = 100;
		fiber.Coating = 0;
		fiber.Ends = FiberEnds.Round;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(RedAt(painted, 185, 60) == 150 && RedAt(painted, 189, 60) == 150 && RedAt(painted, 190, 60) == 145 && RedAt(painted, 199, 60) == 145 && RedAt(painted, 195, 75) == 100,
			"a round end: the core goes on past the endpoint by its radius, the cladding by the outer one, the corner beyond it stays empty");
		fiber.Ends = FiberEnds.Flat;
		fiber.Nodes[0].X = -60;
		fiber.Nodes[0].Y = 40;
		fiber.Nodes[0].SetHandle(true, 0, -80);
		fiber.Nodes[1].X = 60;
		fiber.Nodes[1].Y = 40;
		fiber.Nodes[1].SetHandle(false, 0, -80);
		(double X, double Y)[] bent = FiberPart.Flatten(fiber.Nodes);
		Check(bent.Length > 20 && bent.All(p => Math.Abs(p.X) <= 60 && p.Y >= -20.0001 && p.Y <= 40) && bent.Min(p => Math.Abs(p.Y + 20)) < 0.5 && bent[0] == (-60, 40) && bent[^1] == (60, 40),
			$"a bent path (nodes at (±60, 40), 80 px handles straight up) flattens to {bent.Length} chords within the handles' hull, from node to node through the apex (0, -20)");
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(RedAt(painted, 80, 35) == 150 && RedAt(painted, 80, 25) == 145 && RedAt(painted, 80, 15) == 100 && RedAt(painted, 80, 100) == 100,
			"the bent fibre is painted along the curve: core and cladding at the apex, nothing on the straight line between the nodes");
		Check(RedAt(painted, 20, 99) == 150 && RedAt(painted, 20, 100) == 100 && RedAt(painted, 140, 99) == 150 && RedAt(painted, 140, 100) == 100,
			$"its flat ends are cut across the handles' direction: the row above a node is core, the node's own row background ({RedAt(painted, 20, 99)}, {RedAt(painted, 20, 100)}, {RedAt(painted, 140, 99)}, {RedAt(painted, 140, 100)})");
		var node = new FiberNode { OutX = 30, OutY = 0, InX = -10, InY = 0 };
		node.SetHandle(true, 0, 40);
		Check(node is { OutX: 0, OutY: 40, InX: 0, InY: -10 }, "a smooth node: setting the out-handle turns the in-handle opposite, its length kept");
		node.SetHandle(false, 6, 8, 20.0);
		Check(node is { InX: 6, InY: 8, OutX: -12, OutY: -16 }, "and the in-handle turns the out-handle, to the length given");
		node.Smooth = false;
		node.SetHandle(true, 5, 5);
		Check(node is { OutX: 5, OutY: 5, InX: 6, InY: 8 }, "a corner node leaves the other handle alone");
		var aligned = new FiberPart { Nodes = [new FiberNode { X = -100 }, new FiberNode { X = 0, Y = 50, InX = -10, OutY = 40, Smooth = false }, new FiberNode { X = 100 }] };
		aligned.Nodes[1].Smooth = true;
		Check(aligned.Nodes[1] is { InX: 0, InY: -10, OutX: 0, OutY: 40 }, "switching a node to smooth lines its in-handle up opposite the out-handle, both lengths kept");
		aligned.Nodes[1].InX = 60;
		Check(aligned.Nodes[1] is { InX: 60, InY: -10, OutX: -39, OutY: 7 }, "a typed in-handle of a smooth node turns the out-handle opposite, its length kept");
		var bare = new FiberNode { X = 0, Y = 50, Smooth = false };
		aligned.Nodes[1] = bare;
		bare.Smooth = true;
		Check(bare is { InX: -37, InY: 0, OutX: 37, OutY: 0 }, "a node without handles switched to smooth gets them from its neighbours: along the line between them, a third of the way to each");
		var twin = (FiberPart)aligned.Clone();
		twin.Nodes[1].OutY = 37;
		twin.Nodes[1].OutX = 0;
		Check(twin.Nodes[1] is { OutX: 0, OutY: 37, InX: 0, InY: -37 } && bare is { InX: -37, OutX: 37, OutY: 0 }, "a clone keeps its own nodes smooth, the original's untouched");
		var keeper = new Project();
		var kept = keeper.AddPart<FiberPart>();
		kept.Nodes.Add(new FiberNode { X = 150, Y = 30, InX = -60, OutX = 60, OutY = 1 });
		string keptXml;
		using (var stream = new MemoryStream())
		{
			keeper.Save(stream);
			keptXml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
		}
		Project keptBack;
		using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(keptXml)))
		{
			keptBack = Project.Load(stream);
		}
		Check(kept.Nodes[2] is { InX: -60, InY: 0, OutX: 60, OutY: 1 } && keptBack.Parts[0] is FiberPart { Nodes.Count: 3 } reloaded && reloaded.Nodes[2] is { InX: -60, InY: 0, OutX: 60, OutY: 1, Smooth: true },
			"a node added or loaded is kept as it is, a pixel off the line included: the smooth rule acts on edits only");
		var oddEnds = new FiberPart { Ends = (FiberEnds)7 };
		oddEnds.Normalize();
		Check(oddEnds.Ends == FiberEnds.Flat, "an undefined Ends value normalises to Flat");
		project.Parts.Clear();

		// the cavity: a mirror square with its inside painted back to index 1 keeps a wave in, nothing gets out
		const int cw = 64, ch = 48;
		project.Scene.Width = cw;
		project.Scene.Height = ch;
		var wall = project.AddPart<OutlinePart>();
		wall.X = 32;
		wall.Y = 24;
		wall.Mirror = true;
		foreach ((int px, int py) in new[] { (-16, -16), (16, -16), (16, 16), (-16, 16) })
		{
			wall.Points.Add(new PartPoint { X = px, Y = py });
		}
		var hollow = project.AddPart<OutlinePart>();
		hollow.X = 32;
		hollow.Y = 24;
		hollow.RefractiveIndex = 1.0;
		foreach ((int px, int py) in new[] { (-12, -12), (12, -12), (12, 12), (-12, 12) })
		{
			hollow.Points.Add(new PartPoint { X = px, Y = py });
		}
		ScenePixels cavity = PartPainter.Paint(null, cw, ch, 1.0, project.Parts)!;
		Check(RedAt(cavity, 32, 24) == 100 && RedAt(cavity, 32, 24 - 14) == 0 && RedAt(cavity, 32, 24 - 18) == 100, "a later part paints over an earlier one: the mirror square is hollowed to a 4 px ring");
		SceneData ring = SceneData.FromBgra(cavity.Bgra, cw, ch, cavity.StrideBytes, false, 0);
		var emitters = new List<Emitter> { Make(32, 24, 120, 1.0) };
		float[] field = RunCpu(ring, emitters, 300);
		float inside = 0f, outside = 0f;
		for (int y = 0; y < ch; y++)
		{
			for (int x = 0; x < cw; x++)
			{
				float value = Math.Abs(field[(y + 1) * (cw + 2) + x + 1]);
				if (Math.Abs(x - 32) <= 11 && Math.Abs(y - 24) <= 11)
				{
					inside = Math.Max(inside, value);
				}
				else if (Math.Abs(x - 32) > 17 || Math.Abs(y - 24) > 17)
				{
					outside = Math.Max(outside, value);
				}
			}
		}
		Check(inside > 0.01f && outside < inside * 1e-4f, $"the cavity keeps the wave after 300 steps: max |u| inside {inside}, outside {outside}");
		GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(ring, new EngineOptions(1.0, 1.0, false, false, 10.0, 1.0), emitters, out string problems);
		if (gpu == null)
		{
			Console.WriteLine("  skip GPU: " + problems);
		}
		else
		{
			using (gpu)
			{
				Run(gpu, 300);
				float diff = MaxDiff(field, gpu.FieldData);
				Check(diff < 1e-3f * Math.Max(1f, MaxAbs(field)), "GPU agrees on the painted cavity (max diff " + diff + ")");
			}
		}
		RunSpec spec = RunSpec.Create(project, cavity, preferGpu: false);
		Check(ReferenceEquals(spec.Image, cavity) && !spec.ImageSources && spec.Width == cw && spec.Height == ch, "a canvas scene with parts runs on the painted pixels, without image sources");
		Check(RunSpec.Create(project, new ScenePixels(10, 10, 40, new byte[400]), preferGpu: false).Image == null, "a painted canvas of another size than the scene is ignored");

		// every kind through the XML format, sparsely
		project.Scene.Width = W;
		project.Scene.Height = H;
		project.Parts.Clear();
		project.AddPart<LensPart>();
		var polygon = project.AddPart<OutlinePart>();
		polygon.Points.Add(new PartPoint { X = 3, Y = -4 });
		polygon.Mirror = true;
		project.AddPart<PolygonPart>();
		project.AddPart<RectanglePart>().Width = 7;
		var roundOne = project.AddPart<EllipsePart>();
		roundOne.Linked = true;
		roundOne.Width = 9;
		project.AddPart<TrianglePart>().Type = TriangleType.RightIsosceles;
		project.AddPart<FiberPart>();
		var reflector = project.AddPart<BraggReflectorPart>();
		reflector.Angle = 45;
		reflector.Index2 = 2.2;
		project.AddPart<GrinLensPart>();
		OpticalPart copy = project.Duplicate(reflector);
		Check(copy is BraggReflectorPart { Name: "Bragg reflector 1 copy", Angle: 45, Index2: 2.2 } && project.Parts.IndexOf(copy) == 8 && project.Parts.Count == 10, "Duplicate: a deep copy right after the original, named '… copy'");
		Check(project.Remove(copy) && project.Move(reflector, 0) && project.Parts[0] == reflector && project.Parts.Count == 9, "Remove and Move work on the parts list");
		project.AddPart<XorGatePart>().DesignFrequency = 80;
		string xml;
		using (var stream = new MemoryStream())
		{
			project.Save(stream);
			xml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
		}
		Check(xml.Contains("<BraggReflector Name=\"Bragg reflector 1\" X=\"100\" Y=\"60\" Angle=\"45\" Index2=\"2.2\" />") && xml.Contains("<Lens Name=\"Lens 1\" X=\"100\" Y=\"60\" />") && xml.Contains("<GrinLens Name=\"GRIN lens 1\" X=\"100\" Y=\"60\" />"),
			"the file holds one element per kind, attributes only where they differ from the defaults");
		Check(xml.Contains("<Outline Name=\"Outline 1\" X=\"100\" Y=\"60\" Mirror=\"true\">\r\n\t\t<Point X=\"3\" Y=\"-4\" />\r\n\t</Outline>".Replace("\r\n", Environment.NewLine)) || xml.Contains("<Outline Name=\"Outline 1\" X=\"100\" Y=\"60\" Mirror=\"true\">\n\t\t<Point X=\"3\" Y=\"-4\" />\n\t</Outline>"),
			"an outline's points are Point children");
		Check(xml.Contains("<Polygon Name=\"Polygon 1\" X=\"100\" Y=\"60\" />") && xml.Contains("<Rectangle Name=\"Rectangle 1\" X=\"100\" Y=\"60\" Width=\"7\" />") && xml.Contains("<Ellipse Name=\"Ellipse 1\" X=\"100\" Y=\"60\" Length=\"9\" Width=\"9\" Linked=\"true\" />") && xml.Contains("<Triangle Name=\"Triangle 1\" X=\"100\" Y=\"60\" Type=\"RightIsosceles\" AngleA=\"45\" AngleB=\"45\" />"),
			"the polygon, rectangle, ellipse and triangle elements, sparse");
		Check(xml.Contains("<XorGate Name=\"XOR gate 1\" X=\"100\" Y=\"60\" DesignFrequency=\"80\" />"), "the XOR gate element, sparse");
		Check(xml.IndexOf("<Source", StringComparison.Ordinal) < 0 && xml.IndexOf("<Lens", StringComparison.Ordinal) < xml.IndexOf("<Probe", StringComparison.Ordinal) || !xml.Contains("<Probe"), "the parts sit between the sources and the probes");
		Project back;
		using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)))
		{
			back = Project.Load(stream);
		}
		Check(back.Parts.Count == 10 && back.Parts.Select(p => p.GetType()).SequenceEqual([typeof(BraggReflectorPart), typeof(LensPart), typeof(OutlinePart), typeof(PolygonPart), typeof(RectanglePart), typeof(EllipsePart), typeof(TrianglePart), typeof(FiberPart), typeof(GrinLensPart), typeof(XorGatePart)])
			&& back.Parts[9] is XorGatePart { DesignFrequency: 80 }, "the ten kinds load back in order");
		// the class is the kind: every part class has its file element and is added by its type, so none is half wired
		Type[] partTypes = [.. typeof(OpticalPart).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(OpticalPart)) && !type.IsAbstract)];
		HashSet<Type?> elements = [.. typeof(Project).GetProperty(nameof(Project.Parts))!.GetCustomAttributes<XmlElementAttribute>().Select(element => element.Type)];
		var adder = new Project();
		var placed = new Project();
		placed.Scene.Width = 400;
		placed.Scene.Height = 300;
		Check(placed.AddSource() is { X: 200, Y: 150 } && placed.AddProbe() is { AX: 200, AY: 150, BX: 300, BY: 150 } && placed.AddPart<LensPart>() is { X: 200, Y: 150 }
			&& placed.AddSource(10, 20) is { X: 10, Y: 20, Elements: [{ X: 10, Y: 20 }] } && placed.AddProbe(30, 40) is { AX: 30, AY: 40 } && placed.AddPart(typeof(LensPart), 50, 60) is { X: 50, Y: 60 },
			"a new source, probe or part lands at the scene's centre, or where the application puts it (the canvas it shows); the source's emitter with it");
		Check(partTypes.Length > 0 && partTypes.All(type => elements.Contains(type) && adder.AddPart(type) is { } added && added.GetType() == type && added.Name == added.NameStem + " 1"),
			$"each of the {partTypes.Length} part classes has its file element and is added by its type as '<stem> 1'");
		Check(xml.Contains("<Node X=\"-100\" Y=\"0\" OutX=\"67\" />") && xml.Contains("<Node X=\"100\" Y=\"0\" InX=\"-67\" />"), "a fibre's nodes are sparse Node children: handles only where set, Smooth only when off");
		Check(back.Parts[7] is FiberPart { Nodes.Count: 2, Ends: FiberEnds.Flat } loaded && loaded.Nodes[0] is { X: -100, OutX: 67, Smooth: true } && loaded.Nodes[1] is { X: 100, InX: -67 }, "a loaded fibre has its two nodes with their handles, not the default path on top of them");
		Check(back.Parts[2] is OutlinePart { Mirror: true, Points.Count: 1 } && back.Parts[0] is BraggReflectorPart { Angle: 45, Index2: 2.2, Pairs: 10 } && back.Parts[4] is RectanglePart { Width: 7, Length: 60 } && back.Parts[5] is EllipsePart { Width: 9, Length: 9, Linked: true } && back.Parts[6] is TrianglePart { Type: TriangleType.RightIsosceles, AngleB: 45 },
			"a loaded outline keeps its point and mirror flag, the reflector its angle and index, the rectangle its width, the triangle its type");
		var watched = new ProjectWatcher(back);
		var changes = new List<ProjectChange>();
		watched.Changed += (_, change) => changes.Add(change);
		((OutlinePart)back.Parts[2]).Points[0].X = 7;
		((OutlinePart)back.Parts[2]).Points.Add(new PartPoint());
		back.Parts[1].Angle = 10;
		Check(changes.Count == 3 && changes.All(c => c.AffectsSimulation) && !changes.Any(c => c.AffectsProbes), "the watcher reports a point edit, a point added and a part edit, each as a simulation edit");
		watched.Dispose();
	}

	private static int BlueAt(ScenePixels pixels, int x, int y) => pixels.Bgra[y * pixels.StrideBytes + x * 4];

	/// <summary>
	/// The XOR gate (2026-10-21): the mode solver against a measured straight guide, the design scaling with the wavelength,
	/// the painted layout, and the gate itself driven by sources on its inputs (on the GPU when there is one, the CPU is
	/// slow at this size): one input splits about evenly, both cancel at the XOR output and double at the sum output.
	/// </summary>
	private static void CheckXorGate()
	{
		Console.WriteLine("--- XOR gate");
		// β of a straight guide (core 1.6, 8 px, cladding 1.45 with an absorbing rim, as the gate paints it), solver and engine
		const double lambda = 16.0;
		{
			int w = 600, h = 120, cy = 60;
			var clad = new RectanglePart { Name = "k", X = w / 2, Y = cy, Length = w + 40, Width = 8 + 2 * 49, RefractiveIndex = 1.45 };
			var core = new RectanglePart { Name = "c", X = w / 2, Y = cy, Length = w + 40, Width = 8, RefractiveIndex = 1.6 };
			ScenePixels px = PartPainter.Paint(null, w, h, 1.0, [clad, core])!;
			for (int y = 0; y < h; y++)
			{
				double depth = Math.Abs(y + 0.5 - cy) - 21.0;
				for (int x = 0; x < w && depth > 0 && depth < 32; x++)
				{
					px.Bgra[y * px.StrideBytes + x * 4] = (byte)Math.Round(64 * (depth / 32) * (depth / 32));
				}
			}
			var source = Make(20, cy, 0, 1.0) with { Level = 255.0 * 4.0 * Math.PI / (lambda * 2.0) };
			var sim = new WaveSimulator(SceneData.FromBgra(px.Bgra, w, h, px.StrideBytes, false, 0, true), new EngineOptions(2.0, 1.0, false, true, 10.0, 1.0), [source]);
			double omega = 2.0 * Math.PI / lambda;
			var lockIn = new System.Numerics.Complex[2];
			int steps = 1100, window = 320;
			for (int s = 0; s < steps; s++)
			{
				sim.AdvanceOneStep();
				if (s >= steps - window)
				{
					sim.PrepareFrame(true, false);
					var phase = System.Numerics.Complex.FromPolarCoordinates(1.0, omega * s);
					lockIn[0] += sim.FieldData[(cy + 1) * (w + 2) + 150 + 1] * phase;
					lockIn[1] += sim.FieldData[(cy + 1) * (w + 2) + 450 + 1] * phase;
				}
			}
			// the phase grows by β per pixel along the guide; 300 px of it, known up to whole turns, the β near 0.6
			double turn = 2.0 * Math.PI / 300.0;
			double measured = (lockIn[1].Phase - lockIn[0].Phase) / 300.0;
			measured += turn * Math.Round((0.6 - measured) / turn);
			double[] rows = WaveguideModes.Rows(-57, 114, y => Math.Abs(y) <= 4 ? 1.6 : Math.Abs(y) <= 53 ? 1.45 : 1.0);
			double? solved = WaveguideModes.Beta(rows, WaveguideModes.Omega(lambda), false);
			Check(solved is { } beta && Math.Abs(beta - measured) < 2e-3, $"the mode solver's β {solved:0.00000} matches the engine's guide, {measured:0.00000} rad/px");
		}

		// the design follows the wavelength
		LogicGateDesign? short_ = LogicGateDesign.For(12, 1.6, 1.45, 64, 90), standard = LogicGateDesign.For(16, 1.6, 1.45, 64, 90), long_ = LogicGateDesign.For(32, 1.6, 1.45, 64, 90);
		Check(short_ != null && standard != null && long_ != null && short_.CoreWidth < standard.CoreWidth && standard.CoreWidth < long_.CoreWidth
			&& new[] { short_, standard, long_ }.All(d => d.PortSpacing % 2 == 0 && d.CoupledSpacing % 2 == 0 && d.CouplerLength > 0)
			&& long_.Length > standard.Length && long_.PortSpacing > 64,
			$"the core widens with the wavelength ({short_?.CoreWidth}, {standard?.CoreWidth}, {long_?.CoreWidth} px at 12, 16, 32), even spacings, the ports pushed apart when the design needs it");
		Check(new XorGatePart().Design is { Length: < 650 } && new XorGatePart { DesignFrequency = 100, CoreIndex = 2.0 }.Design is { Length: < 500 },
			$"the gate is short: {new XorGatePart().Design?.Length} px by default (frequency 50, λ 32, core 2.55), {new XorGatePart { DesignFrequency = 100, CoreIndex = 2.0 }.Design?.Length} px at frequency 100 (λ 16) with a core of 2.0");
		Check(Math.Abs(new XorGatePart().Wavelength - Simulation.Wavelength(50, 1.0, 2.0)) < 1e-12 && Math.Abs(new XorGatePart { FrequencyScale = 4.0 }.Wavelength - new XorGatePart().Wavelength / 2.0) < 1e-9,
			$"the wavelength follows from the design frequency and the frequency scale: {new XorGatePart().Wavelength:0.00} px at 50, half at twice the scale");
		Check(LogicGateDesign.For(16, 1.45, 1.45, 64, 90) == null && new XorGatePart { CoreIndex = 1.4 }.Ports() == null, "no design when the core does not guide (core index not above the cladding's)");
		Check(LogicGateDesign.For(32, 2.55, 1.45, 2, 90) is { PhaseOnArmA: true, PhaseIndex: 2.45, PhaseLength: > 0 and < 200 } && LogicGateDesign.For(32, 2.0, 1.45, 2, 90) is { PhaseOnArmA: false, PhaseIndex: 2.1 },
			"a core at the largest index puts its phase section on arm A, lowered; below it the section raises arm B");
		Check(new[] { new XorGatePart(), new XorGatePart { PortSpacing = 120 }, new XorGatePart { DesignFrequency = 100, CoreIndex = 2.0 } }
				.All(g => g.Design is { } d && d.HalfWidth == d.PortSpacing && Math.Abs(g.Outline().Max(p => Math.Abs(p.Y)) - d.PortSpacing) < 1e-9 && d.AbsorberWidth >= LogicGateDesign.MinAbsorber * d.Wavelength - 1e-9),
			$"the unit is twice the port spacing wide, its ports at a quarter and three quarters ({new XorGatePart().Design!.PortSpacing} px spacing, {2 * new XorGatePart().Design!.PortSpacing} px wide by default)");
		{
			// staggered tiling: two gates a port spacing above and below, one length on, take one output each, and touch
			var first = new XorGatePart { X = 400, Y = 300 };
			int s = first.Design!.PortSpacing, length = first.Design.Length;
			var upper = new XorGatePart { X = 400 + length, Y = 300 - s };
			var lower = new XorGatePart { X = 400 + length, Y = 300 + s };
			Check(first.Ports()![2].Y == upper.Ports()![1].Y && first.Ports()![3].Y == lower.Ports()![0].Y
				&& upper.Outline().Max(p => upper.ToCanvas(p.X, p.Y).Y) == lower.Outline().Min(p => lower.ToCanvas(p.X, p.Y).Y),
				"gates set a port spacing above and below the next column: the XOR output meets the upper gate's input B, the sum output the lower gate's input A, and the two touch");
		}
		var odd = new XorGatePart { PortSpacing = 121 };
		Check(new XorGatePart { PortSpacing = 20 }.Design!.PortSpacing == new XorGatePart().Design!.PortSpacing && new XorGatePart { PortSpacing = 120 }.Design!.PortSpacing == 120
			&& odd.Design!.PortSpacing == 122 && odd.PortSpacing == 121,
			$"a port spacing below the design's minimum ({new XorGatePart().Design!.PortSpacing} px) is raised to it, a larger one is used as typed, an odd one rounded up to even with the typed value kept");
		Check(LogicGateDesign.BendShare(0) == 0 && Math.Abs(LogicGateDesign.BendShare(1) - 1) < 1e-12 && Math.Abs(LogicGateDesign.BendShare(0.5) - 0.5) < 1e-12,
			"the S-bend runs from the ports' offset to the coupler's, symmetric about its middle");

		// the painted layout of the default gate (core 2.55, cladding 1.45, λ 32; the phase section lowered on arm A)
		var gate = new XorGatePart { Name = "g" };
		LogicGateDesign design = gate.Design!;
		// room on both sides: the open input ends let a source's backward wave out, and the canvas edge must not send it back
		int width = design.Length + 400, height = (int)(2 * design.HalfWidth) + 200;
		gate.X = width / 2;
		gate.Y = height / 2;
		ScenePixels painted = PartPainter.Paint(null, width, height, 1.0, [gate])!;
		(int X, int Y)[] ports = gate.Ports()!;
		int phaseX = gate.X + (int)((design.PhaseSection.Start + design.PhaseSection.End) / 2);
		int end = gate.X + design.Length / 2 - 1;
		Check(RedAt(painted, ports[0].X + 2, ports[0].Y) == 255 && RedAt(painted, ports[0].X + 2, ports[0].Y - design.CoreWidth / 2 - 3) == 145
			&& RedAt(painted, phaseX, ports[0].Y) == 245 && RedAt(painted, phaseX, ports[1].Y) == 255 && RedAt(painted, ports[2].X, ports[2].Y) == 255 && RedAt(painted, gate.X, 2) == 100,
			"cores of 2.55 in a cladding of 1.45, the phase section of 2.45 on arm A only, the scene untouched beyond");
		int start = gate.X - design.Length / 2 + 1;
		Check(RedAt(painted, end, ports[2].Y) == 160 && RedAt(painted, end, ports[2].Y - design.CoreWidth / 2 - 3) == 120 && RedAt(painted, start, ports[0].Y) == 160
			&& RedAt(painted, end - gate.Coating - 1, ports[2].Y) == 255,
			"both ends carry the AR band: √2.55 on the cores, √1.45 on the cladding, the coating's thickness deep");
		gate.UpperOutputActive = false;
		ScenePixels switched = PartPainter.Paint(null, width, height, 1.0, [gate])!;
		gate.UpperOutputActive = true;
		Check(BlueAt(switched, end, ports[2].Y) > 50 && BlueAt(switched, end, ports[3].Y) == 0 && BlueAt(switched, ports[2].X, ports[2].Y) == 0
			&& BlueAt(switched, end - design.OutputRampLength / 2, ports[2].Y) is > 5 and < 30,
			$"a switched-off XOR output absorbs in its arm's last stretch, rising softly ({BlueAt(switched, end - design.OutputRampLength / 2, ports[2].Y)} halfway, {BlueAt(switched, end, ports[2].Y)} at the end); the sum arm and the port stay clear");
		Check(BlueAt(painted, ports[0].X + 2, ports[0].Y) == 0 && BlueAt(painted, gate.X, gate.Y) == 0 && BlueAt(painted, ports[2].X, ports[2].Y) == 0
			&& BlueAt(painted, gate.X - design.Length / 2 + 1, ports[0].Y) == 0 && BlueAt(painted, ports[2].X, ports[2].Y - (int)(design.HalfWidth - design.PortSpacing / 2.0) + 1) > 50,
			"no absorption along the cores, the coupler, the outputs or the open input ends; the cladding's rim absorbs");

		// driven by sources on its inputs
		SceneData scene = SceneData.FromBgra(painted.Bgra, width, height, painted.StrideBytes, false, 0, true);
		double level = gate.DesignFrequency;
		(double Xor, double Sum, double Past) Outputs(List<Emitter> emitters, SceneData? other = null)
		{
			SceneData run = other ?? scene;
			var options = new EngineOptions(2.0, 1.0, false, true, 10.0, 1.0);
			IWaveEngine engine = GpuWaveSimulator.TryCreate(run, options, emitters, out _) is { } gpu ? gpu : new WaveSimulator(run, options, emitters);
			// the wave crosses the gate in about 1.6 steps a pixel; the rest lets it settle into a steady state; ten periods averaged
			int lambda = (int)Math.Round(design.Wavelength);
			int steps = 2 * design.Length + 60 * lambda, window = 10 * lambda, half = design.CoreWidth / 2 + 2;
			int beyond = end + lambda;
			double xor = 0, sum = 0, past = 0;
			for (int s = 0; s < steps; s++)
			{
				engine.AdvanceOneStep();
				if (s >= steps - window)
				{
					engine.PrepareFrame(true, false);
					float[] u = engine.FieldData;
					// over the core and half a wavelength along it
					for (int dx = -lambda / 2; dx < lambda / 2; dx++)
					{
						for (int dy = -half; dy <= half; dy++)
						{
							float a = u[(ports[2].Y + dy + 1) * (width + 2) + ports[2].X + dx + 1];
							float b = u[(ports[3].Y + dy + 1) * (width + 2) + ports[3].X + dx + 1];
							xor += a * a;
							sum += b * b;
						}
					}
					// what leaves the outputs: a column a wavelength past the right end, a quarter of the unit's width about each
					// output (the open input ends let a source's backward wave into the scene, which passes the gate outside)
					foreach (int py in new[] { ports[2].Y, ports[3].Y })
					{
						for (int y = py - design.PortSpacing / 2; y < py + design.PortSpacing / 2; y++)
						{
							float c = u[(y + 1) * (width + 2) + beyond + 1];
							past += c * c;
						}
					}
				}
			}
			(engine as IDisposable)?.Dispose();
			return (xor, sum, past);
		}
		var sw = System.Diagnostics.Stopwatch.StartNew();
		(double xorA, double sumA, double pastA) = Outputs([Make(ports[0].X, ports[0].Y, 0, 1.0) with { Level = level }]);
		(double xorAB, double sumAB, _) = Outputs([Make(ports[0].X, ports[0].Y, 0, 1.0) with { Level = level }, Make(ports[1].X, ports[1].Y, 0, 1.0) with { Level = level }]);
		double single = xorA + sumA;
		Check(Math.Abs(xorA / single - 0.5) < 0.1, $"one input splits about evenly: {xorA / single:0.000} to the XOR output");
		Check(xorAB / single < 0.03 && sumAB / single > 1.8, $"both inputs: the XOR output keeps {xorAB / single:0.000} of one input's power, the sum output {sumAB / single:0.00}× ({sw.ElapsedMilliseconds} ms)");
		gate.UpperOutputActive = false;
		gate.LowerOutputActive = false;
		ScenePixels closed = PartPainter.Paint(null, width, height, 1.0, [gate])!;
		gate.UpperOutputActive = true;
		gate.LowerOutputActive = true;
		(_, _, double pastClosed) = Outputs([Make(ports[0].X, ports[0].Y, 0, 1.0) with { Level = level }], SceneData.FromBgra(closed.Bgra, width, height, closed.StrideBytes, false, 0, true));
		Check(pastClosed < 0.01 * pastA, $"both outputs switched off: {pastClosed / pastA:0.0000} of the light that left the open gate gets out");

		// AND and OR (2026-10-22): the same unit with another phase shift, the same dimensions and ports
		foreach (LogicGatePart other in new LogicGatePart[] { new AndGatePart { X = gate.X, Y = gate.Y }, new OrGatePart { X = gate.X, Y = gate.Y } })
		{
			Check(other.Design!.Length == design.Length && other.Design.PortSpacing == design.PortSpacing && other.Ports()!.SequenceEqual(ports),
				$"the {other.NameStem} has the XOR gate's dimensions and ports ({2 * other.Design.PortSpacing} × {other.Design.Length} px; phase section {other.Design.PhaseLength} of {other.Design.PhaseRegionLength} px on arm {(other.Design.PhaseOnArmA ? "A" : "B")})");
			ScenePixels otherPixels = PartPainter.Paint(null, width, height, 1.0, [other])!;
			SceneData otherScene = SceneData.FromBgra(otherPixels.Bgra, width, height, otherPixels.StrideBytes, false, 0, true);
			(double upperA, double lowerA, _) = Outputs([Make(ports[0].X, ports[0].Y, 0, 1.0) with { Level = level }], otherScene);
			(double upperAB, double lowerAB, _) = Outputs([Make(ports[0].X, ports[0].Y, 0, 1.0) with { Level = level }, Make(ports[1].X, ports[1].Y, 0, 1.0) with { Level = level }], otherScene);
			double one = upperA + lowerA;
			string table = $"one input {upperA / one:0.00} up / {lowerA / one:0.00} down, both {upperAB / one:0.00} up / {lowerAB / one:0.00} down";
			if (other is AndGatePart)
			{
				Check(upperAB > 3.0 * upperA && lowerAB < 0.03 * one, "AND: the upper output 3 to 4 × as bright with both inputs as with one, the lower (XOR) dark: " + table);
			}
			else
			{
				Check(upperAB > 0.8 * upperA && upperAB < 1.2 * upperA && lowerAB > 2.4 * lowerA, "OR: the upper output as bright with both inputs as with one, the lower about 3 ×: " + table);
			}
		}
	}

	/// <summary>
	/// The zone plate (2026-10-22): the zone radii, the painted blocks (blue only, rising from the faces, sharp at the sides),
	/// the inverse, the frequency scale handed over, the file element, and a plane wave focused near the focal length (on the
	/// GPU when there is one).
	/// </summary>
	private static void CheckZonePlate()
	{
		Console.WriteLine("--- zone plate");
		var plate = new ZonePlatePart { Name = "z", Diameter = 480, FocalLength = 360 };
		double lambda = plate.Wavelength, f = plate.FocalLength;
		double Radius(int n) => Math.Sqrt(n * lambda * f + n * lambda / 2.0 * (n * lambda / 2.0));
		Check(plate.ZoneAt(Radius(1) - 0.5) == 0 && plate.ZoneAt(Radius(1) + 0.5) == 1 && plate.ZoneAt(Radius(3) + 0.5) == 3
			&& !plate.IsBlockAt(0) && plate.IsBlockAt(Radius(1) + 1) && !plate.IsBlockAt(Radius(2) + 1) && plate.ZoneCount == plate.ZoneAt(240) + 1,
			$"the zones change at rₙ = √(nλf + (nλ/2)²) ({Radius(1):0.0}, {Radius(2):0.0}, {Radius(3):0.0} px), the central one open, {plate.ZoneCount} within the diameter");
		plate.Inverse = true;
		Check(plate.IsBlockAt(0) && !plate.IsBlockAt(Radius(1) + 1), "inverse: the central zone a block, the first ring open");
		plate.Inverse = false;

		const int width = 960, height = 640, cx = 240, cy = 320;
		plate.X = cx;
		plate.Y = cy;
		ScenePixels painted = PartPainter.Paint(null, width, height, 1.0, [plate])!;
		int blockY = cy + (int)Math.Round((Radius(1) + Radius(2)) / 2.0);
		Check(BlueAt(painted, cx, blockY) >= ZonePlatePart.BlockAbsorption - 4 && BlueAt(painted, cx, cy) == 0 && BlueAt(painted, cx - plate.Thickness / 2, blockY) < 10
			&& BlueAt(painted, cx - plate.Thickness / 4, blockY) is > 10 and < 60 && RedAt(painted, cx, blockY) == 100 && BlueAt(painted, cx, cy + (int)Radius(1) - 2) == 0 && BlueAt(painted, cx, cy + (int)Radius(1) + 2) > 50,
			$"a block absorbs {ZonePlatePart.BlockAbsorption} in its middle, rising from its faces ({BlueAt(painted, cx - plate.Thickness / 4, blockY)} halfway in), sharp at its sides, the index untouched; the central zone clear");

		var project = new Project();
		project.Parts.Add(plate);
		using (new GatePortFollower(project))
		{
			project.Simulation.SourceFrequencyScale = 4.0;
			Check(plate.FrequencyScale == 4.0 && Math.Abs(plate.Wavelength - lambda / 2.0) < 1e-9, "the simulation's frequency scale reaches the zone plate, halving its wavelength at twice the scale");
			project.Simulation.SourceFrequencyScale = 2.0;
		}
		string xml;
		using (var stream = new MemoryStream())
		{
			project.Save(stream);
			xml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
		}
		Check(xml.Contains("<ZonePlate Name=\"z\" X=\"240\" Y=\"320\" Diameter=\"480\" FocalLength=\"360\" />"), "the zone plate element, sparse");

		// a plane wave from a line of emitters, focused behind the plate
		var emitters = new List<Emitter>();
		for (int y = 10; y < height - 10; y++)
		{
			emitters.Add(Make(60, y, 0, 0.05) with { Level = plate.DesignFrequency });
		}
		double[] Axis(ScenePixels? pixels)
		{
			SceneData scene = pixels != null ? SceneData.FromBgra(pixels.Bgra, width, height, pixels.StrideBytes, false, 0, true) : SceneData.Empty(width, height, 0, 1.0, true);
			var options = new EngineOptions(2.0, 1.0, false, true, 10.0, 1.0);
			IWaveEngine engine = GpuWaveSimulator.TryCreate(scene, options, emitters, out _) is { } gpu ? gpu : new WaveSimulator(scene, options, emitters);
			int steps = 1600, window = (int)Math.Round(10 * lambda);
			var lockIn = new System.Numerics.Complex[width];
			for (int s = 0; s < steps; s++)
			{
				engine.AdvanceOneStep();
				if (s >= steps - window)
				{
					engine.PrepareFrame(true, false);
					var phase = System.Numerics.Complex.FromPolarCoordinates(1.0, 2.0 * Math.PI / lambda * s);
					for (int x = 0; x < width; x++)
					{
						lockIn[x] += engine.FieldData[(cy + 1) * (width + 2) + x + 1] * phase;
					}
				}
			}
			(engine as IDisposable)?.Dispose();
			return [.. lockIn.Select(c => c.Magnitude * c.Magnitude)];
		}
		var sw = System.Diagnostics.Stopwatch.StartNew();
		double[] focused = Axis(painted), free = Axis(null);
		int peak = Enumerable.Range(cx + 100, width - cx - 140).MaxBy(x => focused[x]);
		// the light that passes outside the diameter meets the focused light on the axis and pushes the peak on: measured
		// 2026-10-22, 5 zones 30 % beyond the focal length, 8 zones 3 %; an absorbing surround brought the 5 back to 6 %
		Check(peak - cx > 0.95 * f && peak - cx < 1.35 * f && focused[peak] > 2.0 * free[peak],
			$"a plane wave comes to a focus {peak - cx} px behind the plate (focal length {f}; the light passing outside pushes it on), {focused[peak] / free[peak]:0.0} × as bright as without it ({sw.ElapsedMilliseconds} ms)");

		// the mount: absorbing beyond the diameter, so the focus comes back toward the focal length
		Check(BlueAt(painted, cx, cy + 240 + 30) == 0 && plate.Outline().Max(p => p.Y) == 240, "without a mount nothing absorbs beyond the diameter");
		plate.Mount = true;
		plate.MountWidth = 120;
		ScenePixels mounted = PartPainter.Paint(null, width, height, 1.0, [plate])!;
		Check(BlueAt(mounted, cx, cy + 240 + 30) >= ZonePlatePart.BlockAbsorption - 4 && BlueAt(mounted, cx, cy - 240 - 30) >= ZonePlatePart.BlockAbsorption - 4
			&& BlueAt(mounted, cx, cy) == 0 && plate.Outline().Max(p => p.Y) == 360,
			"the mount absorbs on both sides beyond the diameter, the outline grown by its width");
		double[] withMount = Axis(mounted);
		int mountedPeak = Enumerable.Range(cx + 100, width - cx - 140).MaxBy(x => withMount[x]);
		Check(Math.Abs(mountedPeak - cx - f) < Math.Abs(peak - cx - f) && mountedPeak - cx < 1.15 * f && withMount[mountedPeak] > 2.0 * free[mountedPeak],
			$"with the mount the focus lies {mountedPeak - cx} px behind the plate (focal length {f}), {withMount[mountedPeak] / free[mountedPeak]:0.0} × as bright as without a plate");
		using (var stream = new MemoryStream())
		{
			project.Save(stream);
			xml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
		}
		Check(xml.Contains("<ZonePlate Name=\"z\" X=\"240\" Y=\"320\"") && xml.Contains(" Mount=\"true\"") && xml.Contains(" MountWidth=\"120\""), "the mount in the file");
	}

	/// <summary>Sources on a gate's inputs follow the gate (2026-10-21): spacing, position, angle, a group move, a gate added later; others stay.</summary>
	private static void CheckGatePortFollower()
	{
		var project = new Project();
		var gate = project.AddPart<XorGatePart>();
		(int X, int Y)[] ports = gate.Ports()!;
		var a = new Source { Name = "A", X = ports[0].X, Y = ports[0].Y };
		var b = new Source { Name = "B", X = ports[1].X + 1, Y = ports[1].Y - 1 };
		var away = new Source { Name = "away", X = 100, Y = 100 };
		project.Sources.Add(a);
		project.Sources.Add(b);
		project.Sources.Add(away);
		bool OnInputs() => gate.Ports() is { } p && (a.X, a.Y) == p[0] && (b.X, b.Y) == p[1];
		using (new GatePortFollower(project))
		{
			gate.PortSpacing = 100;
			Check(OnInputs() && gate.Ports()![1].Y - gate.Ports()![0].Y == 100 && (away.X, away.Y) == (100, 100),
				"a wider port spacing takes the sources on the inputs along (one a pixel off its port), not the one elsewhere");
			gate.X += 40;
			gate.Angle = 90;
			gate.DesignFrequency = 70;
			Check(OnInputs(), $"they follow a move, a turn and a new design frequency: A ({a.X}, {a.Y}), B ({b.X}, {b.Y})");
			project.Simulation.SourceFrequencyScale = 2.5;
			Check(gate.FrequencyScale == 2.5 && OnInputs(), "the simulation's frequency scale reaches the gate, and the sources follow the ports it moves");
			(int X, int Y) bBefore = (b.X, b.Y);
			Movable.MoveAll([gate, a], 7, -3);
			Check(OnInputs() && (b.X, b.Y) == (bBefore.X + 7, bBefore.Y - 3), "a group move of the gate with A moves A once, and B follows the gate");
			var second = project.AddPart<XorGatePart>();
			second.Y = 200;
			var c = new Source { Name = "C", X = second.Ports()![0].X, Y = second.Ports()![0].Y };
			project.Sources.Add(c);
			second.PortSpacing = 80;
			Check((c.X, c.Y) == second.Ports()![0], "a gate added later is followed too");
			// a gate taken out of the list is let go; one whose cores guide nothing has no ports, so nothing follows until it guides again
			project.Parts.Remove(second);
			(int X, int Y) cBefore = (c.X, c.Y);
			second.PortSpacing = 120;
			Check((c.X, c.Y) == cBefore, "a gate removed from the project moves nothing");
			(int X, int Y) aBefore = (a.X, a.Y);
			gate.CoreIndex = 1.0;
			gate.X += 10;
			Check(gate.Ports() == null && (a.X, a.Y) == aBefore, "a gate whose cores guide nothing has no ports: its sources stay");
			gate.CoreIndex = 2.55;
			gate.X += 10;
			Check(gate.Ports() != null && (a.X, a.Y) == aBefore, "guiding again, it starts from the ports it has then; the sources it left behind are not on them");
		}
		gate.PortSpacing = 140;
		Check(!OnInputs(), "after Dispose nothing follows");
	}

	private static void CheckAbsorbingParts()
	{
		Console.WriteLine("--- absorbing parts");
		const int W = 200, H = 120;
		var project = new Project();
		project.Scene.Width = W;
		project.Scene.Height = H;
		// a 100 × 100 square of index 1 centred on the corner of pixel (100, 60): x = 50..149, y = 10..109
		var square = project.AddPart<RectanglePart>();
		square.X = 100;
		square.Y = 60;
		square.Length = 100;
		square.Width = 100;
		square.RefractiveIndex = 1.0;
		ScenePixels painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(square.Absorption == 0 && square.AbsorptionRamp == OpticalPart.DefaultAbsorptionRamp && painted.Bgra.Where((_, i) => i % 4 == 0).All(b => b == 0), "a part without absorption paints no blue");
		square.Absorption = 200;
		square.AbsorptionRamp = 20;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		int Expected(double depth) => (int)Math.Round(200 * PartShape.AbsorptionProfile(depth / 20));
		Check(BlueAt(painted, 49, 60) == 0 && BlueAt(painted, 50, 60) == Expected(0.5) && BlueAt(painted, 59, 60) == Expected(9.5) && BlueAt(painted, 69, 60) == Expected(19.5) && BlueAt(painted, 70, 60) == 200 && BlueAt(painted, 100, 60) == 200 && BlueAt(painted, 149, 60) == Expected(0.5),
			"absorption 200 over a 20 px ramp: the profile of the depth at the pixel centre, full from 20 px in, nothing outside (" + string.Join(" ", new[] { 50, 59, 69, 70 }.Select(x => BlueAt(painted, x, 60))) + ")");
		Check(RedAt(painted, 100, 60) == 100 && RedAt(painted, 50, 60) == 100 && painted.Bgra.Where((_, i) => i % 4 == 1).All(g => g == 0), "the index stays as set (red 100) and green untouched");
		Check(PartShape.AbsorptionProfile(0) == 0 && PartShape.AbsorptionProfile(1) == 1 && PartShape.AbsorptionProfile(2) == 1 && PartShape.AbsorptionProfile(0.3) < PartShape.AbsorptionProfile(0.6), "the profile rises from 0 at the outline to 1 at the ramp's end");
		// the image's blue and an earlier part's: the larger value is kept
		(byte[] bgra, int stride) = ScenePixels(W, H);
		bgra[60 * stride + 51 * 4] = 230;   // strong damping in the ramp
		bgra[60 * stride + 100 * 4] = 10;   // weak damping in the full part
		bgra[5 * stride + 5 * 4] = 77;      // outside
		ScenePixels over = PartPainter.Paint(new ScenePixels(W, H, stride, bgra), W, H, 1.0, project.Parts)!;
		Check(BlueAt(over, 51, 60) == 230 && BlueAt(over, 100, 60) == 200 && BlueAt(over, 5, 5) == 77, "over the image's blue the larger value wins: 230 stays in the ramp, 10 becomes 200, 77 outside stays");
		// a part thinner than twice the ramp reaches its full absorption in its middle
		square.Length = 10;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		int deepest = painted.Bgra.Where((_, i) => i % 4 == 0).Max(b => (int)b);
		Check(deepest == 200, "a 10 px thin part with a 20 px ramp: the ramp is cut to its middle, which gets the full 200 (" + deepest + ")");
		square.Length = 100;
		square.Mirror = true;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(painted.Bgra.Where((_, i) => i % 4 == 0).All(b => b == 0), "a mirror ignores its absorption");
		square.Mirror = false;
		// the lens carries the same two settings
		project.Remove(square);
		var lens = project.AddPart<LensPart>();
		lens.X = 100;
		lens.Y = 60;
		lens.Diameter = 100;
		lens.Thickness = 40;
		lens.Absorption = 120;
		lens.AbsorptionRamp = 10;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		Check(BlueAt(painted, 100, 60) == 120 && BlueAt(painted, 80, 60) == (int)Math.Round(120 * PartShape.AbsorptionProfile(0.05)) && BlueAt(painted, 79, 60) == 0 && RedAt(painted, 100, 60) == 150,
			"an absorbing lens: 120 inside, the ramp from its surface, glass of 1.5 still");
		// the file: attributes only where they differ from the defaults; Normalize clamps
		string xml;
		using (var stream = new MemoryStream())
		{
			project.Save(stream);
			xml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
		}
		Check(xml.Contains("Absorption=\"120\" AbsorptionRamp=\"10\""), "the lens's absorption and ramp are written as attributes");
		lens.AbsorptionRamp = OpticalPart.DefaultAbsorptionRamp;
		lens.Absorption = 0;
		using (var stream = new MemoryStream())
		{
			project.Save(stream);
			xml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
		}
		// the attributes themselves: the file's DTD declares both names (2026-10-31)
		Check(!xml.Contains("Absorption=\"") && !xml.Contains("AbsorptionRamp=\""), "at their defaults neither is written");
		lens.Absorption = 300;
		lens.AbsorptionRamp = 0;
		lens.Normalize();
		Check(lens.Absorption == 255 && lens.AbsorptionRamp == 1, "Normalize clamps the absorption to 255 and the ramp to at least 1");
		// CPU = GPU on an absorbing part
		lens.Absorption = 64;
		lens.AbsorptionRamp = 15;
		painted = PartPainter.Paint(null, W, H, 1.0, project.Parts)!;
		SceneData scene = SceneData.FromBgra(painted.Bgra, W, H, painted.StrideBytes, false, 0, true);
		var wave = new List<Emitter> { Make(20, 60, 120, 1.0) };
		float[] cpuField = RunCpu(scene, wave, 300);
		Check(scene.D[60 * W + 100] < 0.76f && scene.D[60 * W + 100] > 0.74f, "the engines read blue 64 as a damping of 1 − 64/255");
		GpuWaveSimulator? gpu = GpuWaveSimulator.TryCreate(scene, new EngineOptions(1.0, 1.0, false, false, 10.0, 1.0), wave, out string problems);
		if (gpu == null)
		{
			Console.WriteLine("  skip GPU: " + problems);
			return;
		}
		using (gpu)
		{
			Run(gpu, 300);
			float diff = MaxDiff(cpuField, gpu.FieldData);
			Check(diff < 1e-3f * Math.Max(1f, MaxAbs(cpuField)), "GPU agrees with the CPU through an absorbing lens (max diff " + diff + ")");
		}
	}

	private static void CheckBrightnessFit()
	{
		Console.WriteLine("--- brightness fit");
		// 10 000 samples: |v| = 0.001 × i for i in 0..9999 (sign alternating), one source cell of 50 among them
		var field = new Half[10_000];
		for (int i = 0; i < field.Length; i++)
		{
			field[i] = (Half)((i % 2 == 0 ? 1 : -1) * 0.001f * i);
		}
		field[1234] = (Half)50f;
		double? fit = BrightnessFit.For(field, SampleKind.Field);
		// the 99.5th percentile of |v| is about 9.95 (the source cell moves it by one rank), so the gain is about 0.1
		Check(fit is >= 0.099 and <= 0.102, "the field fit brings the 99.5th percentile of |value| to the palette's top, a source cell of 50 aside (" + fit + ")");
		var intensity = new Half[10_000];
		for (int i = 0; i < intensity.Length; i++)
		{
			intensity[i] = (Half)MathF.Sqrt(0.0001f * i); // as a 16-bit frame stores it, the square root
		}
		double? intensityFit = BrightnessFit.For(intensity, SampleKind.IntensityRoot);
		// √(0.0001 × 9949) = 0.9974, its inverse 1.003, three digits
		Check(intensityFit == 1.0, "the intensity fit works on the stored square root, as the renderer colours it (" + intensityFit + ")");
		var linearIntensity = new Half[10_000];
		for (int i = 0; i < linearIntensity.Length; i++)
		{
			linearIntensity[i] = (Half)(0.0001f * i); // as a run for an OpenEXR export of I stores it
		}
		Check(BrightnessFit.For(linearIntensity, SampleKind.Intensity) == intensityFit, "the fit of an intensity stored as I takes its square root, to the same brightness");
		Check(BrightnessFit.For(new Half[100], SampleKind.Field) == null && BrightnessFit.For([Half.NaN, Half.PositiveInfinity], SampleKind.Field) == null, "an empty frame has nothing to fit");
		Check(BrightnessFit.For([(Half)1e-6f, (Half)1e-6f], SampleKind.Field) == 1e5, "the fit is clamped to the model's range (1e5 at most)");
		double? rounded = BrightnessFit.For([(Half)0.03f, (Half)0.03f], SampleKind.Field);
		Check(rounded is { } r && r.ToString(CultureInfo.InvariantCulture).Length <= 4, "the fit is rounded to three significant digits (" + rounded + ")");
	}

	private static void CheckRun()
	{
		Console.WriteLine("--- run");
		var project = new Project();
		project.Scene.Width = 64;
		project.Scene.Height = 48;
		project.Scene.DampingEdge = 4;
		project.Simulation.Steps = 200;
		project.Simulation.VisualizeEvery = 2;
		Source source = project.AddSource();
		source.X = 20;
		source.Y = 24;
		source.Frequency = 120;
		Probe probe = project.AddProbe();
		probe.AX = 20;
		probe.AY = 24;
		Probe lineProbe = project.AddProbe();
		lineProbe.Kind = ProbeKind.Line;
		lineProbe.AX = 5;
		lineProbe.AY = 5;
		lineProbe.BX = 40;
		lineProbe.BY = 30;
		Probe integrated = project.AddProbe();
		integrated.Layer = ProbeLayer.Intensity;
		integrated.Measurement = ProbeMeasurement.IntegratedValue;
		integrated.Kind = ProbeKind.Line;
		integrated.AX = 10;
		integrated.AY = 30;
		integrated.BX = 50;
		integrated.BY = 30;
		integrated.StartAfter = 50;
		RunSpec spec = RunSpec.Create(project, null, preferGpu: false);
		Check(spec.FrameCount == 100 && spec.Layers == (FrameLayers.Field | FrameLayers.Intensity) && spec.Emitters.Length == 1 && spec.Probes.Length == 3, "spec: 100 frames, both layers (intensity preview on), 1 emitter, 3 probes");
		Check(ReferenceEquals(spec.Probes[0].Origin, probe) && ReferenceEquals(spec.Probes[1].Origin, lineProbe), "probe specs remember their model probe as the origin");

		string path = Path.Combine(Path.GetTempPath(), "library_check-run-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
		using (var store = FrameStore.Create(path, spec.Width, spec.Height, spec.Layers, spec.FrameCount, spec.VisualizeEvery))
		{
			var run = new SimulationRun(spec, store, null);
			var states = new List<RunState>();
			run.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(SimulationRun.State))
				{
					states.Add(run.State);
				}
			};
			int finished = 0;
			run.Finished += (_, _) => finished++;
			var stopwatch = Stopwatch.StartNew();
			run.Start();
			Check(run.Completion.Wait(30000), "run completes within 30 s");
			Console.WriteLine("  " + run.EngineDescription + ", " + run.Elapsed.TotalMilliseconds.ToString("0") + " ms");
			Check(run.State == RunState.Completed && finished == 1 && run.Error == null, "state Completed, Finished raised once");
			Check(states.SequenceEqual([RunState.Starting, RunState.Running, RunState.Completed]), "states: Starting, Running, Completed");
			Check(run.StepsDone == 200 && run.FrameCount == 100 && store.Count == 100, "200 steps, 100 frames in the store");
			Check(run.Traces[0].Series.Count == 200 && run.Traces[0].StepsSampled == 200, "probe trace has one sample per step");
			Check(run.Traces[1].Profiles.Count == 100 && run.Traces[1].Profiles.All(p => p.Length == 36) && run.Traces[1].ProfileLength > 42.0 && run.Traces[1].Series.Count == 0,
				"line probe trace has one profile per frame (36 pixels, length " + run.Traces[1].ProfileLength.ToString("0.0", CultureInfo.InvariantCulture) + "), no series");
			Check(run.Traces[1].Profiles[0].All(v => v == 0f) && run.Traces[1].Profiles[99].Any(v => v != 0f) && !ReferenceEquals(run.Traces[1].Profiles[0], run.Traces[1].Profiles[99]), "profiles: frame 0 quiet, frame 99 has a wave, distinct arrays");
			ProbeGraphData at = ProbeGraphData.At(run.Traces[0], 40, 20);
			Check(at.Count == 41 && at.StepsSampled == 41 && at.Profile == null, "graph data at frame 20 / step 40: 41 samples");
			ProbeGraphData lineAt = ProbeGraphData.At(run.Traces[1], 40, 20);
			Check(ReferenceEquals(lineAt.Profile, run.Traces[1].Profiles[20]) && ReferenceEquals(ProbeGraphData.At(run.Traces[1], 400, 200).Profile, run.Traces[1].Profiles[99]), "graph data of the line probe: frame 20's profile; the newest stands in beyond the recorded frames");
			Check(run.Traces[2].Profiles.Count == 100 && run.Traces[2].Profiles[20].All(v => v == 0f) && run.Traces[2].Profiles[99].Any(v => v > 0f) && run.Traces[2].Profiles[99].All(v => v >= 0f), "integrated line probe: nothing before its start step, non-negative sums after");
			// the scratch runner records the same probes on its way to a frame (the graph is live before a run, 2026-10-14)
			using (var scratch = new ScratchRunner(null))
			using (var arrived = new ManualResetEventSlim())
			{
				ScratchResult? result = null;
				scratch.Completed += (_, r) =>
				{
					result = r;
					arrived.Set();
				};
				scratch.Request(spec, 20);
				Check(arrived.Wait(30000) && result is { Snapshot: not null, Frame: 20 }, "scratch frame 20 computed");
				Check(result!.Traces.Length == 3 && result.Traces[0].Series.Count == 41 && result.Traces[0].StepsSampled == 41, "scratch traces: the point probe's 41 samples up to step 40");
				Check(result.Traces[0].Series.Values.AsSpan(0, 41).SequenceEqual(run.Traces[0].Series.Values.AsSpan(0, 41)), "scratch series equals the run's first 41 samples bit for bit");
				Check(result.Traces[1].Profiles.Count == 1 && result.Traces[1].Profiles[0].AsSpan().SequenceEqual(run.Traces[1].Profiles[20]), "scratch profile equals the run's frame 20 profile");
				// a second request on the kept engine, past the integrated probe's start step: its sums must start from zero again
				arrived.Reset();
				scratch.Request(spec, 60);
				Check(arrived.Wait(30000) && result is { Frame: 60 } && result.Traces[2].Profiles.Count == 1 && result.Traces[2].Profiles[0].AsSpan().SequenceEqual(run.Traces[2].Profiles[60]), "a second scratch frame (60): the integrated profile equals the run's, the kept engine started its sums afresh");
			}
			Check(run.EngineDescription.StartsWith("CPU"), "CPU engine used");
			var lastFrame = new Half[64 * 48];
			Check(store.CopyLayer(99, FrameLayer.Field, lastFrame) && (float)lastFrame[24 * 64 + 20] != 0f, "last frame has a field at the source");
			run.Dispose();
		}
		Check(!File.Exists(path), "store deleted after the run");

		Console.WriteLine("--- run: pause, stop, GPU preferred");
		project.Simulation.Steps = 100000;
		spec = RunSpec.Create(project, null, preferGpu: true);
		path = Path.Combine(Path.GetTempPath(), "library_check-run-" + Guid.NewGuid().ToString("N") + FrameCache.Extension);
		using (var store = FrameStore.Create(path, spec.Width, spec.Height, spec.Layers, spec.FrameCount, spec.VisualizeEvery))
		using (var run = new SimulationRun(spec, store, null))
		{
			run.Start();
			var wait = Stopwatch.StartNew();
			while (run.State != RunState.Running && wait.ElapsedMilliseconds < 20000)
			{
				Thread.Sleep(10);
			}
			Check(run.State == RunState.Running, "second run is running (" + run.EngineDescription + (run.EngineNote.Length > 0 ? "; " + run.EngineNote : "") + ")");
			Thread.Sleep(200);
			run.Pause();
			wait.Restart();
			while (run.State != RunState.Paused && wait.ElapsedMilliseconds < 5000)
			{
				Thread.Sleep(10);
			}
			int atPause = run.StepsDone;
			Thread.Sleep(200);
			Check(run.State == RunState.Paused && run.StepsDone == atPause && atPause > 0 && atPause < spec.Steps, "paused at step " + atPause + " and holding");
			Check(run.IsPauseRequested, "pause is reported as requested");
			run.TogglePause();
			wait.Restart();
			while (run.State != RunState.Running && wait.ElapsedMilliseconds < 5000)
			{
				Thread.Sleep(10);
			}
			Check(run.State == RunState.Running, "resumed through TogglePause");
			run.Stop();
			Check(run.Completion.Wait(10000) && run.State == RunState.Cancelled && !run.IsActive, "stopped -> Cancelled, " + run.StepsDone + " steps, " + run.FrameCount + " frames");
			Check(run.FrameCount == store.Count && run.FrameCount > 0, "frame count published matches the store");
			if (run.EngineDescription.StartsWith("GPU", StringComparison.Ordinal) && run.FrameCount > 60)
			{
				// the GPU scratch path: two requests on one kept engine (the probe program compiled once), compared with the GPU run
				using var scratch = new ScratchRunner(null);
				using var arrived = new ManualResetEventSlim();
				ScratchResult? result = null;
				scratch.Completed += (_, r) =>
				{
					result = r;
					arrived.Set();
				};
				scratch.Request(spec, 10);
				bool first = arrived.Wait(30000) && result is { Snapshot: not null, Frame: 10 };
				arrived.Reset();
				var clock = Stopwatch.StartNew();
				scratch.Request(spec, 60);
				bool second = arrived.Wait(30000) && result is { Snapshot: not null, Frame: 60 };
				clock.Stop();
				Check(first && second, "GPU scratch frames 10 and 60 computed (" + clock.ElapsedMilliseconds + " ms for the second, probes re-installed without a compile)");
				Check(second && MaxDiff(result!.Traces[0].Series.Values.AsSpan(0, 121), run.Traces[0].Series.Values.AsSpan(0, 121)) <= 1e-5f * Math.Max(1e-3f, MaxAbs(run.Traces[0].Series.Values.AsSpan(0, 121))), "GPU scratch series matches the GPU run's first 121 samples");
				Check(second && MaxDiff(result!.Traces[2].Profiles[0], run.Traces[2].Profiles[60]) <= 1e-5f * Math.Max(1e-3f, MaxAbs(run.Traces[2].Profiles[60])), "GPU scratch integrated profile matches the GPU run's frame 60");
			}
		}
		Check(!File.Exists(path), "second store deleted");
	}
}
