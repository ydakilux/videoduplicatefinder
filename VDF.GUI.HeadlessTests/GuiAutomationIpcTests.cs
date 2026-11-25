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
using System.Threading.Tasks;
using VDF.Core.Automation;
using VDF.GUI.Automation;
using Xunit;

namespace VDF.GUI.HeadlessTests;

public class GuiAutomationIpcTests {

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

		async Task<AutomationResponse> SendOnAsync(StreamWriter w, StreamReader r, string command, object? parameters = null) {
			var req = new { command, parameters };
			await w.WriteLineAsync(JsonSerializer.Serialize(req));
			var resLine = await r.ReadLineAsync();
			Assert.NotNull(resLine);
			return JsonSerializer.Deserialize<AutomationResponse>(resLine)!;
		}

		Task<AutomationResponse> SendCommandAsync(string command, object? parameters = null) =>
			SendOnAsync(writer, reader, command, parameters);

		// 1. Both clients can query status concurrently
		var statusRes = await SendCommandAsync("get_status");
		var statusRes2 = await SendOnAsync(writer2, reader2, "get_status");
		Assert.True(statusRes.Success);
		Assert.True(statusRes2.Success);
		var statusElement = (JsonElement)statusRes.Data!;
		Assert.Equal(4, statusElement.GetProperty("totalDuplicates").GetInt32());
		Assert.Equal(2, statusElement.GetProperty("totalGroups").GetInt32());

		// 2. Get Groups with include_items = true -> verify QualityRanker isBest (1920px beats 1280px, NOT smallest size)
		var groupsRes = await SendCommandAsync("get_groups", new { include_items = true });
		Assert.True(groupsRes.Success);
		var groupsElement = (JsonElement)groupsRes.Data!;
		Assert.Equal(2, groupsElement.GetProperty("totalCount").GetInt32());
		var firstGroup = groupsElement.GetProperty("groups")[0];
		Assert.Equal(@"D:\Videos\Holiday\beach_2019_final.mp4", firstGroup.GetProperty("bestItemPath").GetString());
		Assert.Equal("Resolution", firstGroup.GetProperty("bestReason").GetString());

		// 3. Get Group Thumbnails returns PNG MIME type & base64 frame
		var thumbRes = await SendCommandAsync("get_group_thumbnails", new {
			path = @"D:\Videos\Holiday\beach_2019_final.mp4",
			load_if_missing = false
		});
		Assert.True(thumbRes.Success);
		var thumbElem = (JsonElement)thumbRes.Data!;
		var firstThumbFrames = thumbElem.GetProperty("items")[0].GetProperty("frames");
		Assert.Equal(1, firstThumbFrames.GetArrayLength());
		Assert.Equal("image/png", firstThumbFrames[0].GetProperty("mimeType").GetString());

		// 4. Select by criteria (lowest-quality strategy)
		var selectRes = await SendCommandAsync("select_by_criteria", new {
			strategy = "lowest-quality",
			action = "check"
		});
		Assert.True(selectRes.Success);
		var selectElement = (JsonElement)selectRes.Data!;
		Assert.Equal(2, selectElement.GetProperty("modifiedCount").GetInt32());

		// 5. Audit Selection (should be healthy now)
		var auditRes1 = await SendCommandAsync("audit_selection");
		Assert.True(auditRes1.Success);
		var auditElement1 = (JsonElement)auditRes1.Data!;
		Assert.True(auditElement1.GetProperty("isSafe").GetBoolean());
		Assert.Equal(2, auditElement1.GetProperty("checkedCount").GetInt32());

		// 6. Force All Items in Group 1 to be checked (Unsafe condition)
		var setCheckedRes = await SendCommandAsync("set_checked", new {
			path = @"D:\Videos\Holiday\beach_2019_final.mp4",
			@checked = true
		});
		Assert.True(setCheckedRes.Success);

		// 7. Audit Selection should now flag AllCheckedInGroup Error
		var auditRes2 = await SendCommandAsync("audit_selection");
		Assert.True(auditRes2.Success);
		var auditElement2 = (JsonElement)auditRes2.Data!;
		Assert.False(auditElement2.GetProperty("isSafe").GetBoolean());

		// 8. Undo the unsafe check
		var undoRes = await SendCommandAsync("undo");
		Assert.True(undoRes.Success);

		// 9. Set Active Filter on Holiday path & test security guard
		var setFilterRes = await SendCommandAsync("set_active_filter", new {
			filter_by_path = "Holiday"
		});
		Assert.True(setFilterRes.Success);
		var setFilterElem = (JsonElement)setFilterRes.Data!;
		Assert.Equal(2, setFilterElem.GetProperty("visibleDuplicates").GetInt32());

		var securityCheckRes = await SendCommandAsync("set_checked", new {
			path = @"E:\Archive\Old\wedding.mkv",
			@checked = true,
			current_filter_only = true
		});
		Assert.False(securityCheckRes.Success);
		Assert.Contains("Security check failed", securityCheckRes.Error);

		// 10. Highlight Group & Reset Filter
		var highlightRes = await SendCommandAsync("highlight_group", new {
			path = @"D:\Videos\Holiday\beach_2019_final.mp4"
		});
		Assert.True(highlightRes.Success);

		await SendCommandAsync("set_active_filter", new { filter_by_path = "" });

		// 11. Remove single item from Group 2 from list -> singleton survivor is also dropped automatically
		var removeRes = await SendCommandAsync("remove_from_list", new {
			path = @"E:\Archive\Old\wedding_small.mp4"
		});
		Assert.True(removeRes.Success);
		Assert.Equal(2, vm.Duplicates.Count); // Group 1's 2 items remain; Group 2 became singleton and dropped

		// Clear selection so global state stays clean
		await SendCommandAsync("clear_selection");

		server.Stop();
		Assert.False(server.IsRunning);
	});
}
