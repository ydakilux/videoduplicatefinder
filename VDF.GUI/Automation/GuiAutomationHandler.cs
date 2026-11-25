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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VDF.Core;
using VDF.Core.Automation;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.Utils;
using VDF.GUI.ViewModels;
using AutomationParameters = System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>;

namespace VDF.GUI.Automation {
	internal static class GuiAutomationHandler {
		static readonly AutomationJsonContext JsonContext = AutomationJsonContext.Default;
		static readonly SemaphoreSlim MutationGate = new(1, 1);
		const int MaxListEntries = 10_000;
		const int MaxTextBytes = 4 * 1024;
		const int MaxThumbnailItems = 64;
		const int MaxThumbnailBytes = 8 * 1024 * 1024;

		static async Task<AutomationResponse> MutateAsync(Func<Task<AutomationResponse>> action, CancellationToken ct) {
			await MutationGate.WaitAsync(ct);
			try {
				return await Dispatcher.UIThread.InvokeAsync(() => {
					ct.ThrowIfCancellationRequested();
					return action();
				});
			}
			finally {
				MutationGate.Release();
			}
		}

		public static async Task<AutomationResponse> HandleAsync(
			AutomationRequest request,
			MainWindowVM vm,
			CancellationToken ct = default) {

			if (string.IsNullOrWhiteSpace(request.Command)) {
				return AutomationResponse.Fail("Empty command.");
			}

			try {
				ct.ThrowIfCancellationRequested();
				Task<AutomationResponse> Mutate(Func<AutomationResponse> action) => MutateAsync(() => Task.FromResult(action()), ct);
				Task<AutomationResponse> MutateTask(Func<Task<AutomationResponse>> action) => MutateAsync(action, ct);
				switch (request.Command.Trim().ToLowerInvariant()) {
					case "getstatus":
					case "get_status":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleGetStatus(vm));

					case "getactivefilter":
					case "get_active_filter":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleGetActiveFilter(vm));

					case "setactivefilter":
					case "set_active_filter":
						return await Mutate(() => HandleSetActiveFilter(request.Parameters, vm, ct));

					case "getgroups":
					case "get_groups":
					case "get_duplicate_groups":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleGetGroups(request.Parameters, vm));

					case "getgroupdetails":
					case "get_group_details":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleGetGroupDetails(request.Parameters, vm));

					case "getgroupthumbnails":
					case "get_group_thumbnails":
						return await HandleGetGroupThumbnailsAsync(request.Parameters, vm, ct);

					case "scrollto":
					case "scroll_to":
					case "scrolltoitem":
					case "scroll_to_item":
						return await Mutate(() => HandleScrollTo(request.Parameters, vm, ct));

					case "highlightgroup":
					case "highlight_group":
						return await Mutate(() => HandleHighlightGroup(request.Parameters, vm, ct));

					case "setchecked":
					case "set_checked":
					case "set_item_checked":
						return await Mutate(() => HandleSetChecked(request.Parameters, vm, ct));

					case "selectbycriteria":
					case "select_by_criteria":
						return await Mutate(() => HandleSelectByCriteria(request.Parameters, vm, ct));

					case "auditselection":
					case "audit_selection":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleAuditSelection(request.Parameters, vm, ct));

					case "undo":
					case "undo_selection":
						return await Mutate(() => HandleUndo(vm));

					case "clearselection":
					case "clear_selection":
						return await Mutate(() => HandleClearSelection(vm, ct));

					case "invertselection":
					case "invert_selection":
						return await Mutate(() => HandleInvertSelection(vm, ct));

					case "marknotamatch":
					case "mark_not_a_match":
						return await MutateTask(() => HandleMarkNotAMatchAsync(request.Parameters, vm, ct));

					case "removefromlist":
					case "remove_from_list":
						return await MutateTask(() => HandleRemoveFromListAsync(request.Parameters, vm, ct));

					case "renameitem":
					case "rename_item":
						return await Mutate(() => HandleRenameItem(request.Parameters, vm, ct));

					case "transfersuffix":
					case "transfer_suffix":
					case "transfersuffixandrename":
					case "transfer_suffix_and_rename":
						return await Mutate(() => HandleTransferSuffix(request.Parameters, vm, ct));

					default:
						return AutomationResponse.Fail($"Unknown command '{request.Command}'.");
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				return AutomationResponse.Fail("Automation request canceled.");
			}
			catch (Exception ex) when (ex is RegexMatchTimeoutException or SelectionExpression.MatchTimedOutException) {
				return AutomationResponse.Fail(SelectionExpression.RegexTimeoutMessage);
			}
			catch (Exception ex) {
				return AutomationResponse.Fail($"Error executing '{request.Command}': {ex.Message}");
			}
		}

		static ActiveFilterDto BuildActiveFilterDto(MainWindowVM vm) => new() {
			FilterByPath = vm.FilterByPath ?? string.Empty,
			FileType = vm.FileType?.Name ?? "All",
			SimilarityFrom = vm.FilterSimilarityFrom,
			SimilarityTo = vm.FilterSimilarityTo,
			OnlyGroupsWithCheckedItems = vm.FilterGroupsWithCheckedItems,
			HideGroupsWithOneFileLeft = vm.FilterHideGroupsWithOneFileLeft,
			OnlyAiMatches = vm.FilterOnlyGroupsWithAiMatches
		};

		static AutomationResponse HandleGetStatus(MainWindowVM vm) {
			int total = vm.Duplicates.Count;
			int visible = vm.Duplicates.Count(d => d.IsVisibleInFilter);
			int groups = vm.Duplicates.GroupBy(d => d.ItemInfo.GroupId).Count();
			int visibleGroups = vm.Duplicates.Where(d => d.IsVisibleInFilter).GroupBy(d => d.ItemInfo.GroupId).Count();
			int checkedCount = vm.Duplicates.Count(d => d.Checked);
			int visibleChecked = vm.Duplicates.Count(d => d.IsVisibleInFilter && d.Checked);
			long wastedSpace = vm.Duplicates.Where(d => d.Checked).Sum(d => d.ItemInfo.SizeLong);
			long visibleWastedSpace = vm.Duplicates.Where(d => d.IsVisibleInFilter && d.Checked).Sum(d => d.ItemInfo.SizeLong);

			var dto = new ScanStatusDto {
				IsScanning = vm.IsScanning,
				IsPaused = vm.IsPaused,
				IsReviewState = vm.IsReviewState,
				TotalDuplicates = total,
				VisibleDuplicates = visible,
				TotalGroups = groups,
				VisibleGroups = visibleGroups,
				CheckedCount = checkedCount,
				VisibleCheckedCount = visibleChecked,
				TotalWastedSpaceBytes = wastedSpace,
				VisibleWastedSpaceBytes = visibleWastedSpace,
				StatusMessage = vm.IsBusyOverlayText,
				ActiveFilter = BuildActiveFilterDto(vm)
			};

			return AutomationResponse.Ok(dto, JsonContext.ScanStatusDto);
		}

		static AutomationResponse HandleGetActiveFilter(MainWindowVM vm) {
			var filterDto = BuildActiveFilterDto(vm);
			int total = vm.Duplicates.Count;
			int visible = vm.Duplicates.Count(d => d.IsVisibleInFilter);
			int totalGroups = vm.Duplicates.GroupBy(d => d.ItemInfo.GroupId).Count();
			int visibleGroups = vm.Duplicates.Where(d => d.IsVisibleInFilter).GroupBy(d => d.ItemInfo.GroupId).Count();
			int checkedCount = vm.Duplicates.Count(d => d.Checked);
			int visibleChecked = vm.Duplicates.Count(d => d.IsVisibleInFilter && d.Checked);

			return AutomationResponse.Ok(new ActiveFilterResultDto(filterDto, total, visible, totalGroups,
				visibleGroups, checkedCount, visibleChecked), JsonContext.ActiveFilterResultDto);
		}

		static AutomationResponse HandleSetActiveFilter(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			string? filterByPath = GetStringParam(parameters, "filterByPath") ??
			                       GetStringParam(parameters, "filter_by_path") ??
			                       GetStringParam(parameters, "path");
			CheckTextBytes(filterByPath, "filter_by_path");

			string? fileType = GetStringParam(parameters, "fileType") ??
			                   GetStringParam(parameters, "file_type");

			int? simFrom = GetNullableIntParam(parameters, "similarityFrom") ??
			               GetNullableIntParam(parameters, "similarity_from");

			int? simTo = GetNullableIntParam(parameters, "similarityTo") ??
			             GetNullableIntParam(parameters, "similarity_to");

			bool? onlyChecked = GetNullableBoolParam(parameters, "onlyGroupsWithCheckedItems") ??
			                    GetNullableBoolParam(parameters, "only_groups_with_checked_items");

			bool? hideOneLeft = GetNullableBoolParam(parameters, "hideGroupsWithOneFileLeft") ??
			                    GetNullableBoolParam(parameters, "hide_groups_with_one_file_left");

			bool? onlyAi = GetNullableBoolParam(parameters, "onlyAiMatches") ??
			               GetNullableBoolParam(parameters, "only_ai_matches");

			ct.ThrowIfCancellationRequested();
			if (filterByPath != null) {
				vm.FilterByPath = filterByPath;
			}

			if (!string.IsNullOrWhiteSpace(fileType)) {
				var opt = vm.TypeFilters.FirstOrDefault(f => f.Name.Equals(fileType, StringComparison.OrdinalIgnoreCase));
				if (opt != null) {
					vm.FileType = opt;
				}
			}

			if (simFrom.HasValue) {
				vm.FilterSimilarityFrom = Math.Clamp(simFrom.Value, 0, 100);
			}

			if (simTo.HasValue) {
				vm.FilterSimilarityTo = Math.Clamp(simTo.Value, 0, 100);
			}

			if (onlyChecked.HasValue) {
				vm.FilterGroupsWithCheckedItems = onlyChecked.Value;
			}

			if (hideOneLeft.HasValue) {
				vm.FilterHideGroupsWithOneFileLeft = hideOneLeft.Value;
			}

			if (onlyAi.HasValue) {
				vm.FilterOnlyGroupsWithAiMatches = onlyAi.Value;
			}

			vm.RefreshResultsView();

			vm.SetMcpStatus(FormatLang(
				"Mcp.Status.FilterUpdated",
				"MCP: Filter updated (Path: '{0}', Type: {1}).",
				vm.FilterByPath ?? string.Empty,
				vm.FileType?.Name ?? "All"));

			return HandleGetActiveFilter(vm);
		}

		static DuplicateItemDetailDto MapToItemDetailDto(DuplicateItemVM d, DuplicateItemVM? groupKeeper, string? bestReason) {
			bool isBest = groupKeeper != null && ReferenceEquals(d, groupKeeper);
			string clipOffset = d.ItemInfo.PartialClipOffsetDisplay;
			return new DuplicateItemDetailDto {
				GroupId = d.ItemInfo.GroupId,
				Path = d.ItemInfo.Path,
				Folder = d.ItemInfo.Folder,
				Checked = d.Checked,
				IsVisibleInFilter = d.IsVisibleInFilter,
				IsBest = isBest,
				BestReason = isBest ? bestReason : null,
				IsBestSize = d.ItemInfo.IsBestSize,
				SizeBytes = d.ItemInfo.SizeLong,
				Duration = d.ItemInfo.Duration,
				Resolution = d.ItemInfo.FrameSize ?? string.Empty,
				CodecRank = MainWindowVM.CodecRank(d.ItemInfo.Format),
				BitRateKbs = d.ItemInfo.BitRateKbs,
				Fps = d.ItemInfo.Fps,
				Format = d.ItemInfo.Format ?? string.Empty,
				HdrFormat = d.ItemInfo.HdrFormat ?? string.Empty,
				AudioFormat = d.ItemInfo.AudioFormat ?? string.Empty,
				AudioChannel = d.ItemInfo.AudioChannel ?? string.Empty,
				AudioSampleRate = d.ItemInfo.AudioSampleRate,
				AudioBitRateKbs = d.ItemInfo.AudioBitRateKbs,
				AudioLanguages = d.ItemInfo.AudioLanguages ?? string.Empty,
				SubtitleLanguages = d.ItemInfo.SubtitleLanguages ?? string.Empty,
				DateCreated = d.ItemInfo.DateCreated,
				DateModified = d.ItemInfo.DateModified,
				Similarity = d.ItemInfo.Similarity,
				IsImage = d.ItemInfo.IsImage,
				IsAiMatched = d.ItemInfo.IsAiMatched,
				IsTombstone = d.IsTombstone,
				IsOffline = d.IsOffline,
				PartialClipOffset = string.IsNullOrEmpty(clipOffset) ? null : clipOffset
			};
		}

		static AutomationResponse HandleGetGroups(AutomationParameters? parameters, MainWindowVM vm) {
			int offset = Math.Max(0, GetIntParam(parameters, "offset", 0));
			int limit = GetIntParam(parameters, "limit", 50);
			if (limit <= 0) limit = 50;

			bool currentFilterOnly = GetBoolParam(parameters, "currentFilterOnly", true) &&
			                         GetBoolParam(parameters, "current_filter_only", true);

			bool? hasChecked = GetNullableBoolParam(parameters, "hasChecked") ??
			                   GetNullableBoolParam(parameters, "has_checked");

			bool? hasMixedCodecs = GetNullableBoolParam(parameters, "hasMixedCodecs") ??
			                       GetNullableBoolParam(parameters, "has_mixed_codecs");

			bool? hasMixedResolutions = GetNullableBoolParam(parameters, "hasMixedResolutions") ??
			                           GetNullableBoolParam(parameters, "has_mixed_resolutions");

			float? minSimilarity = GetNullableFloatParam(parameters, "minSimilarity") ??
			                       GetNullableFloatParam(parameters, "min_similarity");

			bool includeItems = GetBoolParam(parameters, "includeItems", false) ||
			                    GetBoolParam(parameters, "include_items", false);
			limit = Math.Min(limit, includeItems ? 100 : 500);

			var allGroupList = vm.Duplicates
				.GroupBy(d => d.ItemInfo.GroupId)
				.ToList();

			var summaries = new List<DuplicateGroupSummaryDto>();
			int matchingGroups = 0;

			foreach (var g in allGroupList) {
				var items = g.ToList();
				var visibleItems = items.Where(i => i.IsVisibleInFilter).ToList();

				if (currentFilterOnly && visibleItems.Count == 0) {
					continue;
				}

				var formats = items
					.Where(i => !string.IsNullOrEmpty(i.ItemInfo.Format))
					.Select(i => i.ItemInfo.Format!.ToLowerInvariant())
					.Distinct()
					.ToList();

				var resolutions = items
					.Where(i => !string.IsNullOrEmpty(i.ItemInfo.FrameSize))
					.Select(i => i.ItemInfo.FrameSize!)
					.Distinct()
					.ToList();

				bool hasMixedFmt = formats.Count > 1;
				bool hasMixedRes = resolutions.Count > 1;
				int checkedCount = items.Count(i => i.Checked);
				float maxSimilarity = items.Max(i => i.ItemInfo.Similarity);

				if (hasChecked.HasValue && (checkedCount > 0) != hasChecked.Value) {
					continue;
				}

				if (hasMixedCodecs.HasValue && hasMixedFmt != hasMixedCodecs.Value) {
					continue;
				}

				if (hasMixedResolutions.HasValue && hasMixedRes != hasMixedResolutions.Value) {
					continue;
				}

				if (minSimilarity.HasValue && maxSimilarity < minSimilarity.Value) {
					continue;
				}
				if (matchingGroups++ < offset || summaries.Count == limit) continue;

				var (keeper, bestReason) = vm.PickGroupKeeper(items);
				var bestPath = keeper?.ItemInfo.Path ?? items[0].ItemInfo.Path;

				var summary = new DuplicateGroupSummaryDto {
					GroupId = g.Key,
					ItemCount = items.Count,
					VisibleItemCount = visibleItems.Count,
					HiddenItemCount = items.Count - visibleItems.Count,
					CheckedCount = checkedCount,
					VisibleCheckedCount = visibleItems.Count(i => i.Checked),
					HiddenCheckedCount = items.Count(i => !i.IsVisibleInFilter && i.Checked),
					MaxSimilarity = maxSimilarity,
					WastedSpaceBytes = items.Where(i => i.Checked).Sum(i => i.ItemInfo.SizeLong),
					BestItemPath = bestPath,
					BestReason = bestReason,
					Formats = formats,
					Resolutions = resolutions,
					HasMixedFormats = hasMixedFmt,
					HasMixedResolutions = hasMixedRes,
					Items = includeItems ? items.Select(d => MapToItemDetailDto(d, keeper, bestReason)).ToList() : null
				};

				summaries.Add(summary);
			}

			return AutomationResponse.Ok(new GroupsResultDto(matchingGroups, offset, limit, summaries), JsonContext.GroupsResultDto);
		}

		static AutomationResponse HandleGetGroupDetails(AutomationParameters? parameters, MainWindowVM vm) {
			var groupIdStr = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			if (string.IsNullOrWhiteSpace(groupIdStr) || !Guid.TryParse(groupIdStr, out var groupId)) {
				return AutomationResponse.Fail("Invalid or missing 'groupId' parameter.");
			}

			var groupItems = vm.Duplicates
				.Where(d => d.ItemInfo.GroupId == groupId)
				.ToList();

			var (keeper, bestReason) = vm.PickGroupKeeper(groupItems);
			var items = groupItems.Select(d => MapToItemDetailDto(d, keeper, bestReason)).ToList();

			return AutomationResponse.Ok(new GroupDetailsResultDto(groupId, items.Count, keeper?.ItemInfo.Path,
				bestReason, items), JsonContext.GroupDetailsResultDto);
		}

		static async Task<AutomationResponse> HandleGetGroupThumbnailsAsync(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			string? groupIdStr = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			string? path = GetStringParam(parameters, "path");
			bool loadIfMissing = GetBoolParam(parameters, "loadIfMissing", true) &&
			                     GetBoolParam(parameters, "load_if_missing", true);
			int maxFrames = Math.Clamp(
				GetNullableIntParam(parameters, "maxFramesPerItem") ??
				GetNullableIntParam(parameters, "max_frames_per_item") ?? 2,
				1, 4);

			var resolved = await Dispatcher.UIThread.InvokeAsync(() => {
				ct.ThrowIfCancellationRequested();
				Guid? gid = null;
				if (!string.IsNullOrWhiteSpace(groupIdStr) && Guid.TryParse(groupIdStr, out var parsed)) {
					gid = parsed;
				}
				else if (!string.IsNullOrWhiteSpace(path)) {
					var match = vm.Duplicates.FirstOrDefault(d =>
						string.Equals(d.ItemInfo.Path, path, StringComparison.OrdinalIgnoreCase) ||
						Path.GetFileName(d.ItemInfo.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
					gid = match?.ItemInfo.GroupId;
				}

				if (!gid.HasValue) {
					return (GroupId: (Guid?)null, Items: new List<DuplicateItemVM>(), Missing: new List<DuplicateItem>());
				}

				var members = vm.Duplicates.Where(d => d.ItemInfo.GroupId == gid.Value).ToList();
				var missing = new List<DuplicateItem>();
				if (loadIfMissing) {
					foreach (var d in members.Take(MaxThumbnailItems)) {
						ct.ThrowIfCancellationRequested();
						if (d.ItemInfo.ImageList.Count == 0 && string.IsNullOrEmpty(d.ThumbnailKey) && File.Exists(d.ItemInfo.Path))
							missing.Add(d.ItemInfo);
					}
				}
				return (GroupId: gid, Items: members, Missing: missing);
			});

			if (!resolved.GroupId.HasValue || resolved.Items.Count == 0) {
				return AutomationResponse.Fail("Group not found. Provide a valid 'group_id' or 'path'.");
			}

			if (resolved.Missing.Count > 0) {
				await Dispatcher.UIThread.InvokeAsync(() => vm.EnsureThumbnailsLoadedAsync(resolved.Missing, ct));
			}

			return await Dispatcher.UIThread.InvokeAsync(() => {
				ct.ThrowIfCancellationRequested();
				var (keeper, _) = vm.PickGroupKeeper(resolved.Items);
				var result = new GroupThumbnailsResultDto {
					GroupId = resolved.GroupId.Value,
					Truncated = resolved.Items.Count > MaxThumbnailItems
				};
				int remaining = MaxThumbnailBytes;

				foreach (var d in resolved.Items.Take(MaxThumbnailItems)) {
					ct.ThrowIfCancellationRequested();
					var itemDto = new GroupThumbnailItemDto {
						Path = d.ItemInfo.Path,
						Checked = d.Checked,
						IsBest = keeper != null && ReferenceEquals(d, keeper),
						Resolution = d.ItemInfo.FrameSize ?? string.Empty,
						Format = d.ItemInfo.Format ?? string.Empty,
						Similarity = d.ItemInfo.Similarity
					};

					if (d.ItemInfo.ImageList.Count > 0) {
						int count = Math.Min(maxFrames, d.ItemInfo.ImageList.Count);
						for (int i = 0; i < count; i++) {
							byte[] bytes = d.ItemInfo.ImageList[i];
							if (bytes == null || bytes.Length == 0) continue;
							if (bytes.Length > remaining) {
								result.Truncated = true;
								continue;
							}
							remaining -= bytes.Length;
							double? ts = i < d.ItemInfo.ThumbnailTimestamps.Count
								? d.ItemInfo.ThumbnailTimestamps[i].TotalSeconds
								: null;
							itemDto.Frames.Add(new GroupThumbnailFrameDto {
								MimeType = SniffImageMimeType(bytes),
								Base64Data = Convert.ToBase64String(bytes),
								TimestampSeconds = ts
							});
						}
					}
					else if (!string.IsNullOrEmpty(d.ThumbnailKey) && ThumbCacheHelpers.Provider != null) {
						try {
							using var stream = ThumbCacheHelpers.Provider.OpenKey(d.ThumbnailKey);
							if (stream != null) {
								byte[]? bytes = ReadThumbnailWithinBudget(stream, remaining);
								if (bytes == null) {
									result.Truncated = true;
								}
								else if (bytes.Length > 0) {
									remaining -= bytes.Length;
									itemDto.Frames.Add(new GroupThumbnailFrameDto {
										MimeType = SniffImageMimeType(bytes),
										Base64Data = Convert.ToBase64String(bytes)
									});
								}
							}
						}
						catch { /* ignore cache read failure */ }
					}

					result.Items.Add(itemDto);
				}

				ct.ThrowIfCancellationRequested();
				return AutomationResponse.Ok(result, JsonContext.GroupThumbnailsResultDto);
			});
		}

		static byte[]? ReadThumbnailWithinBudget(Stream stream, int budget) {
			if (stream.CanSeek && stream.Length - stream.Position > budget) return null;
			using var result = new MemoryStream();
			Span<byte> buffer = stackalloc byte[8192];
			while (true) {
				int read = stream.Read(buffer[..Math.Min(buffer.Length, budget + 1)]);
				if (read == 0) return result.ToArray();
				if (read > budget) return null;
				result.Write(buffer[..read]);
				budget -= read;
			}
		}

		static string SniffImageMimeType(byte[] bytes) {
			if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
				return "image/png";
			if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
				bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
				return "image/webp";
			return "image/jpeg";
		}

		static AutomationResponse HandleScrollTo(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			string? path = GetStringParam(parameters, "path");
			string? groupIdStr = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");

			Guid? groupId = null;
			if (!string.IsNullOrWhiteSpace(groupIdStr) && Guid.TryParse(groupIdStr, out var parsedGuid)) {
				groupId = parsedGuid;
			}

			DuplicateItemVM? targetItem = null;
			if (!string.IsNullOrWhiteSpace(path)) {
				targetItem = vm.Duplicates.FirstOrDefault(d =>
					string.Equals(d.ItemInfo.Path, path, StringComparison.OrdinalIgnoreCase) ||
					Path.GetFileName(d.ItemInfo.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
			}
			else if (groupId.HasValue) {
				targetItem = vm.Duplicates.FirstOrDefault(d => d.ItemInfo.GroupId == groupId.Value);
			}

			if (targetItem == null) {
				return AutomationResponse.Ok(new ScrollResultDto {
					Found = false,
					Message = "Target item or group not found in results."
				}, JsonContext.ScrollResultDto);
			}

			ct.ThrowIfCancellationRequested();
			vm.EnsureGroupExpanded(targetItem.ItemInfo.GroupId);

			var row = vm.ResultsRows
				.OfType<ResultsItemRow>()
				.FirstOrDefault(r => ReferenceEquals(r.Item, targetItem) ||
					string.Equals(r.Item.ItemInfo.Path, targetItem.ItemInfo.Path, StringComparison.OrdinalIgnoreCase));

			if (row != null) {
				vm.NewResultsSelectAndScrollTo?.Invoke(row);
				vm.ResultsFocusRow?.Invoke(row);
				return AutomationResponse.Ok(new ScrollResultDto {
					Found = true,
					Path = targetItem.ItemInfo.Path,
					GroupId = targetItem.ItemInfo.GroupId,
					Message = $"Scrolled to item '{targetItem.ItemInfo.Path}' in group {targetItem.ItemInfo.GroupId}."
				}, JsonContext.ScrollResultDto);
			}

			return AutomationResponse.Ok(new ScrollResultDto {
				Found = true,
				Path = targetItem.ItemInfo.Path,
				GroupId = targetItem.ItemInfo.GroupId,
				Message = $"Found item '{targetItem.ItemInfo.Path}', navigated to group."
			}, JsonContext.ScrollResultDto);
		}

		static AutomationResponse HandleHighlightGroup(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			string? groupIdStr = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			string? path = GetStringParam(parameters, "path");

			Guid? groupId = null;
			if (!string.IsNullOrWhiteSpace(groupIdStr) && Guid.TryParse(groupIdStr, out var parsedGuid)) {
				groupId = parsedGuid;
			}

			if (!groupId.HasValue && !string.IsNullOrWhiteSpace(path)) {
				var item = vm.Duplicates.FirstOrDefault(d =>
					string.Equals(d.ItemInfo.Path, path, StringComparison.OrdinalIgnoreCase) ||
					Path.GetFileName(d.ItemInfo.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
				if (item != null) {
					groupId = item.ItemInfo.GroupId;
				}
			}

			if (!groupId.HasValue) {
				return AutomationResponse.Fail("Missing or invalid 'groupId' or 'path' parameter.");
			}

			ct.ThrowIfCancellationRequested();
			vm.EnsureGroupExpanded(groupId.Value);

			var headerRow = vm.ResultsRows.OfType<ResultsGroupHeader>().FirstOrDefault(h => h.GroupId == groupId.Value);
			var firstItemRow = vm.ResultsRows.OfType<ResultsItemRow>().FirstOrDefault(r => r.Item.ItemInfo.GroupId == groupId.Value);

			object? targetRow = (object?)firstItemRow ?? headerRow;
			if (targetRow != null) {
				if (firstItemRow != null) {
					vm.NewResultsSelectAndScrollTo?.Invoke(firstItemRow);
				}
				vm.ResultsFocusRow?.Invoke(targetRow);

				vm.SetMcpStatus(FormatLang(
					"Mcp.Status.GroupHighlighted",
					"MCP: Group {0} highlighted.",
					groupId.Value));

				return AutomationResponse.Ok(new HighlightGroupResultDto(true, groupId.Value,
					$"Highlighted and focused group {groupId.Value}."), JsonContext.HighlightGroupResultDto);
			}

			return AutomationResponse.Fail($"Group {groupId.Value} is not visible in current results view.");
		}

		// Resolve against the whole results list, not just visible items. A filter
		// must not silently turn an ambiguous name into a mutation target.
		static DuplicateItemVM ResolveMutationPath(MainWindowVM vm, string path, CancellationToken ct) =>
			RequireUniqueMutationTarget(vm.Duplicates.Where(d => {
				ct.ThrowIfCancellationRequested();
				return string.Equals(d.ItemInfo.Path, path, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(Path.GetFileName(d.ItemInfo.Path), path, StringComparison.OrdinalIgnoreCase);
			}), path);

		static HashSet<DuplicateItemVM> ResolveMutationPaths(MainWindowVM vm, IEnumerable<string> paths, CancellationToken ct) {
			var targets = new HashSet<DuplicateItemVM>();
			foreach (var path in paths) {
				ct.ThrowIfCancellationRequested();
				targets.Add(ResolveMutationPath(vm, path, ct));
			}
			return targets;
		}

		static DuplicateItemVM RequireUniqueMutationTarget(IEnumerable<DuplicateItemVM> candidates, string path) {
			var matches = candidates.Take(2).ToList();
			return matches.Count switch {
				1 => matches[0],
				0 => throw new ArgumentException($"Item not found in duplicates: {path}"),
				_ => throw new ArgumentException($"Ambiguous item path '{path}'. Specify a unique full path.")
			};
		}

		static AutomationResponse HandleSetChecked(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			if (GetExplicitBoolParam(parameters, "checked") is not bool checkState) {
				return AutomationResponse.Fail("Provide an explicit boolean 'checked' parameter.");
			}
			bool allowAllInGroup = GetExplicitBoolParam(parameters, "allow_all_in_group") == true ||
			                       GetExplicitBoolParam(parameters, "allowAllInGroup") == true;
			bool currentFilterOnly = GetBoolParam(parameters, "currentFilterOnly", true) &&
			                         GetBoolParam(parameters, "current_filter_only", true);

			var singlePath = GetStringParam(parameters, "path");
			CheckListSize(parameters, "paths", string.IsNullOrWhiteSpace(singlePath) ? 0 : 1, "paths");
			var paths = GetStringListParam(parameters, "paths");
			if (!string.IsNullOrWhiteSpace(singlePath)) {
				paths.Add(singlePath);
			}

			string? groupIdStr = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			Guid? groupId = null;
			if (!string.IsNullOrWhiteSpace(groupIdStr) && Guid.TryParse(groupIdStr, out var parsedGuid)) {
				groupId = parsedGuid;
			}

			if (paths.Count == 0 && !groupId.HasValue) {
				return AutomationResponse.Fail("Either 'path'/'paths' or 'groupId' must be provided.");
			}

			var targets = ResolveMutationPaths(vm, paths, ct);
			if (groupId.HasValue) {
				targets.UnionWith(vm.Duplicates.Where(item => item.ItemInfo.GroupId == groupId.Value));
			}

			if (checkState && currentFilterOnly) {
				var hidden = targets.FirstOrDefault(item => !item.IsVisibleInFilter);
				if (hidden != null) {
					return AutomationResponse.Fail(
						$"Security check failed: Refused to check '{hidden.ItemInfo.Path}' because it is hidden by the active filter. Set 'current_filter_only=false' to override.");
				}
			}

			var changes = targets.Where(item => item.Checked != checkState &&
				(!currentFilterOnly || item.IsVisibleInFilter)).ToList();
			if (checkState && !allowAllInGroup && changes.Count > 0) {
				var keptGroups = vm.Duplicates.Where(item => !item.Checked && !targets.Contains(item))
					.Select(item => item.ItemInfo.GroupId).ToHashSet();
				var lastCopy = changes.FirstOrDefault(item => !keptGroups.Contains(item.ItemInfo.GroupId));
				if (lastCopy != null) {
					return AutomationResponse.Fail($"Refused to check every copy in group {lastCopy.ItemInfo.GroupId}. Set 'allow_all_in_group=true' to override.");
				}
			}

			ct.ThrowIfCancellationRequested();
			using var undoBatch = vm.BeginSelectionUndoBatch();
			foreach (var item in changes) {
				ct.ThrowIfCancellationRequested();
				item.Checked = checkState;
			}
			int modified = changes.Count;

			vm.SetMcpStatus(FormatLang(
				"Mcp.Status.ItemsUpdated",
				"MCP: {0} item(s) updated (checked={1}).",
				modified,
				checkState));

			return AutomationResponse.Ok(new SelectResultDto {
				ModifiedCount = modified,
				TotalMatches = modified,
				SkippedFullGroupsCount = 0,
				Message = $"Updated {modified} item(s) to checked={checkState}."
			}, JsonContext.SelectResultDto);
		}

		static AutomationResponse HandleSelectByCriteria(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			string? expression = GetStringParam(parameters, "expression");
			CheckTextBytes(expression, "expression");
			string? strategy = GetStringParam(parameters, "strategy");
			string action = (GetStringParam(parameters, "action") ?? "check").ToLowerInvariant();
			bool allowAllInGroup = GetBoolParam(parameters, "allowAllInGroup", false) ||
								   GetBoolParam(parameters, "allow_all_in_group", false);
			bool currentFilterOnly = GetBoolParam(parameters, "currentFilterOnly", true) &&
			                         GetBoolParam(parameters, "current_filter_only", true);

			bool targetCheckedState = action != "uncheck";

			var (partialMatches, fullGroups, skippedFullGroups) = SelectionAuditEngine.EvaluateMatches(
				vm.Duplicates.ToList(),
				expression,
				strategy,
				allowAllInGroup,
				currentFilterOnly,
				ct);

			ct.ThrowIfCancellationRequested();
			using var undoBatch = vm.BeginSelectionUndoBatch();
			int modified = 0;

			foreach (var item in partialMatches) {
				ct.ThrowIfCancellationRequested();
				if (item.Checked != targetCheckedState) {
					item.Checked = targetCheckedState;
					modified++;
				}
			}

			if (allowAllInGroup) {
				foreach (var group in fullGroups) {
					foreach (var item in group) {
						ct.ThrowIfCancellationRequested();
						if (item.Checked != targetCheckedState) {
							item.Checked = targetCheckedState;
							modified++;
						}
					}
				}
			}

			int totalMatches = partialMatches.Count + (allowAllInGroup ? fullGroups.Sum(g => g.Count) : 0);
			string message = $"Applied {action} to {modified} items across {partialMatches.Count} matches.";
			if (skippedFullGroups > 0) {
				message += $" Skipped {skippedFullGroups} group(s) where 100% of items matched (safety guard).";
			}

			vm.SetMcpStatus(FormatLang(
				"Mcp.Status.SelectApplied",
				"MCP: Applied '{0}' to {1} item(s) in active filter.",
				action,
				modified));

			return AutomationResponse.Ok(new SelectResultDto {
				ModifiedCount = modified,
				TotalMatches = totalMatches,
				SkippedFullGroupsCount = skippedFullGroups,
				Message = message
			}, JsonContext.SelectResultDto);
		}

		static AutomationResponse HandleAuditSelection(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			string? criteriaExpression = GetStringParam(parameters, "criteriaExpression") ??
										 GetStringParam(parameters, "criteria_expression");
			CheckTextBytes(criteriaExpression, "criteria_expression");
			bool requireAtLeastOneKept = GetBoolParam(parameters, "requireAtLeastOneKept", true) &&
										 GetBoolParam(parameters, "require_at_least_one_kept", true);
			bool currentFilterOnly = GetBoolParam(parameters, "currentFilterOnly", true) &&
			                         GetBoolParam(parameters, "current_filter_only", true);

			vm.SetMcpStatus(FormatLang(
				"Mcp.Status.AuditRunning",
				"MCP: Auditing selection on active filter..."));

			var auditResult = SelectionAuditEngine.AuditSelection(
				vm.Duplicates.ToList(),
				criteriaExpression,
				requireAtLeastOneKept,
				currentFilterOnly,
				ct);
			// The audit engine reports predicate exceptions as per-item warnings.
			// Promote a timeout to a command error; the audit wasn't completed.
			foreach (var violation in auditResult.Violations) {
				ct.ThrowIfCancellationRequested();
				if (violation.Type != "CriteriaEvaluationError" ||
					!violation.Message.EndsWith(SelectionExpression.RegexTimeoutMessage, StringComparison.Ordinal)) continue;
				vm.SetMcpStatus(SelectionExpression.RegexTimeoutMessage);
				return AutomationResponse.Fail(SelectionExpression.RegexTimeoutMessage);
			}

			ct.ThrowIfCancellationRequested();
			vm.SetMcpStatus(FormatLang(
				"Mcp.Status.AuditCompleted",
				"MCP: Audit completed ({0} checked, {1} violation(s)).",
				auditResult.CheckedCount,
				auditResult.Violations.Count), announce: false);

			var response = AutomationResponse.Ok(auditResult, JsonContext.AuditResultDto);
			ct.ThrowIfCancellationRequested();
			return response;
		}

		static AutomationResponse HandleUndo(MainWindowVM vm) {
			var command = vm.UndoSelectionCommand;
			if (((System.Windows.Input.ICommand)command).CanExecute(null)) {
				command.Execute().Subscribe();
				vm.SetMcpStatus(FormatLang("Mcp.Status.Undo", "MCP: Selection undone."));
				return AutomationResponse.Ok("Undo executed successfully.", JsonContext.String);
			}
			return AutomationResponse.Fail("Nothing to undo.");
		}

		static AutomationResponse HandleClearSelection(MainWindowVM vm, CancellationToken ct) {
			vm.UpdateScopedSelection(invert: false, ct);
			vm.SetMcpStatus(FormatLang("Mcp.Status.Cleared", "MCP: Cleared all selections."));
			return AutomationResponse.Ok("Cleared all selections.", JsonContext.String);
		}

		static AutomationResponse HandleInvertSelection(MainWindowVM vm, CancellationToken ct) {
			vm.UpdateScopedSelection(invert: true, ct);
			vm.SetMcpStatus(FormatLang("Mcp.Status.Inverted", "MCP: Inverted selections."));
			return AutomationResponse.Ok("Inverted all selections.", JsonContext.String);
		}

		static async Task<AutomationResponse> HandleMarkNotAMatchAsync(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			string? singleGroup = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			CheckListSize(parameters, "group_ids", string.IsNullOrWhiteSpace(singleGroup) ? 0 : 1, "groupIds", "group_ids");
			var groupIds = new List<Guid>();
			var seen = new HashSet<Guid>();
			if (!string.IsNullOrWhiteSpace(singleGroup) && Guid.TryParse(singleGroup, out var parsedSingle)) {
				groupIds.Add(parsedSingle);
				seen.Add(parsedSingle);
			}
			foreach (var s in GetStringListParam(parameters, "groupIds").Concat(GetStringListParam(parameters, "group_ids"))) {
				ct.ThrowIfCancellationRequested();
				if (Guid.TryParse(s, out var g) && seen.Add(g))
					groupIds.Add(g);
			}

			if (groupIds.Count == 0) {
				return AutomationResponse.Fail("Provide 'group_id' or 'group_ids' to mark as Not a Match.");
			}

			int markedGroups = 0;
			foreach (var gid in groupIds) {
				ct.ThrowIfCancellationRequested();
				var (marked, error) = await vm.TryMarkGroupAsNotAMatchAsync(gid, ct);
				if (marked) markedGroups++;
				if (error != null) return AutomationResponse.Fail($"Mark as Not a Match failed for group {gid} ({markedGroups} group(s) marked): {error}");
			}

			vm.SetMcpStatus($"MCP: Marked {markedGroups} group(s) as 'Not a Match'.");

			return AutomationResponse.Ok(new MarkNotAMatchResultDto(markedGroups, groupIds,
				$"Marked {markedGroups} group(s) as 'Not a Match' and excluded them from future scans."), JsonContext.MarkNotAMatchResultDto);
		}

		static async Task<AutomationResponse> HandleRemoveFromListAsync(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			bool excludeFromFutureScans = GetBoolParam(parameters, "excludeFromFutureScans", false) ||
			                              GetBoolParam(parameters, "exclude_from_future_scans", false);
			bool currentFilterOnly = GetBoolParam(parameters, "currentFilterOnly", true) &&
			                         GetBoolParam(parameters, "current_filter_only", true);
			bool checkedOnly = GetBoolParam(parameters, "checkedOnly", false) ||
			                   GetBoolParam(parameters, "checked_only", false);

			CheckListSize(parameters, "paths", parameters?.ContainsKey("path") == true ? 1 : 0, "paths");
			var paths = new List<string>();
			bool hasPathList = false;
			if (parameters != null && parameters.TryGetValue("paths", out var pathList)) {
				hasPathList = true;
				if (pathList.ValueKind != JsonValueKind.Array) {
					return AutomationResponse.Fail("'paths' must be an array of non-empty paths.");
				}
				foreach (var entry in pathList.EnumerateArray()) {
					ct.ThrowIfCancellationRequested();
					if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString())) {
						return AutomationResponse.Fail("'paths' must be an array of non-empty paths.");
					}
					paths.Add(entry.GetString()!);
				}
			}
			if (parameters != null && parameters.TryGetValue("path", out var singlePath)) {
				if (singlePath.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(singlePath.GetString())) {
					return AutomationResponse.Fail("'path' must be a non-empty path.");
				}
				paths.Add(singlePath.GetString()!);
			}

			Guid? groupId = null;
			if (parameters != null && (parameters.TryGetValue("groupId", out var group) || parameters.TryGetValue("group_id", out group))) {
				if (group.ValueKind != JsonValueKind.String || !Guid.TryParse(group.GetString(), out var parsedGuid)) {
					return AutomationResponse.Fail("'group_id' must be a valid group ID.");
				}
				groupId = parsedGuid;
			}

			bool hasExplicitScope = paths.Count > 0 || hasPathList || groupId.HasValue;
			if (!hasExplicitScope && !checkedOnly) {
				return AutomationResponse.Fail("Provide 'path', 'paths', 'group_id', or 'checked_only=true'.");
			}

			var pathTargets = ResolveMutationPaths(vm, paths, ct);
			var targets = vm.Duplicates.Where(item =>
				(!hasExplicitScope || pathTargets.Contains(item) || item.ItemInfo.GroupId == groupId) &&
				(!checkedOnly || item.Checked) &&
				(!currentFilterOnly || item.IsVisibleInFilter)).ToList();

			int removed = await vm.RemoveItemsFromListAsync(targets, excludeFromFutureScans, ct);
			vm.SetMcpStatus($"MCP: Removed {removed} item(s) from list (files untouched on disk).");

			return AutomationResponse.Ok(new RemoveFromListResultDto(removed, excludeFromFutureScans,
				$"Removed {removed} item(s) from results list without deleting files from disk."), JsonContext.RemoveFromListResultDto);
		}

		static AutomationResponse HandleRenameItem(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			string? path = GetStringParam(parameters, "path");
			string? newName = GetStringParam(parameters, "newName") ?? GetStringParam(parameters, "new_name");
			if (newName?.Length > 255) return AutomationResponse.Fail("'new_name' cannot exceed 255 characters.");
			if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(newName)) {
				return AutomationResponse.Fail("Missing 'path' or 'new_name'.");
			}

			EnsureRenameIdle(vm);
			var item = ResolveMutationPath(vm, path, ct);
			string oldPath = item.ItemInfo.Path;
			string extension = Path.GetExtension(oldPath);
			ValidateRenameName(newName);
			string finalName = newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? newName : newName + extension;
			var (newPath, moves) = PlanRename(IndexFullPaths(vm), item, finalName);
			ApplyRenames(vm, moves, ct);
			return AutomationResponse.Ok(new RenameItemResultDto(oldPath, newPath,
				moves.Count == 0 ? "File already has the requested name." :
					$"Successfully renamed '{Path.GetFileName(oldPath)}' to '{Path.GetFileName(newPath)}'."), JsonContext.RenameItemResultDto);
		}

		static void ValidateRenameName(string name) {
			if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
				Path.IsPathRooted(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 ||
				name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
				(OperatingSystem.IsWindows() && (name.EndsWith('.') || name.EndsWith(' ') || IsWindowsDeviceName(name)))) {
				throw new ArgumentException("Rename requires a plain filename in the same directory.");
			}
		}

		static bool IsWindowsDeviceName(string name) {
			int dot = name.IndexOf('.');
			string stem = (dot < 0 ? name : name[..dot]).TrimEnd().ToUpperInvariant();
			return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
				(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
				 "0123456789¹²³".Contains(stem[3]));
		}

		sealed class RenameMove(string source, string destination, DuplicateItemVM? model, FileEntry? database) {
			public string Source { get; } = source;
			public string Destination { get; } = destination;
			public DuplicateItem? Model { get; } = model?.ItemInfo;
			public string? OriginalModelPath { get; } = model?.ItemInfo.Path;
			public FileEntry? Database { get; } = database;
			public string? OriginalDatabasePath { get; } = database?.Path;
			public bool Moved { get; set; }
		}

		static bool SamePath(string path, string other) => string.Equals(path, other,
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

		static ILookup<string, DuplicateItemVM> IndexFullPaths(MainWindowVM vm) =>
			vm.Duplicates.ToLookup(d => d.ItemInfo.Path, StringComparer.OrdinalIgnoreCase);

		static (string Destination, List<RenameMove> Moves) PlanRename(ILookup<string, DuplicateItemVM> pathIndex, DuplicateItemVM item, string name) {
			ValidateRenameName(name);
			string source = Path.GetFullPath(item.ItemInfo.Path);
			if (!File.Exists(source)) throw new FileNotFoundException($"File does not exist on disk: {source}");
			string destination = Path.Combine(Path.GetDirectoryName(source)!, name);
			if (string.Equals(source, destination, StringComparison.Ordinal)) return (destination, []);

			// Check entries even when their files are missing. DB updates otherwise silently
			// drop the renamed entry when the destination key is already in the hash set.
			void RequireFreeDestination(string target) {
				if (Path.Exists(target) || pathIndex.Contains(target) ||
					DatabaseUtils.Database.Contains(new FileEntry { Path = target })) {
					throw new IOException($"Rename target already exists: {target}");
				}
			}

			RequireFreeDestination(destination);
			string sourceCover = source + ".jpg";
			string destinationCover = destination + ".jpg";
			RequireFreeDestination(destinationCover);
			ScanEngine.GetFromDatabase(source, out var entry);
			var moves = new List<RenameMove> { new(source, destination, item, entry) };
			if (File.Exists(sourceCover)) {
				var coverItems = pathIndex[sourceCover].Where(d => SamePath(d.ItemInfo.Path, sourceCover)).Take(2).ToList();
				if (coverItems.Count > 1) throw new ArgumentException($"Ambiguous cover image path '{sourceCover}'.");
				ScanEngine.GetFromDatabase(sourceCover, out var coverEntry);
				moves.Add(new RenameMove(sourceCover, destinationCover, coverItems.SingleOrDefault(), coverEntry));
			}
			return (destination, moves);
		}

		static void EnsureRenameIdle(MainWindowVM vm) {
			if (vm.IsScanning || vm.IsBusy) throw new InvalidOperationException("Cannot rename while the GUI is busy.");
		}

		static void SetDatabasePath(FileEntry entry, string path) {
			var database = DatabaseUtils.Database;
			if (database.TryGetValue(new FileEntry { Path = path }, out var target) && !ReferenceEquals(target, entry))
				throw new IOException($"Database already has a different entry at '{path}'.");
			if (database.TryGetValue(entry, out var current) && ReferenceEquals(current, entry)) database.Remove(entry);
			entry.Path = path;
			if (!database.Add(entry)) throw new IOException($"Could not update the database entry for '{path}'.");
		}

		static void ApplyRenames(MainWindowVM vm, List<RenameMove> moves, CancellationToken ct) {
			if (moves.Count == 0) return;
			string? tempDb = null;
			if (moves.Any(move => move.Database != null)) {
				// SaveDatabase stages its write here, and startup prefers that staging file.
				// Never overwrite a preexisting recovery file as part of this transaction.
				tempDb = Path.Combine(DatabaseUtils.GetDatabaseFolderPath(), "ScannedFiles_new.db");
				if (Path.Exists(tempDb)) throw new IOException("Cannot rename while a database recovery file exists.");
			}

			vm.IsBusy = true;
			try {
				// Check before the first move; once started, finish or roll back the whole batch.
				ct.ThrowIfCancellationRequested();
				CommitRenames(vm, moves, tempDb);
			}
			finally {
				vm.IsBusy = false;
			}
		}

		static void CommitRenames(MainWindowVM vm, List<RenameMove> moves, string? tempDb) {
			bool saveAttempted = false;
			try {
				foreach (var move in moves) {
					File.Move(move.Source, move.Destination, overwrite: false);
					move.Moved = true;
					if (move.Database != null) SetDatabasePath(move.Database, move.Destination);
					if (move.Model != null) move.Model.Path = move.Destination;
				}
				vm.RefreshResultsView();
				if (tempDb != null) {
					saveAttempted = true;
					ScanEngine.SaveDatabase();
				}
			}
			catch (Exception ex) {
				var rollbackErrors = new List<string>();
				void TryRollback(Action action) {
					try { action(); }
					catch (Exception rollbackError) { rollbackErrors.Add(rollbackError.Message); }
				}

				for (int i = moves.Count - 1; i >= 0; i--) {
					var move = moves[i];
					if (!move.Moved) continue;
					TryRollback(() => {
						File.Move(move.Destination, move.Source, overwrite: false);
						move.Moved = false;
					});
					// If the disk move back failed, keep metadata pointed at the new location.
					if (move.Database is { } entry) {
						TryRollback(() => SetDatabasePath(entry, move.Moved ? move.Destination : move.OriginalDatabasePath!));
					}
					if (move.Model is { } model) {
						TryRollback(() => model.Path = move.Moved ? move.Destination : move.OriginalModelPath!);
					}
				}
				if (saveAttempted) TryRollback(() => File.Delete(tempDb!));
				if (moves.Any(move => move.Moved && move.Database != null)) TryRollback(ScanEngine.SaveDatabase);
				TryRollback(vm.RefreshResultsView);
				string detail = rollbackErrors.Count == 0 ? "Changes rolled back." :
					$"Rollback incomplete; check files and database: {string.Join("; ", rollbackErrors)}";
				throw new IOException($"Rename failed: {ex.Message} {detail}", ex);
			}
		}

		static AutomationResponse HandleTransferSuffix(AutomationParameters? parameters, MainWindowVM vm, CancellationToken ct) {
			EnsureRenameIdle(vm);
			string? groupIdStr = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			bool currentFilterOnly = GetBoolParam(parameters, "currentFilterOnly", true) &&
									 GetBoolParam(parameters, "current_filter_only", true);
			bool preview = GetExplicitBoolParam(parameters, "preview") ?? true;
			string suffixRegexStr = GetStringParam(parameters, "suffixPattern") ??
									GetStringParam(parameters, "suffix_pattern") ??
									@"(_{2,}.*)$";
			if (suffixRegexStr.Length > 512) return AutomationResponse.Fail("'suffix_pattern' cannot exceed 512 characters.");

			Regex regex;
			try {
				regex = new Regex(suffixRegexStr, RegexOptions.None, SelectionExpression.RegexTimeout);
			}
			catch (ArgumentException ex) {
				return AutomationResponse.Fail($"Invalid regex pattern: {ex.Message}");
			}

			Guid? targetGroupId = null;
			if (groupIdStr != null) {
				if (!Guid.TryParse(groupIdStr, out var parsedGuid)) return AutomationResponse.Fail("Invalid 'group_id'.");
				targetGroupId = parsedGuid;
			}

			var scope = currentFilterOnly
				? vm.Duplicates.Where(d => d.IsVisibleInFilter).ToList()
				: vm.Duplicates.ToList();

			var groups = scope
				.GroupBy(d => d.ItemInfo.GroupId)
				.Where(g => !targetGroupId.HasValue || g.Key == targetGroupId.Value)
				.ToList();

			var renames = new List<TransferSuffixRenameDto>();
			var skippedConflicts = new List<TransferSuffixConflictDto>();
			var pendingMoves = new List<RenameMove>();
			var plannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var pathIndex = IndexFullPaths(vm);

			foreach (var g in groups) {
				ct.ThrowIfCancellationRequested();
				var items = g.ToList();
				var checkedItems = items.Where(i => i.Checked).ToList();
				var keptItems = items.Where(i => !i.Checked).ToList();

				if (checkedItems.Count == 0 || keptItems.Count == 0) continue;

				string? foundSuffix = null;
				foreach (var checkedItem in checkedItems) {
					ct.ThrowIfCancellationRequested();
					string baseName = Path.GetFileNameWithoutExtension(checkedItem.ItemInfo.Path);
					var match = regex.Match(baseName);
					if (match.Success) {
						foundSuffix = match.Groups[1].Value;
						break;
					}
				}

				if (string.IsNullOrEmpty(foundSuffix)) continue;

				foreach (var keptItem in keptItems) {
					ct.ThrowIfCancellationRequested();
					if (keptItem.ItemInfo.IsImage) continue;

					string keptBaseName = Path.GetFileNameWithoutExtension(keptItem.ItemInfo.Path);
					if (keptBaseName.Contains(foundSuffix, StringComparison.OrdinalIgnoreCase)) {
						continue;
					}
					if (regex.IsMatch(keptBaseName)) {
						continue;
					}

					string oldPath = keptItem.ItemInfo.Path;
					string name = keptBaseName + foundSuffix + Path.GetExtension(oldPath);
					string newFullPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(oldPath))!, name);
					List<RenameMove> moves;
					try {
						(newFullPath, moves) = PlanRename(pathIndex, RequireUniqueMutationTarget(pathIndex[oldPath], oldPath), name);
						if (moves.Any(move => plannedPaths.Contains(move.Source) || plannedPaths.Contains(move.Destination))) {
							throw new IOException("Another planned rename uses the same path.");
						}
					}
					catch (IOException) {
						skippedConflicts.Add(new TransferSuffixConflictDto(g.Key, oldPath, newFullPath));
						continue;
					}
					if (moves.Count == 0) continue;
					foreach (var move in moves) {
						plannedPaths.Add(move.Source);
						plannedPaths.Add(move.Destination);
					}
					pendingMoves.AddRange(moves);

					renames.Add(new TransferSuffixRenameDto(g.Key, oldPath, newFullPath, foundSuffix));
				}
			}

			// Finish all regex evaluation before any disk mutation, so a timeout
			// cannot leave a partially renamed batch.
			if (!preview) ApplyRenames(vm, pendingMoves, ct);

			return AutomationResponse.Ok(new TransferSuffixResultDto(preview, renames.Count, skippedConflicts.Count,
				skippedConflicts, renames), JsonContext.TransferSuffixResultDto);
		}

		static string FormatLang(string key, string fallback, params object[] args) {
			string template = App.Lang[key];
			if (string.IsNullOrWhiteSpace(template) || string.Equals(template, key, StringComparison.Ordinal)) {
				template = fallback;
			}
			return args.Length > 0 ? string.Format(CultureInfo.CurrentCulture, template, args) : template;
		}

		static string? GetStringParam(AutomationParameters? parameters, string key) {
			if (!TryGetParam(parameters, key, out var val)) return null;
			return val.ValueKind == JsonValueKind.String ? val.GetString() : val.ToString();
		}

		static void CheckTextBytes(string? value, string name) {
			if (value != null && Encoding.UTF8.GetByteCount(value) > MaxTextBytes)
				throw new ArgumentException($"'{name}' cannot exceed {MaxTextBytes} UTF-8 bytes.");
		}

		static void CheckListSize(AutomationParameters? parameters, string name, int count, params string[] aliases) {
			foreach (var alias in aliases) {
				if (TryGetParam(parameters, alias, out var value) && value.ValueKind == JsonValueKind.Array)
					count += value.GetArrayLength();
			}
			if (count > MaxListEntries)
				throw new ArgumentException($"'{name}' cannot contain more than {MaxListEntries} entries.");
		}

		static int GetIntParam(AutomationParameters? parameters, string key, int defaultValue) =>
			GetNullableIntParam(parameters, key) ?? defaultValue;

		static int? GetNullableIntParam(AutomationParameters? parameters, string key) {
			if (!TryGetParam(parameters, key, out var val)) return null;
			if (val.TryGetInt32(out int i)) return i;
			if (int.TryParse(val.ToString(), out int parsed)) return parsed;
			return null;
		}

		static bool GetBoolParam(AutomationParameters? parameters, string key, bool defaultValue) =>
			GetNullableBoolParam(parameters, key) ?? defaultValue;

		static bool? GetExplicitBoolParam(AutomationParameters? parameters, string key) {
			if (!TryGetParam(parameters, key, out var value)) return null;
			return value.ValueKind switch {
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				_ => null
			};
		}

		static bool? GetNullableBoolParam(AutomationParameters? parameters, string key) {
			if (!TryGetParam(parameters, key, out var val)) return null;
			if (val.ValueKind == JsonValueKind.True) return true;
			if (val.ValueKind == JsonValueKind.False) return false;
			if (bool.TryParse(val.ToString(), out bool b)) return b;
			return null;
		}

		static float? GetNullableFloatParam(AutomationParameters? parameters, string key) {
			if (!TryGetParam(parameters, key, out var val)) return null;
			if (val.TryGetSingle(out float f)) return f;
			if (float.TryParse(val.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) return parsed;
			return null;
		}

		static List<string> GetStringListParam(AutomationParameters? parameters, string key) {
			var list = new List<string>();
			if (TryGetParam(parameters, key, out var val) && val.ValueKind == JsonValueKind.Array) {
				foreach (var el in val.EnumerateArray()) {
					var s = el.GetString();
					if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
				}
			}
			return list;
		}

		static bool TryGetParam(AutomationParameters? parameters, string key, out JsonElement value) {
			value = default;
			return parameters != null && parameters.TryGetValue(key, out value) &&
				value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
		}
	}
}
