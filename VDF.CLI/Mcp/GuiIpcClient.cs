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
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VDF.Core.Automation;

namespace VDF.CLI.Mcp {

	internal sealed class IpcRequestDto {
		[JsonPropertyName("command")]
		public required string Command { get; init; }

		[JsonPropertyName("parameters")]
		public Dictionary<string, JsonElement>? Parameters { get; init; }
	}

	public sealed class IpcResponseDto {
		[JsonPropertyName("success")]
		[JsonRequired]
		public bool Success { get; set; }

		[JsonPropertyName("error")]
		public string? Error { get; set; }

		[JsonPropertyName("data")]
		public JsonElement? Data { get; set; }
	}

	public sealed class GuiIpcClient : IDisposable {
		const int MaxResponseBytes = 64 * 1024 * 1024;
		readonly string pipeName;
		readonly int maxResponseBytes;
		readonly TimeProvider timeProvider;
		NamedPipeClientStream? clientStream;
		BoundedLineReader? responseReader;
		StreamWriter? writer;
		readonly SemaphoreSlim sendLock = new(1, 1);

		public GuiIpcClient(string pipeName = AutomationConstants.DefaultPipeName)
			: this(pipeName, MaxResponseBytes) { }

		internal GuiIpcClient(string pipeName, int maxResponseBytes, TimeProvider? timeProvider = null) {
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBytes);
			this.pipeName = string.IsNullOrWhiteSpace(pipeName) ? AutomationConstants.DefaultPipeName : pipeName;
			this.maxResponseBytes = maxResponseBytes;
			this.timeProvider = timeProvider ?? TimeProvider.System;
		}

		static bool IsReadOnly(string command) => command is "get_status" or "get_active_filter" or "get_groups"
			or "get_duplicate_groups" or "get_group_details" or "get_group_thumbnails" or "audit_selection";

		internal static TimeSpan CommandTimeout(string command, Dictionary<string, JsonElement>? parameters) {
			if (command == "get_group_thumbnails" && !(parameters != null
				&& parameters.TryGetValue("load_if_missing", out var load)
				&& bool.TryParse(load.ToString(), out bool loadIfMissing) && !loadIfMissing)) {
				return TimeSpan.FromSeconds(120);
			}
			return TimeSpan.FromSeconds(command is "set_active_filter" or "scroll_to" or "highlight_group" or "set_checked"
				or "select_by_criteria" or "audit_selection" or "undo" or "clear_selection" or "invert_selection"
				or "mark_not_a_match" or "remove_from_list" or "rename_item" or "transfer_suffix_and_rename" ? 60 : 30);
		}

		public async Task<IpcResponseDto> SendCommandAsync(
			string command,
			Dictionary<string, JsonElement>? parameters = null,
			CancellationToken ct = default) {

			bool readOnly = IsReadOnly(command);
			TimeSpan timeout = CommandTimeout(command, parameters);
			using var deadline = new CancellationTokenSource(timeout, timeProvider);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
			var token = linked.Token;
			bool lockHeld = false;
			bool writeAttempted = false;
			try {
				await sendLock.WaitAsync(token);
				lockHeld = true;
				var json = JsonSerializer.Serialize(new IpcRequestDto {
					Command = command,
					Parameters = parameters
				}, McpJsonContext.Default.IpcRequestDto);

				for (int attempt = 0; ; attempt++) {
					if (!await EnsureConnectedAsync(token)) {
						token.ThrowIfCancellationRequested();
						return Failure($"Cannot connect to VDF GUI pipe '{pipeName}'. Please ensure Video Duplicate Finder desktop app is open with Automation enabled in settings.");
					}

					try {
						token.ThrowIfCancellationRequested();
						// Even a failed write may have sent a complete command. Never replay a mutation.
						writeAttempted = true;
						await writer!.WriteLineAsync(json.AsMemory(), token);
						var (responseLine, tooLong) = await responseReader!.ReadLineAsync(token, drainOversize: false, requireTerminator: true);
						if (tooLong) throw new InvalidDataException($"GUI response exceeds the {maxResponseBytes}-byte line limit.");
						if (string.IsNullOrWhiteSpace(responseLine)) {
							throw new IOException("GUI closed connection unexpectedly or returned an empty response.");
						}

						var response = JsonSerializer.Deserialize(responseLine, McpJsonContext.Default.IpcResponseDto)
							?? throw new InvalidDataException("Received null response from GUI.");
						token.ThrowIfCancellationRequested();
						return response;
					}
					catch (IOException) when (readOnly && attempt == 0) {
						ResetConnection();
						token.ThrowIfCancellationRequested();
					}
				}
			}
			catch (Exception ex) {
				if (lockHeld) ResetConnection();
				ct.ThrowIfCancellationRequested();
				string reason = deadline.IsCancellationRequested
					? $"GUI command '{command}' timed out after {timeout.TotalSeconds:0} seconds."
					: $"IPC communication failure: {ex.Message}";
				return Failure(!readOnly && writeAttempted
					? $"Outcome unknown: the GUI may have applied '{command}'. Verify its state before retrying. {reason}"
					: reason);
			}
			finally {
				if (lockHeld) sendLock.Release();
			}
		}

		static IpcResponseDto Failure(string error) => new() { Success = false, Error = error };

		async Task<bool> EnsureConnectedAsync(CancellationToken ct) {
			if (clientStream != null && clientStream.IsConnected) {
				return true;
			}

			ResetConnection();

			try {
				var pipe = new NamedPipeClientStream(
					".",
					pipeName,
					PipeDirection.InOut,
					PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
				clientStream = pipe;
				using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				timeoutCts.CancelAfter(2000);

				await pipe.ConnectAsync(timeoutCts.Token);

				responseReader = new BoundedLineReader(new Utf8ChunkReader(pipe, strict: true), maxResponseBytes, countUtf8Bytes: true);
				writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };
				return true;
			}
			catch when (!ct.IsCancellationRequested) {
				ResetConnection();
				return false;
			}
		}

		void ResetConnection() {
			// Close the pipe before disposing the writer so it cannot flush buffered data.
			try { clientStream?.Dispose(); } catch { }
			try { writer?.Dispose(); } catch { }
			responseReader = null;
			writer = null;
			clientStream = null;
		}

		public void Dispose() {
			ResetConnection();
			sendLock.Dispose();
		}
	}

	// Read one stream chunk at a time: StreamReader.ReadAsync may wait to fill its
	// character buffer even if a complete line has already arrived on the pipe.
	internal sealed class Utf8ChunkReader(Stream stream, bool strict = false) : TextReader {
		readonly Decoder decoder = new UTF8Encoding(false, strict).GetDecoder();
		readonly byte[] bytes = new byte[BoundedLineReader.BufferSize - 3];
		bool firstChunk = true;

		public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) {
			if (buffer.IsEmpty) return 0;
			ArgumentOutOfRangeException.ThrowIfLessThan(buffer.Length, 4);
			while (true) {
				// Leave room for characters from an incomplete UTF-8 sequence carried over by the decoder.
				int count = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, buffer.Length - 3)), cancellationToken);
				int decoded = decoder.GetChars(bytes.AsSpan(0, count), buffer.Span, flush: count == 0);
				if (firstChunk && decoded != 0) {
					firstChunk = false;
					if (buffer.Span[0] == '\uFEFF') {
						buffer[1..decoded].CopyTo(buffer);
						decoded--;
					}
				}
				if (decoded != 0 || count == 0) return decoded;
			}
		}
	}

	// Frame lines without asking TextReader.ReadLineAsync to allocate an unbounded string.
	internal sealed class BoundedLineReader(TextReader source, int limit, bool countUtf8Bytes = false) {
		internal const int BufferSize = 4096;
		readonly char[] buffer = new char[BufferSize];
		int start, end;
		bool skipLineFeed;

		public async ValueTask<(string? Line, bool TooLong)> ReadLineAsync(
			CancellationToken ct, bool drainOversize = true, bool requireTerminator = false) {
			StringBuilder? line = null;
			int length = 0;
			bool tooLong = false, endsWithHighSurrogate = false;
			while (true) {
				ct.ThrowIfCancellationRequested();
				if (start == end) {
					start = 0;
					end = await source.ReadAsync(buffer.AsMemory(), ct);
					if (end == 0) {
						if (requireTerminator && line != null) throw new EndOfStreamException("GUI returned an unterminated response line.");
						return (line?.ToString(), tooLong);
					}
				}
				if (skipLineFeed) {
					skipLineFeed = false;
					if (buffer[start] == '\n' && ++start == end) continue;
				}

				var available = buffer.AsSpan(start, end - start);
				int separator = available.IndexOfAny('\r', '\n');
				var chunk = separator < 0 ? available : available[..separator];
				if (!tooLong && !chunk.IsEmpty) {
					int added = chunk.Length;
					if (countUtf8Bytes) {
						added = Encoding.UTF8.GetByteCount(chunk);
						// A pair split across reads costs four bytes, not two three-byte replacements.
						if (endsWithHighSurrogate && char.IsLowSurrogate(chunk[0])) added -= 2;
						endsWithHighSurrogate = char.IsHighSurrogate(chunk[^1]);
					}
					if (added > limit - length) {
						if (!drainOversize) return (null, true);
						tooLong = true;
						line = null;
					} else {
						length += added;
						(line ??= new StringBuilder()).Append(chunk);
					}
				}
				start += chunk.Length;
				if (separator >= 0) {
					skipLineFeed = buffer[start++] == '\r';
					return (tooLong ? null : line?.ToString() ?? string.Empty, tooLong);
				}
			}
		}
	}
}
