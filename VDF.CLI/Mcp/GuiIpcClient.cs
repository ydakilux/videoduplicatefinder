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

	public sealed class IpcResponseDto {
		[JsonPropertyName("success")]
		public bool Success { get; set; }

		[JsonPropertyName("error")]
		public string? Error { get; set; }

		[JsonPropertyName("data")]
		public JsonElement? Data { get; set; }
	}

	public sealed class GuiIpcClient : IDisposable {
		readonly string pipeName;
		NamedPipeClientStream? clientStream;
		StreamReader? reader;
		StreamWriter? writer;
		readonly SemaphoreSlim sendLock = new(1, 1);

		public GuiIpcClient(string pipeName = AutomationConstants.DefaultPipeName) {
			this.pipeName = string.IsNullOrWhiteSpace(pipeName) ? AutomationConstants.DefaultPipeName : pipeName;
		}

		public async Task<IpcResponseDto> SendCommandAsync(
			string command,
			Dictionary<string, object?>? parameters = null,
			CancellationToken ct = default) {

			await sendLock.WaitAsync(ct);
			try {
				// If a previously cached pipe broke because VDF.GUI restarted, reconnect transparently once.
				for (int attempt = 0; attempt < 2; attempt++) {
					bool reusedExisting = clientStream != null && clientStream.IsConnected;
					if (!await EnsureConnectedAsync(ct)) {
						return new IpcResponseDto {
							Success = false,
							Error = $"Cannot connect to VDF GUI pipe '{pipeName}'. Please ensure Video Duplicate Finder desktop app is open with Automation enabled in settings."
						};
					}

					var payload = new AutomationRequest {
						Command = command,
						Parameters = parameters
					};

					try {
						var json = JsonSerializer.Serialize(payload);
						await writer!.WriteLineAsync(json.AsMemory(), ct);

						string? responseLine = await reader!.ReadLineAsync(ct);
						if (string.IsNullOrWhiteSpace(responseLine)) {
							ResetConnection();
							if (reusedExisting && attempt == 0) {
								continue;
							}
							return new IpcResponseDto {
								Success = false,
								Error = "GUI closed connection unexpectedly."
							};
						}

						var response = JsonSerializer.Deserialize<IpcResponseDto>(responseLine);
						return response ?? new IpcResponseDto {
							Success = false,
							Error = "Received null response from GUI."
						};
					}
					catch (IOException) when (reusedExisting && attempt == 0) {
						ResetConnection();
					}
				}

				return new IpcResponseDto {
					Success = false,
					Error = "Failed to communicate with VDF GUI after reconnect attempt."
				};
			}
			catch (Exception ex) {
				ResetConnection();
				return new IpcResponseDto {
					Success = false,
					Error = $"IPC communication failure: {ex.Message}"
				};
			}
			finally {
				sendLock.Release();
			}
		}

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
				using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				timeoutCts.CancelAfter(2000);

				await pipe.ConnectAsync(timeoutCts.Token);

				clientStream = pipe;
				reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
				writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };
				return true;
			}
			catch {
				ResetConnection();
				return false;
			}
		}

		void ResetConnection() {
			try { reader?.Dispose(); } catch { }
			try { writer?.Dispose(); } catch { }
			try { clientStream?.Dispose(); } catch { }
			reader = null;
			writer = null;
			clientStream = null;
		}

		public void Dispose() {
			ResetConnection();
			sendLock.Dispose();
		}
	}
}
