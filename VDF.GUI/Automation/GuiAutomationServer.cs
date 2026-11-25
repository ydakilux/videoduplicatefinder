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
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using VDF.Core.Automation;
using VDF.Core.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Automation {
	internal sealed class GuiAutomationServer : IDisposable {
		internal const int MaxRequestLineChars = 4 * 1024 * 1024;
		internal const int MaxConcurrentClients = 8;
		static readonly AutomationJsonContext JsonContext = AutomationJsonContext.Default;

		readonly MainWindowVM vm;
		readonly string pipeName;
		readonly Func<AutomationRequest, MainWindowVM, CancellationToken, Task<AutomationResponse>> handleRequest;
		readonly ConcurrentDictionary<int, NamedPipeServerStream> activeStreams = new();
		int nextClientId;
		CancellationTokenSource? cts;
		Task? listenerTask;

		public bool IsRunning => listenerTask != null && !listenerTask.IsCompleted;

		public GuiAutomationServer(MainWindowVM vm, string pipeName = AutomationConstants.DefaultPipeName,
			Func<AutomationRequest, MainWindowVM, CancellationToken, Task<AutomationResponse>>? handleRequest = null) {
			this.vm = vm ?? throw new ArgumentNullException(nameof(vm));
			this.pipeName = string.IsNullOrWhiteSpace(pipeName) ? AutomationConstants.DefaultPipeName : pipeName;
			this.handleRequest = handleRequest ?? GuiAutomationHandler.HandleAsync;
		}

		public void Start() {
			if (IsRunning) return;

			cts = new CancellationTokenSource();
			var token = cts.Token;
			listenerTask = Task.Run(() => ListenLoopAsync(token));
			Logger.Instance.Info($"GUI Automation IPC Server started on pipe '{pipeName}'.");
		}

		public void Stop() {
			if (cts == null) return;
			try {
				cts.Cancel();
			}
			catch { /* ignore */ }

			foreach (var stream in activeStreams.Values) {
				try { stream.Dispose(); } catch { /* ignore */ }
			}

			cts.Dispose();
			cts = null;
			listenerTask = null;
			Logger.Instance.Info("GUI Automation IPC Server stopped.");
		}

		async Task ListenLoopAsync(CancellationToken ct) {
			while (!ct.IsCancellationRequested) {
				NamedPipeServerStream? server = null;
				try {
					server = new NamedPipeServerStream(
						pipeName,
						PipeDirection.InOut,
						NamedPipeServerStream.MaxAllowedServerInstances,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

					await server.WaitForConnectionAsync(ct);
					ct.ThrowIfCancellationRequested();
					if (activeStreams.Count >= MaxConcurrentClients) {
						await RejectExcessClientAsync(server, ct);
						server.Dispose();
						continue;
					}

					int clientId = Interlocked.Increment(ref nextClientId);
					activeStreams[clientId] = server;
					Logger.Instance.Info($"Automation client #{clientId} connected to GUI pipe.");

					// Handle each connected client concurrently so multiple MCP sessions never block each other.
					var connectedStream = server;
					server = null; // Ownership transferred to client task
					_ = Task.Run(() => ProcessClientAsync(clientId, connectedStream, ct), CancellationToken.None);
				}
				catch (OperationCanceledException) {
					server?.Dispose();
					break;
				}
				catch (Exception ex) {
					server?.Dispose();
					if (!ct.IsCancellationRequested) {
						Logger.Instance.Warn($"Automation IPC connection error: {ex.Message}");
						try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
					}
				}
			}
		}

		static async Task RejectExcessClientAsync(NamedPipeServerStream stream, CancellationToken ct) {
			using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
			grace.CancelAfter(TimeSpan.FromMilliseconds(500));
			try {
				using var writer = CreateWriter(stream);
				await WriteResponseAsync(writer, AutomationResponse.Fail(
					$"GUI automation server is busy: at most {MaxConcurrentClients} concurrent clients are allowed."), grace.Token);
				// Let send-first clients write their request and read the rejection before
				// closing the pipe. Keep only one rejection connection, for a bounded time.
				var discard = new byte[4096];
				while (await stream.ReadAsync(discard.AsMemory(), grace.Token) != 0) { }
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
		}

		async Task ProcessClientAsync(int clientId, NamedPipeServerStream stream, CancellationToken ct) {
			try {
				using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
				using var writer = CreateWriter(stream);
				using var clientLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
				var token = clientLifetime.Token;
				// Keep reading while a handler is running so EOF/broken pipes cancel it, not just
				// its response write. Clients normally send one request and await the reply.
				var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
				var readTask = ReadRequestsAsync(reader, lines.Writer, clientLifetime);
				try {
					await foreach (var line in lines.Reader.ReadAllAsync(token)) {
						token.ThrowIfCancellationRequested();
						AutomationResponse response;
						try {
							var request = JsonSerializer.Deserialize(line, JsonContext.AutomationRequest);
							response = request == null ? AutomationResponse.Fail("Invalid JSON request.") :
								await handleRequest(request, vm, token);
						}
						catch (JsonException ex) {
							response = AutomationResponse.Fail($"Server error parsing request: {ex.Message}");
						}
						await WriteResponseAsync(writer, response, token);
					}
				}
				catch (RequestLimitException ex) {
					await WriteResponseAsync(writer, AutomationResponse.Fail(ex.Message), ct);
				}
				finally {
					clientLifetime.Cancel();
					await readTask;
				}
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
			finally {
				try { stream.Dispose(); } catch { /* ignore */ }
				activeStreams.TryRemove(clientId, out _);
				Logger.Instance.Info($"Automation client #{clientId} disconnected.");
			}
		}

		static StreamWriter CreateWriter(Stream stream) =>
			new(stream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

		static Task WriteResponseAsync(StreamWriter writer, AutomationResponse response, CancellationToken ct) =>
			writer.WriteLineAsync(SerializeResponse(response).AsMemory(), ct);

		sealed class RequestLimitException(string message) : Exception(message) { }

		static async Task ReadRequestsAsync(StreamReader reader, ChannelWriter<string> lines, CancellationTokenSource lifetime) {
			var buffer = new char[4096];
			var line = new StringBuilder();
			bool skipLineFeed = false;
			try {
				int count;
				while ((count = await reader.ReadAsync(buffer.AsMemory(), lifetime.Token)) != 0) {
					for (int start = 0; start < count;) {
						if (skipLineFeed) {
							skipLineFeed = false;
							if (buffer[start] == '\n' && ++start == count) break;
						}
						int separator = buffer.AsSpan(start, count - start).IndexOfAny('\r', '\n');
						int length = separator < 0 ? count - start : separator;
						if (length > MaxRequestLineChars - line.Length)
							throw new RequestLimitException($"Request line exceeds {MaxRequestLineChars} characters.");
						line.Append(buffer, start, length);
						start += length;
						if (separator < 0) break;
						skipLineFeed = buffer[start++] == '\r';
						var request = line.ToString();
						line.Clear();
						if (!string.IsNullOrWhiteSpace(request) && !lines.TryWrite(request))
							throw new RequestLimitException("Too many pipelined requests. Wait for the previous response before sending another request.");
					}
				}
				lifetime.Cancel();
				lines.TryComplete();
			}
			catch (RequestLimitException ex) {
				lines.TryComplete(ex);
				// A handler may still be running when pipelined input hits a limit.
				// Keep watching for disconnect without buffering any more requests.
				try {
					while (await reader.ReadAsync(buffer.AsMemory(), lifetime.Token) != 0) { }
				}
				catch (Exception readError) when (readError is IOException or ObjectDisposedException or OperationCanceledException) { }
				lifetime.Cancel();
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) {
				lifetime.Cancel();
				lines.TryComplete();
			}
		}

		internal static string SerializeResponse(AutomationResponse response) {
			try {
				return JsonSerializer.Serialize(response, JsonContext.AutomationResponse);
			}
			catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException) {
				Logger.Instance.Warn($"Automation IPC response serialization failed: {ex.Message}");
				return JsonSerializer.Serialize(AutomationResponse.Fail("Server error serializing response."), JsonContext.AutomationResponse);
			}
		}

		public void Dispose() => Stop();
	}
}
