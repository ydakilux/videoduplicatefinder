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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VDF.Core.Automation;

namespace VDF.CLI.Mcp {
	public sealed class McpServer : IDisposable {
		const string SupportedProtocolVersion = "2025-11-25";
		const int MaxInputChars = 4 * 1024 * 1024;
		const int MaxInFlightTools = 8;
		const string ServerInstructions = """
			Inspect scan status and duplicate groups (get_scan_status, get_duplicate_groups), then filter the results (set_active_filter).
			Select items with set_item_checked or select_by_criteria, then run audit_selection to verify at least one copy is kept per group.
			For suffix transfer, review the plan with transfer_suffix_and_rename in preview mode (the default). Set "preview": false only when ready to apply the reviewed renames.
			Disk renames are irreversible within VDF and require external recovery to undo. undo_selection only undoes selection changes.
			""";

		readonly GuiIpcClient ipcClient;
		readonly int maxInputChars;

		public McpServer(string pipeName = AutomationConstants.DefaultPipeName) : this(pipeName, MaxInputChars) { }

		internal McpServer(string pipeName, int maxInputChars) {
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputChars);
			ipcClient = new GuiIpcClient(pipeName);
			this.maxInputChars = maxInputChars;
		}

		static McpToolAnnotations ReadOnly() => new() {
			ReadOnlyHint = true,
			DestructiveHint = false,
			IdempotentHint = true,
			OpenWorldHint = false
		};

		static McpToolAnnotations UiAction(bool idempotent = true, bool destructive = false) => new() {
			ReadOnlyHint = false,
			DestructiveHint = destructive,
			IdempotentHint = idempotent,
			OpenWorldHint = false
		};

		static McpToolAnnotations DiskMutation() => new() {
			ReadOnlyHint = false,
			DestructiveHint = true,
			IdempotentHint = false,
			OpenWorldHint = false
		};

		static JsonElement ParseInputSchema(string json) {
			using var document = JsonDocument.Parse(json);
			return document.RootElement.Clone();
		}

		static readonly JsonElement EmptyInputSchema = ParseInputSchema("""{"type":"object","properties":{}}""");

		public static readonly List<McpToolDefinition> Tools = new() {
			new() {
				Name = "get_scan_status",
				Description = "Get current status of Video Duplicate Finder (scanning state, total duplicates, visible vs total duplicates/groups/checked, wasted space, active filter).",
				Title = "Get Scan Status",
				Annotations = ReadOnly(),
				InputSchema = EmptyInputSchema
			},
			new() {
				Name = "get_active_filter",
				Description = "Get current active filter configuration in GUI (path/wildcard, file type, similarity range, secondary flags) and visible vs total item/group counts.",
				Title = "Get Active Filter",
				Annotations = ReadOnly(),
				InputSchema = EmptyInputSchema
			},
			new() {
				Name = "set_active_filter",
				Description = "Dynamically update GUI active filters (filter by path/folder, file type, similarity range, and secondary flags).",
				Title = "Set Active Filter",
				Annotations = UiAction(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"filter_by_path": { "type": "string", "description": "Substring or wildcard pattern to filter paths (e.g. 'D:\\Movies' or '*holiday*')" },
							"file_type": { "type": "string", "enum": [ "All", "Videos", "Images" ], "description": "Filter by file type" },
							"similarity_from": { "type": "integer", "description": "Minimum similarity percentage (0-100)" },
							"similarity_to": { "type": "integer", "description": "Maximum similarity percentage (0-100)" },
							"only_groups_with_checked_items": { "type": "boolean", "description": "Show only groups with at least one checked item" },
							"hide_groups_with_one_file_left": { "type": "boolean", "description": "Hide groups where only one file remains" },
							"only_ai_matches": { "type": "boolean", "description": "Show only groups containing AI-matched files" }
						}
					}
					""")
			},
			new() {
				Name = "get_duplicate_groups",
				Description = "List duplicate groups with summaries, QualityRanker best keeper + reason, synthetic metadata (codecs, resolutions, mixed flags), and optional full item details in 1 call.",
				Title = "List Duplicate Groups",
				Annotations = ReadOnly(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"offset": { "type": "integer", "description": "Pagination offset (default 0)" },
							"limit": { "type": "integer", "description": "Number of groups to return (default 50)" },
							"current_filter_only": { "type": "boolean", "description": "Only return groups containing items visible in the active filter (default true)" },
							"include_items": { "type": "boolean", "description": "Include full item details for each group in a single fast call (default false)" },
							"has_checked": { "type": "boolean", "description": "Filter groups with checked items (true) or without checked items (false)" },
							"has_mixed_codecs": { "type": "boolean", "description": "Filter groups containing multiple distinct codecs (e.g. AV1 and H.264)" },
							"has_mixed_resolutions": { "type": "boolean", "description": "Filter groups containing multiple distinct resolutions" },
							"min_similarity": { "type": "number", "description": "Minimum group similarity percentage" }
						}
					}
					""")
			},
			new() {
				Name = "get_group_details",
				Description = "Get complete details for all duplicate items in a group (paths, resolution, bitrate, fps, codec, HDR, audio/subtitle languages, QualityRanker isBest + bestReason, tombstone/offline flags, etc.).",
				Title = "Get Group Details",
				Annotations = ReadOnly(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"group_id": { "type": "string", "description": "Guid of the duplicate group" }
						},
						"required": [ "group_id" ]
					}
					""")
			},
			new() {
				Name = "get_group_thumbnails",
				Description = "Retrieve visual preview frames (as native MCP images) for every item in a duplicate group so a multimodal AI can visually compare candidates for watermarks, cropping, quality, or false positives.",
				Title = "Get Group Preview Frames",
				Annotations = ReadOnly(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"group_id": { "type": "string", "description": "Guid of the duplicate group" },
							"path": { "type": "string", "description": "Optional path or filename belonging to the group if group_id is omitted" },
							"load_if_missing": { "type": "boolean", "description": "Extract thumbnails via FFmpeg on demand if not yet cached (default true)" },
							"max_frames_per_item": { "type": "integer", "description": "Maximum frames returned per item (1-4, default 2)" }
						}
					}
					""")
			},
			new() {
				Name = "scroll_to_item",
				Description = "Scroll the GUI list directly to a specific video item or group so the user can visually inspect it on screen.",
				Title = "Scroll To Item",
				Annotations = UiAction(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"path": { "type": "string", "description": "Full path or filename of the video to focus" },
							"group_id": { "type": "string", "description": "Guid of the duplicate group to scroll to" }
						}
					}
					""")
			},
			new() {
				Name = "highlight_group",
				Description = "Highlight, expand, scroll to, and focus a duplicate group in the GUI interface for visual inspection.",
				Title = "Highlight Group",
				Annotations = UiAction(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"group_id": { "type": "string", "description": "Guid of the duplicate group to highlight" },
							"path": { "type": "string", "description": "File path or filename belonging to the group" }
						}
					}
					""")
			},
			new() {
				Name = "set_item_checked",
				Description = "Check or uncheck specific video files or an entire group in the GUI, with active filter safety guard.",
				Title = "Check / Uncheck Items",
				Annotations = UiAction(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"paths": { "type": "array", "items": { "type": "string" }, "description": "List of file paths to check/uncheck" },
							"path": { "type": "string", "description": "Single file path to check/uncheck" },
							"group_id": { "type": "string", "description": "Group ID to check/uncheck all items in" },
							"checked": { "type": "boolean", "description": "True to check (mark for deletion), False to uncheck" },
							"allow_all_in_group": { "type": "boolean", "description": "Allow checking every item in a group (default false, to keep at least one copy)" },
							"current_filter_only": { "type": "boolean", "description": "Refuse to check items currently hidden by the active filter (default true)" }
						},
						"required": [ "checked" ]
					}
					""")
			},
			new() {
				Name = "select_by_criteria",
				Description = "Select (check or uncheck) duplicate videos using a criteria expression or preset strategy (with active filter awareness and safety guards).",
				Title = "Select By Criteria",
				Annotations = UiAction(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"expression": { "type": "string", "description": "Dynamic C# expression evaluated on DuplicateItem (e.g. 'item.SizeLong > 5000000 && item.BitRateKbs < 2500')" },
							"strategy": { "type": "string", "description": "Preset strategy: 'best-codec', 'lowest-quality', 'smallest-file', 'largest-file', 'worst-resolution', 'shortest-duration', 'identical', 'identical-except-size', 'oldest', 'newest'" },
							"action": { "type": "string", "enum": [ "check", "uncheck" ], "description": "'check' (default) or 'uncheck'" },
							"allow_all_in_group": { "type": "boolean", "description": "Allow checking 100% of items in a group (defaults to false to prevent accidental total deletion)" },
							"current_filter_only": { "type": "boolean", "description": "Only apply selection to items visible in the active filter (default true)" }
						}
					}
					""")
			},
			new() {
				Name = "audit_selection",
				Description = "Audit current selection for safety (wipeout protection aware of hidden items), quality inversions, codec inversions, and criteria compliance.",
				Title = "Audit Selection",
				Annotations = ReadOnly(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"criteria_expression": { "type": "string", "description": "Optional expression that all checked items must satisfy" },
							"require_at_least_one_kept": { "type": "boolean", "description": "Ensure at least one copy in every group is kept (defaults to true)" },
							"current_filter_only": { "type": "boolean", "description": "Only audit items and groups visible in the active filter (default true)" }
						}
					}
					""")
			},
			new() {
				Name = "undo_selection",
				Description = "Undo the last selection action in the GUI (equivalent to Ctrl+Z).",
				Title = "Undo Selection",
				Annotations = UiAction(idempotent: false),
				InputSchema = EmptyInputSchema
			},
			new() {
				Name = "clear_selection",
				Description = "Uncheck all items in the GUI.",
				Title = "Clear Selection",
				Annotations = UiAction(),
				InputSchema = EmptyInputSchema
			},
			new() {
				Name = "invert_selection",
				Description = "Invert all checkboxes in the GUI.",
				Title = "Invert Selection",
				Annotations = UiAction(idempotent: false),
				InputSchema = EmptyInputSchema
			},
			new() {
				Name = "mark_not_a_match",
				Description = "Mark one or more false-positive duplicate groups as 'Not a Match' so they are hidden immediately and excluded from future scans without changing media files.",
				Title = "Mark Group As Not a Match",
				Annotations = UiAction(destructive: true),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"group_id": { "type": "string", "description": "Guid of a single duplicate group to mark as Not a Match" },
							"group_ids": { "type": "array", "items": { "type": "string" }, "description": "List of duplicate group Guids to mark as Not a Match" }
						}
					}
					""")
			},
			new() {
				Name = "remove_from_list",
				Description = "Remove items or groups from the VDF results list (and optionally exclude them from future scans) WITHOUT deleting any file on disk.",
				Title = "Remove From Results List",
				Annotations = UiAction(destructive: true),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"path": { "type": "string", "description": "Single file path or filename to remove from the list" },
							"paths": { "type": "array", "items": { "type": "string" }, "description": "List of file paths to remove from the list" },
							"group_id": { "type": "string", "description": "Remove all items of this group from the list" },
							"checked_only": { "type": "boolean", "description": "If true, remove all currently checked items in scope from the list (default false)" },
							"exclude_from_future_scans": { "type": "boolean", "description": "If true, also mark removed file entries as excluded in ScannedFiles.db (default false)" },
							"current_filter_only": { "type": "boolean", "description": "Only remove items visible in the active filter (default true)" }
						}
					}
					""")
			},
			new() {
				Name = "rename_item",
				Description = "Rename a duplicate item within its current folder, updating VDF memory and the database (keeps UI and companion cover image synchronized).",
				Title = "Rename File On Disk",
				Annotations = DiskMutation(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"path": { "type": "string", "description": "Current path or filename of the item to rename" },
							"new_name": { "type": "string", "description": "New filename only, with or without extension. Renames within the same folder; paths and directory separators are not allowed." }
						},
						"required": [ "path", "new_name" ]
					}
					""")
			},
			new() {
				Name = "transfer_suffix_and_rename",
				Description = "Plan tag/suffix transfers (e.g. '___tag' or '______notes') from checked items onto kept items. Defaults to preview; explicitly set preview to false to rename files and update VDF memory and the database without overwriting existing files.",
				Title = "Transfer Suffix & Rename On Disk",
				Annotations = DiskMutation(),
				InputSchema = ParseInputSchema("""
					{
						"type": "object",
						"properties": {
							"group_id": { "type": "string", "description": "Optional duplicate group ID (omitted to apply across all groups in active filter)" },
							"current_filter_only": { "type": "boolean", "description": "Only process groups visible in the active filter (default true)" },
							"preview": { "type": "boolean", "default": true, "description": "Preview planned renames without changing files, item paths, or the database (default true). Explicitly set to false to execute the renames." },
							"suffix_pattern": { "type": "string", "description": "Regex pattern matching the tag suffix (default: '(_{2,}.*)$')" }
						}
					}
					""")
			}
		};

		public async Task RunAsync(CancellationToken ct) {
			Console.Error.WriteLine("VDF MCP Server started. Listening on stdio...");

			using var input = Console.OpenStandardInput();
			using var reader = new Utf8ChunkReader(input);
			using var writer = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
			await RunAsync(reader, writer, ct);
		}

		public async Task RunAsync(TextReader reader, TextWriter writer, CancellationToken ct) {
			using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var runToken = shutdown.Token;
			using var writeLock = new SemaphoreSlim(1, 1);
			var lines = new BoundedLineReader(reader, maxInputChars);
			// Only the stdin loop adds/removes entries and disposes their cancellation sources.
			var inFlight = new List<InFlightTool>(MaxInFlightTools);
			try {
				while (!runToken.IsCancellationRequested) {
					(string? line, bool tooLong) input;
					try {
						input = await lines.ReadLineAsync(runToken);
					}
					catch (Exception ex) when (ex is not OperationCanceledException || !runToken.IsCancellationRequested) {
						Console.Error.WriteLine($"Error reading stdin: {ex.Message}");
						break;
					}

					if (input.tooLong) {
						await WriteResponseAsync(JsonRpcResponse.CreateError(default, -32600, $"Invalid Request: input line exceeds the {maxInputChars}-character limit."));
						continue;
					}
					if (input.line == null) break;
					if (string.IsNullOrWhiteSpace(input.line)) continue;

					JsonElement root;
					try {
						root = JsonSerializer.Deserialize(input.line, McpJsonContext.Default.JsonElement);
					}
					catch (JsonException ex) {
						Console.Error.WriteLine($"Failed to parse JSON-RPC line: {ex.Message}");
						await WriteResponseAsync(JsonRpcResponse.CreateError(default, -32700, "Parse error"));
						continue;
					}

					if (!TryReadRequest(root, out var req, out var id)) {
						await WriteResponseAsync(JsonRpcResponse.CreateError(id, -32600, "Invalid Request"));
						continue;
					}

					for (int i = inFlight.Count - 1; i >= 0; i--) {
						if (!inFlight[i].Work.IsCompleted) continue;
						inFlight[i].Dispose();
						inFlight.RemoveAt(i);
					}
					if (id.ValueKind == JsonValueKind.Undefined) {
						if (req.Method == "notifications/cancelled" && req.Params.ValueKind == JsonValueKind.Object
							&& req.Params.TryGetProperty("requestId", out var cancelledId)) {
							inFlight.Find(call => JsonElement.DeepEquals(call.Id, cancelledId))?.Cancel();
						}
						continue;
					}

					if (inFlight.Exists(call => JsonElement.DeepEquals(call.Id, id))) {
						await WriteResponseAsync(JsonRpcResponse.CreateError(id, -32600, "Duplicate in-flight request ID."));
					} else if (req.Method != "tools/call") {
						await WriteResponseAsync(await HandleRequestSafelyAsync(req, runToken));
					} else if (inFlight.Count >= MaxInFlightTools) {
						await WriteResponseAsync(JsonRpcResponse.CreateError(id, -32000, "Too many in-flight tool calls (maximum 8)."));
					} else {
						var call = new InFlightTool(id, runToken);
						inFlight.Add(call);
						// Start in arrival order, without Task.Run: each call enters the GUI send lock in this order.
						call.Work = RunToolAsync(req, call);
					}
				}
			}
			catch (OperationCanceledException) when (runToken.IsCancellationRequested) { }
			finally {
				shutdown.Cancel();
				foreach (var call in inFlight) {
					await call.Work;
					call.Dispose();
				}
			}

			async Task RunToolAsync(JsonRpcRequest request, InFlightTool call) {
				try {
					var response = await HandleRequestSafelyAsync(request, call.Token);
					await WriteResponseAsync(response, call);
				}
				catch (OperationCanceledException) when (call.Token.IsCancellationRequested) { }
				catch (Exception ex) {
					Console.Error.WriteLine($"Error writing MCP tool response: {ex.Message}");
					shutdown.Cancel();
				}
			}

			async Task WriteResponseAsync(JsonRpcResponse response, InFlightTool? call = null) {
				var token = call?.Token ?? runToken;
				token.ThrowIfCancellationRequested();
				string line = JsonSerializer.Serialize(response, McpJsonContext.Default.JsonRpcResponse);
				await writeLock.WaitAsync(token);
				try {
					if (call != null && !call.TryStartReply()) return;
					// Once a line starts, only session shutdown can interrupt it, not request cancellation.
					await writer.WriteLineAsync(line.AsMemory(), runToken);
				}
				finally {
					writeLock.Release();
				}
			}
		}

		async Task<JsonRpcResponse> HandleRequestSafelyAsync(JsonRpcRequest request, CancellationToken ct) {
			try {
				return await HandleRequestAsync(request, ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
				Console.Error.WriteLine($"MCP request failed: {ex.Message}");
				return JsonRpcResponse.CreateError(request.Id, -32603, "Internal error");
			}
		}

		sealed class InFlightTool(JsonElement id, CancellationToken sessionToken) : IDisposable {
			readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
			int replyState; // 0 = pending, 1 = writing, 2 = cancelled
			public JsonElement Id { get; } = id;
			public CancellationToken Token => cancellation.Token;
			public Task Work { get; set; } = Task.CompletedTask;

			public bool TryStartReply() => Interlocked.CompareExchange(ref replyState, 1, 0) == 0;
			public void Cancel() {
				if (Interlocked.CompareExchange(ref replyState, 2, 0) == 0) cancellation.Cancel();
			}
			public void Dispose() => cancellation.Dispose();
		}

		static bool TryReadRequest(JsonElement root, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonRpcRequest? request, out JsonElement id) {
			request = null;
			id = default;
			if (root.ValueKind != JsonValueKind.Object) return false;

			if (root.TryGetProperty("id", out var candidate)) {
				if (candidate.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number or JsonValueKind.String)) return false;
				id = candidate;
			}

			try {
				request = root.Deserialize(McpJsonContext.Default.JsonRpcRequest);
			}
			catch (JsonException) {
				return false;
			}

			return request != null
				&& request.JsonRpc == "2.0"
				&& !string.IsNullOrWhiteSpace(request.Method)
				&& (request.Method is "initialize" or "tools/call" || request.Params.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array);
		}

		public async Task<JsonRpcResponse> HandleRequestAsync(JsonRpcRequest req, CancellationToken ct) {
			var jsonContext = McpJsonContext.Default;
			switch (req.Method) {
				case "initialize":
					return HandleInitialize(req);

				case "ping":
					return JsonRpcResponse.Success(req.Id, new McpPingResult(), jsonContext.McpPingResult);

				case "tools/list":
					return JsonRpcResponse.Success(req.Id, new McpToolsListResult { Tools = Tools }, jsonContext.McpToolsListResult);

				case "tools/call":
					return await HandleToolCallAsync(req, ct);

				default:
					return JsonRpcResponse.CreateError(req.Id, -32601, $"Method '{req.Method}' not found.");
			}
		}

		static JsonRpcResponse HandleInitialize(JsonRpcRequest req) {
			if (req.Params.ValueKind != JsonValueKind.Object) {
				return JsonRpcResponse.CreateError(req.Id, -32602, "Initialize params must be an object.");
			}

			var jsonContext = McpJsonContext.Default;
			McpInitializeParams? args;
			try {
				args = req.Params.Deserialize(jsonContext.McpInitializeParams);
			}
			catch (JsonException) {
				args = null;
			}
			if (args == null || string.IsNullOrWhiteSpace(args.ProtocolVersion)
				|| args.Capabilities.ValueKind != JsonValueKind.Object || args.ClientInfo == null
				|| string.IsNullOrWhiteSpace(args.ClientInfo.Name) || string.IsNullOrWhiteSpace(args.ClientInfo.Version)) {
				return JsonRpcResponse.CreateError(req.Id, -32602,
					"Initialize requires a protocolVersion string, a capabilities object, and a clientInfo object with name and version strings.");
			}

			// Legacy negotiation: echo a supported request, otherwise offer the supported fallback.
			var protocolVersion = args.ProtocolVersion == SupportedProtocolVersion ? args.ProtocolVersion : SupportedProtocolVersion;
			return JsonRpcResponse.Success(req.Id, new McpInitializeResult {
				ProtocolVersion = protocolVersion,
				Instructions = ServerInstructions
			}, jsonContext.McpInitializeResult);
		}

		async Task<JsonRpcResponse> HandleToolCallAsync(JsonRpcRequest req, CancellationToken ct) {
			if (req.Params.ValueKind != JsonValueKind.Object) {
				return JsonRpcResponse.CreateError(req.Id, -32602, "Tool call params must be an object.");
			}

			var toolName = req.Params.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
				? name.GetString()
				: null;
			if (string.IsNullOrWhiteSpace(toolName)) {
				return JsonRpcResponse.CreateError(req.Id, -32602, "Tool name must be a non-empty string.");
			}
			if (req.Params.TryGetProperty("arguments", out var arguments) && arguments.ValueKind != JsonValueKind.Object) {
				return JsonRpcResponse.CreateError(req.Id, -32602, "Tool arguments must be an object.");
			}
			if (!Tools.Exists(tool => tool.Name == toolName)) {
				return JsonRpcResponse.CreateError(req.Id, -32602, $"Unknown tool '{toolName}'.");
			}

			var toolResult = await ExecuteToolAsync(toolName, arguments, ct);
			return JsonRpcResponse.Success(req.Id, toolResult, McpJsonContext.Default.McpToolResult);
		}

		async Task<McpToolResult> ExecuteToolAsync(string toolName, JsonElement arguments, CancellationToken ct) {
			var paramDict = new Dictionary<string, JsonElement>();
			if (arguments.ValueKind == JsonValueKind.Object) {
				foreach (var prop in arguments.EnumerateObject()) {
					paramDict[prop.Name] = prop.Value;
				}
			}

			string commandName = toolName switch {
				"get_scan_status" => "get_status",
				"get_active_filter" => "get_active_filter",
				"set_active_filter" => "set_active_filter",
				"get_duplicate_groups" => "get_groups",
				"get_group_details" => "get_group_details",
				"get_group_thumbnails" => "get_group_thumbnails",
				"scroll_to_item" => "scroll_to",
				"highlight_group" => "highlight_group",
				"set_item_checked" => "set_checked",
				"select_by_criteria" => "select_by_criteria",
				"audit_selection" => "audit_selection",
				"undo_selection" => "undo",
				"clear_selection" => "clear_selection",
				"invert_selection" => "invert_selection",
				"mark_not_a_match" => "mark_not_a_match",
				"remove_from_list" => "remove_from_list",
				"rename_item" => "rename_item",
				"transfer_suffix_and_rename" => "transfer_suffix_and_rename",
				_ => toolName
			};

			var ipcRes = await ipcClient.SendCommandAsync(commandName, paramDict, ct);
			if (!ipcRes.Success) {
				return McpToolResult.Text(ipcRes.Error ?? "Unknown error communicating with GUI.", isError: true);
			}

			if (toolName == "get_group_thumbnails" && ipcRes.Data.HasValue) {
				return BuildThumbnailsMcpResult(ipcRes.Data.Value);
			}

			string formattedText = ipcRes.Data.HasValue
				? JsonSerializer.Serialize(ipcRes.Data.Value, McpJsonContext.Indented.JsonElement)
				: "Action completed successfully.";

			return McpToolResult.Text(formattedText, isError: false);
		}

		static McpToolResult BuildThumbnailsMcpResult(JsonElement data) {
			var content = new List<McpContentItem>();
			var dto = data.Deserialize(McpJsonContext.Default.GroupThumbnailsResultDto);
			if (dto == null) {
				return McpToolResult.Text(data.GetRawText(), isError: false);
			}

			content.Add(McpContentItem.FromText($"Group {dto.GroupId} — {dto.Items.Count} item(s):"));
			foreach (var item in dto.Items) {
				string flags = $"{(item.IsBest ? "[BEST] " : "")}{(item.Checked ? "[CHECKED] " : "[KEPT] ")}";
				content.Add(McpContentItem.FromText(
					$"{flags}{item.Path} ({item.Resolution}, {item.Format}, {item.Similarity:F1}%, {item.Frames.Count} frame(s))"));
				foreach (var frame in item.Frames) {
					if (!string.IsNullOrEmpty(frame.Base64Data)) {
						content.Add(McpContentItem.FromImage(frame.Base64Data, frame.MimeType));
					}
				}
			}

			return new McpToolResult {
				IsError = false,
				Content = content
			};
		}

		public void Dispose() => ipcClient.Dispose();
	}
}
