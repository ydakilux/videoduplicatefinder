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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace VDF.CLI.Mcp {

	public sealed class JsonRpcRequest {
		[JsonPropertyName("jsonrpc")]
		public string? JsonRpc { get; set; }

		[JsonPropertyName("id")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
		public JsonElement Id { get; set; }

		[JsonPropertyName("method")]
		public string? Method { get; set; }

		[JsonPropertyName("params")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
		public JsonElement Params { get; set; }
	}

	public sealed class JsonRpcResponse {
		[JsonPropertyName("jsonrpc")]
		public string JsonRpc { get; set; } = "2.0";

		[JsonPropertyName("id")]
		public JsonElement? Id { get; set; }

		[JsonPropertyName("result")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public JsonElement? Result { get; set; }

		[JsonPropertyName("error")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public JsonRpcError? Error { get; set; }

		public static JsonRpcResponse Success<T>(JsonElement id, T result, JsonTypeInfo<T> typeInfo) => new() {
			Id = NormalizeId(id),
			Result = JsonSerializer.SerializeToElement(result, typeInfo)
		};

		public static JsonRpcResponse CreateError(JsonElement id, int code, string message) => new() {
			Id = NormalizeId(id),
			Error = new JsonRpcError {
				Code = code,
				Message = message
			}
		};

		static JsonElement? NormalizeId(JsonElement id) => id.ValueKind == JsonValueKind.Undefined ? null : id;
	}

	internal sealed class McpInitializeParams {
		[JsonPropertyName("protocolVersion")]
		public string? ProtocolVersion { get; set; }

		[JsonPropertyName("capabilities")]
		public JsonElement Capabilities { get; set; }

		[JsonPropertyName("clientInfo")]
		public McpClientInfo? ClientInfo { get; set; }
	}

	internal sealed class McpClientInfo {
		[JsonPropertyName("name")]
		public string? Name { get; set; }

		[JsonPropertyName("version")]
		public string? Version { get; set; }
	}

	internal sealed class McpInitializeResult {
		[JsonPropertyName("protocolVersion")]
		public required string ProtocolVersion { get; init; }

		[JsonPropertyName("capabilities")]
		public McpServerCapabilities Capabilities { get; set; } = new();

		[JsonPropertyName("serverInfo")]
		public McpServerInfo ServerInfo { get; set; } = new();

		[JsonPropertyName("instructions")]
		public required string Instructions { get; init; }
	}

	internal sealed class McpServerCapabilities {
		[JsonPropertyName("tools")]
		public McpToolsCapability Tools { get; set; } = new();
	}

	internal sealed class McpToolsCapability { }

	internal sealed class McpServerInfo {
		[JsonPropertyName("name")]
		public string Name { get; set; } = "vdf-mcp";

		[JsonPropertyName("version")]
		public string Version { get; set; } = "1.1.0";
	}

	internal sealed class McpPingResult { }

	internal sealed class McpToolsListResult {
		[JsonPropertyName("tools")]
		public required List<McpToolDefinition> Tools { get; init; }
	}

	public sealed class JsonRpcError {
		[JsonPropertyName("code")]
		public int Code { get; set; }

		[JsonPropertyName("message")]
		public string Message { get; set; } = string.Empty;
	}

	public sealed class McpToolAnnotations {
		[JsonPropertyName("readOnlyHint")]
		public bool ReadOnlyHint { get; set; }

		[JsonPropertyName("destructiveHint")]
		public bool DestructiveHint { get; set; }

		[JsonPropertyName("idempotentHint")]
		public bool IdempotentHint { get; set; }

		[JsonPropertyName("openWorldHint")]
		public bool OpenWorldHint { get; set; }
	}

	public sealed class McpToolDefinition {
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;

		[JsonPropertyName("title")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Title { get; set; }

		[JsonPropertyName("description")]
		public string Description { get; set; } = string.Empty;

		[JsonPropertyName("inputSchema")]
		public JsonElement InputSchema { get; set; }

		[JsonPropertyName("annotations")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public McpToolAnnotations? Annotations { get; set; }
	}

	public sealed class McpToolResult {
		[JsonPropertyName("content")]
		public List<McpContentItem> Content { get; set; } = new();

		[JsonPropertyName("isError")]
		public bool IsError { get; set; }

		public static McpToolResult Text(string text, bool isError = false) => new() {
			IsError = isError,
			Content = new List<McpContentItem> {
				McpContentItem.FromText(text)
			}
		};
	}

	public sealed class McpContentItem {
		[JsonPropertyName("type")]
		public string Type { get; set; } = "text";

		[JsonPropertyName("text")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Text { get; set; }

		[JsonPropertyName("data")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Data { get; set; }

		[JsonPropertyName("mimeType")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? MimeType { get; set; }

		public static McpContentItem FromText(string text) => new() {
			Type = "text",
			Text = text
		};

		public static McpContentItem FromImage(string base64Data, string mimeType) => new() {
			Type = "image",
			Data = base64Data,
			MimeType = mimeType
		};
	}
}
