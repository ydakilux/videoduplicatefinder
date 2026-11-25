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

namespace VDF.GUI.Automation {
	internal static class GuiAutomationHandler {

		public static async Task<AutomationResponse> HandleAsync(
			AutomationRequest request,
			MainWindowVM vm,
			CancellationToken ct = default) {

			if (string.IsNullOrWhiteSpace(request.Command)) {
				return AutomationResponse.Fail("Empty command.");
			}

			try {
				switch (request.Command.Trim().ToLowerInvariant()) {
					case "getstatus":
					case "get_status":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleGetStatus(vm));

					case "getactivefilter":
					case "get_active_filter":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleGetActiveFilter(vm));

					case "setactivefilter":
					case "set_active_filter":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleSetActiveFilter(request.Parameters, vm));

					case "getgroups":
					case "get_groups":
					case "get_duplicate_groups":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleGetGroups(request.Parameters, vm));

					case "getgroupdetails":
					case "get_group_details":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleGetGroupDetails(request.Parameters, vm));

					case "getgroupthumbnails":
					case "get_group_thumbnails":
						return await HandleGetGroupThumbnailsAsync(request.Parameters, vm);

					case "scrollto":
					case "scroll_to":
					case "scrolltoitem":
					case "scroll_to_item":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleScrollTo(request.Parameters, vm));

					case "highlightgroup":
					case "highlight_group":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleHighlightGroup(request.Parameters, vm));

					case "setchecked":
					case "set_checked":
					case "set_item_checked":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleSetChecked(request.Parameters, vm));

					case "selectbycriteria":
					case "select_by_criteria":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleSelectByCriteria(request.Parameters, vm));

					case "auditselection":
					case "audit_selection":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleAuditSelection(request.Parameters, vm));

					case "undo":
					case "undo_selection":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleUndo(vm));

					case "clearselection":
					case "clear_selection":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleClearSelection(vm));

					case "invertselection":
					case "invert_selection":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleInvertSelection(vm));

					case "marknotamatch":
					case "mark_not_a_match":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleMarkNotAMatchAsync(request.Parameters, vm));

					case "removefromlist":
					case "remove_from_list":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleRemoveFromListAsync(request.Parameters, vm));

					case "renameitem":
					case "rename_item":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleRenameItem(request.Parameters, vm));

					case "transfersuffix":
					case "transfer_suffix":
					case "transfersuffixandrename":
					case "transfer_suffix_and_rename":
						return await Dispatcher.UIThread.InvokeAsync(() => HandleTransferSuffix(request.Parameters, vm));

					default:
						return AutomationResponse.Fail($"Unknown command '{request.Command}'.");
				}
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

			return AutomationResponse.Ok(dto);
		}

		static AutomationResponse HandleGetActiveFilter(MainWindowVM vm) {
			var filterDto = BuildActiveFilterDto(vm);
			int total = vm.Duplicates.Count;
			int visible = vm.Duplicates.Count(d => d.IsVisibleInFilter);
			int totalGroups = vm.Duplicates.GroupBy(d => d.ItemInfo.GroupId).Count();
			int visibleGroups = vm.Duplicates.Where(d => d.IsVisibleInFilter).GroupBy(d => d.ItemInfo.GroupId).Count();
			int checkedCount = vm.Duplicates.Count(d => d.Checked);
			int visibleChecked = vm.Duplicates.Count(d => d.IsVisibleInFilter && d.Checked);

			return AutomationResponse.Ok(new {
				activeFilter = filterDto,
				totalDuplicates = total,
				visibleDuplicates = visible,
				totalGroups,
				visibleGroups,
				checkedCount,
				visibleCheckedCount = visibleChecked
			});
		}

		static AutomationResponse HandleSetActiveFilter(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			string? filterByPath = GetStringParam(parameters, "filterByPath") ??
			                       GetStringParam(parameters, "filter_by_path") ??
			                       GetStringParam(parameters, "path");

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

		static AutomationResponse HandleGetGroups(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			int offset = GetIntParam(parameters, "offset", 0);
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

			var allGroupList = vm.Duplicates
				.GroupBy(d => d.ItemInfo.GroupId)
				.ToList();

			var summaries = new List<DuplicateGroupSummaryDto>();

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

			var paged = summaries.Skip(offset).Take(limit).ToList();

			return AutomationResponse.Ok(new {
				totalCount = summaries.Count,
				offset,
				limit,
				groups = paged
			});
		}

		static AutomationResponse HandleGetGroupDetails(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			var groupIdStr = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			if (string.IsNullOrWhiteSpace(groupIdStr) || !Guid.TryParse(groupIdStr, out var groupId)) {
				return AutomationResponse.Fail("Invalid or missing 'groupId' parameter.");
			}

			var groupItems = vm.Duplicates
				.Where(d => d.ItemInfo.GroupId == groupId)
				.ToList();

			var (keeper, bestReason) = vm.PickGroupKeeper(groupItems);
			var items = groupItems.Select(d => MapToItemDetailDto(d, keeper, bestReason)).ToList();

			return AutomationResponse.Ok(new {
				groupId,
				count = items.Count,
				bestItemPath = keeper?.ItemInfo.Path,
				bestReason,
				items
			});
		}

		static async Task<AutomationResponse> HandleGetGroupThumbnailsAsync(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			string? groupIdStr = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			string? path = GetStringParam(parameters, "path");
			bool loadIfMissing = GetBoolParam(parameters, "loadIfMissing", true) &&
			                     GetBoolParam(parameters, "load_if_missing", true);
			int maxFrames = Math.Clamp(
				GetNullableIntParam(parameters, "maxFramesPerItem") ??
				GetNullableIntParam(parameters, "max_frames_per_item") ?? 2,
				1, 4);

			var resolved = await Dispatcher.UIThread.InvokeAsync(() => {
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
				var missing = members
					.Where(d => d.ItemInfo.ImageList.Count == 0 && string.IsNullOrEmpty(d.ThumbnailKey) && File.Exists(d.ItemInfo.Path))
					.Select(d => d.ItemInfo)
					.ToList();
				return (GroupId: gid, Items: members, Missing: missing);
			});

			if (!resolved.GroupId.HasValue || resolved.Items.Count == 0) {
				return AutomationResponse.Fail("Group not found. Provide a valid 'group_id' or 'path'.");
			}

			if (loadIfMissing && resolved.Missing.Count > 0) {
				await Dispatcher.UIThread.InvokeAsync(() => vm.EnsureThumbnailsLoadedAsync(resolved.Missing));
			}

			return await Dispatcher.UIThread.InvokeAsync(() => {
				var (keeper, _) = vm.PickGroupKeeper(resolved.Items);
				var result = new GroupThumbnailsResultDto {
					GroupId = resolved.GroupId.Value
				};

				foreach (var d in resolved.Items) {
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
								using var ms = new MemoryStream();
								stream.CopyTo(ms);
								byte[] bytes = ms.ToArray();
								if (bytes.Length > 0) {
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

				return AutomationResponse.Ok(result);
			});
		}

		static string SniffImageMimeType(byte[] bytes) {
			if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
				return "image/png";
			if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
				bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
				return "image/webp";
			return "image/jpeg";
		}

		static AutomationResponse HandleScrollTo(Dictionary<string, object?>? parameters, MainWindowVM vm) {
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
				});
			}

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
				});
			}

			return AutomationResponse.Ok(new ScrollResultDto {
				Found = true,
				Path = targetItem.ItemInfo.Path,
				GroupId = targetItem.ItemInfo.GroupId,
				Message = $"Found item '{targetItem.ItemInfo.Path}', navigated to group."
			});
		}

		static AutomationResponse HandleHighlightGroup(Dictionary<string, object?>? parameters, MainWindowVM vm) {
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

				return AutomationResponse.Ok(new {
					highlighted = true,
					groupId = groupId.Value,
					message = $"Highlighted and focused group {groupId.Value}."
				});
			}

			return AutomationResponse.Fail($"Group {groupId.Value} is not visible in current results view.");
		}

		static AutomationResponse HandleSetChecked(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			bool checkState = GetBoolParam(parameters, "checked", true);
			bool currentFilterOnly = GetBoolParam(parameters, "currentFilterOnly", true) &&
			                         GetBoolParam(parameters, "current_filter_only", true);

			var paths = GetStringListParam(parameters, "paths");
			var singlePath = GetStringParam(parameters, "path");
			if (!string.IsNullOrWhiteSpace(singlePath) && paths.Count == 0) {
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

			var pathsSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

			if (checkState && currentFilterOnly) {
				foreach (var item in vm.Duplicates) {
					bool target = (groupId.HasValue && item.ItemInfo.GroupId == groupId.Value) ||
					              pathsSet.Contains(item.ItemInfo.Path) ||
					              pathsSet.Contains(Path.GetFileName(item.ItemInfo.Path));
					if (target && !item.IsVisibleInFilter) {
						return AutomationResponse.Fail(
							$"Security check failed: Refused to check '{item.ItemInfo.Path}' because it is hidden by the active filter. Set 'current_filter_only=false' to override.");
					}
				}
			}

			using var undoBatch = vm.BeginSelectionUndoBatch();
			int modified = 0;

			foreach (var item in vm.Duplicates) {
				bool shouldToggle = false;
				if (groupId.HasValue && item.ItemInfo.GroupId == groupId.Value) {
					shouldToggle = true;
				}
				else if (pathsSet.Contains(item.ItemInfo.Path) || pathsSet.Contains(Path.GetFileName(item.ItemInfo.Path))) {
					shouldToggle = true;
				}

				if (currentFilterOnly && !item.IsVisibleInFilter) {
					continue;
				}

				if (shouldToggle && item.Checked != checkState) {
					item.Checked = checkState;
					modified++;
				}
			}

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
			});
		}

		static AutomationResponse HandleSelectByCriteria(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			string? expression = GetStringParam(parameters, "expression");
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
				currentFilterOnly);

			using var undoBatch = vm.BeginSelectionUndoBatch();
			int modified = 0;

			foreach (var item in partialMatches) {
				if (item.Checked != targetCheckedState) {
					item.Checked = targetCheckedState;
					modified++;
				}
			}

			if (allowAllInGroup) {
				foreach (var group in fullGroups) {
					foreach (var item in group) {
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
			});
		}

		static AutomationResponse HandleAuditSelection(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			string? criteriaExpression = GetStringParam(parameters, "criteriaExpression") ??
										 GetStringParam(parameters, "criteria_expression");
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
				currentFilterOnly);

			vm.SetMcpStatus(FormatLang(
				"Mcp.Status.AuditCompleted",
				"MCP: Audit completed ({0} checked, {1} violation(s)).",
				auditResult.CheckedCount,
				auditResult.Violations.Count), announce: false);

			return AutomationResponse.Ok(auditResult);
		}

		static AutomationResponse HandleUndo(MainWindowVM vm) {
			if (((System.Windows.Input.ICommand)vm.UndoSelectionCommand).CanExecute(null)) {
				vm.UndoSelectionCommand.Execute().Subscribe();
				vm.SetMcpStatus(FormatLang("Mcp.Status.Undo", "MCP: Selection undone."));
				return AutomationResponse.Ok("Undo executed successfully.");
			}
			return AutomationResponse.Fail("Nothing to undo.");
		}

		static AutomationResponse HandleClearSelection(MainWindowVM vm) {
			vm.ClearCheckedItemsCommand.Execute().Subscribe();
			vm.SetMcpStatus(FormatLang("Mcp.Status.Cleared", "MCP: Cleared all selections."));
			return AutomationResponse.Ok("Cleared all selections.");
		}

		static AutomationResponse HandleInvertSelection(MainWindowVM vm) {
			vm.InvertCheckedItemsCommand.Execute().Subscribe();
			vm.SetMcpStatus(FormatLang("Mcp.Status.Inverted", "MCP: Inverted selections."));
			return AutomationResponse.Ok("Inverted all selections.");
		}

		static async Task<AutomationResponse> HandleMarkNotAMatchAsync(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			var groupIds = new List<Guid>();
			string? singleGroup = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			if (!string.IsNullOrWhiteSpace(singleGroup) && Guid.TryParse(singleGroup, out var parsedSingle)) {
				groupIds.Add(parsedSingle);
			}
			foreach (var s in GetStringListParam(parameters, "groupIds").Concat(GetStringListParam(parameters, "group_ids"))) {
				if (Guid.TryParse(s, out var g) && !groupIds.Contains(g))
					groupIds.Add(g);
			}

			if (groupIds.Count == 0) {
				return AutomationResponse.Fail("Provide 'group_id' or 'group_ids' to mark as Not a Match.");
			}

			int markedGroups = 0;
			foreach (var gid in groupIds) {
				if (vm.Duplicates.Any(d => d.ItemInfo.GroupId == gid)) {
					await vm.MarkGroupAsNotAMatch(gid);
					markedGroups++;
				}
			}

			vm.SetMcpStatus($"MCP: Marked {markedGroups} group(s) as 'Not a Match'.");

			return AutomationResponse.Ok(new {
				markedGroupsCount = markedGroups,
				groupIds = groupIds,
				message = $"Marked {markedGroups} group(s) as 'Not a Match' and excluded them from future scans."
			});
		}

		static async Task<AutomationResponse> HandleRemoveFromListAsync(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			bool excludeFromFutureScans = GetBoolParam(parameters, "excludeFromFutureScans", false) ||
			                              GetBoolParam(parameters, "exclude_from_future_scans", false);
			bool currentFilterOnly = GetBoolParam(parameters, "currentFilterOnly", true) &&
			                         GetBoolParam(parameters, "current_filter_only", true);
			bool checkedOnly = GetBoolParam(parameters, "checkedOnly", false) ||
			                   GetBoolParam(parameters, "checked_only", false);

			var paths = GetStringListParam(parameters, "paths");
			var singlePath = GetStringParam(parameters, "path");
			if (!string.IsNullOrWhiteSpace(singlePath) && paths.Count == 0) {
				paths.Add(singlePath);
			}

			string? groupIdStr = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			Guid? groupId = null;
			if (!string.IsNullOrWhiteSpace(groupIdStr) && Guid.TryParse(groupIdStr, out var parsedGuid)) {
				groupId = parsedGuid;
			}

			if (paths.Count == 0 && !groupId.HasValue && !checkedOnly) {
				return AutomationResponse.Fail("Provide 'path', 'paths', 'group_id', or 'checked_only=true'.");
			}

			var pathsSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
			var targets = vm.Duplicates.Where(item => {
				if (currentFilterOnly && !item.IsVisibleInFilter) return false;
				if (checkedOnly) return item.Checked;
				if (groupId.HasValue && item.ItemInfo.GroupId == groupId.Value) return true;
				return pathsSet.Contains(item.ItemInfo.Path) || pathsSet.Contains(Path.GetFileName(item.ItemInfo.Path));
			}).ToList();

			int removed = await vm.RemoveItemsFromListAsync(targets, excludeFromFutureScans);
			vm.SetMcpStatus($"MCP: Removed {removed} item(s) from list (files untouched on disk).");

			return AutomationResponse.Ok(new {
				removedCount = removed,
				excludedFromFutureScans = excludeFromFutureScans,
				message = $"Removed {removed} item(s) from results list without deleting files from disk."
			});
		}

		static AutomationResponse HandleRenameItem(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			string? path = GetStringParam(parameters, "path");
			string? newName = GetStringParam(parameters, "newName") ?? GetStringParam(parameters, "new_name");
			if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(newName)) {
				return AutomationResponse.Fail("Missing 'path' or 'new_name'.");
			}

			var item = vm.Duplicates.FirstOrDefault(d =>
				string.Equals(d.ItemInfo.Path, path, StringComparison.OrdinalIgnoreCase) ||
				Path.GetFileName(d.ItemInfo.Path).Equals(path, StringComparison.OrdinalIgnoreCase));

			if (item == null) {
				return AutomationResponse.Fail($"Item not found in duplicates: {path}");
			}

			if (!File.Exists(item.ItemInfo.Path)) {
				return AutomationResponse.Fail($"File does not exist on disk: {item.ItemInfo.Path}");
			}

			var fi = new FileInfo(item.ItemInfo.Path);
			string targetFullPath;
			if (Path.IsPathRooted(newName)) {
				targetFullPath = newName;
			}
			else {
				string ext = fi.Extension;
				string finalName = newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? newName : newName + ext;
				targetFullPath = Path.Combine(fi.DirectoryName!, finalName);
			}

			if (File.Exists(targetFullPath) && !string.Equals(fi.FullName, targetFullPath, StringComparison.OrdinalIgnoreCase)) {
				return AutomationResponse.Fail($"Target file already exists: {targetFullPath}");
			}

			try {
				ScanEngine.GetFromDatabase(item.ItemInfo.Path, out var dbEntry);
				fi.MoveTo(targetFullPath, overwrite: false);
				if (dbEntry != null) {
					ScanEngine.UpdateFilePathInDatabase(targetFullPath, dbEntry);
					ScanEngine.SaveDatabase();
				}
				string oldPath = item.ItemInfo.Path;
				item.ItemInfo.Path = targetFullPath;
				item.ItemInfo.Folder = Path.GetDirectoryName(targetFullPath) ?? item.ItemInfo.Folder;

				string oldCover = oldPath + ".jpg";
				string newCover = targetFullPath + ".jpg";
				if (File.Exists(oldCover) && !File.Exists(newCover)) {
					File.Move(oldCover, newCover, overwrite: false);
					var coverItem = vm.Duplicates.FirstOrDefault(d => string.Equals(d.ItemInfo.Path, oldCover, StringComparison.OrdinalIgnoreCase));
					if (coverItem != null) {
						coverItem.ItemInfo.Path = newCover;
						coverItem.ItemInfo.Folder = Path.GetDirectoryName(newCover) ?? coverItem.ItemInfo.Folder;
					}
				}

				vm.RefreshResultsView();
				return AutomationResponse.Ok(new {
					oldPath,
					newPath = targetFullPath,
					message = $"Successfully renamed '{Path.GetFileName(oldPath)}' to '{Path.GetFileName(targetFullPath)}'."
				});
			}
			catch (Exception ex) {
				return AutomationResponse.Fail($"Error renaming item: {ex.Message}");
			}
		}

		static AutomationResponse HandleTransferSuffix(Dictionary<string, object?>? parameters, MainWindowVM vm) {
			string? groupIdStr = GetStringParam(parameters, "groupId") ?? GetStringParam(parameters, "group_id");
			bool currentFilterOnly = GetBoolParam(parameters, "currentFilterOnly", true) &&
									 GetBoolParam(parameters, "current_filter_only", true);
			bool preview = GetBoolParam(parameters, "preview", false);
			string suffixRegexStr = GetStringParam(parameters, "suffixPattern") ??
									GetStringParam(parameters, "suffix_pattern") ??
									@"(_{2,}.*)$";

			Regex regex;
			try {
				regex = new Regex(suffixRegexStr, RegexOptions.Compiled);
			}
			catch (Exception ex) {
				return AutomationResponse.Fail($"Invalid regex pattern: {ex.Message}");
			}

			Guid? targetGroupId = null;
			if (!string.IsNullOrWhiteSpace(groupIdStr) && Guid.TryParse(groupIdStr, out var parsedGuid)) {
				targetGroupId = parsedGuid;
			}

			var scope = currentFilterOnly
				? vm.Duplicates.Where(d => d.IsVisibleInFilter).ToList()
				: vm.Duplicates.ToList();

			var groups = scope
				.GroupBy(d => d.ItemInfo.GroupId)
				.Where(g => !targetGroupId.HasValue || g.Key == targetGroupId.Value)
				.ToList();

			var renames = new List<object>();
			var skippedConflicts = new List<object>();
			int renamedCount = 0;
			bool dbModified = false;

			foreach (var g in groups) {
				var items = g.ToList();
				var checkedItems = items.Where(i => i.Checked).ToList();
				var keptItems = items.Where(i => !i.Checked).ToList();

				if (checkedItems.Count == 0 || keptItems.Count == 0) continue;

				string? foundSuffix = null;
				foreach (var checkedItem in checkedItems) {
					string baseName = Path.GetFileNameWithoutExtension(checkedItem.ItemInfo.Path);
					var match = regex.Match(baseName);
					if (match.Success) {
						foundSuffix = match.Groups[1].Value;
						break;
					}
				}

				if (string.IsNullOrEmpty(foundSuffix)) continue;

				foreach (var keptItem in keptItems) {
					if (keptItem.ItemInfo.IsImage) continue;

					string keptBaseName = Path.GetFileNameWithoutExtension(keptItem.ItemInfo.Path);
					if (keptBaseName.Contains(foundSuffix, StringComparison.OrdinalIgnoreCase)) {
						continue;
					}
					if (regex.IsMatch(keptBaseName)) {
						continue;
					}

					var fi = new FileInfo(keptItem.ItemInfo.Path);
					string ext = fi.Extension;
					string newBaseName = keptBaseName + foundSuffix;
					string newFullPath = Path.Combine(fi.DirectoryName!, newBaseName + ext);

					// Never overwrite an existing target file on disk
					if (File.Exists(newFullPath) && !string.Equals(fi.FullName, newFullPath, StringComparison.OrdinalIgnoreCase)) {
						skippedConflicts.Add(new {
							groupId = g.Key,
							oldPath = keptItem.ItemInfo.Path,
							conflictingPath = newFullPath
						});
						continue;
					}

					renames.Add(new {
						groupId = g.Key,
						oldPath = keptItem.ItemInfo.Path,
						newPath = newFullPath,
						suffix = foundSuffix
					});

					if (!preview) {
						try {
							if (File.Exists(keptItem.ItemInfo.Path)) {
								ScanEngine.GetFromDatabase(keptItem.ItemInfo.Path, out var dbEntry);
								fi.MoveTo(newFullPath, overwrite: false);
								if (dbEntry != null) {
									ScanEngine.UpdateFilePathInDatabase(newFullPath, dbEntry);
									dbModified = true;
								}
								string oldPath = keptItem.ItemInfo.Path;
								keptItem.ItemInfo.Path = newFullPath;
								keptItem.ItemInfo.Folder = Path.GetDirectoryName(newFullPath) ?? keptItem.ItemInfo.Folder;

								string oldCover = oldPath + ".jpg";
								string newCover = newFullPath + ".jpg";
								if (File.Exists(oldCover) && !File.Exists(newCover)) {
									File.Move(oldCover, newCover, overwrite: false);
									var coverItem = vm.Duplicates.FirstOrDefault(d => string.Equals(d.ItemInfo.Path, oldCover, StringComparison.OrdinalIgnoreCase));
									if (coverItem != null) {
										coverItem.ItemInfo.Path = newCover;
										coverItem.ItemInfo.Folder = Path.GetDirectoryName(newCover) ?? coverItem.ItemInfo.Folder;
									}
								}
								renamedCount++;
							}
						}
						catch (Exception ex) {
							Logger.Instance.Warn($"Failed to rename '{keptItem.ItemInfo.Path}' to '{newFullPath}': {ex.Message}");
						}
					}
				}
			}

			if (dbModified) {
				ScanEngine.SaveDatabase();
			}

			if (!preview && renamedCount > 0) {
				vm.RefreshResultsView();
			}

			return AutomationResponse.Ok(new {
				preview,
				count = preview ? renames.Count : renamedCount,
				skippedConflictsCount = skippedConflicts.Count,
				skippedConflicts,
				renames
			});
		}

		static string FormatLang(string key, string fallback, params object[] args) {
			string template = App.Lang[key];
			if (string.IsNullOrWhiteSpace(template) || string.Equals(template, key, StringComparison.Ordinal)) {
				template = fallback;
			}
			return args.Length > 0 ? string.Format(CultureInfo.CurrentCulture, template, args) : template;
		}

		static string? GetStringParam(Dictionary<string, object?>? parameters, string key) {
			if (parameters == null || !parameters.TryGetValue(key, out var val) || val == null) return null;
			if (val is JsonElement je) {
				return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
			}
			return val.ToString();
		}

		static int GetIntParam(Dictionary<string, object?>? parameters, string key, int defaultValue) {
			if (parameters == null || !parameters.TryGetValue(key, out var val) || val == null) return defaultValue;
			if (val is JsonElement je && je.TryGetInt32(out int i)) return i;
			if (int.TryParse(val.ToString(), out int parsed)) return parsed;
			return defaultValue;
		}

		static int? GetNullableIntParam(Dictionary<string, object?>? parameters, string key) {
			if (parameters == null || !parameters.TryGetValue(key, out var val) || val == null) return null;
			if (val is JsonElement je && je.TryGetInt32(out int i)) return i;
			if (int.TryParse(val.ToString(), out int parsed)) return parsed;
			return null;
		}

		static bool GetBoolParam(Dictionary<string, object?>? parameters, string key, bool defaultValue) {
			if (parameters == null || !parameters.TryGetValue(key, out var val) || val == null) return defaultValue;
			if (val is JsonElement je) {
				if (je.ValueKind == JsonValueKind.True) return true;
				if (je.ValueKind == JsonValueKind.False) return false;
			}
			if (bool.TryParse(val.ToString(), out bool b)) return b;
			return defaultValue;
		}

		static bool? GetNullableBoolParam(Dictionary<string, object?>? parameters, string key) {
			if (parameters == null || !parameters.TryGetValue(key, out var val) || val == null) return null;
			if (val is JsonElement je) {
				if (je.ValueKind == JsonValueKind.True) return true;
				if (je.ValueKind == JsonValueKind.False) return false;
			}
			if (bool.TryParse(val.ToString(), out bool b)) return b;
			return null;
		}

		static float? GetNullableFloatParam(Dictionary<string, object?>? parameters, string key) {
			if (parameters == null || !parameters.TryGetValue(key, out var val) || val == null) return null;
			if (val is JsonElement je && je.TryGetSingle(out float f)) return f;
			if (float.TryParse(val.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) return parsed;
			return null;
		}

		static List<string> GetStringListParam(Dictionary<string, object?>? parameters, string key) {
			var list = new List<string>();
			if (parameters == null || !parameters.TryGetValue(key, out var val) || val == null) return list;

			if (val is JsonElement je && je.ValueKind == JsonValueKind.Array) {
				foreach (var el in je.EnumerateArray()) {
					var s = el.GetString();
					if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
				}
			}
			else if (val is IEnumerable<string> strEnum) {
				list.AddRange(strEnum);
			}
			return list;
		}
	}
}
