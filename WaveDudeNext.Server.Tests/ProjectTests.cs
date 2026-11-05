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

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WaveDudeNext.Model;
using WaveDudeNext.Parts;
using WaveDudeNext.Server.Projects;
using WaveDudeNext.Server.Workspaces;

namespace WaveDudeNext.Server.Tests;

[TestClass]
public sealed class ProjectTests
{
	[TestMethod]
	public async Task GetAndPostExchangeWholeProjectAndRejectStaleEdits()
	{
		await using var factory = new WebApplicationFactory<Program>();
		using var client = factory.CreateClient();
		var workspace = await Create(client);
		var url = $"/api/workspaces/{workspace.WorkspaceId}/project";
		var initial = (await client.GetFromJsonAsync<ProjectSnapshot>(url))!;
		Assert.AreEqual(1L, initial.Revision);
		Assert.AreEqual("Source 1", initial.Project["sources"]![0]!["name"]!.GetValue<string>());
		var sourceId = initial.Project["sources"]![0]!["id"]!.GetValue<string>();
		initial.Project["sources"]![0]!["frequency"] = 125.5;
		using var edit = await client.PostAsJsonAsync(url, new { expectedRevision = initial.Revision, project = initial.Project });
		Assert.AreEqual(HttpStatusCode.OK, edit.StatusCode);
		var updated = (await edit.Content.ReadFromJsonAsync<ProjectSnapshot>())!;
		Assert.AreEqual(2L, updated.Revision);
		Assert.AreEqual(125.5, updated.Project["sources"]![0]!["frequency"]!.GetValue<double>());
		Assert.AreEqual(sourceId, updated.Project["sources"]![0]!["id"]!.GetValue<string>());
		using var stale = await client.PutAsJsonAsync(url, new { expectedRevision = initial.Revision, project = initial.Project });
		Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
		var current = (await client.GetFromJsonAsync<ProjectSnapshot>(url))!;
		Assert.IsTrue(JsonNode.DeepEquals(updated.Project, current.Project));
		var other = await Create(client);
		var isolated = (await client.GetFromJsonAsync<ProjectSnapshot>($"/api/workspaces/{other.WorkspaceId}/project"))!;
		Assert.AreEqual(100.0, isolated.Project["sources"]![0]!["frequency"]!.GetValue<double>());
	}

	[TestMethod]
	public async Task ObjectCreationUsesBackendDefaultsAndNormalisesDependentBounds()
	{
		await using var factory = new WebApplicationFactory<Program>();
		using var client = factory.CreateClient();
		var workspace = await Create(client);
		var url = $"/api/workspaces/{workspace.WorkspaceId}/project";
		await client.GetFromJsonAsync<ProjectSnapshot>(url);
		using var response = await client.PostAsJsonAsync(url + "/objects", new { expectedRevision = 1, kind = "source" });
		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		var snapshot = (await response.Content.ReadFromJsonAsync<ProjectSnapshot>())!;
		var source = snapshot.Project["sources"]![1]!;
		Assert.AreEqual("Source 2", source["name"]!.GetValue<string>());
		Assert.AreEqual(960, source["x"]!.GetValue<int>());
		Assert.AreEqual(540, source["y"]!.GetValue<int>());
		snapshot.Project["display"]!["exportFrom"] = 10;
		snapshot.Project["display"]!["exportTo"] = 5;
		using var normalised = await client.PutAsJsonAsync(url, new { expectedRevision = 2, project = snapshot.Project });
		Assert.AreEqual(HttpStatusCode.OK, normalised.StatusCode);
		var next = (await normalised.Content.ReadFromJsonAsync<ProjectSnapshot>())!;
		Assert.AreEqual(0, next.Project["display"]!["exportTo"]!.GetValue<int>());
	}

	[TestMethod]
	public async Task InvalidShapesRangesEnumsAndIdsLeaveCommittedProjectUntouched()
	{
		await using var factory = new WebApplicationFactory<Program>();
		using var client = factory.CreateClient();
		var workspace = await Create(client);
		var url = $"/api/workspaces/{workspace.WorkspaceId}/project";
		var initial = (await client.GetFromJsonAsync<ProjectSnapshot>(url))!;
		Action<JsonObject>[] corruptions = [
			project => project.Remove("scene"),
			project => project["sources"]![0]!["frequency"] = 256,
			project => project["sources"]![0]!["mode"] = "unknown",
			project => project["sources"]![0]!["mode"] = 0,
			project => project["sources"]![0]!["array"] = new JsonObject(),
			project => project["sources"]![0]!["id"] = "invalid",
			project => project["scene"]!["imageData"] = "injected bytes",
			project => project["scene"]!["imageAssetId"] = Guid.NewGuid().ToString(),
			project => project["scene"] = null,
			project => ((JsonArray)project["sources"]!).Add(project["sources"]![0]!.DeepClone()),
		];
		foreach (var corrupt in corruptions)
		{
			var document = (JsonObject)initial.Project.DeepClone();
			corrupt(document);
			using var response = await client.PostAsJsonAsync(url, new { expectedRevision = 1, project = document });
			Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, document.ToJsonString());
			Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		}
		var current = (await client.GetFromJsonAsync<ProjectSnapshot>(url))!;
		Assert.AreEqual(1L, current.Revision);
		Assert.IsTrue(JsonNode.DeepEquals(initial.Project, current.Project));
		using var missing = await client.GetAsync($"/api/workspaces/{Guid.NewGuid()}/project");
		Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
	}

	[TestMethod]
	public async Task ConcurrentEditsOnlyCommitOnce()
	{
		await using var factory = new WebApplicationFactory<Program>();
		using var client = factory.CreateClient();
		var workspace = await Create(client);
		var url = $"/api/workspaces/{workspace.WorkspaceId}/project";
		var snapshot = (await client.GetFromJsonAsync<ProjectSnapshot>(url))!;
		var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
			client.PostAsJsonAsync(url, new { expectedRevision = 1, project = snapshot.Project })));
		try { CollectionAssert.AreEquivalent(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(response => response.StatusCode).ToArray()); }
		finally { foreach (var response in responses) response.Dispose(); }
	}

	[TestMethod]
	public void EveryExampleAndPartRoundTripsWithoutComputedState()
	{
		var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
		var paths = Directory.GetFiles(Path.Combine(root, "Examples"), "*.wdproj");
		Assert.AreEqual(24, paths.Length);
		foreach (var path in paths)
		{
			var original = new ProjectDocument(Project.Load(path));
			var json = original.Write();
			var parsed = ProjectDocument.Read(JsonSerializer.SerializeToElement(json), original.Assets);
			Assert.IsTrue(JsonNode.DeepEquals(json, parsed.Write()), path);
			Assert.IsNull(json["scene"]!["imageData"]);
			Assert.IsNull(json["sources"]?.AsArray().FirstOrDefault()?["elements"]);
		}
		var model = new Project();
		foreach (var type in ProjectContract.PartKinds.Values) model.AddPart(type);
		model.AddSource().Array = new SourceArray();
		model.AddColorScheme();
		model.AddProbe();
		model.Normalize();
		var all = new ProjectDocument(model);
		var document = all.Write();
		var restored = ProjectDocument.Read(JsonSerializer.SerializeToElement(document), all.Assets);
		Assert.IsTrue(JsonNode.DeepEquals(document, restored.Write()));
	}

	[TestMethod]
	public void ReconciliationKeepsIdentitySchemeRenameAndGateFollowerRules()
	{
		var model = new Project();
		var scheme = model.AddColorScheme();
		model.Display.Field.ColorScheme = scheme.Name;
		var gate = model.AddPart<XorGatePart>();
		using (var follower = new GatePortFollower(model))
		{
			var port = gate.Ports()![0];
			model.AddSource(port.X, port.Y);
		}
		model.Normalize();
		var original = new ProjectDocument(model);
		var json = original.Write();
		json["colorSchemes"]![0]!["name"] = "Renamed";
		json["parts"]![0]!["x"] = gate.X + 100;
		var incoming = ProjectDocument.Read(JsonSerializer.SerializeToElement(json), original.Assets);
		var updated = original.Reconcile(incoming);
		Assert.AreEqual("Renamed", updated.Model.Display.Field.ColorScheme);
		Assert.AreEqual(model.Sources[0].X + 100, updated.Model.Sources[0].X);
		Assert.AreEqual(original.Ids[model.Sources[0]], updated.Ids[updated.Model.Sources[0]]);
		Assert.AreEqual(gate.X, model.Parts[0].X);
	}

	[TestMethod]
	public void MovingProbeKeepsAttachedGraphAndReorderingKeepsObjectIds()
	{
		var model = new Project();
		var probe = model.AddProbe();
		probe.BoxX = probe.AX;
		probe.BoxY = probe.AY;
		model.AddSource();
		model.AddSource();
		var original = new ProjectDocument(model);
		var json = original.Write();
		var firstId = json["sources"]![0]!["id"]!.GetValue<string>();
		var second = json["sources"]![1]!.DeepClone();
		json["sources"]![1] = json["sources"]![0]!.DeepClone();
		json["sources"]![0] = second;
		json["probes"]![0]!["ax"] = probe.AX + 100;
		var updated = original.Reconcile(ProjectDocument.Read(JsonSerializer.SerializeToElement(json), original.Assets));
		Assert.AreEqual(updated.Model.Probes[0].AX, updated.Model.Probes[0].BoxX);
		Assert.AreEqual(firstId, updated.Write()["sources"]![1]!["id"]!.GetValue<string>());
	}

	private static async Task<WorkspaceStatus> Create(HttpClient client)
	{
		using var response = await client.PostAsync("/api/workspaces", null);
		return (await response.Content.ReadFromJsonAsync<WorkspaceStatus>())!;
	}
}
