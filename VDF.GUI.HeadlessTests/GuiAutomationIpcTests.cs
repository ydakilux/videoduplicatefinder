// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using VDF.Core;
using VDF.Core.Automation;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.GUI.Automation;
using VDF.GUI.ViewModels;
using Xunit;

namespace VDF.GUI.HeadlessTests;

public class GuiAutomationIpcTests {
	static readonly AutomationJsonContext JsonContext = AutomationJsonContext.Default;

	[Fact]
	public Task AutomationServer_BoundsRequestLinesWithoutWaitingForNewline() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		string pipeName = $"vdf-test-pipe-{Guid.NewGuid():N}";
		using var server = new GuiAutomationServer(vm, pipeName);
		server.Start();
		using var client = await ConnectAsync(pipeName);
		using var reader = new StreamReader(client, leaveOpen: true);
		using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
		const string command = """{"command":"get_status"}""";
		await writer.WriteLineAsync(command.PadRight(GuiAutomationServer.MaxRequestLineChars));
		Assert.True((await ReadResponseAsync(reader)).Success);
		await writer.WriteAsync(new string(' ', GuiAutomationServer.MaxRequestLineChars + 1));
		var response = await ReadResponseAsync(reader);
		Assert.False(response.Success);
		Assert.Contains($"{GuiAutomationServer.MaxRequestLineChars} characters", response.Error);
	});

	[Fact]
	public Task AutomationServer_RejectsNinthClientWithoutDispatching() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		string pipeName = $"vdf-test-pipe-{Guid.NewGuid():N}";
		using var server = new GuiAutomationServer(vm, pipeName);
		server.Start();
		var clients = new List<NamedPipeClientStream>();
		try {
			for (int i = 0; i < GuiAutomationServer.MaxConcurrentClients; i++) {
				var client = await ConnectAsync(pipeName);
				clients.Add(client);
				using var reader = new StreamReader(client, leaveOpen: true);
				using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
				await writer.WriteLineAsync("""{"command":"get_status"}""");
				Assert.True((await ReadResponseAsync(reader)).Success);
			}
			using var excess = await ConnectAsync(pipeName);
			using var excessReader = new StreamReader(excess, leaveOpen: true);
			using var excessWriter = new StreamWriter(excess, leaveOpen: true) { AutoFlush = true };
			await excessWriter.WriteLineAsync("""{"command":"get_status"}""");
			var response = await ReadResponseAsync(excessReader);
			Assert.False(response.Success);
			Assert.Contains("at most 8 concurrent clients", response.Error);
		}
		finally {
			foreach (var client in clients) client.Dispose();
		}
	});

	[Fact]
	public Task AutomationServer_DisconnectCancelsRunningHandler() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		string pipeName = $"vdf-test-pipe-{Guid.NewGuid():N}";
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var server = new GuiAutomationServer(vm, pipeName, async (_, _, token) => {
			started.SetResult();
			try {
				await Task.Delay(Timeout.InfiniteTimeSpan, token);
				return AutomationResponse.Fail("Handler should have been canceled.");
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested) {
				canceled.SetResult();
				throw;
			}
		});
		server.Start();
		using (var client = await ConnectAsync(pipeName)) {
			using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
			await writer.WriteLineAsync("""{"command":"get_status"}""");
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
		await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(server.IsRunning);
	});

	static async Task<NamedPipeClientStream> ConnectAsync(string pipeName) {
		var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		try {
			await client.ConnectAsync(5000);
			return client;
		}
		catch {
			client.Dispose();
			throw;
		}
	}

	static async Task<AutomationResponse> ReadResponseAsync(StreamReader reader) {
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var line = await reader.ReadLineAsync(timeout.Token);
		Assert.NotNull(line);
		return JsonSerializer.Deserialize(line, JsonContext.AutomationResponse)!;
	}

	[Fact]
	public Task AutomationServer_EndToEndIpcWorkflow_MultiClientAndNewTools() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		// Attach a tiny synthetic PNG header to the first item to test get_group_thumbnails
		byte[] fakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];
		vm.Duplicates[0].ItemInfo.SetThumbnails(new List<byte[]> { fakePng }, new List<TimeSpan> { TimeSpan.FromSeconds(12) });

		string pipeName = $"vdf-test-pipe-{Guid.NewGuid():N}";

		using var server = new GuiAutomationServer(vm, pipeName);
		server.Start();
		Assert.True(server.IsRunning);

		using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await client.ConnectAsync(3000);
		Assert.True(client.IsConnected);

		// Verify concurrent second client can connect while first client holds its persistent stream open!
		using var client2 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await client2.ConnectAsync(3000);
		Assert.True(client2.IsConnected);

		using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
		using var writer = new StreamWriter(client, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

		using var reader2 = new StreamReader(client2, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
		using var writer2 = new StreamWriter(client2, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

		async Task<AutomationResponse> SendOnAsync(StreamWriter w, StreamReader r, string command, Dictionary<string, JsonElement>? parameters = null) {
			var req = new AutomationRequest { Command = command, Parameters = parameters };
			await w.WriteLineAsync(JsonSerializer.Serialize(req, JsonContext.AutomationRequest));
			var resLine = await r.ReadLineAsync();
			Assert.NotNull(resLine);
			return JsonSerializer.Deserialize(resLine, JsonContext.AutomationResponse)!;
		}

		Task<AutomationResponse> SendCommandAsync(string command, Dictionary<string, JsonElement>? parameters = null) =>
			SendOnAsync(writer, reader, command, parameters);

		// 1. Both clients can query status concurrently
		var statusRes = await SendCommandAsync("get_status");
		var statusRes2 = await SendOnAsync(writer2, reader2, "get_status");
		Assert.True(statusRes.Success);
		Assert.True(statusRes2.Success);
		var statusElement = statusRes.Data;
		Assert.Equal(4, statusElement.GetProperty("totalDuplicates").GetInt32());
		Assert.Equal(2, statusElement.GetProperty("totalGroups").GetInt32());

		// 2. Get Groups with include_items = true -> verify QualityRanker isBest (1920px beats 1280px, NOT smallest size)
		var groupsRes = await SendCommandAsync("get_groups", new() { ["include_items"] = Param(true) });
		Assert.True(groupsRes.Success);
		var groupsElement = groupsRes.Data;
		Assert.Equal(2, groupsElement.GetProperty("totalCount").GetInt32());
		var firstGroup = groupsElement.GetProperty("groups")[0];
		Assert.Equal(@"D:\Videos\Holiday\beach_2019_final.mp4", firstGroup.GetProperty("bestItemPath").GetString());
		Assert.Equal("Resolution", firstGroup.GetProperty("bestReason").GetString());

		// 3. Get Group Thumbnails returns PNG MIME type & base64 frame
		var thumbRes = await SendCommandAsync("get_group_thumbnails", new() {
			["path"] = Param(@"D:\Videos\Holiday\beach_2019_final.mp4"),
			["load_if_missing"] = Param(false)
		});
		Assert.True(thumbRes.Success);
		var thumbElem = thumbRes.Data;
		Assert.False(thumbElem.GetProperty("truncated").GetBoolean());
		var firstThumbFrames = thumbElem.GetProperty("items")[0].GetProperty("frames");
		Assert.Equal(1, firstThumbFrames.GetArrayLength());
		Assert.Equal("image/png", firstThumbFrames[0].GetProperty("mimeType").GetString());

		// 4. Select by criteria (lowest-quality strategy)
		var selectRes = await SendCommandAsync("select_by_criteria", new() {
			["strategy"] = Param("lowest-quality"),
			["action"] = Param("check")
		});
		Assert.True(selectRes.Success);
		var selectElement = selectRes.Data;
		Assert.Equal(2, selectElement.GetProperty("modifiedCount").GetInt32());

		// 5. Audit Selection (should be healthy now)
		var auditRes1 = await SendCommandAsync("audit_selection");
		Assert.True(auditRes1.Success);
		var auditElement1 = auditRes1.Data;
		Assert.True(auditElement1.GetProperty("isSafe").GetBoolean());
		Assert.Equal(2, auditElement1.GetProperty("checkedCount").GetInt32());

		// 6. Force All Items in Group 1 to be checked (Unsafe condition)
		var setCheckedRes = await SendCommandAsync("set_checked", new() {
			["path"] = Param(@"D:\Videos\Holiday\beach_2019_final.mp4"),
			["checked"] = Param(true),
			["allow_all_in_group"] = Param(true)
		});
		Assert.True(setCheckedRes.Success);

		// 7. Audit Selection should now flag AllCheckedInGroup Error
		var auditRes2 = await SendCommandAsync("audit_selection");
		Assert.True(auditRes2.Success);
		var auditElement2 = auditRes2.Data;
		Assert.False(auditElement2.GetProperty("isSafe").GetBoolean());

		// 8. Undo the unsafe check
		var undoRes = await SendCommandAsync("undo_selection");
		Assert.True(undoRes.Success);
		Assert.False(vm.Duplicates[0].Checked);
		Assert.True(vm.Duplicates[1].Checked);
		Assert.Equal(2, vm.Duplicates.Count(item => item.Checked));

		// 9. Set Active Filter on Holiday path & test security guard
		var setFilterRes = await SendCommandAsync("set_active_filter", new() {
			["filter_by_path"] = Param("Holiday")
		});
		Assert.True(setFilterRes.Success);
		var setFilterElem = setFilterRes.Data;
		Assert.Equal(2, setFilterElem.GetProperty("visibleDuplicates").GetInt32());

		var securityCheckRes = await SendCommandAsync("set_checked", new() {
			["path"] = Param(@"E:\Archive\Old\wedding.mkv"),
			["checked"] = Param(true),
			["current_filter_only"] = Param(true)
		});
		Assert.False(securityCheckRes.Success);
		Assert.Contains("Security check failed", securityCheckRes.Error);

		// 10. Highlight Group & Reset Filter
		var highlightRes = await SendCommandAsync("highlight_group", new() {
			["path"] = Param(@"D:\Videos\Holiday\beach_2019_final.mp4")
		});
		Assert.True(highlightRes.Success);

		await SendCommandAsync("set_active_filter", new() { ["filter_by_path"] = Param("") });

		// 11. Remove single item from Group 2 from list -> singleton survivor is also dropped automatically
		var removeRes = await SendCommandAsync("remove_from_list", new() {
			["path"] = Param(@"E:\Archive\Old\wedding_small.mp4")
		});
		Assert.True(removeRes.Success);
		Assert.Equal(2, vm.Duplicates.Count); // Group 1's 2 items remain; Group 2 became singleton and dropped

		// Clear selection so global state stays clean
		await SendCommandAsync("clear_selection");

		// System.Text.Json cannot write NaN. A failed payload must not drop this client.
		var item = vm.Duplicates[0].ItemInfo;
		float originalSimilarity = item.Similarity;
		try {
			item.Similarity = float.NaN;
			var invalid = await SendCommandAsync("get_group_details", new() { ["group_id"] = Param(item.GroupId.ToString()) });
			Assert.False(invalid.Success);
			Assert.Equal(JsonValueKind.Null, invalid.Data.ValueKind);
			Assert.True((await SendCommandAsync("get_status")).Success);
		}
		finally {
			item.Similarity = originalSimilarity;
		}

		server.Stop();
		Assert.False(server.IsRunning);
	});

	static JsonElement Param(string value) => JsonSerializer.SerializeToElement(value, JsonContext.String);
	static JsonElement Param(bool value) => JsonElement.Parse(value ? "true" : "false");
	static JsonElement StringListParam(List<string> values) => JsonSerializer.SerializeToElement(values, JsonContext.ListString);

	[Theory]
	[InlineData("""{"command":"get_status","parameters":null}""")]
	[InlineData("""{"command":"get_status","parameters":{}}""")]
	[InlineData("""{"command":"get_groups","parameters":{"include_items":true,"offset":1,"minSimilarity":97.5,"path":"sample.mp4","paths":["one","two"],"unused":null,"extra":{"nested":false}}}""")]
	public void AutomationRequest_RoundTripsWireParameters(string json) {
		var request = JsonSerializer.Deserialize(json, JsonContext.AutomationRequest)!;
		var serialized = JsonSerializer.SerializeToElement(request, JsonContext.AutomationRequest);
		Assert.True(JsonElement.DeepEquals(JsonElement.Parse(json), serialized));
	}

	[Fact]
	public void AutomationResponse_UnserializableEnvelopeReturnsWireError() {
		var failed = GuiAutomationServer.SerializeResponse(new AutomationResponse { Success = true, Data = default });
		Assert.True(JsonElement.DeepEquals(
			JsonElement.Parse("""{"success":false,"error":"Server error serializing response.","data":null}"""),
			JsonElement.Parse(failed)));
	}

	[Fact]
	public void AutomationResponse_AllPayloadsRoundTripWithGeneratedMetadata() {
		var filter = new ActiveFilterDto { FilterByPath = "Holiday" };
		Assert.Equal("Holiday", RoundTripPayload(new ScanStatusDto { ActiveFilter = filter }, JsonContext.ScanStatusDto)
			.GetProperty("activeFilter").GetProperty("filterByPath").GetString());
		var filterResult = RoundTripPayload(new ActiveFilterResultDto(filter, 4, 3, 2, 1, 2, 1), JsonContext.ActiveFilterResultDto);
		Assert.True(JsonElement.DeepEquals(JsonElement.Parse("""
			{"activeFilter":{"filterByPath":"Holiday","fileType":"All","similarityFrom":0,"similarityTo":100,
			"onlyGroupsWithCheckedItems":false,"hideGroupsWithOneFileLeft":false,"onlyAiMatches":false},
			"totalDuplicates":4,"visibleDuplicates":3,"totalGroups":2,"visibleGroups":1,"checkedCount":2,"visibleCheckedCount":1}
			"""), filterResult));

		Guid groupId = Guid.Parse("aa30635e-b3d4-47ee-ab29-6bccebd3c8d2");
		var item = new DuplicateItemDetailDto { GroupId = groupId, Path = "keep.mp4", Duration = TimeSpan.FromSeconds(42) };
		var group = new DuplicateGroupSummaryDto { GroupId = groupId, Items = [item] };
		var groups = RoundTripPayload(new GroupsResultDto(2, 1, 1, [group]), JsonContext.GroupsResultDto);
		Assert.Equal(2, groups.GetProperty("totalCount").GetInt32());
		Assert.Equal(1, groups.GetProperty("offset").GetInt32());
		Assert.Equal(1, groups.GetProperty("limit").GetInt32());
		var groupJson = groups.GetProperty("groups")[0];
		Assert.False(groupJson.TryGetProperty("bestReason", out _));
		Assert.Equal("00:00:42", groupJson.GetProperty("items")[0].GetProperty("duration").GetString());
		var details = RoundTripPayload(new GroupDetailsResultDto(groupId, 1, null, null, [item]), JsonContext.GroupDetailsResultDto);
		Assert.Equal(groupId, details.GetProperty("groupId").GetGuid());
		Assert.Equal(1, details.GetProperty("count").GetInt32());
		Assert.Equal(JsonValueKind.Null, details.GetProperty("bestItemPath").ValueKind);
		Assert.Equal(JsonValueKind.Null, details.GetProperty("bestReason").ValueKind);
		var detail = details.GetProperty("items")[0];
		Assert.Equal("keep.mp4", detail.GetProperty("path").GetString());
		Assert.False(detail.TryGetProperty("partialClipOffset", out _));

		var thumbnails = RoundTripPayload(new GroupThumbnailsResultDto {
			GroupId = groupId,
			Items = [new() { Path = "keep.mp4", Frames = [new() { Base64Data = "AQID" }, new() { TimestampSeconds = 12 }] }]
		}, JsonContext.GroupThumbnailsResultDto);
		var frames = thumbnails.GetProperty("items")[0].GetProperty("frames");
		Assert.Equal("AQID", frames[0].GetProperty("base64Data").GetString());
		Assert.False(frames[0].TryGetProperty("timestampSeconds", out _));
		Assert.Equal(12d, frames[1].GetProperty("timestampSeconds").GetDouble());

		var audit = RoundTripPayload(new AuditResultDto {
			Violations = [new() { GroupId = groupId, Type = "AllCheckedInGroup" }]
		}, JsonContext.AuditResultDto);
		Assert.Equal("AllCheckedInGroup", audit.GetProperty("violations")[0].GetProperty("type").GetString());
		Assert.Equal(1, RoundTripPayload(new SelectResultDto { ModifiedCount = 1 }, JsonContext.SelectResultDto)
			.GetProperty("modifiedCount").GetInt32());
		Assert.Equal(JsonValueKind.Null, RoundTripPayload(new ScrollResultDto(), JsonContext.ScrollResultDto)
			.GetProperty("groupId").ValueKind);
		Assert.True(RoundTripPayload(new HighlightGroupResultDto(true, groupId, "Focused"), JsonContext.HighlightGroupResultDto)
			.GetProperty("highlighted").GetBoolean());
		Assert.Equal(groupId, RoundTripPayload(new MarkNotAMatchResultDto(1, [groupId], "Marked"), JsonContext.MarkNotAMatchResultDto)
			.GetProperty("groupIds")[0].GetGuid());
		Assert.False(RoundTripPayload(new RemoveFromListResultDto(1, false, "Removed"), JsonContext.RemoveFromListResultDto)
			.GetProperty("excludedFromFutureScans").GetBoolean());
		var rename = RoundTripPayload(new RenameItemResultDto("old.mp4", "new.mp4", "Renamed"), JsonContext.RenameItemResultDto);
		Assert.True(JsonElement.DeepEquals(JsonElement.Parse("""{"oldPath":"old.mp4","newPath":"new.mp4","message":"Renamed"}"""), rename));
		var transfer = RoundTripPayload(new TransferSuffixResultDto(true, 1, 1,
			[new(groupId, "blocked.mp4", "blocked__tag.mp4")], [new(groupId, "keep.mp4", "keep__tag.mp4", "__tag")]),
			JsonContext.TransferSuffixResultDto);
		Assert.True(JsonElement.DeepEquals(JsonElement.Parse($$"""
			{"preview":true,"count":1,"skippedConflictsCount":1,
			"skippedConflicts":[{"groupId":"{{groupId}}","oldPath":"blocked.mp4","conflictingPath":"blocked__tag.mp4"}],
			"renames":[{"groupId":"{{groupId}}","oldPath":"keep.mp4","newPath":"keep__tag.mp4","suffix":"__tag"}]}
			"""), transfer));
		Assert.Equal("Cleared", RoundTripPayload("Cleared", JsonContext.String).GetString());
	}

	static JsonElement RoundTripPayload<T>(T payload, JsonTypeInfo<T> typeInfo) {
		var json = JsonSerializer.Serialize(AutomationResponse.Ok(payload, typeInfo), JsonContext.AutomationResponse);
		var wire = JsonElement.Parse(json);
		Assert.True(wire.GetProperty("success").GetBoolean());
		Assert.Equal(JsonValueKind.Null, wire.GetProperty("error").ValueKind);
		var response = JsonSerializer.Deserialize(json, JsonContext.AutomationResponse)!;
		var decoded = response.Data.Deserialize(typeInfo);
		Assert.NotNull(decoded);
		var roundTripped = JsonSerializer.SerializeToElement(decoded!, typeInfo);
		Assert.True(JsonElement.DeepEquals(wire.GetProperty("data"), roundTripped));
		return roundTripped;
	}

	static async Task<AutomationResponse> Handle(MainWindowVM vm, string command, Dictionary<string, JsonElement> args, CancellationToken ct = default) {
		var requestJson = JsonSerializer.Serialize(new AutomationRequest { Command = command, Parameters = args }, JsonContext.AutomationRequest);
		var request = JsonSerializer.Deserialize(requestJson, JsonContext.AutomationRequest)!;
		var response = await GuiAutomationHandler.HandleAsync(request, vm, ct);
		return JsonSerializer.Deserialize(JsonSerializer.Serialize(response, JsonContext.AutomationResponse), JsonContext.AutomationResponse)!;
	}

	[Fact]
	public Task AutomationParameters_RejectOversizedInputsAndClampGroupPageLimits() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		var paths = StringListParam(Enumerable.Repeat("sample.mp4", 10_000).ToList());
		var groupIds = StringListParam(Enumerable.Repeat(Guid.NewGuid().ToString(), 5001).ToList());
		var overlongUtf8 = Param(new string('é', 2049));
		var cases = new (string Command, Dictionary<string, JsonElement> Args, string Error)[] {
			("set_checked", new() { ["paths"] = paths, ["path"] = Param("other.mp4"), ["checked"] = Param(true) }, "10000 entries"),
			("remove_from_list", new() { ["paths"] = paths, ["path"] = Param("other.mp4") }, "10000 entries"),
			("mark_not_a_match", new() { ["groupIds"] = groupIds, ["group_ids"] = groupIds }, "10000 entries"),
			("select_by_criteria", new() { ["expression"] = overlongUtf8 }, "4096 UTF-8 bytes"),
			("audit_selection", new() { ["criteriaExpression"] = overlongUtf8 }, "4096 UTF-8 bytes"),
			("set_active_filter", new() { ["filter_by_path"] = overlongUtf8 }, "4096 UTF-8 bytes"),
			("rename_item", new() { ["path"] = Param("sample.mp4"), ["new_name"] = Param(new string('a', 256)) }, "255 characters"),
			("transfer_suffix", new() { ["suffixPattern"] = Param(new string('a', 513)) }, "512 characters")
		};
		foreach (var (command, args, error) in cases) {
			var result = await Handle(vm, command, args);
			Assert.False(result.Success, command);
			Assert.Contains(error, result.Error);
		}
		Assert.Equal(4, vm.Duplicates.Count);
		Assert.All(vm.Duplicates, item => Assert.False(item.Checked));
		var groups = await Handle(vm, "get_groups", new() { ["limit"] = JsonElement.Parse("601") });
		Assert.Equal(500, groups.Data.GetProperty("limit").GetInt32());
		var groupsWithItems = await Handle(vm, "get_groups", new() { ["limit"] = JsonElement.Parse("601"), ["include_items"] = Param(true) });
		Assert.Equal(100, groupsWithItems.Data.GetProperty("limit").GetInt32());
		Assert.Equal(2, groupsWithItems.Data.GetProperty("totalCount").GetInt32());
	});

	[Fact]
	public Task GroupThumbnails_TruncateItemCountAndAggregateRawBytes() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		var first = vm.Duplicates[0];
		var second = vm.Duplicates[1];
		for (int i = 0; i < 63; i++) AddRemovalTestItem(vm, first, $"thumbnail-{i}.jpg");
		var args = new Dictionary<string, JsonElement> {
			["group_id"] = Param(first.ItemInfo.GroupId.ToString()), ["load_if_missing"] = Param(false)
		};
		var response = await Handle(vm, "get_group_thumbnails", args);
		Assert.True(response.Success, response.Error);
		Assert.True(response.Data.GetProperty("truncated").GetBoolean());
		Assert.Equal(64, response.Data.GetProperty("items").GetArrayLength());

		while (vm.Duplicates.Count > 4) vm.Duplicates.RemoveAt(vm.Duplicates.Count - 1);
		var payload = new byte[4 * 1024 * 1024 + 1];
		first.ItemInfo.SetThumbnails([payload], [TimeSpan.Zero]);
		second.ItemInfo.SetThumbnails([payload], [TimeSpan.Zero]);
		response = await Handle(vm, "get_group_thumbnails", args);
		Assert.True(response.Success, response.Error);
		Assert.True(response.Data.GetProperty("truncated").GetBoolean());
		var items = response.Data.GetProperty("items");
		Assert.Equal(2, items.GetArrayLength());
		Assert.Equal(payload.Length, items[0].GetProperty("frames")[0].GetProperty("base64Data").GetBytesFromBase64().Length);
		Assert.Equal(0, items[1].GetProperty("frames").GetArrayLength());
	});

	[Fact]
	public Task MutatingCommands_WaitForDbMutationAndCancelWhileQueued() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		using var dir = new RenameTestDirectory();
		string? oldFolder = DatabaseUtils.CustomDatabaseFolder;
		bool oldBackup = VDF.GUI.Data.SettingsFile.Instance.BackupAfterListChanged;
		DatabaseUtils.CustomDatabaseFolder = dir.Root;
		DatabaseUtils.InvalidateDatabaseFolder();
		VDF.GUI.Data.SettingsFile.Instance.BackupAfterListChanged = false;
		try {
			using var queuedCancellation = new CancellationTokenSource();
			string firstPath = vm.Duplicates[0].ItemInfo.Path;
			string secondPath = vm.Duplicates[2].ItemInfo.Path;
			Task<AutomationResponse>? second = null;
			Task<AutomationResponse>? canceled = null;
			bool wasQueued = false;
			vm.PropertyChanged += (_, e) => {
				if (e.PropertyName != nameof(vm.IsBusy) || !vm.IsBusy || second != null) return;
				second = Handle(vm, "remove_from_list", new() { ["path"] = Param(secondPath) });
				canceled = Handle(vm, "set_active_filter", new() { ["filter_by_path"] = Param("does-not-match") }, queuedCancellation.Token);
				wasQueued = !second.IsCompleted && !canceled.IsCompleted;
				queuedCancellation.Cancel();
			};
			var first = await Handle(vm, "remove_from_list", new() { ["path"] = Param(firstPath) });
			Assert.True(first.Success, first.Error);
			Assert.True(wasQueued);
			Assert.NotNull(second);
			Assert.NotNull(canceled);
			var secondResult = await second;
			Assert.True(secondResult.Success, secondResult.Error);
			Assert.Equal(1, secondResult.Data.GetProperty("removedCount").GetInt32());
			var canceledResult = await canceled;
			Assert.False(canceledResult.Success);
			Assert.Contains("canceled", canceledResult.Error);
			Assert.False(vm.IsBusy);
			Assert.Empty(vm.Duplicates);
		}
		finally {
			DatabaseUtils.CustomDatabaseFolder = oldFolder;
			DatabaseUtils.InvalidateDatabaseFolder();
			VDF.GUI.Data.SettingsFile.Instance.BackupAfterListChanged = oldBackup;
		}
	});

	[Fact]
	public Task MarkNotAMatch_SaveFailureReturnsWithoutDialogOrListChanges() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		using var dir = new RenameTestDirectory();
		var settings = VDF.GUI.Data.SettingsFile.Instance;
		var oldFolder = settings.CustomDatabaseFolder;
		settings.CustomDatabaseFolder = dir.Root;
		try {
			Directory.CreateDirectory(vm.BlacklistedGroupsFile);
			var mark = Handle(vm, "mark_not_a_match", new() { ["group_id"] = Param(vm.Duplicates[0].ItemInfo.GroupId.ToString()) });
			var result = await mark.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(result.Success);
			Assert.Contains("0 group(s) marked", result.Error);
			Assert.False(vm.IsBusy);
			Assert.Equal(4, vm.Duplicates.Count);
		}
		finally {
			settings.CustomDatabaseFolder = oldFolder;
		}
	});

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public Task SelectionOrAudit_CancelsDuringCriteriaWithoutMutatingOrReturningPartialData(bool audit, bool throwFromPredicate) => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		vm.Duplicates.Clear();
		using var cancellation = new CancellationTokenSource();
		const int cancelAt = 64;
		int evaluated = 0;
		bool armed = false;
		var groupId = Guid.NewGuid();
		for (int i = 0; i < 512; i++) {
			var item = new CriteriaProbeItem(() => {
				if (!armed || ++evaluated != cancelAt) return;
				cancellation.Cancel();
				if (throwFromPredicate) cancellation.Token.ThrowIfCancellationRequested();
			}) { GroupId = groupId, Path = $"probe-{i}.mp4", Folder = string.Empty };
			vm.Duplicates.Add(new DuplicateItemVM(item) { Checked = audit });
		}
		armed = true;
		var result = await Handle(vm, audit ? "audit_selection" : "select_by_criteria", new() {
			[audit ? "criteria_expression" : "expression"] = Param("item.ToString() == \"match\""),
			["current_filter_only"] = Param(false),
			["allow_all_in_group"] = Param(true)
		}, cancellation.Token);
		Assert.Equal(cancelAt, evaluated);
		Assert.False(result.Success);
		Assert.Contains("canceled", result.Error);
		Assert.Equal(JsonValueKind.Null, result.Data.ValueKind);
		Assert.All(vm.Duplicates, item => Assert.Equal(audit, item.Checked));
	});

	sealed class CriteriaProbeItem(Action onEvaluation) : DuplicateItem {
		public override string ToString() {
			onEvaluation();
			return "match";
		}
	}

	[Fact]
	public Task SetChecked_CancellationStopsBeforeNextItemAndKeepsUndo() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		using var cancellation = new CancellationTokenSource();
		var groupId = vm.Duplicates[0].ItemInfo.GroupId;
		foreach (var item in vm.Duplicates) {
			item.PropertyChanged += (_, e) => {
				if (e.PropertyName == nameof(item.Checked) && item.Checked) cancellation.Cancel();
			};
		}
		var result = await Handle(vm, "set_checked", new() {
			["group_id"] = Param(groupId.ToString()), ["checked"] = Param(true), ["allow_all_in_group"] = Param(true)
		}, cancellation.Token);
		Assert.False(result.Success);
		Assert.Contains("canceled", result.Error);
		Assert.Single(vm.Duplicates, item => item.Checked);
		Assert.True((await Handle(vm, "undo_selection", new())).Success);
		Assert.All(vm.Duplicates, item => Assert.False(item.Checked));
	});

	[Fact]
	public Task RenameItem_CanceledAtCommitDoesNotMoveFile() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		using var dir = new RenameTestDirectory();
		using var cancellation = new CancellationTokenSource();
		string source = vm.Duplicates[0].ItemInfo.Path = dir.File("videos", "source.mp4");
		vm.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(vm.IsBusy) && vm.IsBusy) cancellation.Cancel();
		};
		var result = await Handle(vm, "rename_item", new() { ["path"] = Param(source), ["new_name"] = Param("renamed") }, cancellation.Token);
		Assert.False(result.Success);
		Assert.Contains("canceled", result.Error);
		Assert.True(File.Exists(source));
		Assert.False(File.Exists(Path.Combine(dir.Root, "videos", "renamed.mp4")));
		Assert.Equal(source, vm.Duplicates[0].ItemInfo.Path);
		Assert.False(vm.IsBusy);
	});

	[Fact]
	public Task PathMutations_RejectAmbiguityEvenAcrossFilters_WithoutChangingAnyItem() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		var first = vm.Duplicates[0];
		var other = vm.Duplicates[2];
		using var dir = new RenameTestDirectory();
		first.ItemInfo.Path = dir.File("visible", "copy.mp4");
		other.ItemInfo.Path = dir.File("hidden", "copy.mp4");
		vm.FilterByPath = "visible";
		vm.RefreshResultsView();
		Assert.True(first.IsVisibleInFilter);
		Assert.False(other.IsVisibleInFilter);

		var response = await Handle(vm, "set_item_checked", new() { ["paths"] = StringListParam([first.ItemInfo.Path, "copy.mp4"]), ["checked"] = Param(true) });
		Assert.False(response.Success);
		Assert.Contains("Ambiguous", response.Error);
		Assert.All(vm.Duplicates, item => Assert.False(item.Checked));

		response = await Handle(vm, "remove_from_list", new() { ["path"] = Param("copy.mp4"), ["checked_only"] = Param(true) });
		Assert.False(response.Success);
		Assert.Contains("Ambiguous", response.Error);
		Assert.Equal(4, vm.Duplicates.Count);

		response = await Handle(vm, "rename_item", new() { ["path"] = Param("copy.mp4"), ["new_name"] = Param("renamed") });
		Assert.False(response.Success);
		Assert.Contains("Ambiguous", response.Error);
		Assert.True(File.Exists(first.ItemInfo.Path));
		Assert.True(File.Exists(other.ItemInfo.Path));
		response = await Handle(vm, "set_item_checked", new() { ["path"] = Param(first.ItemInfo.Path), ["checked"] = Param(true) });
		Assert.True(response.Success, response.Error);
		Assert.True(first.Checked);
		Assert.False(other.Checked);

		// Even a full path is ambiguous if multiple results refer to it.
		other.ItemInfo.Path = first.ItemInfo.Path;
		response = await Handle(vm, "set_item_checked", new() { ["path"] = Param(first.ItemInfo.Path), ["checked"] = Param(false) });
		Assert.False(response.Success);
		Assert.Contains("Ambiguous", response.Error);
		Assert.True(first.Checked);
	});

	static DuplicateItemVM AddRemovalTestItem(MainWindowVM vm, DuplicateItemVM groupMember, string name) {
		var item = new DuplicateItemVM(new DuplicateItem {
			Path = name, Folder = string.Empty, GroupId = groupMember.ItemInfo.GroupId, Similarity = 100f
		});
		vm.Duplicates.Add(item);
		return item;
	}

	[Theory]
	[InlineData("path")]
	[InlineData("paths")]
	[InlineData("group_id")]
	[InlineData("union")]
	public Task RemoveFromList_CheckedOnlyIntersectsExplicitScope(string scope) => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		var target = vm.Duplicates[0];
		var uncheckedGroupMember = vm.Duplicates[1];
		var otherTarget = vm.Duplicates[2];
		// Keep enough survivors that normal singleton pruning does not affect the assertions.
		AddRemovalTestItem(vm, target, "extra-holiday.mp4");
		var outsideScope = AddRemovalTestItem(vm, otherTarget, "extra-wedding.mp4");
		vm.RebuildResultsList();
		target.Checked = otherTarget.Checked = outsideScope.Checked = true;
		var args = new Dictionary<string, JsonElement> { ["checked_only"] = Param(true) };
		if (scope is "path" or "union") args["path"] = Param(target.ItemInfo.Path);
		if (scope == "paths") args["paths"] = StringListParam([target.ItemInfo.Path, uncheckedGroupMember.ItemInfo.Path]);
		if (scope is "group_id" or "union") args["group_id"] = Param(target.ItemInfo.GroupId.ToString());
		if (scope == "union") args["paths"] = StringListParam([otherTarget.ItemInfo.Path]);

		var response = await Handle(vm, "remove_from_list", args);

		Assert.True(response.Success, response.Error);
		Assert.Equal(scope == "union" ? 2 : 1, response.Data.GetProperty("removedCount").GetInt32());
		Assert.DoesNotContain(target, vm.Duplicates);
		Assert.Contains(uncheckedGroupMember, vm.Duplicates);
		Assert.Contains(outsideScope, vm.Duplicates);
		Assert.Equal(scope != "union", vm.Duplicates.Contains(otherTarget));
	});

	[Fact]
	public Task RemoveFromList_CheckedOnlyDoesNotBroadenInvalidOrEmptyScope() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		vm.Duplicates[0].Checked = true;
		var invalid = await Handle(vm, "remove_from_list", new() { ["checked_only"] = Param(true), ["group_id"] = Param("not-a-guid") });
		Assert.False(invalid.Success);
		var unmatched = await Handle(vm, "remove_from_list", new() { ["checked_only"] = Param(true), ["group_id"] = Param(Guid.NewGuid().ToString()) });
		Assert.True(unmatched.Success, unmatched.Error);
		Assert.Equal(0, unmatched.Data.GetProperty("removedCount").GetInt32());
		var emptyPaths = await Handle(vm, "remove_from_list", new() { ["checked_only"] = Param(true), ["paths"] = StringListParam([]) });
		Assert.True(emptyPaths.Success, emptyPaths.Error);
		Assert.Equal(0, emptyPaths.Data.GetProperty("removedCount").GetInt32());
		Assert.Equal(4, vm.Duplicates.Count);
	});

	[Fact]
	public Task RemoveFromList_CheckedOnlyWithoutScopeUsesActiveFilter() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		var visible = vm.Duplicates[0];
		var hidden = vm.Duplicates[2];
		visible.Checked = hidden.Checked = true;
		vm.FilterByPath = "Holiday";
		vm.RefreshResultsView();
		Assert.True(visible.IsVisibleInFilter);
		Assert.False(hidden.IsVisibleInFilter);

		var response = await Handle(vm, "remove_from_list", new() { ["checked_only"] = Param(true) });

		Assert.True(response.Success, response.Error);
		Assert.Equal(1, response.Data.GetProperty("removedCount").GetInt32());
		Assert.DoesNotContain(visible, vm.Duplicates);
		Assert.Contains(hidden, vm.Duplicates);
	});

	[Fact]
	public Task SetItemChecked_RequiresBooleanAndProtectsLastKeptCopy() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		var first = vm.Duplicates[0];
		var other = vm.Duplicates[1];
		var response = await Handle(vm, "set_item_checked", new() { ["path"] = Param(first.ItemInfo.Path) });
		Assert.False(response.Success);
		Assert.Contains("'checked'", response.Error);
		response = await Handle(vm, "set_item_checked", new() { ["path"] = Param(first.ItemInfo.Path), ["checked"] = Param("true") });
		Assert.False(response.Success);
		Assert.False(first.Checked);

		response = await Handle(vm, "set_item_checked", new() { ["group_id"] = Param(first.ItemInfo.GroupId.ToString()), ["checked"] = Param(true) });
		Assert.False(response.Success);
		Assert.All(vm.Duplicates, item => Assert.False(item.Checked));

		other.Checked = true;
		vm.FilterSimilarityFrom = 99;
		Assert.False(other.IsVisibleInFilter);
		response = await Handle(vm, "set_item_checked", new() { ["path"] = Param(first.ItemInfo.Path), ["checked"] = Param(true), ["allow_all_in_group"] = Param("true") });
		Assert.False(response.Success);
		Assert.Contains("every copy", response.Error);
		Assert.False(first.Checked);

		response = await Handle(vm, "set_item_checked", new() { ["path"] = Param(first.ItemInfo.Path), ["checked"] = Param(true), ["allowAllInGroup"] = Param(true) });
		Assert.True(response.Success, response.Error);
		Assert.True(first.Checked);
		response = await Handle(vm, "set_item_checked", new() { ["path"] = Param(first.ItemInfo.Path), ["checked"] = Param(false) });
		Assert.True(response.Success, response.Error);
		Assert.False(first.Checked);
	});

	[Fact]
	public Task RenameItem_OnlyAcceptsSameDirectoryNameAndNeverOverwrites() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		var item = vm.Duplicates[0].ItemInfo;
		using var dir = new RenameTestDirectory();
		string source = item.Path = dir.File("videos", "source.mp4");
		string target = Path.Combine(dir.Root, "videos", "renamed.mp4");
		File.WriteAllText(source + ".jpg", "cover");
		foreach (string name in new[] { "../escaped", "sub/new.mp4", @"sub\new.mp4", Path.Combine(dir.Root, "escaped.mp4"), "C:escape.mp4", ".", ".." }) {
			var invalid = await Handle(vm, "rename_item", new() { ["path"] = Param(source), ["new_name"] = Param(name) });
			Assert.False(invalid.Success);
			Assert.Contains("same directory", invalid.Error);
			Assert.Equal(source, item.Path);
			Assert.True(File.Exists(source));
		}
		File.WriteAllText(target + ".jpg", "unrelated");
		var response = await Handle(vm, "rename_item", new() { ["path"] = Param(source), ["new_name"] = Param("renamed") });
		Assert.False(response.Success);
		Assert.Equal("unrelated", File.ReadAllText(target + ".jpg"));
		Assert.Equal(source, item.Path);
		File.Delete(target + ".jpg");
		var staleDestination = new FileEntry { Path = target };
		DatabaseUtils.Database.Add(staleDestination);
		try {
			response = await Handle(vm, "rename_item", new() { ["path"] = Param(source), ["new_name"] = Param("renamed") });
			Assert.False(response.Success);
			Assert.Contains("already exists", response.Error);
			Assert.Equal(source, item.Path);
			Assert.True(File.Exists(source));
		}
		finally {
			DatabaseUtils.Database.Remove(staleDestination);
		}

		response = await Handle(vm, "rename_item", new() { ["path"] = Param("source.mp4"), ["new_name"] = Param("renamed") });
		Assert.True(response.Success, response.Error);
		Assert.Equal(target, item.Path);
		Assert.False(File.Exists(source));
		Assert.False(File.Exists(source + ".jpg"));
		Assert.True(File.Exists(target));
		Assert.Equal("cover", File.ReadAllText(target + ".jpg"));
	});

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public Task RenameItem_RollsBackDiskDatabaseAndModelOnFailure(bool failSavingDatabase, bool blockCoverRollback) => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		var item = vm.Duplicates[0].ItemInfo;
		var cover = vm.Duplicates[2].ItemInfo;
		using var dir = new RenameTestDirectory();
		string source = item.Path = dir.File("videos", "source.mp4");
		string sourceCover = cover.Path = dir.File("videos", "source.mp4.jpg");
		string target = Path.Combine(dir.Root, "videos", "renamed.mp4");
		string targetCover = target + ".jpg";
		string stagedDb = Path.Combine(dir.Root, "ScannedFiles_new.db");
		string? oldDbFolder = DatabaseUtils.CustomDatabaseFolder;
		DatabaseUtils.CustomDatabaseFolder = dir.Root;
		DatabaseUtils.InvalidateDatabaseFolder();
		var entry = new FileEntry(source);
		var coverEntry = new FileEntry(sourceCover);
		DatabaseUtils.Database.Add(entry);
		DatabaseUtils.Database.Add(coverEntry);
		try {
			if (failSavingDatabase) Directory.CreateDirectory(Path.Combine(dir.Root, "ScannedFiles.db"));
			cover.PropertyChanged += (_, e) => {
				if (e.PropertyName != nameof(cover.Path) || cover.Path != targetCover) return;
				if (!failSavingDatabase) throw new InvalidOperationException("Injected model update failure");
				// Simulate the old sidecar path being reused while the file is at its new path.
				if (blockCoverRollback) Directory.CreateDirectory(sourceCover);
			};

			var response = await Handle(vm, "rename_item", new() { ["path"] = Param(source), ["new_name"] = Param("renamed") });
			Assert.False(response.Success);
			Assert.Contains(blockCoverRollback ? "Rollback incomplete" : "Changes rolled back", response.Error);
			Assert.Equal(source, item.Path);
			string actualCover = blockCoverRollback ? targetCover : sourceCover;
			Assert.Equal(actualCover, cover.Path);
			Assert.True(File.Exists(source));
			Assert.True(File.Exists(actualCover));
			Assert.False(File.Exists(target));
			Assert.False(File.Exists(blockCoverRollback ? sourceCover : targetCover));
			Assert.True(ScanEngine.GetFromDatabase(source, out var restoredEntry));
			Assert.True(ScanEngine.GetFromDatabase(actualCover, out var restoredCoverEntry));
			Assert.Same(entry, restoredEntry);
			Assert.Same(coverEntry, restoredCoverEntry);

			// A complete rollback discards the original stage. An incomplete disk rollback
			// retries the save with reconciled paths; the blocked main DB leaves that new stage.
			Assert.Equal(blockCoverRollback, File.Exists(stagedDb));
			if (blockCoverRollback) {
				// Exercise startup recovery without replacing other tests' live database state.
				var wrapperField = typeof(DatabaseUtils).GetField("DbWrapper",
					System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
				var originalWrapper = wrapperField.GetValue(null);
				File.WriteAllText(Path.Combine(dir.Root, "ScannedFiles.fpheal1"), "Skip migration marker writes during test recovery.");
				try {
					Assert.True(DatabaseUtils.LoadDatabase());
					Assert.True(ScanEngine.GetFromDatabase(source, out var recoveredEntry));
					Assert.True(ScanEngine.GetFromDatabase(targetCover, out var recoveredCover));
					Assert.NotSame(entry, recoveredEntry);
					Assert.NotSame(coverEntry, recoveredCover);
					Assert.DoesNotContain(new FileEntry { Path = target }, DatabaseUtils.Database);
					Assert.DoesNotContain(new FileEntry { Path = sourceCover }, DatabaseUtils.Database);
				}
				finally {
					wrapperField.SetValue(null, originalWrapper);
				}
			}
		}
		finally {
			DatabaseUtils.Database.Remove(entry);
			DatabaseUtils.Database.Remove(coverEntry);
			DatabaseUtils.CustomDatabaseFolder = oldDbFolder;
			DatabaseUtils.InvalidateDatabaseFolder();
		}
	});

	[Fact]
	public Task TransferSuffix_OmittedPreviewPlansWithoutChanges_ExplicitFalseAppliesTransaction() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		var kept = vm.Duplicates[0].ItemInfo;
		var donor = vm.Duplicates[1];
		using var dir = new RenameTestDirectory();
		string source = kept.Path = dir.File("videos", "keep.mp4");
		string sourceCover = dir.File("videos", "keep.mp4.jpg");
		string donorPath = donor.ItemInfo.Path = dir.File("videos", "copy__tag.mp4");
		donor.Checked = true;
		string target = Path.Combine(dir.Root, "videos", "keep__tag.mp4");
		string targetCover = target + ".jpg";
		string dbPath = Path.Combine(dir.Root, "ScannedFiles.db");
		string? oldDbFolder = DatabaseUtils.CustomDatabaseFolder;
		DatabaseUtils.CustomDatabaseFolder = dir.Root;
		DatabaseUtils.InvalidateDatabaseFolder();
		var entry = new FileEntry(source);
		DatabaseUtils.Database.Add(entry);
		try {
			var args = new Dictionary<string, JsonElement> { ["current_filter_only"] = Param(false) };
			var response = await Handle(vm, "transfer_suffix_and_rename", args);
			Assert.True(response.Success, response.Error);
			var plan = response.Data.Deserialize(JsonContext.TransferSuffixResultDto)!;
			Assert.True(plan.Preview);
			var rename = Assert.Single(plan.Renames);
			Assert.Equal(source, rename.OldPath);
			Assert.Equal(target, rename.NewPath);
			Assert.Equal(source, kept.Path);
			Assert.True(File.Exists(source));
			Assert.True(File.Exists(sourceCover));
			Assert.False(File.Exists(target));
			Assert.False(File.Exists(targetCover));
			Assert.True(ScanEngine.GetFromDatabase(source, out var originalEntry));
			Assert.Same(entry, originalEntry);
			Assert.False(File.Exists(dbPath));

			args["preview"] = Param(false);
			response = await Handle(vm, "transfer_suffix_and_rename", args);
			Assert.True(response.Success, response.Error);
			var applied = response.Data.Deserialize(JsonContext.TransferSuffixResultDto)!;
			Assert.False(applied.Preview);
			Assert.Equal(rename, Assert.Single(applied.Renames));
			Assert.Equal(target, kept.Path);
			Assert.False(File.Exists(source));
			Assert.False(File.Exists(sourceCover));
			Assert.Equal("keep.mp4", File.ReadAllText(target));
			Assert.Equal("keep.mp4.jpg", File.ReadAllText(targetCover));
			Assert.Equal(donorPath, donor.ItemInfo.Path);
			Assert.True(File.Exists(donorPath));
			Assert.False(ScanEngine.GetFromDatabase(source, out _));
			Assert.True(ScanEngine.GetFromDatabase(target, out var movedEntry));
			Assert.Same(entry, movedEntry);
			Assert.True(File.Exists(dbPath));
		}
		finally {
			DatabaseUtils.Database.Remove(entry);
			DatabaseUtils.CustomDatabaseFolder = oldDbFolder;
			DatabaseUtils.InvalidateDatabaseFolder();
		}
	});

	[Fact]
	public Task AutomationRegexTimeouts_ReturnErrorsBeforeAnyMutation() => HeadlessUi.Run(async () => {
		var vm = ResultsFixture.CreatePopulatedViewModel();
		using var dir = new RenameTestDirectory();
		string original = vm.Duplicates[0].ItemInfo.Path = dir.File("videos", "keep.mp4");
		vm.Duplicates[1].ItemInfo.Path = dir.File("videos", "copy__tag.mp4");
		vm.Duplicates[2].ItemInfo.Path = new string('a', 8192) + "!.mp4";
		const string expression = "Regex.IsMatch(item.Path, \"^(a+)+$\")";
		var response = await Handle(vm, "select_by_criteria", new() { ["expression"] = Param(expression), ["current_filter_only"] = Param(false) });
		Assert.False(response.Success);
		Assert.Contains("timed out", response.Error);
		Assert.All(vm.Duplicates, item => Assert.False(item.Checked));

		vm.Duplicates[1].Checked = true;
		vm.Duplicates[2].Checked = true;
		response = await Handle(vm, "audit_selection", new() { ["criteria_expression"] = Param(expression), ["current_filter_only"] = Param(false) });
		Assert.False(response.Success);
		Assert.Contains("timed out", response.Error);

		response = await Handle(vm, "transfer_suffix_and_rename", new() {
			["suffix_pattern"] = Param("(_{2,}.*)$|^(a+)+$"), ["current_filter_only"] = Param(false), ["preview"] = Param(false)
		});
		Assert.False(response.Success);
		Assert.Contains("timed out", response.Error);
		Assert.Equal(original, vm.Duplicates[0].ItemInfo.Path);
		Assert.True(File.Exists(original));
		Assert.False(File.Exists(Path.Combine(dir.Root, "videos", "keep__tag.mp4")));
	});

	sealed class RenameTestDirectory : IDisposable {
		public string Root { get; } = Directory.CreateTempSubdirectory("vdf-automation-").FullName;
		public string File(string folder, string name) {
			var directory = Directory.CreateDirectory(Path.Combine(Root, folder));
			string path = Path.Combine(directory.FullName, name);
			System.IO.File.WriteAllText(path, name);
			return path;
		}
		public void Dispose() => Directory.Delete(Root, recursive: true);
	}
}
