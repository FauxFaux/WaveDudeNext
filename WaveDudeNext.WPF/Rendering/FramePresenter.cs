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

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NLog;
using WaveDudeNext.Engine;
using WaveDudeNext.Rendering;
using WaveDudeNext.Runs;

namespace WaveDudeNext.WPF.Rendering;

/// <summary>
/// Turns frames of an <see cref="IFrameSource"/> (a run's store, or a single scratch frame) into the preview's
/// two <see cref="WriteableBitmap"/>s, on one of two paths (2026-10-23, the user's design): a frame of 8-bit palette
/// indices, the fast preview, is coloured at its own size and WPF scales the picture; a frame of 16-bit samples is
/// rendered at the size its picture takes on screen (<see cref="SetTargetSize"/>, <see cref="ScreenRenderer"/>:
/// resampled in value space, coloured from the gradient, optionally dithered, <see cref="DitherBits"/>), into a bitmap
/// of that many pixels, which the picture (<see cref="Controls.CanvasImage"/>, sized by the canvas) draws 1:1. A UI-thread
/// object: <see cref="Source"/>, <see cref="SetLayer"/> and <see cref="Show"/> are called there; the colouring
/// runs on the thread pool, one job at a time, and only the newest request is honoured (a scrub that outruns
/// the renderer skips frames rather than queueing them). The bitmaps are written on the UI thread.
/// </summary>
public sealed class FramePresenter
{
	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

	/// <summary>Every this many presented frames the average cost is logged at Debug level.</summary>
	private const int RenderLogInterval = 100;

	private readonly Dispatcher _dispatcher;
	private readonly LayerSettings[] _settings = [new(), new()];
	private readonly (int Width, int Height)[] _targets = new (int, int)[2];
	/// <summary>The screen renderer of each layer, kept while the sizes stay; the render thread's alone.</summary>
	private readonly ScreenRenderer?[] _screens = new ScreenRenderer?[2];
	private int _ditherBits;
	private (int Width, int Height) _shownSize;
	private IFrameSource? _source;
	private Job? _job;
	private int _wanted;
	private int _version;
	private int _busy;
	private int _renderedFrame = -1;
	private Job? _renderedJob;
	private readonly Stopwatch _renderClock = new();
	private readonly Stopwatch _copyClock = new(), _colourClock = new(), _writeClock = new();
	private int _renderCount;

	/// <param name="dispatcher">The UI thread's dispatcher, where the bitmaps live.</param>
	public FramePresenter(Dispatcher dispatcher) => _dispatcher = dispatcher;

	/// <summary>Colour and visibility of one layer, as last set through <see cref="SetLayer"/>.</summary>
	private sealed record LayerSettings
	{
		public ColorLut Lut { get; init; } = ColorLut.Grey;
		public float Brightness { get; init; } = 1f;
		public bool Enabled { get; init; } = true;
	}

	/// <summary>An immutable snapshot of everything one render needs, replaced wholesale on every change.</summary>
	private sealed class Job(IFrameSource source, WriteableBitmap? field, WriteableBitmap? intensity, LayerSettings[] settings, (int Width, int Height)[] targets, int ditherBits)
	{
		public IFrameSource Source { get; } = source;
		/// <summary>The pictures of palette indices, made with the source; a 16-bit picture is taken (or made) when it is written.</summary>
		public WriteableBitmap?[] Bitmaps { get; } = [field, intensity];
		public LayerSettings[] Settings { get; } = settings;
		/// <summary>The size each layer is rendered at: the source's for palette indices, the picture's on screen for 16-bit samples (the source's until it is known).</summary>
		public (int Width, int Height)[] Sizes { get; } = [.. targets.Select(target =>
			source.Format == FrameFormat.Half && target.Width > 0 && target.Height > 0 ? target : (source.Width, source.Height))];
		public int DitherBits { get; } = ditherBits;
		/// <summary>The plane read from the source as halves (a <see cref="FrameFormat.Half"/> source), else null.</summary>
		public Half[]? Halves { get; } = source.Format == FrameFormat.Half ? new Half[source.Width * source.Height] : null;
		/// <summary>The plane read from the source as palette indices (a <see cref="FrameFormat.Index"/> source), else null.</summary>
		public byte[]? Indices { get; } = source.Format == FrameFormat.Index ? new byte[source.Width * source.Height] : null;
		/// <summary>Each layer's rendered pixels, made on first use by the render thread.</summary>
		public int[]?[] Pixels { get; } = new int[]?[2];
	}

	/// <summary>
	/// The frames to show; null shows nothing. The bitmaps (<see cref="FieldBitmap"/>, <see cref="IntensityBitmap"/>)
	/// are kept across sources of the same size and planes, so switching between a run's store and a scratch
	/// frame does not swap images; a new size creates new ones: at once for palette indices, with the first frame
	/// rendered for 16-bit samples (<see cref="BitmapsChanged"/>).
	/// </summary>
	public IFrameSource? Source
	{
		get => _source;
		set
		{
			if (ReferenceEquals(_source, value))
			{
				return;
			}
			bool sameShape = _source != null && value != null && _source.Width == value.Width && _source.Height == value.Height
				&& _source.Format == value.Format && _source.Has(FrameLayer.Intensity) == value.Has(FrameLayer.Intensity);
			Logger.Trace("Presenter source: {Source} (same shape {Same})", value?.GetType().Name ?? "none", sameShape);
			_source = value;
			if (!sameShape)
			{
				// the old pictures go first, so their memory can be reclaimed for the new ones; a 16-bit picture is made at
				// its size on screen when its first frame is written
				FieldBitmap = null;
				IntensityBitmap = null;
				bool indexed = value is { Format: FrameFormat.Index };
				FieldBitmap = indexed && value!.Has(FrameLayer.Field) ? NewBitmap(value.Width, value.Height, 96, 96) : null;
				IntensityBitmap = indexed && value!.Has(FrameLayer.Intensity) ? NewBitmap(value.Width, value.Height, 96, 96) : null;
			}
			Rebuild();
		}
	}

	/// <summary>The field picture of the current store, or null.</summary>
	public WriteableBitmap? FieldBitmap { get; private set; }

	/// <summary>The intensity picture of the current store, or null (no store, or no intensity plane).</summary>
	public WriteableBitmap? IntensityBitmap { get; private set; }

	/// <summary>A picture of 16-bit samples was replaced by one of a new size, with its first frame in it; raised on the UI thread.</summary>
	public event EventHandler? BitmapsChanged;

	/// <summary>
	/// The dithering of 16-bit pictures: 0 for none, else one of <see cref="ScreenRenderer.DitherDepths"/> (any other value
	/// means none). Preview only; a frame of palette indices has nothing finer to dither. A change redraws a 16-bit frame.
	/// </summary>
	public int DitherBits
	{
		get => _ditherBits;
		set
		{
			int bits = ScreenRenderer.DitherDepths.Contains(value) ? value : 0;
			if (bits != _ditherBits)
			{
				_ditherBits = bits;
				RebuildIfScreenSized();
			}
		}
	}

	/// <summary>
	/// The size a layer's picture takes on screen, in device pixels (0 while unknown): where a frame of 16-bit samples is
	/// rendered; palette indices are rendered at their own size and scaled by WPF. A change redraws a 16-bit frame.
	/// </summary>
	public void SetTargetSize(FrameLayer layer, int width, int height)
	{
		if (_targets[(int)layer] != (width, height))
		{
			_targets[(int)layer] = (width, height);
			RebuildIfScreenSized();
		}
	}

	private void RebuildIfScreenSized()
	{
		if (_source is { Format: FrameFormat.Half })
		{
			Rebuild();
		}
	}

	/// <summary>Sets how a layer is coloured and whether it is rendered at all; the shown frame is redrawn.</summary>
	/// <param name="layer">The plane.</param>
	/// <param name="lut">The colours (<see cref="ColorLut.Grey"/> for greyscale).</param>
	/// <param name="brightness">Gain before the colour lookup.</param>
	/// <param name="enabled">False leaves the bitmap untouched (the picture is hidden anyway).</param>
	public void SetLayer(FrameLayer layer, ColorLut lut, float brightness, bool enabled)
	{
		_settings[(int)layer] = new LayerSettings { Lut = lut, Brightness = brightness, Enabled = enabled };
		Rebuild();
	}

	/// <summary>
	/// Whether a frame is being rendered right now (2026-10-16): the pane advances the shown position only when this is
	/// false, so the overlays and the timeline are redrawn once per picture that can be shown, not sixty times a
	/// second for pictures that never appear; on a machine that renders at the display rate nothing changes.
	/// </summary>
	public bool IsBusy => Volatile.Read(ref _busy) == 1;

	/// <summary>Requests frame <paramref name="frame"/> (clamped to the frames available); coalesced with pending requests.</summary>
	public void Show(int frame)
	{
		Volatile.Write(ref _wanted, frame);
		Kick();
	}

	/// <summary>
	/// A picture of that many pixels at that resolution, or null when Windows has no memory for it (the bitmap is a shared
	/// section, which fails at the commit limit as MILERR_WIN32ERROR; it took the application down on 2026-10-19 after a
	/// run had filled the memory): logged as an error, the picture then stays empty until a source with a new shape comes.
	/// </summary>
	private static WriteableBitmap? NewBitmap(int width, int height, double dpiX, double dpiY)
	{
		try
		{
			return new WriteableBitmap(width, height, dpiX, dpiY, PixelFormats.Bgr32, null);
		}
		catch (Exception ex) when (ex is COMException or OutOfMemoryException)
		{
			Logger.Error(ex, "No memory for a preview picture of {Width}×{Height}: the picture stays empty", width, height);
			return null;
		}
	}

	private void Rebuild()
	{
		_job = _source == null ? null : new Job(_source, FieldBitmap, IntensityBitmap, [.. _settings], [.. _targets], _ditherBits);
		Kick();
	}

	/// <summary>The layer's screen renderer for these sizes, made anew when they changed (logged); render thread.</summary>
	private ScreenRenderer Screen(FrameLayer layer, int sourceWidth, int sourceHeight, int width, int height)
	{
		ScreenRenderer? screen = _screens[(int)layer];
		if (screen == null || screen.SourceWidth != sourceWidth || screen.SourceHeight != sourceHeight || screen.TargetWidth != width || screen.TargetHeight != height)
		{
			screen = new ScreenRenderer(sourceWidth, sourceHeight, width, height);
			_screens[(int)layer] = screen;
			double scale = width / (double)sourceWidth;
			// the factor is text before NLog formats the entry, so it takes the invariant culture here, as NLog's own
			// parameters do (2026-11-03, the user's rule: no "1,29×" in the log)
			string how = width == sourceWidth && height == sourceHeight ? "1:1"
				: scale < 1 ? string.Create(CultureInfo.InvariantCulture, $"{1 / scale:0.##}× smaller, each pixel the mean of the samples it covers")
				: string.Create(CultureInfo.InvariantCulture, $"{scale:0.##}× larger, blended between the nearest samples");
			Logger.Debug("Preview {Layer}: 16-bit frames of {SourceWidth}×{SourceHeight} drawn at {Width}×{Height} screen pixels ({How})",
				layer, sourceWidth, sourceHeight, width, height, how);
		}
		return screen;
	}

	/// <summary>Marks a change and starts the render loop unless one is running (it re-checks the version before it ends).</summary>
	private void Kick()
	{
		Interlocked.Increment(ref _version);
		if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
		{
			Task.Run(RenderLoop);
		}
	}

	private void RenderLoop()
	{
		try
		{
			while (true)
			{
				int version = Volatile.Read(ref _version);
				Job? job = Volatile.Read(ref _job);
				int frame = Volatile.Read(ref _wanted);
				if (job != null)
				{
					RenderOne(job, frame);
				}
				Volatile.Write(ref _busy, 0);
				if (Volatile.Read(ref _version) == version || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
				{
					return;
				}
			}
		}
		catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
		{
			// the dispatcher is shutting down; nothing left to show
			Volatile.Write(ref _busy, 0);
		}
		catch (Exception ex)
		{
			Volatile.Write(ref _busy, 0);
			Logger.Error(ex, "Preview rendering failed");
		}
	}

	private void RenderOne(Job job, int frame)
	{
		int count = job.Source.Count;
		if (count == 0)
		{
			return;
		}
		frame = Math.Clamp(frame, 0, count - 1);
		if (frame == _renderedFrame && ReferenceEquals(job, _renderedJob))
		{
			return;
		}
		int width = job.Source.Width;
		int height = job.Source.Height;
		Logger.Trace("Presenter render: frame {Frame} of {Source} (job {Job})", frame, job.Source.GetType().Name, job.GetHashCode());
		_renderClock.Start();
		var locked = new List<(WriteableBitmap Bitmap, Int32Rect Rect, FrameLayer Layer, bool IsNew)>(2);
		try
		{
			foreach (FrameLayer layer in new[] { FrameLayer.Field, FrameLayer.Intensity })
			{
				LayerSettings settings = job.Settings[(int)layer];
				if (!settings.Enabled || !job.Source.Has(layer) || (job.Indices != null && job.Bitmaps[(int)layer] == null))
				{
					continue;
				}
				_copyClock.Start();
				bool copied = job.Indices != null ? job.Source.CopyLayer(frame, layer, job.Indices.AsSpan()) : job.Source.CopyLayer(frame, layer, job.Halves.AsSpan());
				_copyClock.Stop();
				if (!copied)
				{
					continue;
				}
				(int outWidth, int outHeight) = job.Sizes[(int)layer];
				int[] pixels = job.Pixels[(int)layer] ??= new int[outWidth * outHeight];
				_colourClock.Start();
				if (job.Indices != null)
				{
					// palette indices: the brightness was applied when the frame was captured, only the palette is looked up
					FrameRenderer.RenderIndexed(job.Indices, width, height, settings.Lut, pixels);
				}
				else
				{
					// 16-bit samples: resampled to the picture's size on screen, every level of the gradient, dithered if chosen
					Screen(layer, width, height, outWidth, outHeight).Render(job.Halves, settings.Lut, job.Source.KindOf(layer), settings.Brightness, job.DitherBits, pixels);
				}
				_colourClock.Stop();
				_shownSize = (outWidth, outHeight);
				// the copy into the bitmap runs here, on the pool thread, between a lock and an unlock on the UI thread
				// (2026-10-16): WritePixels did the 8 to 12 MB copy on the UI thread, over 100 ms of every second at eleven
				// frames. The back buffer's pointer and stride are read inside the lock call, on the UI thread: they are
				// dependency properties with thread affinity (read from here they threw on every frame, 2026-10-16). Both
				// layers are unlocked in one call below, one render pass per presented frame instead of two. Render
				// priority, on purpose: at background priority the calls waited behind every mouse move of a scrub.
				// A 16-bit picture of another size is made here and shown once written; the picture sizes itself by the canvas
				// (CanvasImage), so its bitmap is plain screen pixels and a new one never moves the layout. It is kept while its
				// pixel size fits (2026-10-23: a resolution set to make it measure the canvas came back from WPF in single
				// precision, a compare with it failed on every frame, and two new pictures a frame made the whole pane flicker)
				_writeClock.Start();
				(WriteableBitmap? bitmap, bool isNew, IntPtr buffer, int stride) = _dispatcher.Invoke(() =>
				{
					WriteableBitmap? target = job.Indices != null ? job.Bitmaps[(int)layer] : layer == FrameLayer.Field ? FieldBitmap : IntensityBitmap;
					bool fresh = false;
					if (job.Indices == null && (target == null || target.PixelWidth != outWidth || target.PixelHeight != outHeight))
					{
						target = NewBitmap(outWidth, outHeight, 96, 96);
						fresh = true;
						Logger.Debug("Preview {Layer}: a new 16-bit picture of {Width}×{Height} screen pixels", layer, outWidth, outHeight);
					}
					if (target == null)
					{
						return ((WriteableBitmap?)null, false, IntPtr.Zero, 0);
					}
					target.Lock();
					return ((WriteableBitmap?)target, fresh, target.BackBuffer, target.BackBufferStride);
				}, DispatcherPriority.Render);
				if (bitmap == null)
				{
					_writeClock.Stop();
					continue;
				}
				locked.Add((bitmap, new Int32Rect(0, 0, outWidth, outHeight), layer, isNew));
				if (stride == outWidth * 4)
				{
					Marshal.Copy(pixels, 0, buffer, outWidth * outHeight);
				}
				else
				{
					for (int y = 0; y < outHeight; y++)
					{
						Marshal.Copy(pixels, y * outWidth, buffer + y * stride, outWidth);
					}
				}
				_writeClock.Stop();
			}
		}
		finally
		{
			if (locked.Count > 0)
			{
				_dispatcher.Invoke(() =>
				{
					bool replaced = false;
					foreach ((WriteableBitmap bitmap, Int32Rect rect, FrameLayer layer, bool isNew) in locked)
					{
						bitmap.AddDirtyRect(rect);
						bitmap.Unlock();
						// a new 16-bit picture takes its place only while its source is still the one shown (a source of palette
						// indices set meanwhile has pictures of its own)
						if (isNew && ReferenceEquals(job.Source, _source))
						{
							if (layer == FrameLayer.Field)
							{
								FieldBitmap = bitmap;
							}
							else
							{
								IntensityBitmap = bitmap;
							}
							replaced = true;
						}
					}
					if (replaced)
					{
						BitmapsChanged?.Invoke(this, EventArgs.Empty);
					}
				}, DispatcherPriority.Render);
			}
		}
		_renderClock.Stop();
		_renderedFrame = frame;
		_renderedJob = job;
		if (++_renderCount % RenderLogInterval == 0)
		{
			// the cost of one presented frame (copy, colouring, bitmap write), averaged; for judging the pipeline at full resolution
			Logger.Debug("Preview: {Width}x{Height} shown at {ShownWidth}x{ShownHeight}, {Average:0.0} ms per frame over the last {Count} (copy out of the store {Copy:0.0}, colouring {Colour:0.0}, copy into the bitmap {Write:0.0}, all on the pool)",
				width, height, _shownSize.Width, _shownSize.Height, _renderClock.Elapsed.TotalMilliseconds / RenderLogInterval, RenderLogInterval,
				_copyClock.Elapsed.TotalMilliseconds / RenderLogInterval, _colourClock.Elapsed.TotalMilliseconds / RenderLogInterval, _writeClock.Elapsed.TotalMilliseconds / RenderLogInterval);
			_renderClock.Reset();
			_copyClock.Reset();
			_colourClock.Reset();
			_writeClock.Reset();
		}
	}
}
