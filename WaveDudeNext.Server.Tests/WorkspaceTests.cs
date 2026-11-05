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
using System.Net.ServerSentEvents;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WaveDudeNext.Server.Workspaces;

namespace WaveDudeNext.Server.Tests;

[TestClass]
public sealed class WorkspaceTests
{
	[TestMethod]
	public async Task CreationReturnsDistinctWorkspaceIdentitiesWithoutProjectState()
	{
		await using var factory = new WebApplicationFactory<Program>();
		using var client = factory.CreateClient();
		using var first = await client.PostAsync("/api/workspaces", null);
		using var second = await client.PostAsync("/api/workspaces", null);
		Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
		Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
		var a = await first.Content.ReadFromJsonAsync<WorkspaceStatus>();
		var b = await second.Content.ReadFromJsonAsync<WorkspaceStatus>();
		Assert.IsNotNull(a);
		Assert.IsNotNull(b);
		Assert.AreNotEqual(Guid.Empty, a.WorkspaceId);
		Assert.AreNotEqual(a.WorkspaceId, b.WorkspaceId);
		Assert.AreEqual(1L, a.StatusSequence);
		Assert.AreEqual(TimeSpan.Zero, a.CreatedAt.Offset);
		Assert.AreEqual($"/api/workspaces/{a.WorkspaceId}/events", first.Headers.Location?.OriginalString);
		using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
		CollectionAssert.AreEquivalent(new[] { "workspaceId", "createdAt", "statusSequence" },
			json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
	}

	[TestMethod]
	public async Task UnknownAndMalformedWorkspacesReturnProblemDetails()
	{
		await using var factory = new WebApplicationFactory<Program>();
		using var client = factory.CreateClient();
		foreach (var id in new[] { Guid.NewGuid().ToString(), "invalid" })
		{
			using var response = await client.GetAsync($"/api/workspaces/{id}/events");
			Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
			Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		}
	}

	[TestMethod]
	public async Task StreamSendsInitialSnapshotAndReconnectsToCurrentStatus()
	{
		await using var factory = new WebApplicationFactory<Program>();
		using var client = factory.CreateClient();
		using var creation = await client.PostAsync("/api/workspaces", null);
		var created = await creation.Content.ReadFromJsonAsync<WorkspaceStatus>();
		Assert.IsNotNull(created);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using (var response = await client.GetAsync(creation.Headers.Location,
			HttpCompletionOption.ResponseHeadersRead, timeout.Token))
		{
			Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
			Assert.IsTrue(response.Headers.CacheControl?.NoStore);
			await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
			await using var events = SseParser.Create(stream).EnumerateAsync(timeout.Token).GetAsyncEnumerator();
			Assert.IsTrue(await events.MoveNextAsync());
			Assert.AreEqual("workspace-status", events.Current.EventType);
			Assert.AreEqual("1", events.Current.EventId);
			Assert.AreEqual(created, JsonSerializer.Deserialize<WorkspaceStatus>(events.Current.Data, JsonSerializerOptions.Web));
		}

		var workspace = factory.Services.GetRequiredService<WorkspaceService>().Find(created.WorkspaceId);
		Assert.IsNotNull(workspace);
		workspace.PublishStatus();
		using var request = new HttpRequestMessage(HttpMethod.Get, creation.Headers.Location);
		request.Headers.Add("Last-Event-ID", "1");
		using var reconnect = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
		await using var reconnectedStream = await reconnect.Content.ReadAsStreamAsync(timeout.Token);
		await using var reconnectedEvents = SseParser.Create(reconnectedStream).EnumerateAsync(timeout.Token).GetAsyncEnumerator();
		Assert.IsTrue(await reconnectedEvents.MoveNextAsync());
		var current = JsonSerializer.Deserialize<WorkspaceStatus>(reconnectedEvents.Current.Data, JsonSerializerOptions.Web);
		Assert.AreEqual(workspace.Status, current);
		Assert.AreEqual("2", reconnectedEvents.Current.EventId);
	}

	[TestMethod]
	public async Task SlowSubscribersCoalesceWithoutAffectingOtherWorkspaces()
	{
		using var service = new WorkspaceService();
		var first = service.Find(service.Create().WorkspaceId)!;
		var second = service.Find(service.Create().WorkspaceId)!;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await using var slow = first.Subscribe(timeout.Token).GetAsyncEnumerator();
		await using var fast = first.Subscribe(timeout.Token).GetAsyncEnumerator();
		await using var isolated = second.Subscribe(timeout.Token).GetAsyncEnumerator();
		Assert.IsTrue(await slow.MoveNextAsync());
		Assert.IsTrue(await fast.MoveNextAsync());
		Assert.IsTrue(await isolated.MoveNextAsync());
		for (var i = 0; i < 100; i++)
		{
			first.PublishStatus();
			Assert.IsTrue(await fast.MoveNextAsync());
			Assert.AreEqual(i + 2L, fast.Current.Data.StatusSequence);
		}
		Assert.IsTrue(await slow.MoveNextAsync());
		Assert.AreEqual(101L, slow.Current.Data.StatusSequence);
		Assert.AreEqual(1L, second.Status.StatusSequence);
		second.PublishStatus();
		Assert.IsTrue(await isolated.MoveNextAsync());
		Assert.AreEqual(second.Status, isolated.Current.Data);
	}

	[TestMethod]
	public async Task DisconnectOnlyDetachesThatSubscriberAndShutdownCompletesStreams()
	{
		using var service = new WorkspaceService();
		var workspace = service.Find(service.Create().WorkspaceId)!;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var disconnect = new CancellationTokenSource();
		await using var detached = workspace.Subscribe(disconnect.Token).GetAsyncEnumerator();
		await using var remaining = workspace.Subscribe(timeout.Token).GetAsyncEnumerator();
		Assert.IsTrue(await detached.MoveNextAsync());
		Assert.IsTrue(await remaining.MoveNextAsync());
		var pending = detached.MoveNextAsync().AsTask();
		disconnect.Cancel();
		await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
		workspace.PublishStatus();
		Assert.IsTrue(await remaining.MoveNextAsync());
		Assert.AreEqual(2L, remaining.Current.Data.StatusSequence);
		service.Dispose();
		Assert.IsFalse(await remaining.MoveNextAsync());
	}

	[TestMethod]
	public async Task HostShutdownEndsHttpStreamWithoutAnUnhandledCancellation()
	{
		await using var factory = new WebApplicationFactory<Program>();
		using var client = factory.CreateClient();
		using var creation = await client.PostAsync("/api/workspaces", null);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var response = await client.GetAsync(creation.Headers.Location,
			HttpCompletionOption.ResponseHeadersRead, timeout.Token);
		await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
		await using var events = SseParser.Create(stream).EnumerateAsync(timeout.Token).GetAsyncEnumerator();
		Assert.IsTrue(await events.MoveNextAsync());
		var pending = events.MoveNextAsync().AsTask();
		factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
		Assert.IsFalse(await pending);
	}

	[TestMethod]
	public async Task IdleStreamSendsKeepaliveWithoutChangingStatusSequence()
	{
		using var workspace = new Workspace();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
		await using var events = workspace.Subscribe(timeout.Token).GetAsyncEnumerator();
		Assert.IsTrue(await events.MoveNextAsync());
		Assert.IsTrue(await events.MoveNextAsync());
		Assert.AreEqual("keepalive", events.Current.EventType);
		Assert.AreEqual(1L, events.Current.Data.StatusSequence);
	}

}
