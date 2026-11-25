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
using System.CommandLine;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
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

	[Theory]
	[InlineData("2025-11-25", "2025-11-25")]
	[InlineData("2024-11-05", "2025-11-25")]
	[InlineData("2026-07-28", "2025-11-25")]
	public async Task McpServer_Initialize_NegotiatesProtocolAndReturnsServerInfo(string requestedVersion, string expectedVersion) {
		using var server = new McpServer("vdf-offline-pipe");
		var req = ReadRequest($$"""
			{
				"jsonrpc": "2.0", "id": 1, "method": "initialize",
				"params": {
					"protocolVersion": "{{requestedVersion}}",
					"capabilities": {},
					"clientInfo": { "name": "vdf-test", "version": "1" }
				}
			}
			""");

		var res = await server.HandleRequestAsync(req, CancellationToken.None);
		Assert.NotNull(res);
		Assert.Equal(1, res.Id!.Value.GetInt32());
		Assert.Null(res.Error);

		var result = res.Result!.Value;
		Assert.Equal(expectedVersion, result.GetProperty("protocolVersion").GetString());
		Assert.Equal("vdf-mcp", result.GetProperty("serverInfo").GetProperty("name").GetString());
		Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").GetProperty("tools").ValueKind);
		var instructions = result.GetProperty("instructions").GetString();
		Assert.Contains("get_duplicate_groups", instructions);
		Assert.Contains("set_active_filter", instructions);
		Assert.Contains("select_by_criteria", instructions);
		Assert.Contains("audit_selection", instructions);
		Assert.Contains("\"preview\": false", instructions);
		Assert.Contains("external recovery", instructions);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("null")]
	[InlineData("[]")]
	[InlineData("true")]
	[InlineData("""{"capabilities":{},"clientInfo":{"name":"test","version":"1"}}""")]
	[InlineData("""{"protocolVersion":123,"capabilities":{},"clientInfo":{"name":"test","version":"1"}}""")]
	[InlineData("""{"protocolVersion":" ","capabilities":{},"clientInfo":{"name":"test","version":"1"}}""")]
	[InlineData("""{"protocolVersion":"2025-11-25","clientInfo":{"name":"test","version":"1"}}""")]
	[InlineData("""{"protocolVersion":"2025-11-25","capabilities":[],"clientInfo":{"name":"test","version":"1"}}""")]
	[InlineData("""{"protocolVersion":"2025-11-25","capabilities":{}}""")]
	[InlineData("""{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":[]}""")]
	[InlineData("""{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"version":"1"}}""")]
	[InlineData("""{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test"}}""")]
	public async Task McpServer_Initialize_RejectsInvalidOrMissingParams(string? parameters) {
		using var server = new McpServer("vdf-offline-pipe");
		var paramsProperty = parameters == null ? "" : $"\"params\":{parameters},";
		using var input = new StringReader($$"""{ {{paramsProperty}} "jsonrpc":"2.0","id":1,"method":"initialize" }""");
		using var output = new StringWriter();
		await server.RunAsync(input, output, CancellationToken.None);

		using var doc = JsonDocument.Parse(output.ToString());
		var response = doc.RootElement;
		Assert.Equal(1, response.GetProperty("id").GetInt32());
		Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
		Assert.False(response.TryGetProperty("result", out _));
	}

	[Fact]
	public async Task McpServer_ToolsList_ReturnsAll18ToolsWithTitlesAndSafetyAnnotations() {
		using var server = new McpServer("vdf-offline-pipe");
		var req = ReadRequest("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

		var res = await server.HandleRequestAsync(req, CancellationToken.None);
		Assert.NotNull(res);
		Assert.Null(res.Error);

		var tools = res.Result!.Value.GetProperty("tools");

		var byName = new Dictionary<string, JsonElement>();
		foreach (var tool in tools.EnumerateArray()) {
			string name = tool.GetProperty("name").GetString()!;
			byName[name] = tool;
			Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("title").GetString()), $"Tool '{name}' is missing a title.");
			Assert.True(tool.TryGetProperty("annotations", out var annotations), $"Tool '{name}' is missing MCP annotations.");
			Assert.False(annotations.TryGetProperty("title", out _), $"Tool '{name}' has an unsupported nested title.");
			foreach (string hint in new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" }) {
				Assert.True(annotations.GetProperty(hint).ValueKind is JsonValueKind.True or JsonValueKind.False);
			}
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

		// Verify read-only and destructive annotations.
		Assert.Equal("Get Scan Status", byName["get_scan_status"].GetProperty("title").GetString());
		Assert.True(byName["get_scan_status"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
		Assert.False(byName["get_scan_status"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());

		Assert.True(byName["rename_item"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
		Assert.True(byName["mark_not_a_match"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
		Assert.True(byName["remove_from_list"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
		Assert.False(byName["set_item_checked"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
		var setCheckedProperties = byName["set_item_checked"].GetProperty("inputSchema").GetProperty("properties");
		Assert.Equal("boolean", setCheckedProperties.GetProperty("allow_all_in_group").GetProperty("type").GetString());
		var renameProperties = byName["rename_item"].GetProperty("inputSchema").GetProperty("properties");
		Assert.Contains("same folder", renameProperties.GetProperty("new_name").GetProperty("description").GetString());
		var transferSchema = byName["transfer_suffix_and_rename"].GetProperty("inputSchema");
		var preview = transferSchema.GetProperty("properties").GetProperty("preview");
		Assert.Equal("boolean", preview.GetProperty("type").GetString());
		Assert.True(preview.GetProperty("default").GetBoolean());
		Assert.False(transferSchema.TryGetProperty("required", out _));
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

		var req = ReadRequest(reqJson);
		var res = await server.HandleRequestAsync(req, CancellationToken.None);

		Assert.NotNull(res);
		Assert.Null(res.Error);

		var result = res.Result!.Value.Deserialize(McpJsonContext.Default.McpToolResult)!;
		Assert.True(result.IsError);
		Assert.Contains("Cannot connect to VDF GUI pipe", result.Content[0].Text);
	}

	[Fact]
	public async Task McpServer_UnknownMethod_ReturnsError() {
		using var server = new McpServer("vdf-offline-pipe");
		var req = ReadRequest("""{"jsonrpc":"2.0","id":99,"method":"nonexistent/method"}""");

		var res = await server.HandleRequestAsync(req, CancellationToken.None);
		Assert.NotNull(res);
		Assert.NotNull(res.Error);
		Assert.Equal(-32601, res.Error.Code);
	}

	[Fact]
	public void McpJsonContext_SerializesIpcRequestAndReadsThumbnails_WithoutFallback() {
		var context = McpJsonContext.Default;
		var arguments = JsonSerializer.Deserialize("""{"group_id":"group","load_if_missing":false}""", context.JsonElement);
		var payload = new IpcRequestDto {
			Command = "get_group_thumbnails",
			Parameters = new() {
				["group_id"] = arguments.GetProperty("group_id"),
				["load_if_missing"] = arguments.GetProperty("load_if_missing")
			}
		};
		var json = JsonSerializer.Serialize(payload, context.IpcRequestDto);
		using var doc = JsonDocument.Parse(json);
		Assert.Equal("get_group_thumbnails", doc.RootElement.GetProperty("command").GetString());
		Assert.Equal("group", doc.RootElement.GetProperty("parameters").GetProperty("group_id").GetString());
		Assert.False(doc.RootElement.GetProperty("parameters").GetProperty("load_if_missing").GetBoolean());

		var response = JsonSerializer.Deserialize("""
			{"success":true,"data":{"items":[{"frames":[{"base64Data":"AA==","mimeType":"image/jpeg"}]}]}}
			""", context.IpcResponseDto)!;
		Assert.True(response.Success);
		var thumbnails = response.Data!.Value.Deserialize(context.GroupThumbnailsResultDto)!;
		Assert.Equal("AA==", Assert.Single(Assert.Single(thumbnails.Items).Frames).Base64Data);
		Assert.Null(context.GetTypeInfo(typeof(McpServerTests)));
	}

	[Fact]
	public async Task McpServer_MalformedRequests_DoNotInterruptStream() {
		using var server = new McpServer("vdf-offline-pipe");
		using var input = new StringReader("""
			{"jsonrpc":"2.0",
			null
			[]
			42
			{"jsonrpc":"2.0"}
			{"id":1,"method":"ping"}
			{"jsonrpc":"1.0","id":"bad-version","method":"ping"}
			{"jsonrpc":2,"id":"numeric-version","method":"ping"}
			{"jsonrpc":"2.0","id":11}
			{"jsonrpc":"2.0","id":null,"method":null}
			{"jsonrpc":"2.0","id":12,"method":3}
			{"jsonrpc":"2.0","id":{},"method":"ping"}
			{"jsonrpc":"2.0","id":"bad-params","method":"ping","params":true}
			{"jsonrpc":"2.0","id":2,"method":"tools/call"}
			{"jsonrpc":"2.0","id":3,"method":"tools/call","params":[]}
			{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":17}}
			{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_scan_status","arguments":null}}
			{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"get_scan_status","arguments":[]}}
			{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"unlisted_tool"}}
			{"jsonrpc":"2.0","method":"notifications/initialized"}
			{"jsonrpc":"2.0","method":"ping"}
			{"jsonrpc":"2.0","id":"next","method":"ping"}
			{"jsonrpc":"2.0","id":null,"method":"ping"}
			""");
		using var output = new StringWriter();
		await server.RunAsync(input, output, CancellationToken.None);

		using var responses = new StringReader(output.ToString());
		AssertError(-32700, "null");
		foreach (string id in new[] { "null", "null", "null", "null", "1", "\"bad-version\"", "\"numeric-version\"", "11", "null", "12", "null", "\"bad-params\"" }) {
			AssertError(-32600, id);
		}
		foreach (string id in new[] { "2", "3", "4", "5", "6", "7" }) AssertError(-32602, id);
		using var ping = JsonDocument.Parse(responses.ReadLine()!);
		Assert.Equal("next", ping.RootElement.GetProperty("id").GetString());
		Assert.Equal("{}", ping.RootElement.GetProperty("result").GetRawText());
		using var nullIdPing = JsonDocument.Parse(responses.ReadLine()!);
		Assert.Equal(JsonValueKind.Null, nullIdPing.RootElement.GetProperty("id").ValueKind);
		Assert.Equal("{}", nullIdPing.RootElement.GetProperty("result").GetRawText());
		Assert.Null(responses.ReadLine());

		void AssertError(int code, string rawId) {
			using var doc = JsonDocument.Parse(responses.ReadLine()!);
			var response = doc.RootElement;
			Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
			Assert.Equal(code, response.GetProperty("error").GetProperty("code").GetInt32());
			Assert.False(response.TryGetProperty("result", out _));
			Assert.Equal(rawId, response.GetProperty("id").GetRawText());
		}
	}

	[Fact]
	public async Task McpServer_InvalidThumbnailPayload_DoesNotInterruptStream() {
		using var gui = new FakeGui();
		await using var session = new McpSession(gui);
		session.Send(ToolCall("\"thumbnails\"", "get_group_thumbnails"));
		await gui.ConnectAsync(session.Token);
		Assert.Equal("get_group_thumbnails", (await gui.ReadRequestAsync(session.Token)).Command);
		await gui.ReplyAsync("""{"success":true,"data":{"items":false}}""", session.Token);
		var failedToolCall = await session.ReadAsync();
		Assert.Equal("thumbnails", failedToolCall.GetProperty("id").GetString());
		Assert.Equal(-32603, ErrorCode(failedToolCall));
		session.Send(Ping(2));
		Assert.Equal(2, (await session.ReadAsync()).GetProperty("id").GetInt32());
		await session.FinishAsync();
	}

	[Fact]
	public async Task McpServer_PingAndToolsListRespondWhileGuiCallIsBlocked() {
		using var gui = new FakeGui();
		await using var session = new McpSession(gui);
		session.Send(ToolCall("1"));
		await gui.ConnectAsync(session.Token);
		Assert.Equal("get_status", (await gui.ReadRequestAsync(session.Token)).Command);
		session.Send(Ping(2));
		session.Send("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");
		Assert.Equal(2, (await session.ReadAsync()).GetProperty("id").GetInt32());
		var list = await session.ReadAsync();
		Assert.Equal(3, list.GetProperty("id").GetInt32());
		Assert.NotEmpty(list.GetProperty("result").GetProperty("tools").EnumerateArray());
		await gui.ReplyOkAsync(session.Token);
		Assert.Equal(1, (await session.ReadAsync()).GetProperty("id").GetInt32());
		await session.FinishAsync();
	}

	[Fact]
	public async Task McpServer_CancellationNotificationClosesPendingGuiReadWithoutReply() {
		using var gui = new FakeGui();
		await using var session = new McpSession(gui);
		session.Send(ToolCall("\"ca\\u006cl\""));
		await gui.ConnectAsync(session.Token);
		await gui.ReadRequestAsync(session.Token);
		session.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"call"}}""");
		session.Send(Ping(2));
		Assert.Equal(2, (await session.ReadAsync()).GetProperty("id").GetInt32());
		Assert.Null(await gui.ReadLineAsync(session.Token));
		await session.FinishAsync();
	}

	[Fact]
	public async Task McpServer_RejectsInFlightDuplicateAndNinthCall_AndSendsAdmittedCallsInOrder() {
		using var gui = new FakeGui();
		await using var session = new McpSession(gui);
		session.Send(ToolCall("1"));
		await gui.ConnectAsync(session.Token);
		Assert.Equal(1, (await gui.ReadRequestAsync(session.Token)).Parameters!["sequence"].GetInt32());
		for (int id = 2; id <= 8; id++) session.Send(ToolCall(id.ToString()));
		session.Send("""{"jsonrpc":"2.0","id":1.0,"method":"ping"}""");
		session.Send(ToolCall("9"));
		session.Send("""{"jsonrpc":"2.0","id":"1","method":"ping"}""");
		var duplicate = await session.ReadAsync();
		Assert.Equal(-32600, ErrorCode(duplicate));
		Assert.Contains("Duplicate", duplicate.GetProperty("error").GetProperty("message").GetString());
		var overCap = await session.ReadAsync();
		Assert.Equal(9, overCap.GetProperty("id").GetInt32());
		Assert.Equal(-32000, ErrorCode(overCap));
		Assert.Equal("1", (await session.ReadAsync()).GetProperty("id").GetString());

		await gui.ReplyOkAsync(session.Token);
		for (int id = 2; id <= 8; id++) {
			Assert.Equal(id, (await gui.ReadRequestAsync(session.Token)).Parameters!["sequence"].GetInt32());
			await gui.ReplyOkAsync(session.Token);
		}
		var ids = new HashSet<int>();
		for (int i = 0; i < 8; i++) Assert.True(ids.Add((await session.ReadAsync()).GetProperty("id").GetInt32()));
		Assert.True(ids.SetEquals(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
		await session.FinishAsync();
	}

	[Fact]
	public async Task McpServer_EofCancelsRunningAndQueuedCallsAndWaitsForDisconnect() {
		using var gui = new FakeGui();
		await using var session = new McpSession(gui);
		session.Send(ToolCall("1"));
		await gui.ConnectAsync(session.Token);
		await gui.ReadRequestAsync(session.Token);
		session.Send(ToolCall("2"));
		session.Send(Ping(3));
		Assert.Equal(3, (await session.ReadAsync()).GetProperty("id").GetInt32());
		await session.FinishAsync();
		Assert.Null(await gui.ReadLineAsync(session.Token));
	}

	[Fact]
	public async Task McpServer_OversizeInputIsDrainedWithoutLosingNextLine() {
		const int limit = 64;
		using var server = new McpServer("vdf-offline-pipe", limit);
		using var input = new StringReader(new string('x', 8192) + "\r\n" + Ping(2).PadRight(limit));
		using var output = new StringWriter();
		await server.RunAsync(input, output, CancellationToken.None);
		using var lines = new StringReader(output.ToString());
		var oversized = Parse(lines.ReadLine()!);
		Assert.Equal(-32600, ErrorCode(oversized));
		Assert.Equal(JsonValueKind.Null, oversized.GetProperty("id").ValueKind);
		Assert.Equal("Invalid Request: input line exceeds the 64-character limit.", oversized.GetProperty("error").GetProperty("message").GetString());
		Assert.Equal(2, Parse(lines.ReadLine()!).GetProperty("id").GetInt32());
		Assert.Null(lines.ReadLine());
	}

	[Fact]
	public async Task GuiIpcClient_DoesNotReplayMutationAfterGuiReadsItAndDropsCachedConnection() {
		using var gui = new FakeGui();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var client = new GuiIpcClient(gui.Name);
		var warmup = client.SendCommandAsync("get_status", ct: timeout.Token);
		await gui.ConnectAsync(timeout.Token);
		Assert.Equal("get_status", (await gui.ReadRequestAsync(timeout.Token)).Command);
		await gui.ReplyOkAsync(timeout.Token);
		Assert.True((await warmup).Success);

		var mutation = client.SendCommandAsync("rename_item", ct: timeout.Token);
		Assert.Equal("rename_item", (await gui.ReadRequestAsync(timeout.Token)).Command);
		gui.Disconnect();
		var next = ServeNextAsync();
		var result = await mutation;
		Assert.False(result.Success);
		Assert.Contains("Outcome unknown", result.Error);
		Assert.True((await client.SendCommandAsync("get_status", ct: timeout.Token)).Success);
		Assert.Equal("get_status", await next);

		async Task<string> ServeNextAsync() {
			await gui.ConnectAsync(timeout.Token);
			var request = await gui.ReadRequestAsync(timeout.Token);
			await gui.ReplyOkAsync(timeout.Token);
			return request.Command;
		}
	}

	[Fact]
	public async Task GuiIpcClient_ReadOnlyCommandCanRetryAfterMissingResponse() {
		using var gui = new FakeGui();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var client = new GuiIpcClient(gui.Name);
		var status = client.SendCommandAsync("get_status", ct: timeout.Token);
		await gui.ConnectAsync(timeout.Token);
		Assert.Equal("get_status", (await gui.ReadRequestAsync(timeout.Token)).Command);
		gui.Disconnect();
		await gui.ConnectAsync(timeout.Token);
		Assert.Equal("get_status", (await gui.ReadRequestAsync(timeout.Token)).Command);
		await gui.ReplyOkAsync(timeout.Token);
		Assert.True((await status).Success);
	}

	[Fact]
	public async Task GuiIpcClient_OverByteLimitWithoutTerminatorClosesPipeAndNextCommandRecovers() {
		using var gui = new FakeGui();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var client = new GuiIpcClient(gui.Name, maxResponseBytes: 64);
		var status = client.SendCommandAsync("get_status", ct: timeout.Token);
		await gui.ConnectAsync(timeout.Token);
		await gui.ReadRequestAsync(timeout.Token);
		await gui.WriteRawAsync(new string('é', 33), timeout.Token);
		var result = await status;
		Assert.False(result.Success);
		Assert.Contains("64-byte line limit", result.Error);
		Assert.Null(await gui.ReadLineAsync(timeout.Token));
		gui.Disconnect();

		var next = client.SendCommandAsync("get_status", ct: timeout.Token);
		await gui.ConnectAsync(timeout.Token);
		Assert.Equal("get_status", (await gui.ReadRequestAsync(timeout.Token)).Command);
		await gui.ReplyOkAsync(timeout.Token);
		Assert.True((await next).Success);
	}

	[Theory]
	[InlineData("get_status", null, 30)]
	[InlineData("select_by_criteria", null, 60)]
	[InlineData("audit_selection", null, 60)]
	[InlineData("rename_item", null, 60)]
	[InlineData("get_group_thumbnails", null, 120)]
	[InlineData("get_group_thumbnails", "true", 120)]
	[InlineData("get_group_thumbnails", "false", 30)]
	public void GuiIpcClient_UsesPerCommandDeadline(string command, string? loadIfMissing, int seconds) {
		Dictionary<string, JsonElement>? parameters = loadIfMissing == null ? null : new() { ["load_if_missing"] = Parse(loadIfMissing) };
		Assert.Equal(TimeSpan.FromSeconds(seconds), GuiIpcClient.CommandTimeout(command, parameters));
	}

	[Fact]
	public async Task GuiIpcClient_DeadlineReturnsTimeoutAndUnknownMutationOutcomeWithoutCancellingCaller() {
		using var gui = new FakeGui();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var clock = new ManualClock();
		using var client = new GuiIpcClient(gui.Name, maxResponseBytes: 64, timeProvider: clock);
		var mutation = client.SendCommandAsync("rename_item", ct: timeout.Token);
		await gui.ConnectAsync(timeout.Token);
		Assert.Equal("rename_item", (await gui.ReadRequestAsync(timeout.Token)).Command);
		clock.Expire();
		var result = await mutation;
		Assert.False(result.Success);
		Assert.Contains("timed out after 60 seconds", result.Error);
		Assert.Contains("Outcome unknown", result.Error);
		Assert.False(timeout.IsCancellationRequested);
		Assert.Null(await gui.ReadLineAsync(timeout.Token));
	}

	static string Ping(int id) => $$"""{"jsonrpc":"2.0","id":{{id}},"method":"ping"}""";
	static string ToolCall(string rawId, string name = "get_scan_status") => $$"""{"jsonrpc":"2.0","id":{{rawId}},"method":"tools/call","params":{"name":"{{name}}","arguments":{"sequence":{{rawId}} } } }""";
	static JsonElement Parse(string line) => JsonSerializer.Deserialize(line, McpJsonContext.Default.JsonElement);
	static int ErrorCode(JsonElement response) => response.GetProperty("error").GetProperty("code").GetInt32();

	static JsonRpcRequest ReadRequest(string json) => JsonSerializer.Deserialize(json, McpJsonContext.Default.JsonRpcRequest)!;

	sealed class ManualClock : TimeProvider {
		Action? expire;
		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
			expire = () => callback(state);
			return new NoopTimer();
		}
		public void Expire() => expire!();

		sealed class NoopTimer : ITimer {
			public bool Change(TimeSpan dueTime, TimeSpan period) => true;
			public void Dispose() { }
			public ValueTask DisposeAsync() => ValueTask.CompletedTask;
		}
	}

	sealed class FakeGui : IDisposable {
		readonly NamedPipeServerStream pipe;
		StreamReader? reader;
		public string Name { get; } = $"vdf-mcp-test-{Guid.NewGuid():N}";

		public FakeGui() => pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

		public async Task ConnectAsync(CancellationToken ct) {
			await pipe.WaitForConnectionAsync(ct);
			reader = new StreamReader(pipe, leaveOpen: true);
		}

		public ValueTask<string?> ReadLineAsync(CancellationToken ct) => reader!.ReadLineAsync(ct);
		public async Task<IpcRequestDto> ReadRequestAsync(CancellationToken ct) =>
			JsonSerializer.Deserialize((await ReadLineAsync(ct))!, McpJsonContext.Default.IpcRequestDto)!;
		public ValueTask WriteRawAsync(string value, CancellationToken ct) => pipe.WriteAsync(Encoding.UTF8.GetBytes(value).AsMemory(), ct);
		public ValueTask ReplyAsync(string payload, CancellationToken ct) => WriteRawAsync(payload + "\n", ct);
		public ValueTask ReplyOkAsync(CancellationToken ct) => ReplyAsync("""{"success":true}""", ct);

		public void Disconnect() {
			reader?.Dispose();
			reader = null;
			pipe.Disconnect();
		}
		public void Dispose() {
			reader?.Dispose();
			pipe.Dispose();
		}
	}

	sealed class McpSession : IAsyncDisposable {
		readonly McpServer server;
		readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
		readonly ChannelInput input = new();
		readonly ChannelOutput output = new();
		readonly Task run;
		public CancellationToken Token => timeout.Token;

		public McpSession(FakeGui gui) {
			server = new McpServer(gui.Name);
			run = server.RunAsync(input, output, Token);
		}

		public void Send(string line) => Assert.True(input.Lines.Writer.TryWrite(line + "\n"));
		public async Task<JsonElement> ReadAsync() => Parse(await output.Lines.Reader.ReadAsync(Token));
		public async Task FinishAsync() {
			input.Lines.Writer.TryComplete();
			await run.WaitAsync(Token);
			Assert.False(output.Lines.Reader.TryRead(out _));
		}
		public async ValueTask DisposeAsync() {
			timeout.Cancel();
			try { await run; }
			finally {
				server.Dispose();
				timeout.Dispose();
			}
		}

		sealed class ChannelInput : TextReader {
			public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
			string current = "";
			int offset;

			public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) {
				if (offset == current.Length) {
					if (!await Lines.Reader.WaitToReadAsync(cancellationToken)) return 0;
					current = await Lines.Reader.ReadAsync(cancellationToken);
					offset = 0;
				}
				int length = Math.Min(current.Length - offset, buffer.Length);
				current.AsMemory(offset, length).CopyTo(buffer);
				offset += length;
				return length;
			}
		}

		sealed class ChannelOutput : TextWriter {
			public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
			public override Encoding Encoding => Encoding.UTF8;
			public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
				Lines.Writer.WriteAsync(buffer.ToString(), cancellationToken).AsTask();
		}
	}
}
