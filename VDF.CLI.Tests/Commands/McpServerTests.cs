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

using System.Collections.Generic;
using System.CommandLine;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VDF.CLI.Commands;
using VDF.CLI.Mcp;
using Xunit;

namespace VDF.CLI.Tests.Commands;

public class McpServerTests {

	[Fact]
	public void McpCommand_BuildsAndParsesPipeName() {
		var cmd = McpCommand.Build();
		Assert.Equal("mcp", cmd.Name);

		var parseResult = cmd.Parse("mcp --pipe-name test-custom-pipe");
		Assert.Empty(parseResult.Errors);
		Assert.Equal("test-custom-pipe", parseResult.GetValue<string>("--pipe-name"));
	}

	[Fact]
	public async Task McpServer_Initialize_ReturnsProtocolAndServerInfo() {
		using var server = new McpServer("vdf-offline-pipe");
		var req = new JsonRpcRequest {
			Id = 1,
			Method = "initialize"
		};

		var res = await server.HandleRequestAsync(req, CancellationToken.None);
		Assert.NotNull(res);
		Assert.Equal(1, res.Id);
		Assert.Null(res.Error);

		var json = JsonSerializer.Serialize(res.Result);
		using var doc = JsonDocument.Parse(json);
		Assert.Equal("2024-11-05", doc.RootElement.GetProperty("protocolVersion").GetString());
		Assert.Equal("vdf-mcp", doc.RootElement.GetProperty("serverInfo").GetProperty("name").GetString());
	}

	[Fact]
	public async Task McpServer_ToolsList_ReturnsAll18ToolsWithSafetyAnnotations() {
		using var server = new McpServer("vdf-offline-pipe");
		var req = new JsonRpcRequest {
			Id = 2,
			Method = "tools/list"
		};

		var res = await server.HandleRequestAsync(req, CancellationToken.None);
		Assert.NotNull(res);
		Assert.Null(res.Error);

		var json = JsonSerializer.Serialize(res.Result);
		using var doc = JsonDocument.Parse(json);
		var tools = doc.RootElement.GetProperty("tools");

		var byName = new Dictionary<string, JsonElement>();
		foreach (var tool in tools.EnumerateArray()) {
			string name = tool.GetProperty("name").GetString()!;
			byName[name] = tool;
			Assert.True(tool.TryGetProperty("annotations", out _), $"Tool '{name}' is missing MCP annotations.");
		}

		Assert.Equal(18, byName.Count);
		Assert.Contains("get_scan_status", byName.Keys);
		Assert.Contains("get_active_filter", byName.Keys);
		Assert.Contains("set_active_filter", byName.Keys);
		Assert.Contains("get_duplicate_groups", byName.Keys);
		Assert.Contains("get_group_details", byName.Keys);
		Assert.Contains("get_group_thumbnails", byName.Keys);
		Assert.Contains("scroll_to_item", byName.Keys);
		Assert.Contains("highlight_group", byName.Keys);
		Assert.Contains("set_item_checked", byName.Keys);
		Assert.Contains("select_by_criteria", byName.Keys);
		Assert.Contains("audit_selection", byName.Keys);
		Assert.Contains("undo_selection", byName.Keys);
		Assert.Contains("clear_selection", byName.Keys);
		Assert.Contains("invert_selection", byName.Keys);
		Assert.Contains("mark_not_a_match", byName.Keys);
		Assert.Contains("remove_from_list", byName.Keys);
		Assert.Contains("rename_item", byName.Keys);
		Assert.Contains("transfer_suffix_and_rename", byName.Keys);

		// Verify read-only vs disk-mutating annotations
		Assert.True(byName["get_scan_status"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
		Assert.False(byName["get_scan_status"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());

		Assert.True(byName["rename_item"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
		Assert.False(byName["set_item_checked"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
	}

	[Fact]
	public async Task McpServer_ToolCallWhenOffline_ReturnsClearErrorMessage() {
		using var server = new McpServer("vdf-nonexistent-pipe-xyz");
		var reqJson = """
		{
			"jsonrpc": "2.0",
			"id": 42,
			"method": "tools/call",
			"params": {
				"name": "get_scan_status",
				"arguments": {}
			}
		}
		""";

		var req = JsonSerializer.Deserialize<JsonRpcRequest>(reqJson)!;
		var res = await server.HandleRequestAsync(req, CancellationToken.None);

		Assert.NotNull(res);
		Assert.Null(res.Error);

		var result = (McpToolResult)res.Result!;
		Assert.True(result.IsError);
		Assert.Contains("Cannot connect to VDF GUI pipe", result.Content[0].Text);
	}

	[Fact]
	public async Task McpServer_UnknownMethod_ReturnsError() {
		using var server = new McpServer("vdf-offline-pipe");
		var req = new JsonRpcRequest {
			Id = 99,
			Method = "nonexistent/method"
		};

		var res = await server.HandleRequestAsync(req, CancellationToken.None);
		Assert.NotNull(res);
		Assert.NotNull(res.Error);
		Assert.Equal(-32601, res.Error.Code);
	}
}
