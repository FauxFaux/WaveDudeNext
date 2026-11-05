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

using Microsoft.AspNetCore.Http.HttpResults;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;

namespace WaveDudeNext.Server.Workspaces;

/// <summary>The workspace HTTP boundary; no project operations are exposed.</summary>
internal static class WorkspaceEndpoints
{
	/// <summary>Maps creation and a single status stream for each workspace.</summary>
	public static void MapWorkspaceEndpoints(this IEndpointRouteBuilder endpoints)
	{
		var group = endpoints.MapGroup("/api/workspaces");
		group.MapPost("/", Create);
		group.MapGet("/{workspaceId:guid}/events", Subscribe);
	}

	private static Created<WorkspaceStatus> Create(WorkspaceService workspaces)
	{
		var status = workspaces.Create();
		return TypedResults.Created($"/api/workspaces/{status.WorkspaceId}/events", status);
	}

	private static Results<ServerSentEventsResult<WorkspaceStatus>, ProblemHttpResult> Subscribe(
		Guid workspaceId, WorkspaceService workspaces, HttpContext context, IHostApplicationLifetime lifetime)
	{
		var workspace = workspaces.Find(workspaceId);
		if (workspace is null)
		{
			return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound,
				title: "Workspace not found", detail: "The workspace is not available on this server.");
		}
		return TypedResults.ServerSentEvents(Events(workspace, context.RequestAborted, lifetime.ApplicationStopping));
	}

	private static async IAsyncEnumerable<SseItem<WorkspaceStatus>> Events(
		Workspace workspace, CancellationToken requestAborted,
		[EnumeratorCancellation] CancellationToken stopping)
	{
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, stopping);
		await using var events = workspace.Subscribe(cancellation.Token).GetAsyncEnumerator();
		while (!cancellation.IsCancellationRequested)
		{
			try
			{
				if (!await events.MoveNextAsync())
				{
					break;
				}
			}
			catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
			{
				break;
			}
			yield return events.Current;
		}
	}
}
