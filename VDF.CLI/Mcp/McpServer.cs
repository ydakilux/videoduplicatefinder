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
		readonly GuiIpcClient ipcClient;

		public McpServer(string pipeName = AutomationConstants.DefaultPipeName) {
			ipcClient = new GuiIpcClient(pipeName);
		}

		static McpToolAnnotations ReadOnly(string title) => new() {
			Title = title,
			ReadOnlyHint = true,
			DestructiveHint = false,
			IdempotentHint = true,
			OpenWorldHint = false
		};

		static McpToolAnnotations UiAction(string title, bool idempotent = true) => new() {
			Title = title,
			ReadOnlyHint = false,
			DestructiveHint = false,
			IdempotentHint = idempotent,
			OpenWorldHint = false
		};

		static McpToolAnnotations DiskMutation(string title) => new() {
			Title = title,
			ReadOnlyHint = false,
			DestructiveHint = true,
			IdempotentHint = false,
			OpenWorldHint = false
		};

		public static readonly List<McpToolDefinition> Tools = new() {
			new() {
				Name = "get_scan_status",
				Description = "Get current status of Video Duplicate Finder (scanning state, total duplicates, visible vs total duplicates/groups/checked, wasted space, active filter).",
				Annotations = ReadOnly("Get Scan Status"),
				InputSchema = new {
					type = "object",
					properties = new { }
				}
			},
			new() {
				Name = "get_active_filter",
				Description = "Get current active filter configuration in GUI (path/wildcard, file type, similarity range, secondary flags) and visible vs total item/group counts.",
				Annotations = ReadOnly("Get Active Filter"),
				InputSchema = new {
					type = "object",
					properties = new { }
				}
			},
			new() {
				Name = "set_active_filter",
				Description = "Dynamically update GUI active filters (filter by path/folder, file type, similarity range, and secondary flags).",
				Annotations = UiAction("Set Active Filter"),
				InputSchema = new {
					type = "object",
					properties = new {
						filter_by_path = new { type = "string", description = "Substring or wildcard pattern to filter paths (e.g. 'D:\\Movies' or '*holiday*')" },
						file_type = new { type = "string", @enum = new[] { "All", "Videos", "Images" }, description = "Filter by file type" },
						similarity_from = new { type = "integer", description = "Minimum similarity percentage (0-100)" },
						similarity_to = new { type = "integer", description = "Maximum similarity percentage (0-100)" },
						only_groups_with_checked_items = new { type = "boolean", description = "Show only groups with at least one checked item" },
						hide_groups_with_one_file_left = new { type = "boolean", description = "Hide groups where only one file remains" },
						only_ai_matches = new { type = "boolean", description = "Show only groups containing AI-matched files" }
					}
				}
			},
			new() {
				Name = "get_duplicate_groups",
				Description = "List duplicate groups with summaries, QualityRanker best keeper + reason, synthetic metadata (codecs, resolutions, mixed flags), and optional full item details in 1 call.",
				Annotations = ReadOnly("List Duplicate Groups"),
				InputSchema = new {
					type = "object",
					properties = new {
						offset = new { type = "integer", description = "Pagination offset (default 0)" },
						limit = new { type = "integer", description = "Number of groups to return (default 50)" },
						current_filter_only = new { type = "boolean", description = "Only return groups containing items visible in the active filter (default true)" },
						include_items = new { type = "boolean", description = "Include full item details for each group in a single fast call (default false)" },
						has_checked = new { type = "boolean", description = "Filter groups with checked items (true) or without checked items (false)" },
						has_mixed_codecs = new { type = "boolean", description = "Filter groups containing multiple distinct codecs (e.g. AV1 and H.264)" },
						has_mixed_resolutions = new { type = "boolean", description = "Filter groups containing multiple distinct resolutions" },
						min_similarity = new { type = "number", description = "Minimum group similarity percentage" }
					}
				}
			},
			new() {
				Name = "get_group_details",
				Description = "Get complete details for all duplicate items in a group (paths, resolution, bitrate, fps, codec, HDR, audio/subtitle languages, QualityRanker isBest + bestReason, tombstone/offline flags, etc.).",
				Annotations = ReadOnly("Get Group Details"),
				InputSchema = new {
					type = "object",
					properties = new {
						group_id = new { type = "string", description = "Guid of the duplicate group" }
					},
					required = new[] { "group_id" }
				}
			},
			new() {
				Name = "get_group_thumbnails",
				Description = "Retrieve visual preview frames (as native MCP images) for every item in a duplicate group so a multimodal AI can visually compare candidates for watermarks, cropping, quality, or false positives.",
				Annotations = ReadOnly("Get Group Preview Frames"),
				InputSchema = new {
					type = "object",
					properties = new {
						group_id = new { type = "string", description = "Guid of the duplicate group" },
						path = new { type = "string", description = "Optional path or filename belonging to the group if group_id is omitted" },
						load_if_missing = new { type = "boolean", description = "Extract thumbnails via FFmpeg on demand if not yet cached (default true)" },
						max_frames_per_item = new { type = "integer", description = "Maximum frames returned per item (1-4, default 2)" }
					}
				}
			},
			new() {
				Name = "scroll_to_item",
				Description = "Scroll the GUI list directly to a specific video item or group so the user can visually inspect it on screen.",
				Annotations = UiAction("Scroll To Item"),
				InputSchema = new {
					type = "object",
					properties = new {
						path = new { type = "string", description = "Full path or filename of the video to focus" },
						group_id = new { type = "string", description = "Guid of the duplicate group to scroll to" }
					}
				}
			},
			new() {
				Name = "highlight_group",
				Description = "Highlight, expand, scroll to, and focus a duplicate group in the GUI interface for visual inspection.",
				Annotations = UiAction("Highlight Group"),
				InputSchema = new {
					type = "object",
					properties = new {
						group_id = new { type = "string", description = "Guid of the duplicate group to highlight" },
						path = new { type = "string", description = "File path or filename belonging to the group" }
					}
				}
			},
			new() {
				Name = "set_item_checked",
				Description = "Check or uncheck specific video files or an entire group in the GUI, with active filter safety guard.",
				Annotations = UiAction("Check / Uncheck Items"),
				InputSchema = new {
					type = "object",
					properties = new {
						paths = new { type = "array", items = new { type = "string" }, description = "List of file paths to check/uncheck" },
						path = new { type = "string", description = "Single file path to check/uncheck" },
						group_id = new { type = "string", description = "Group ID to check/uncheck all items in" },
						@checked = new { type = "boolean", description = "True to check (mark for deletion), False to uncheck" },
						current_filter_only = new { type = "boolean", description = "Refuse to check items currently hidden by the active filter (default true)" }
					},
					required = new[] { "checked" }
				}
			},
			new() {
				Name = "select_by_criteria",
				Description = "Select (check or uncheck) duplicate videos using a criteria expression or preset strategy (with active filter awareness and safety guards).",
				Annotations = UiAction("Select By Criteria"),
				InputSchema = new {
					type = "object",
					properties = new {
						expression = new { type = "string", description = "Dynamic C# expression evaluated on DuplicateItem (e.g. 'item.SizeLong > 5000000 && item.BitRateKbs < 2500')" },
						strategy = new { type = "string", description = "Preset strategy: 'best-codec', 'lowest-quality', 'smallest-file', 'largest-file', 'worst-resolution', 'shortest-duration', 'identical', 'identical-except-size', 'oldest', 'newest'" },
						action = new { type = "string", @enum = new[] { "check", "uncheck" }, description = "'check' (default) or 'uncheck'" },
						allow_all_in_group = new { type = "boolean", description = "Allow checking 100% of items in a group (defaults to false to prevent accidental total deletion)" },
						current_filter_only = new { type = "boolean", description = "Only apply selection to items visible in the active filter (default true)" }
					}
				}
			},
			new() {
				Name = "audit_selection",
				Description = "Audit current selection for safety (wipeout protection aware of hidden items), quality inversions, codec inversions, and criteria compliance.",
				Annotations = ReadOnly("Audit Selection"),
				InputSchema = new {
					type = "object",
					properties = new {
						criteria_expression = new { type = "string", description = "Optional expression that all checked items must satisfy" },
						require_at_least_one_kept = new { type = "boolean", description = "Ensure at least one copy in every group is kept (defaults to true)" },
						current_filter_only = new { type = "boolean", description = "Only audit items and groups visible in the active filter (default true)" }
					}
				}
			},
			new() {
				Name = "undo_selection",
				Description = "Undo the last selection action in the GUI (equivalent to Ctrl+Z).",
				Annotations = UiAction("Undo Selection", idempotent: false),
				InputSchema = new {
					type = "object",
					properties = new { }
				}
			},
			new() {
				Name = "clear_selection",
				Description = "Uncheck all items in the GUI.",
				Annotations = UiAction("Clear Selection"),
				InputSchema = new {
					type = "object",
					properties = new { }
				}
			},
			new() {
				Name = "invert_selection",
				Description = "Invert all checkboxes in the GUI.",
				Annotations = UiAction("Invert Selection", idempotent: false),
				InputSchema = new {
					type = "object",
					properties = new { }
				}
			},
			new() {
				Name = "mark_not_a_match",
				Description = "Mark one or more false-positive duplicate groups as 'Not a Match' so they are hidden immediately and excluded from future scans (files on disk are never touched).",
				Annotations = UiAction("Mark Group As Not a Match"),
				InputSchema = new {
					type = "object",
					properties = new {
						group_id = new { type = "string", description = "Guid of a single duplicate group to mark as Not a Match" },
						group_ids = new { type = "array", items = new { type = "string" }, description = "List of duplicate group Guids to mark as Not a Match" }
					}
				}
			},
			new() {
				Name = "remove_from_list",
				Description = "Remove items or groups from the VDF results list (and optionally exclude them from future scans) WITHOUT deleting any file on disk.",
				Annotations = UiAction("Remove From Results List"),
				InputSchema = new {
					type = "object",
					properties = new {
						path = new { type = "string", description = "Single file path or filename to remove from the list" },
						paths = new { type = "array", items = new { type = "string" }, description = "List of file paths to remove from the list" },
						group_id = new { type = "string", description = "Remove all items of this group from the list" },
						checked_only = new { type = "boolean", description = "If true, remove all currently checked items in scope from the list (default false)" },
						exclude_from_future_scans = new { type = "boolean", description = "If true, also mark removed file entries as excluded in ScannedFiles.db (default false)" },
						current_filter_only = new { type = "boolean", description = "Only remove items visible in the active filter (default true)" }
					}
				}
			},
			new() {
				Name = "rename_item",
				Description = "Rename a duplicate item on disk, in VDF memory, and in the VDF database (keeps UI and companion cover image synchronized).",
				Annotations = DiskMutation("Rename File On Disk"),
				InputSchema = new {
					type = "object",
					properties = new {
						path = new { type = "string", description = "Current path or filename of the item to rename" },
						new_name = new { type = "string", description = "New file name (with or without extension) or new full path" }
					},
					required = new[] { "path", "new_name" }
				}
			},
			new() {
				Name = "transfer_suffix_and_rename",
				Description = "Transfer tags/suffixes (e.g. '___tag' or '______notes') from checked/deleted items onto the kept items in the duplicate group, updating VDF memory, DB, and disk without overwriting existing files.",
				Annotations = DiskMutation("Transfer Suffix & Rename On Disk"),
				InputSchema = new {
					type = "object",
					properties = new {
						group_id = new { type = "string", description = "Optional duplicate group ID (omitted to apply across all groups in active filter)" },
						current_filter_only = new { type = "boolean", description = "Only process groups visible in the active filter (default true)" },
						preview = new { type = "boolean", description = "If true, only returns planned renames without modifying files (default false)" },
						suffix_pattern = new { type = "string", description = "Regex pattern matching the tag suffix (default: '(_{2,}.*)$')" }
					}
				}
			}
		};

		public async Task RunAsync(CancellationToken ct) {
			Console.Error.WriteLine("VDF MCP Server started. Listening on stdio...");

			var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
			var writer = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };

			while (!ct.IsCancellationRequested) {
				string? line;
				try {
					line = await reader.ReadLineAsync(ct);
				}
				catch (Exception ex) {
					Console.Error.WriteLine($"Error reading stdin: {ex.Message}");
					break;
				}

				if (line == null) break;
				if (string.IsNullOrWhiteSpace(line)) continue;

				JsonRpcRequest? req = null;
				try {
					req = JsonSerializer.Deserialize<JsonRpcRequest>(line);
				}
				catch (Exception ex) {
					Console.Error.WriteLine($"Failed to parse JSON-RPC line: {ex.Message}");
					var errRes = JsonRpcResponse.CreateError(null, -32700, "Parse error");
					await writer.WriteLineAsync(JsonSerializer.Serialize(errRes));
					continue;
				}

				if (req == null) continue;

				if (req.Method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase)) {
					continue;
				}

				var res = await HandleRequestAsync(req, ct);
				if (res != null) {
					var resJson = JsonSerializer.Serialize(res);
					await writer.WriteLineAsync(resJson);
				}
			}
		}

		public async Task<JsonRpcResponse?> HandleRequestAsync(JsonRpcRequest req, CancellationToken ct) {
			switch (req.Method) {
				case "initialize":
					return JsonRpcResponse.Success(req.Id, new {
						protocolVersion = "2024-11-05",
						capabilities = new {
							tools = new { }
						},
						serverInfo = new {
							name = "vdf-mcp",
							version = "1.1.0"
						}
					});

				case "ping":
					return JsonRpcResponse.Success(req.Id, new { });

				case "tools/list":
					return JsonRpcResponse.Success(req.Id, new {
						tools = Tools
					});

				case "tools/call":
					return await HandleToolCallAsync(req, ct);

				default:
					return JsonRpcResponse.CreateError(req.Id, -32601, $"Method '{req.Method}' not found.");
			}
		}

		async Task<JsonRpcResponse> HandleToolCallAsync(JsonRpcRequest req, CancellationToken ct) {
			if (!req.Params.HasValue) {
				return JsonRpcResponse.CreateError(req.Id, -32602, "Missing params object.");
			}

			string? toolName = null;
			JsonElement arguments = default;

			if (req.Params.Value.TryGetProperty("name", out var nameProp))
				toolName = nameProp.GetString();

			if (req.Params.Value.TryGetProperty("arguments", out var argsProp))
				arguments = argsProp;

			if (string.IsNullOrWhiteSpace(toolName)) {
				return JsonRpcResponse.CreateError(req.Id, -32602, "Missing tool name in params.");
			}

			var toolResult = await ExecuteToolAsync(toolName, arguments, ct);
			return JsonRpcResponse.Success(req.Id, toolResult);
		}

		async Task<McpToolResult> ExecuteToolAsync(string toolName, JsonElement arguments, CancellationToken ct) {
			var paramDict = new Dictionary<string, object?>();
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
				? JsonSerializer.Serialize(ipcRes.Data.Value, new JsonSerializerOptions { WriteIndented = true })
				: "Action completed successfully.";

			return McpToolResult.Text(formattedText, isError: false);
		}

		static McpToolResult BuildThumbnailsMcpResult(JsonElement data) {
			var content = new List<McpContentItem>();
			var dto = JsonSerializer.Deserialize<GroupThumbnailsResultDto>(data.GetRawText());
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
