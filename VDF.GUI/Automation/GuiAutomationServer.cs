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
using System.Threading.Tasks;
using VDF.Core.Automation;
using VDF.Core.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Automation {
	internal sealed class GuiAutomationServer : IDisposable {
		readonly MainWindowVM vm;
		readonly string pipeName;
		readonly ConcurrentDictionary<int, NamedPipeServerStream> activeStreams = new();
		int nextClientId;
		CancellationTokenSource? cts;
		Task? listenerTask;

		public bool IsRunning => listenerTask != null && !listenerTask.IsCompleted;

		public GuiAutomationServer(MainWindowVM vm, string pipeName = AutomationConstants.DefaultPipeName) {
			this.vm = vm ?? throw new ArgumentNullException(nameof(vm));
			this.pipeName = string.IsNullOrWhiteSpace(pipeName) ? AutomationConstants.DefaultPipeName : pipeName;
		}

		public void Start() {
			if (IsRunning) return;

			cts = new CancellationTokenSource();
			listenerTask = Task.Run(() => ListenLoopAsync(cts.Token));
			Logger.Instance.Info($"GUI Automation IPC Server started on pipe '{pipeName}'.");
		}

		public void Stop() {
			if (cts == null) return;
			try {
				cts.Cancel();
			}
			catch { /* ignore */ }

			foreach (var kv in activeStreams) {
				if (activeStreams.TryRemove(kv.Key, out var stream)) {
					try { stream.Dispose(); } catch { /* ignore */ }
				}
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

		async Task ProcessClientAsync(int clientId, NamedPipeServerStream stream, CancellationToken ct) {
			try {
				using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
				using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

				while (!ct.IsCancellationRequested && stream.IsConnected) {
					string? line;
					try {
						line = await reader.ReadLineAsync(ct);
					}
					catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) {
						break;
					}

					if (line == null) {
						break;
					}

					if (string.IsNullOrWhiteSpace(line)) {
						continue;
					}

					AutomationResponse response;
					try {
						var request = JsonSerializer.Deserialize<AutomationRequest>(line);
						if (request == null) {
							response = AutomationResponse.Fail("Invalid JSON request.");
						}
						else {
							response = await GuiAutomationHandler.HandleAsync(request, vm, ct);
						}
					}
					catch (Exception ex) {
						response = AutomationResponse.Fail($"Server error parsing request: {ex.Message}");
					}

					try {
						var responseJson = JsonSerializer.Serialize(response);
						await writer.WriteLineAsync(responseJson.AsMemory(), ct);
					}
					catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) {
						break;
					}
				}
			}
			finally {
				activeStreams.TryRemove(clientId, out _);
				try {
					if (stream.IsConnected) stream.Disconnect();
				}
				catch { /* ignore */ }
				try { stream.Dispose(); } catch { /* ignore */ }
				Logger.Instance.Info($"Automation client #{clientId} disconnected.");
			}
		}

		public void Dispose() => Stop();
	}
}
