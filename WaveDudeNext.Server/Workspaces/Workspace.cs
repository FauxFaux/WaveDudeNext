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
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WaveDudeNext.Server.Workspaces;

/// <summary>Owns thread-safe workspace status and bounded subscriptions, independently of HTTP requests.</summary>
internal sealed class Workspace : IDisposable
{
	private readonly Lock gate = new();
	private readonly HashSet<Channel<WorkspaceStatus>> subscribers = [];
	private WorkspaceStatus status = new(Guid.NewGuid(), DateTimeOffset.UtcNow, 1);
	private bool disposed;

	/// <summary>Gets the current immutable snapshot under the workspace lock.</summary>
	public WorkspaceStatus Status
	{
		get
		{
			lock (gate)
			{
				return status;
			}
		}
	}

	/// <summary>Publishes a new status sequence without blocking on slow subscribers.</summary>
	public void PublishStatus()
	{
		lock (gate)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			status = status with { StatusSequence = checked(status.StatusSequence + 1) };
			foreach (var subscriber in subscribers)
			{
				subscriber.Writer.TryWrite(status);
			}
		}
	}

	/// <summary>Subscribes atomically with the initial snapshot; cancellation detaches only this view.</summary>
	public async IAsyncEnumerable<SseItem<WorkspaceStatus>> Subscribe(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var channel = Channel.CreateBounded<WorkspaceStatus>(new BoundedChannelOptions(1)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true,
			AllowSynchronousContinuations = false
		});
		lock (gate)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			subscribers.Add(channel);
			channel.Writer.TryWrite(status);
		}

		try
		{
			using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
			var available = channel.Reader.WaitToReadAsync(cancellationToken).AsTask();
			var tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
			while (true)
			{
				if (await Task.WhenAny(available, tick) == available)
				{
					if (!await available) yield break;
					if (channel.Reader.TryRead(out var snapshot))
					{
						yield return new SseItem<WorkspaceStatus>(snapshot, "workspace-status")
						{
							EventId = snapshot.StatusSequence.ToString(CultureInfo.InvariantCulture)
						};
					}
					available = channel.Reader.WaitToReadAsync(cancellationToken).AsTask();
				}
				else
				{
					if (!await tick) yield break;
					yield return new SseItem<WorkspaceStatus>(Status, "keepalive");
					tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
				}
			}
		}
		finally
		{
			lock (gate)
			{
				subscribers.Remove(channel);
				channel.Writer.TryComplete();
			}
		}
	}

	/// <summary>Completes all subscriptions when the server releases the workspace.</summary>
	public void Dispose()
	{
		lock (gate)
		{
			disposed = true;
			foreach (var subscriber in subscribers)
			{
				subscriber.Writer.TryComplete();
			}
			subscribers.Clear();
		}
	}
}
