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

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WaveDudeNext.Server.Workspaces;

namespace WaveDudeNext.Server.Projects;

/// <summary>A complete editable document and its workspace revision.</summary>
internal sealed record ProjectSnapshot(long Revision, JsonObject Project);

/// <summary>A complete edit, checked against the last acknowledged revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EditProjectRequest([property: JsonRequired] long ExpectedRevision, [property: JsonRequired] JsonElement Project);

/// <summary>A closed creation command; omitted coordinates use the canvas centre.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateObjectRequest([property: JsonRequired] long ExpectedRevision, [property: JsonRequired] string Kind, string? PartKind = null, int? X = null, int? Y = null);

/// <summary>A stale edit cannot replace a newer project.</summary>
internal sealed class ProjectConflictException : Exception;

/// <summary>HTTP boundaries for revisioned, in-memory project exchange.</summary>
internal static class ProjectEndpoints
{
	internal static void MapProjectEndpoints(this IEndpointRouteBuilder endpoints)
	{
		var group = endpoints.MapGroup("/api/workspaces/{workspaceId:guid}/project");
		group.MapGet("/", (Guid workspaceId, WorkspaceService workspaces) =>
			Execute(workspaceId, workspaces, workspace => workspace.GetProject()));
		group.MapPost("/", (Guid workspaceId, EditProjectRequest request, WorkspaceService workspaces) =>
			Execute(workspaceId, workspaces, workspace => workspace.SetProject(request.ExpectedRevision, request.Project)));
		group.MapPut("/", (Guid workspaceId, EditProjectRequest request, WorkspaceService workspaces) =>
			Execute(workspaceId, workspaces, workspace => workspace.SetProject(request.ExpectedRevision, request.Project)));
		group.MapPost("/objects", (Guid workspaceId, CreateObjectRequest request, WorkspaceService workspaces) =>
			Execute(workspaceId, workspaces, workspace => workspace.AddObject(request)));
	}

	private static IResult Execute(Guid workspaceId, WorkspaceService workspaces, Func<Workspace, ProjectSnapshot> operation)
	{
		var workspace = workspaces.Find(workspaceId);
		if (workspace is null) return Results.Problem(statusCode: 404, title: "Workspace not found");
		try { return Results.Ok(operation(workspace)); }
		catch (ProjectConflictException) { return Results.Problem(statusCode: 409, title: "Project revision conflict", detail: "Fetch the current project before retrying the edit."); }
		catch (JsonException error) { return Results.Problem(statusCode: 400, title: "Invalid project", detail: error.Message); }
	}
}
