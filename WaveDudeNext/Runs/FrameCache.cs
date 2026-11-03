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
using NLog;

namespace WaveDudeNext.Runs;

/// <summary>
/// Where <see cref="FrameStore"/> files live: one per run under the user's local application data, named so
/// that a leftover of a crashed process is recognisable. UI thread.
/// </summary>
public static class FrameCache
{
	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
	private static int _sequence;

	/// <summary>File extension of a frame store.</summary>
	public const string Extension = ".wdframes";

	/// <summary><c>%LOCALAPPDATA%\WaveDudeNext\cache</c>; created on first use.</summary>
	public static string Directory { get; } =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveDudeNext", "cache");

	/// <summary>A fresh file path for a run's store: <c>run-&lt;time&gt;-&lt;pid&gt;-&lt;n&gt;.wdframes</c> in <see cref="Directory"/>.</summary>
	public static string NewPath()
	{
		System.IO.Directory.CreateDirectory(Directory);
		int n = Interlocked.Increment(ref _sequence);
		return Path.Combine(Directory, $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{n}{Extension}");
	}

	/// <summary>
	/// Checks that the cache drive has room for a store of <paramref name="bytes"/> bytes (plus a small margin).
	/// </summary>
	/// <exception cref="RunSpecException">Not enough free space; the message states the size needed.</exception>
	public static void CheckSpace(long bytes)
	{
		System.IO.Directory.CreateDirectory(Directory);
		var drive = new DriveInfo(Path.GetPathRoot(Directory) ?? Directory);
		long needed = bytes + (64L << 20);
		if (drive.AvailableFreeSpace < needed)
		{
			// the text reaches the log, the status bar and the dialog alike, so its numbers take the decimal point of every
			// other number in the log (2026-11-03, the user's rule: a report's reader must not trip over "1806,2 GB")
			throw new RunSpecException(
				string.Create(CultureInfo.InvariantCulture,
					$"The run needs {bytes / (1024.0 * 1024 * 1024):0.#} GB of frame cache on {drive.Name}, but only {drive.AvailableFreeSpace / (1024.0 * 1024 * 1024):0.#} GB is free. ")
				+ "Reduce the canvas, the steps or raise 'Visualize every', or turn the intensity preview off.");
		}
	}

	/// <summary>
	/// Deletes leftover stores that no running process holds open (a live store is opened without sharing,
	/// so deleting it fails and it is skipped). Call once at start-up.
	/// </summary>
	public static void Sweep()
	{
		if (!System.IO.Directory.Exists(Directory))
		{
			return;
		}
		foreach (string file in System.IO.Directory.GetFiles(Directory, "*" + Extension))
		{
			try
			{
				File.Delete(file);
				Logger.Debug("Deleted leftover frame cache {0}", file);
			}
			catch (IOException)
			{
				// in use by another instance
			}
			catch (UnauthorizedAccessException)
			{
				// in use by another instance
			}
		}
	}
}
