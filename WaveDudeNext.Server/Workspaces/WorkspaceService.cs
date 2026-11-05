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

namespace WaveDudeNext.Server.Workspaces;

/// <summary>Owns isolated in-memory workspaces for the server lifetime. All access is thread-safe.</summary>
internal sealed class WorkspaceService : IDisposable
{
	private readonly Lock gate = new();
	private readonly Dictionary<Guid, Workspace> workspaces = [];
	private bool disposed;

	/// <summary>Creates an empty workspace without creating a project or starting any work.</summary>
	public WorkspaceStatus Create()
	{
		lock (gate)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			var workspace = new Workspace();
			var status = workspace.Status;
			workspaces.Add(status.WorkspaceId, workspace);
			return status;
		}
	}

	/// <summary>Finds a workspace by its server-issued identity.</summary>
	public Workspace? Find(Guid workspaceId)
	{
		lock (gate)
		{
			return workspaces.GetValueOrDefault(workspaceId);
		}
	}

	/// <summary>Releases workspace subscriptions on host disposal.</summary>
	public void Dispose()
	{
		lock (gate)
		{
			disposed = true;
			foreach (var workspace in workspaces.Values)
			{
				workspace.Dispose();
			}
			workspaces.Clear();
		}
	}
}
