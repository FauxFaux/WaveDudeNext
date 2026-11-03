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

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NLog;
using WaveDudeNext.Engine;

namespace WaveDudeNext.Runs;

/// <summary>
/// The frames of one run: per frame one plane per layer (<see cref="Layers"/>) in the run's <see cref="Shape"/>:
/// <see cref="Width"/> × <see cref="Height"/> samples row-major with the engine's padding stripped, the canvas
/// shrunk by the shape's divisor, each sample a <see cref="Half"/> or a palette index byte (<see cref="Format"/>,
/// 2026-10-15; the reduced preview). Two tiers
/// (2026-10-14): the first frames of the run, as many as the memory budget given at creation holds
/// (<see cref="RamFrames"/>), live in native memory; the rest go to a temporary file that is created only when
/// a run does not fit. A run that fits never touches the disk, so it is bound by the engine and not by the
/// drive; a run that does not fit is disk-bound for its tail only. The file is pre-sized for the frames it
/// will hold, marked temporary, and deleted when the store is disposed or the process ends
/// (<see cref="FileOptions.DeleteOnClose"/>).
/// </summary>
/// <remarks>
/// Why not the operating system's cache or a memory mapping: both keep dirty pages in memory only up to
/// the memory manager's own allowance and then write at disk speed whatever the file's attributes say
/// (a 10 GB full-HD run took 14 s against the drive with 40 GB of memory free), and a mapping paid a page
/// fault per fresh page and flushed everything on unmap. The budget is ours, decided from what is free when
/// the run starts (<see cref="FrameMemory.Budget(int, long)"/>), so the machine is never pushed into its page file.
/// Threads: the run's worker is the only writer (<see cref="Write(int, ReadOnlySpan{Half}, ReadOnlySpan{Half})"/>,
/// frames in order); any thread may read frames below <see cref="Count"/>, which is published after the
/// frame's bytes. Reads and writes run concurrently; <see cref="Dispose"/> waits for them, frees the memory
/// under the lock (quick) and closes the file outside it (deleting a large file can take a second).
/// A background task allocates the memory blocks a few frames ahead of the writer and touches every page of
/// them (<see cref="PrepareBlocks"/>): a fresh block costs a page fault per 4 KB on first touch, 8 to 14 ms
/// for a full-HD frame, which would make the writer slower than the engine; prepared, a frame is a plain copy.
/// File layout: a <see cref="HeaderSize"/>-byte header ("WDFS", version, width, height, layers,
/// visualize-every, capacity, first frame in the file, format, divisor, canvas width, canvas height, 1 when the
/// intensity is stored as its square root), then one
/// <see cref="FrameBytes"/> record per spilled frame; within a frame the planes follow the order of
/// <see cref="FrameLayer"/>, only those present.
/// </remarks>
public sealed unsafe class FrameStore : IFrameSource, IDisposable
{
	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

	/// <summary>Size of the file header in bytes.</summary>
	public const int HeaderSize = 64;

	private const uint Magic = 0x53464457; // "WDFS" as little-endian bytes
	private const int Version = 3;

	/// <summary>Value of <see cref="_firstSpilled"/> while no frame goes to the file.</summary>
	private const int NoSpill = int.MaxValue;

	/// <summary>Memory blocks allocated and touched ahead of the writer.</summary>
	private const int PrepareDepth = 4;

	/// <summary>Page size assumed when touching a fresh block.</summary>
	private const int PageSize = 4096;

	private readonly ReaderWriterLockSlim _lifetime = new();
	private readonly IntPtr[] _ram;
	private readonly BlockingCollection<IntPtr> _prepared = new(PrepareDepth);
	private readonly CancellationTokenSource _prepareCancel = new();
	private readonly Task _preparer;
	private readonly Task? _memoryReleased;
	private readonly byte[] _staging;
	private byte[]? _staging2;
	private FileStream? _file;
	private SafeFileHandle? _handle;
	private volatile int _firstSpilled = NoSpill;
	private int _count;
	private bool _disposed;

	private FrameStore(string path, FrameShape shape, FrameLayers layers, int capacity, int visualizeEvery, long ramBudget, Task? memoryReleased)
	{
		_memoryReleased = memoryReleased;
		shape.Validate();
		ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(visualizeEvery, 1);
		if (layers == FrameLayers.None)
		{
			throw new ArgumentException("A store needs at least one layer.", nameof(layers));
		}
		Path = path;
		Shape = shape;
		Layers = layers;
		Capacity = capacity;
		VisualizeEvery = visualizeEvery;
		LayerCount = (Has(FrameLayer.Field) ? 1 : 0) + (Has(FrameLayer.Intensity) ? 1 : 0);
		RamFrames = Math.Min(capacity, FramesInBudget(shape, layers, ramBudget));
		_ram = new IntPtr[RamFrames];
		_staging = new byte[shape.PlaneBytes];
		if (RamFrames < capacity)
		{
			// the run will not fit: the file for the rest is made now, so a lack of disk space shows at the start
			_firstSpilled = RamFrames;
			CreateFile(RamFrames);
		}
		_preparer = RamFrames > 0 ? Task.Run(PrepareBlocks) : Task.CompletedTask;
	}

	/// <summary>
	/// The background producer of memory blocks: allocates one per frame of the memory tier, touches every page
	/// so the writer never takes a page fault (<see cref="TouchPages"/>), and queues it, at most <see cref="PrepareDepth"/> ahead. Allocates
	/// nothing before the memory the budget counts on is released (<see cref="_memoryReleased"/>, the outgoing run's
	/// store; 2026-10-16: the budget had counted it as free while the old store still held it, a race of tens of
	/// milliseconds that a machine at its commit limit lost). Ends when every block is made, the store is disposed,
	/// or an allocation fails (the commit limit), after which the writer spills the remaining frames to the file.
	/// </summary>
	private void PrepareBlocks()
	{
		CancellationToken token = _prepareCancel.Token;
		try
		{
			if (_memoryReleased != null)
			{
				try
				{
					_memoryReleased.Wait(token);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (AggregateException)
				{
					// the release failed; the memory is in whatever state it is, the allocations below find out
				}
			}
			for (int i = 0; i < RamFrames && !token.IsCancellationRequested; i++)
			{
				void* block;
				try
				{
					block = NativeMemory.Alloc((nuint)FrameBytes);
				}
				catch (OutOfMemoryException)
				{
					break;
				}
				TouchPages((IntPtr)block, FrameBytes);
				try
				{
					_prepared.Add((IntPtr)block, token);
				}
				catch (OperationCanceledException)
				{
					NativeMemory.Free(block);
					break;
				}
			}
		}
		finally
		{
			_prepared.CompleteAdding();
		}
	}

	/// <summary>The next prepared block for a frame of the memory tier, waiting for the producer; false when it ended (the memory is exhausted).</summary>
	private bool TryTakeBlock(out void* block)
	{
		if (_prepared.TryTake(out IntPtr pointer, Timeout.Infinite))
		{
			block = (void*)pointer;
			return true;
		}
		block = null;
		return false;
	}

	/// <summary>
	/// Creates a store for a run of <paramref name="capacity"/> frames: in memory up to <paramref name="ramBudget"/>
	/// bytes, the rest in the file at <paramref name="path"/> (which must not exist; it is only created when needed).
	/// </summary>
	/// <param name="path">File path for the spilled frames, normally from <see cref="FrameCache.NewPath"/>.</param>
	/// <param name="shape">The frames' geometry and format (<see cref="RunSpec.Shape"/>).</param>
	/// <param name="layers">The planes every frame holds; at least one.</param>
	/// <param name="capacity">Number of frames the run produces (<see cref="RunSpec.FrameCount"/>).</param>
	/// <param name="visualizeEvery">Steps per frame, kept for <see cref="StepOf"/>.</param>
	/// <param name="ramBudget">Bytes of memory the frames may take; null takes <see cref="FrameMemory.Budget(int, long)"/> at the default share.</param>
	/// <param name="memoryReleased">Completes when memory the budget counts on is released (the outgoing run's store being disposed); the memory tier is not allocated before. Null: nothing to wait for.</param>
	/// <exception cref="IOException">The file cannot be created or sized (for instance no disk space).</exception>
	public static FrameStore Create(string path, FrameShape shape, FrameLayers layers, int capacity, int visualizeEvery, long? ramBudget = null, Task? memoryReleased = null) =>
		new(path, shape, layers, capacity, visualizeEvery, ramBudget ?? FrameMemory.Budget(FrameMemory.DefaultPercent), memoryReleased);

	/// <summary>Creates a full-quality store (every cell as a <see cref="Half"/>) for a canvas of <paramref name="width"/> × <paramref name="height"/>; see <see cref="Create(string, FrameShape, FrameLayers, int, int, long?, Task?)"/>.</summary>
	public static FrameStore Create(string path, int width, int height, FrameLayers layers, int capacity, int visualizeEvery, long? ramBudget = null, Task? memoryReleased = null) =>
		Create(path, FrameShape.Full(width, height), layers, capacity, visualizeEvery, ramBudget, memoryReleased);

	/// <summary>Bytes a file holding <paramref name="capacity"/> frames of this shape takes, for <see cref="FrameCache.CheckSpace"/> (the spilled part of a run).</summary>
	public static long SizeOf(FrameShape shape, FrameLayers layers, int capacity) => HeaderSize + capacity * FrameBytesOf(shape, layers);

	/// <summary>Bytes <paramref name="frames"/> frames of this shape take in memory, no file header: a run's need against <see cref="FrameMemory.Budget(int, long)"/>.</summary>
	public static long MemoryOf(FrameShape shape, FrameLayers layers, int frames) => frames * FrameBytesOf(shape, layers);

	/// <summary>How many frames of this shape fit into <paramref name="budget"/> bytes of memory.</summary>
	public static int FramesInBudget(FrameShape shape, FrameLayers layers, long budget) =>
		(int)Math.Clamp(Math.Max(0, budget) / FrameBytesOf(shape, layers), 0, int.MaxValue);

	private static long FrameBytesOf(FrameShape shape, FrameLayers layers)
	{
		int layerCount = ((layers & FrameLayers.Field) != 0 ? 1 : 0) + ((layers & FrameLayers.Intensity) != 0 ? 1 : 0);
		return layerCount * shape.PlaneBytes;
	}

	/// <summary>Full path of the file for the spilled frames; it exists only once a frame went there (<see cref="HasFile"/>).</summary>
	public string Path { get; }

	/// <summary>The frames' geometry and format.</summary>
	public FrameShape Shape { get; }

	/// <summary>Frame width in samples.</summary>
	public int Width => Shape.Width;

	/// <summary>Frame height in samples.</summary>
	public int Height => Shape.Height;

	/// <inheritdoc/>
	public int CanvasWidth => Shape.CanvasWidth;

	/// <inheritdoc/>
	public int CanvasHeight => Shape.CanvasHeight;

	/// <inheritdoc/>
	public FrameFormat Format => Shape.Format;

	/// <inheritdoc/>
	public bool IntensityRoot => Shape.IntensityRoot;

	/// <summary>The planes every frame holds.</summary>
	public FrameLayers Layers { get; }

	/// <summary>Number of planes per frame.</summary>
	public int LayerCount { get; }

	/// <summary>Number of frames the store has room for.</summary>
	public int Capacity { get; }

	/// <summary>Steps per frame: frame k shows the state after step k × this.</summary>
	public int VisualizeEvery { get; }

	/// <summary>Frames the memory budget holds, the first ones of the run; the rest go to the file.</summary>
	public int RamFrames { get; }

	/// <summary>Index of the first frame kept in the file; <see cref="Capacity"/> when every frame fits in memory. Falls below <see cref="RamFrames"/> only when an allocation failed.</summary>
	public int FirstSpilledFrame => Math.Min(_firstSpilled, Capacity);

	/// <summary>Frames written so far; frames 0..Count-1 may be read from any thread.</summary>
	public int Count => Volatile.Read(ref _count);

	/// <summary>Frames written so far that live in memory.</summary>
	public int FramesInMemory => Math.Min(Count, FirstSpilledFrame);

	/// <summary>Frames written so far that live in the file.</summary>
	public int FramesOnDisk => Math.Max(0, Count - FirstSpilledFrame);

	/// <summary>Whether the file for spilled frames exists.</summary>
	public bool HasFile => _file != null;

	/// <summary>Bytes of one plane: <see cref="Width"/> × <see cref="Height"/> × the sample size (<see cref="FrameShape.PlaneBytes"/>).</summary>
	public long PlaneBytes => Shape.PlaneBytes;

	/// <summary>Bytes of one frame: <see cref="PlaneBytes"/> × <see cref="LayerCount"/>.</summary>
	public long FrameBytes => PlaneBytes * LayerCount;

	/// <summary>Bytes all frames of the run take once written, memory and file together (without the file header).</summary>
	public long TotalBytes => Capacity * FrameBytes;

	/// <summary>Bytes the memory tier takes once its frames are written.</summary>
	public long RamBytes => FirstSpilledFrame * FrameBytes;

	/// <summary>Size of the file, 0 while there is none.</summary>
	public long FileBytes => _file == null ? 0 : HeaderSize + (Capacity - FirstSpilledFrame) * FrameBytes;

	/// <summary>Whether frames hold the given plane.</summary>
	public bool Has(FrameLayer layer) => (Layers & (FrameLayers)(1 << (int)layer)) != 0;

	/// <summary>The 0-based simulation step frame <paramref name="frame"/> shows.</summary>
	public int StepOf(int frame) => frame * VisualizeEvery;

	/// <summary>Raised on the writer's thread after <see cref="Count"/> advanced, with the new count.</summary>
	public event Action<int>? FrameWritten;

	/// <summary>
	/// Stores the next frame from the engine's padded buffers (row y starts at <c>(y + 1) × stride + 1</c>),
	/// converting them to the store's shape first (<see cref="FrameConversion"/>, rows in parallel). Writer thread
	/// only; frames must arrive in order. The run itself uses the snapshot overload with the engine's own captures.
	/// </summary>
	/// <param name="frame">Index of the frame; must equal <see cref="Count"/>.</param>
	/// <param name="field">Padded field buffer, <c>stride × (canvas height + 2)</c> floats.</param>
	/// <param name="intensity">Padded intensity buffer, or null when the store has no intensity plane.</param>
	/// <param name="stride">Row pitch of the padded buffers (<see cref="CanvasWidth"/> + 2, plus twice the rim).</param>
	/// <param name="rim">Cells of the grid beyond each side of the canvas (<see cref="Engine.IWaveEngine.Rim"/>): the canvas starts that far into the padded rows.</param>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is not the next frame or beyond <see cref="Capacity"/>.</exception>
	/// <exception cref="ArgumentNullException">The store has an intensity plane but <paramref name="intensity"/> is null.</exception>
	/// <exception cref="ObjectDisposedException">The store is disposed.</exception>
	public void Write(int frame, float[] field, float[]? intensity, int stride, int rim = 0)
	{
		byte[] fieldPlane = _staging;
		FrameConversion.Convert(field, (rim + 1) * stride + rim + 1, stride, Shape, false, fieldPlane);
		byte[]? intensityPlane = null;
		if (Has(FrameLayer.Intensity))
		{
			ArgumentNullException.ThrowIfNull(intensity);
			intensityPlane = _staging2 ??= new byte[Shape.PlaneBytes];
			FrameConversion.Convert(intensity, (rim + 1) * stride + rim + 1, stride, Shape, true, intensityPlane);
		}
		Write(frame, fieldPlane, intensityPlane);
	}

	/// <summary>Stores the next frame from <see cref="Half"/> planes; the store's samples must be halves (<see cref="Format"/>). See <see cref="Write(int, ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.</summary>
	/// <exception cref="InvalidOperationException">The store's samples are palette indices.</exception>
	public void Write(int frame, ReadOnlySpan<Half> field, ReadOnlySpan<Half> intensity)
	{
		RequireHalves();
		Write(frame, MemoryMarshal.AsBytes(field), MemoryMarshal.AsBytes(intensity));
	}

	/// <summary>
	/// Stores the next frame from planes in the store's own layout (what <see cref="Engine.IWaveEngine.CaptureFrame"/>
	/// produces for the store's <see cref="Shape"/>): a copy into memory while the budget lasts, one positional
	/// write per plane into the file beyond it. Any thread, but only one writer at a time; frames must arrive in
	/// order. When the memory for a frame within the budget cannot be had after all (the commit limit of the
	/// machine), that frame and every later one go to the file instead, noted once.
	/// </summary>
	/// <param name="frame">Index of the frame; must equal <see cref="Count"/>.</param>
	/// <param name="field">Field plane, <see cref="PlaneBytes"/> bytes.</param>
	/// <param name="intensity">Intensity plane, or empty when the store has no intensity plane.</param>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is not the next frame or beyond <see cref="Capacity"/>, or a plane is too short.</exception>
	/// <exception cref="ArgumentNullException">The store has an intensity plane but <paramref name="intensity"/> is missing.</exception>
	/// <exception cref="ObjectDisposedException">The store is disposed.</exception>
	public void Write(int frame, ReadOnlySpan<byte> field, ReadOnlySpan<byte> intensity)
	{
		int length = (int)PlaneBytes;
		_lifetime.EnterReadLock();
		try
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (frame != _count || frame >= Capacity)
			{
				throw new ArgumentOutOfRangeException(nameof(frame), frame, "Frames must be written in order and within the capacity.");
			}
			if (Has(FrameLayer.Field) && field.Length < length)
			{
				throw new ArgumentOutOfRangeException(nameof(field), "The field plane is too short.");
			}
			if (Has(FrameLayer.Intensity) && intensity.Length < length)
			{
				throw new ArgumentNullException(nameof(intensity), "The store has an intensity plane; the intensity is missing or too short.");
			}
			if (frame < _firstSpilled)
			{
				if (!TryTakeBlock(out void* memory))
				{
					Spill(frame);
				}
				else
				{
					// one plain copy, 1.2 ms for a full-HD frame on one core, and the clock of a 16-bit run in RAM against the card's
					// 1.3 ms a frame. Every faster copy lost in a run (2026-11-03, measured in the app): chunks on the thread pool
					// queued behind the presenter's rendering rows; two or four dedicated threads, at normal or above-normal
					// priority, copied faster but slowed the card's DMA into host memory by more than they gained, the memory bus
					// being shared with it, the producer's page zeroing and the presenter
					if (Has(FrameLayer.Field))
					{
						field[..length].CopyTo(Plane(memory, FrameLayer.Field));
					}
					if (Has(FrameLayer.Intensity))
					{
						intensity[..length].CopyTo(Plane(memory, FrameLayer.Intensity));
					}
					_ram[frame] = (IntPtr)memory;
				}
			}
			if (frame >= _firstSpilled)
			{
				if (Has(FrameLayer.Field))
				{
					RandomAccess.Write(_handle!, field[..length], PlaneOffset(frame, FrameLayer.Field));
				}
				if (Has(FrameLayer.Intensity))
				{
					RandomAccess.Write(_handle!, intensity[..length], PlaneOffset(frame, FrameLayer.Intensity));
				}
			}
			Volatile.Write(ref _count, frame + 1);
		}
		finally
		{
			_lifetime.ExitReadLock();
		}
		FrameWritten?.Invoke(frame + 1);
	}

	/// <summary>Stores a captured frame (<see cref="Engine.IWaveEngine.CaptureFrame"/>, awaited); the snapshot's size and format must match the store's.</summary>
	/// <exception cref="ArgumentException">The snapshot has another layout.</exception>
	public void Write(int frame, FrameSnapshot snapshot)
	{
		if (!snapshot.Shape.SameLayoutAs(Shape))
		{
			throw new ArgumentException("The snapshot does not match the store's size or format.", nameof(snapshot));
		}
		Write(frame, snapshot.FieldBytes, snapshot.HasIntensity ? snapshot.IntensityBytes : Span<byte>.Empty);
	}

	/// <summary>Reads one plane as <see cref="Half"/> values; the store's samples must be halves (<see cref="Format"/>). See <see cref="CopyLayer(int, FrameLayer, Span{byte})"/>.</summary>
	/// <exception cref="InvalidOperationException">The store's samples are palette indices.</exception>
	public bool CopyLayer(int frame, FrameLayer layer, Span<Half> destination)
	{
		RequireHalves();
		return CopyLayer(frame, layer, MemoryMarshal.AsBytes(destination));
	}

	/// <summary>
	/// Reads one plane of a stored frame into <paramref name="destination"/> (at least <see cref="PlaneBytes"/>
	/// bytes), from memory or from the file. Safe against a concurrent <see cref="Dispose"/>; returns false
	/// instead of reading when the store is already disposed.
	/// </summary>
	/// <param name="frame">Frame index below <see cref="Count"/>.</param>
	/// <param name="layer">A plane the store holds.</param>
	/// <param name="destination">Receives the samples row-major.</param>
	/// <exception cref="ArgumentOutOfRangeException">The frame is not written yet, the plane is not stored, or the destination is too small.</exception>
	public bool CopyLayer(int frame, FrameLayer layer, Span<byte> destination)
	{
		int length = (int)PlaneBytes;
		if (destination.Length < length)
		{
			throw new ArgumentOutOfRangeException(nameof(destination), "The destination is smaller than a plane.");
		}
		_lifetime.EnterReadLock();
		try
		{
			if (_disposed)
			{
				return false;
			}
			if (frame < 0 || frame >= Count)
			{
				throw new ArgumentOutOfRangeException(nameof(frame), frame, "The frame is not written yet.");
			}
			if (!Has(layer))
			{
				throw new ArgumentOutOfRangeException(nameof(layer), layer, "The store has no such plane.");
			}
			if (frame < _firstSpilled)
			{
				Plane((void*)_ram[frame], layer).CopyTo(destination);
				return true;
			}
			Span<byte> bytes = destination[..length];
			long offset = PlaneOffset(frame, layer);
			while (bytes.Length > 0)
			{
				int read = RandomAccess.Read(_handle!, bytes, offset);
				if (read <= 0)
				{
					throw new IOException("The frame store file ended early.");
				}
				bytes = bytes[read..];
				offset += read;
			}
			return true;
		}
		catch (ObjectDisposedException)
		{
			// disposed while reading: the frame is gone with the run
			return false;
		}
		finally
		{
			_lifetime.ExitReadLock();
		}
	}

	/// <summary>
	/// Frees the memory tier and closes the file, which deletes it. Marks the store disposed and frees the frames
	/// under the lock (so no read or write starts on freed memory) but closes the file outside it: deleting a
	/// large file can take a second, and a reader must not wait for that. A file read or write in flight ends
	/// with <see cref="ObjectDisposedException"/>, which <see cref="CopyLayer(int, FrameLayer, Span{byte})"/> turns into false.
	/// </summary>
	public void Dispose()
	{
		_lifetime.EnterWriteLock();
		try
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			_prepareCancel.Cancel();
			for (int i = 0; i < _ram.Length; i++)
			{
				if (_ram[i] != IntPtr.Zero)
				{
					NativeMemory.Free((void*)_ram[i]);
					_ram[i] = IntPtr.Zero;
				}
			}
		}
		finally
		{
			_lifetime.ExitWriteLock();
		}
		_file?.Dispose();
		// the producer ends at its next check; the blocks it queued (and any it adds before noticing) are freed here
		try
		{
			_preparer.Wait(TimeSpan.FromSeconds(5));
		}
		catch (AggregateException)
		{
			// cancelled
		}
		while (_prepared.TryTake(out IntPtr block))
		{
			NativeMemory.Free((void*)block);
		}
		_prepareCancel.Dispose();
	}

	/// <summary>A plane of a frame kept in memory, as a span over the native block.</summary>
	private Span<byte> Plane(void* frameMemory, FrameLayer layer) =>
		new((byte*)frameMemory + PlaneIndex(layer) * PlaneBytes, (int)PlaneBytes);

	/// <summary>
	/// Writes one byte per page of a fresh block, so the writer never takes a page fault, on up to four cores: Windows
	/// hands out and zeroes the pages at 5 GB/s for one thread, 1.7 ms for a full-HD frame's block, which had been the
	/// clock of a run that fits RAM ahead of the writer's copy (2026-11-03); four threads make it 0.6 ms, eight gain
	/// nothing more. A block under four megabytes is touched on the calling thread.
	/// </summary>
	private static void TouchPages(IntPtr block, long bytes)
	{
		long pages = (bytes + PageSize - 1) / PageSize;
		int threads = bytes < 4L << 20 ? 1 : Math.Min(Environment.ProcessorCount, 4);
		if (threads < 2)
		{
			byte* single = (byte*)block;
			for (long page = 0; page < pages; page++)
			{
				single[page * PageSize] = 0;
			}
			return;
		}
		long perThread = (pages + threads - 1) / threads;
		Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
		{
			byte* bytes = (byte*)block;
			long last = Math.Min(pages, (t + 1) * perThread);
			for (long page = t * perThread; page < last; page++)
			{
				bytes[page * PageSize] = 0;
			}
		});
	}

	/// <summary>The <see cref="Half"/> overloads only apply to a store of halves.</summary>
	/// <exception cref="InvalidOperationException">The store's samples are palette indices.</exception>
	private void RequireHalves()
	{
		if (Format != FrameFormat.Half)
		{
			throw new InvalidOperationException("The store's samples are palette indices, not halves.");
		}
	}

	/// <summary>Byte offset of a plane in the file; the file holds the frames from <see cref="_firstSpilled"/> on.</summary>
	private long PlaneOffset(int frame, FrameLayer layer) =>
		HeaderSize + (frame - _firstSpilled) * FrameBytes + PlaneIndex(layer) * PlaneBytes;

	/// <summary>Position of a plane within a frame: planes follow the <see cref="FrameLayer"/> order, only those present.</summary>
	private int PlaneIndex(FrameLayer layer) => layer == FrameLayer.Intensity && Has(FrameLayer.Field) ? 1 : 0;

	/// <summary>
	/// The memory for frame <paramref name="frame"/> could not be had: from here on every frame goes to the file,
	/// which is created now if the run was expected to fit, or enlarged to take the extra frames otherwise
	/// (nothing has been written to it yet, since frames arrive in order).
	/// </summary>
	private void Spill(int frame)
	{
		Logger.Warn("No memory for frame {Frame} ({Bytes} per frame): the remaining {Rest} frames go to disk", frame, FrameBytes, Capacity - frame);
		_firstSpilled = frame;
		if (_file == null)
		{
			CreateFile(frame);
		}
		else
		{
			_file.SetLength(HeaderSize + (Capacity - frame) * FrameBytes);
			WriteHeader(frame);
		}
	}

	/// <summary>Creates the pre-sized temporary file for the frames from <paramref name="firstFrame"/> on.</summary>
	private void CreateFile(int firstFrame)
	{
		_file = new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
		try
		{
			// a temporary file: the cache manager keeps its pages in memory while it can instead of writing
			// them out eagerly (a hint only); the file is deleted in the end
			try
			{
				File.SetAttributes(Path, FileAttributes.Temporary | FileAttributes.NotContentIndexed);
			}
			catch (IOException)
			{
				// a hint only
			}
			catch (UnauthorizedAccessException)
			{
				// a hint only
			}
			_file.SetLength(HeaderSize + (Capacity - firstFrame) * FrameBytes);
			_handle = _file.SafeFileHandle;
			WriteHeader(firstFrame);
		}
		catch
		{
			_file.Dispose();
			_file = null;
			_handle = null;
			throw;
		}
	}

	private void WriteHeader(int firstFrame)
	{
		Span<byte> header = stackalloc byte[HeaderSize];
		BitConverter.TryWriteBytes(header, Magic);
		BitConverter.TryWriteBytes(header[4..], Version);
		BitConverter.TryWriteBytes(header[8..], Width);
		BitConverter.TryWriteBytes(header[12..], Height);
		BitConverter.TryWriteBytes(header[16..], (int)Layers);
		BitConverter.TryWriteBytes(header[20..], VisualizeEvery);
		BitConverter.TryWriteBytes(header[24..], Capacity);
		BitConverter.TryWriteBytes(header[28..], firstFrame);
		BitConverter.TryWriteBytes(header[32..], (int)Format);
		BitConverter.TryWriteBytes(header[36..], Shape.Divisor);
		BitConverter.TryWriteBytes(header[40..], CanvasWidth);
		BitConverter.TryWriteBytes(header[44..], CanvasHeight);
		BitConverter.TryWriteBytes(header[48..], IntensityRoot ? 1 : 0);
		RandomAccess.Write(_handle!, header, 0);
	}
}
