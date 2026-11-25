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

namespace VDF.CLI.Mcp {

	public sealed class JsonRpcRequest {
		[JsonPropertyName("jsonrpc")]
		public string JsonRpc { get; set; } = "2.0";

		[JsonPropertyName("id")]
		public object? Id { get; set; }

		[JsonPropertyName("method")]
		public string Method { get; set; } = string.Empty;

		[JsonPropertyName("params")]
		public JsonElement? Params { get; set; }
	}

	public sealed class JsonRpcResponse {
		[JsonPropertyName("jsonrpc")]
		public string JsonRpc { get; set; } = "2.0";

		[JsonPropertyName("id")]
		public object? Id { get; set; }

		[JsonPropertyName("result")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public object? Result { get; set; }

		[JsonPropertyName("error")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public JsonRpcError? Error { get; set; }

		public static JsonRpcResponse Success(object? id, object? result) => new() {
			Id = id,
			Result = result
		};

		public static JsonRpcResponse CreateError(object? id, int code, string message) => new() {
			Id = id,
			Error = new JsonRpcError {
				Code = code,
				Message = message
			}
		};
	}

	public sealed class JsonRpcError {
		[JsonPropertyName("code")]
		public int Code { get; set; }

		[JsonPropertyName("message")]
		public string Message { get; set; } = string.Empty;
	}

	public sealed class McpToolAnnotations {
		[JsonPropertyName("title")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Title { get; set; }

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

		[JsonPropertyName("description")]
		public string Description { get; set; } = string.Empty;

		[JsonPropertyName("inputSchema")]
		public object InputSchema { get; set; } = new();

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
