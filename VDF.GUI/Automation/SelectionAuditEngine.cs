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
using System.Linq;
using VDF.Core.Automation;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.GUI.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Automation {
	internal static class SelectionAuditEngine {

		/// <summary>
		/// Evaluates which items match an expression or preset strategy across duplicate groups,
		/// respecting the active filter and full-group deletion safety guards.
		/// </summary>
		public static (List<DuplicateItemVM> PartialMatches, List<List<DuplicateItemVM>> FullGroups, int SkippedFullGroupsCount)
			EvaluateMatches(
				IReadOnlyList<DuplicateItemVM> allItems,
				string? expression,
				string? strategy,
				bool allowAllInGroup,
				bool currentFilterOnly = true) {

			Func<DuplicateItem, bool>? interpreter = null;
			if (!string.IsNullOrWhiteSpace(expression)) {
				interpreter = SelectionExpression.Compile(expression);
			}

			// Group by GroupId across all items to know the complete group context
			var groups = allItems
				.GroupBy(d => d.ItemInfo.GroupId)
				.ToList();

			var partialMatches = new List<DuplicateItemVM>();
			var fullGroups = new List<List<DuplicateItemVM>>();

			foreach (var group in groups) {
				var groupAllItems = group.ToList();
				var scopeItems = currentFilterOnly
					? groupAllItems.Where(d => d.IsVisibleInFilter).ToList()
					: groupAllItems;

				if (scopeItems.Count == 0) continue;

				List<DuplicateItemVM> matchedInGroup;
				if (interpreter != null) {
					matchedInGroup = scopeItems.Where(item => interpreter(item.ItemInfo)).ToList();
				}
				else if (!string.IsNullOrWhiteSpace(strategy)) {
					matchedInGroup = EvaluateStrategyOnGroup(groupAllItems, scopeItems, strategy);
				}
				else {
					throw new ArgumentException("Either 'expression' or 'strategy' must be provided.");
				}

				if (matchedInGroup.Count == 0) continue;

				// Safety calculation: would checking these items leave ZERO kept files across the ENTIRE group?
				int hiddenKept = groupAllItems.Count(d => !scopeItems.Contains(d) && !d.Checked);
				int scopeKept = scopeItems.Count - matchedInGroup.Count;
				bool wouldWipeEntireGroup = (scopeKept + hiddenKept) == 0;

				if (wouldWipeEntireGroup) {
					fullGroups.Add(matchedInGroup);
				}
				else {
					partialMatches.AddRange(matchedInGroup);
				}
			}

			int skipped = 0;
			if (!allowAllInGroup && fullGroups.Count > 0) {
				skipped = fullGroups.Count;
				fullGroups = new List<List<DuplicateItemVM>>();
			}

			return (partialMatches, fullGroups, skipped);
		}

		static List<DuplicateItemVM> EvaluateStrategyOnGroup(
			List<DuplicateItemVM> fullGroup,
			List<DuplicateItemVM> scopeItems,
			string strategy) {

			var normalized = strategy.Trim().ToLowerInvariant().Replace("_", "-");
			switch (normalized) {
				case "lowest-quality":
				case "quality": {
					var activeCriteria = MainWindowVM.ResolveCriteria(
						Data.SettingsFile.Instance.QualityCriteriaOrder,
						Data.SettingsFile.Instance.QualityCriteriaDisabled);
					var keeper = QualityRanker.PickKeeper(fullGroup, activeCriteria, d => d.ItemInfo.IsImage);
					return scopeItems.Where(d => !ReferenceEquals(d, keeper)).ToList();
				}
				case "best-codec":
				case "codec": {
					// Mark files for deletion having an inferior codec when a modern encoding exists at equivalent or better resolution
					var matches = new List<DuplicateItemVM>();
					foreach (var item in scopeItems) {
						if (item.ItemInfo.IsImage) continue;
						int currentRank = MainWindowVM.CodecRank(item.ItemInfo.Format);
						bool hasBetter = fullGroup.Any(other =>
							!ReferenceEquals(other, item) &&
							!other.ItemInfo.IsImage &&
							other.ItemInfo.FrameSizeInt >= item.ItemInfo.FrameSizeInt &&
							MainWindowVM.CodecRank(other.ItemInfo.Format) > currentRank);
						if (hasBetter) {
							matches.Add(item);
						}
					}
					return matches;
				}
				case "smallest-file": {
					long maxSize = fullGroup.Max(d => d.ItemInfo.SizeLong);
					return scopeItems.Where(d => d.ItemInfo.SizeLong < maxSize).ToList();
				}
				case "largest-file": {
					long minSize = fullGroup.Min(d => d.ItemInfo.SizeLong);
					return scopeItems.Where(d => d.ItemInfo.SizeLong > minSize).ToList();
				}
				case "worst-resolution": {
					int maxRes = fullGroup.Max(d => d.ItemInfo.FrameSizeInt);
					return scopeItems.Where(d => d.ItemInfo.FrameSizeInt < maxRes).ToList();
				}
				case "shortest-duration": {
					var maxDur = fullGroup.Max(d => d.ItemInfo.Duration);
					return scopeItems.Where(d => d.ItemInfo.Duration < maxDur).ToList();
				}
				case "identical": {
					var first = fullGroup[0];
					return scopeItems.Where(d => !ReferenceEquals(d, first) && d.EqualsFull(first)).ToList();
				}
				case "identical-except-size": {
					var first = fullGroup[0];
					return scopeItems.Where(d => !ReferenceEquals(d, first) && d.EqualsButQuality(first)).ToList();
				}
				case "oldest": {
					var newest = fullGroup.Max(d => d.ItemInfo.DateCreated);
					return scopeItems.Where(d => d.ItemInfo.DateCreated < newest).ToList();
				}
				case "newest": {
					var oldest = fullGroup.Min(d => d.ItemInfo.DateCreated);
					return scopeItems.Where(d => d.ItemInfo.DateCreated > oldest).ToList();
				}
				default:
					throw new ArgumentException($"Unknown strategy '{strategy}'. Supported strategies: lowest-quality, best-codec, smallest-file, largest-file, worst-resolution, shortest-duration, identical, identical-except-size, oldest, newest.");
			}
		}

		/// <summary>
		/// Audits the current check state across duplicate items, checking for safety,
		/// quality inversions, codec inversions, and criteria compliance.
		/// </summary>
		public static AuditResultDto AuditSelection(
			IReadOnlyList<DuplicateItemVM> allItems,
			string? criteriaExpression = null,
			bool requireAtLeastOneKept = true,
			bool currentFilterOnly = true) {

			Func<DuplicateItem, bool>? criteriaPredicate = null;
			if (!string.IsNullOrWhiteSpace(criteriaExpression)) {
				criteriaPredicate = SelectionExpression.Compile(criteriaExpression);
			}

			var groups = allItems
				.GroupBy(d => d.ItemInfo.GroupId)
				.ToList();

			var violations = new List<AuditViolationDto>();
			int totalChecked = 0;
			int totalKept = 0;
			long totalWastedSpace = 0;
			bool hasPartiallyFiltered = false;

			var activeCriteria = MainWindowVM.ResolveCriteria(
				Data.SettingsFile.Instance.QualityCriteriaOrder,
				Data.SettingsFile.Instance.QualityCriteriaDisabled);

			int auditedGroupsCount = 0;
			int auditedItemsCount = 0;

			foreach (var group in groups) {
				var fullGroupItems = group.ToList();
				var visibleItems = fullGroupItems.Where(d => d.IsVisibleInFilter).ToList();
				var hiddenItems = fullGroupItems.Where(d => !d.IsVisibleInFilter).ToList();

				// If auditing filter only, skip groups that have no visible items
				if (currentFilterOnly && visibleItems.Count == 0) {
					continue;
				}

				auditedGroupsCount++;
				var scopeItems = currentFilterOnly ? visibleItems : fullGroupItems;
				auditedItemsCount += scopeItems.Count;

				var checkedInScope = scopeItems.Where(d => d.Checked).ToList();
				var keptInScope = scopeItems.Where(d => !d.Checked).ToList();

				int hiddenKeptCount = hiddenItems.Count(d => !d.Checked);
				int hiddenCheckedCount = hiddenItems.Count(d => d.Checked);

				totalChecked += checkedInScope.Count;
				totalKept += keptInScope.Count;
				totalWastedSpace += checkedInScope.Sum(d => d.ItemInfo.SizeLong);

				if (hiddenItems.Count > 0) {
					hasPartiallyFiltered = true;
				}

				// 1. Safety Check: All items checked across the entire group
				bool allInScopeChecked = checkedInScope.Count == scopeItems.Count && scopeItems.Count > 0;
				bool entireGroupWiped = allInScopeChecked && hiddenKeptCount == 0;

				if (requireAtLeastOneKept && entireGroupWiped) {
					violations.Add(new AuditViolationDto {
						GroupId = group.Key,
						Path = scopeItems[0].ItemInfo.Path,
						Type = "AllCheckedInGroup",
						Severity = "Error",
						Message = $"Group {group.Key} has ALL {fullGroupItems.Count} copies marked for deletion! Deleting will permanently eliminate this video."
					});
				}
				else if (allInScopeChecked && hiddenKeptCount > 0) {
					violations.Add(new AuditViolationDto {
						GroupId = group.Key,
						Path = scopeItems[0].ItemInfo.Path,
						Type = "PartiallyFilteredGroupKeep",
						Severity = "Info",
						Message = $"Group {group.Key}: all {visibleItems.Count} visible items are checked, but {hiddenKeptCount} copy/copies remain kept outside the active filter."
					});
				}

				// 2. Custom Criteria Expression verification on checked items
				if (criteriaPredicate != null) {
					foreach (var checkedItem in checkedInScope) {
						bool satisfiesCriteria;
						try {
							satisfiesCriteria = criteriaPredicate(checkedItem.ItemInfo);
						}
						catch (Exception ex) {
							violations.Add(new AuditViolationDto {
								GroupId = group.Key,
								Path = checkedItem.ItemInfo.Path,
								Type = "CriteriaEvaluationError",
								Severity = "Warning",
								Message = $"Failed evaluating criteria on '{checkedItem.ItemInfo.Path}': {ex.Message}"
							});
							continue;
						}

						if (!satisfiesCriteria) {
							violations.Add(new AuditViolationDto {
								GroupId = group.Key,
								Path = checkedItem.ItemInfo.Path,
								Type = "CriteriaMismatch",
								Severity = "Warning",
								Message = $"Item '{checkedItem.ItemInfo.Path}' is checked, but does NOT satisfy criteria '{criteriaExpression}'."
							});
						}
					}
				}

				// 3. Quality Inversion check: Did the user check the best item while keeping an inferior one?
				if (checkedInScope.Count > 0 && keptInScope.Count > 0 && fullGroupItems.Count > 1) {
					var (keeper, decidedBy) = QualityRanker.PickKeeperWithReason(fullGroupItems, activeCriteria, d => d.ItemInfo.IsImage);
					if (keeper != null && keeper.Checked && scopeItems.Contains(keeper)) {
						var keptExample = keptInScope.FirstOrDefault(k => !ReferenceEquals(k, keeper));
						if (keptExample != null) {
							string reason = decidedBy != null ? $"by {decidedBy.Name}" : "by quality ranking";
							violations.Add(new AuditViolationDto {
								GroupId = group.Key,
								Path = keeper.ItemInfo.Path,
								Type = "QualityInversion",
								Severity = "Warning",
								Message = $"Quality Inversion: Best item '{keeper.ItemInfo.Path}' ({reason}) is checked for deletion, while lower quality item '{keptExample.ItemInfo.Path}' is kept!"
							});
						}
					}
				}

				// 4. Codec Inversion check: Is a modern/superior codec (AV1, HEVC) checked while an inferior codec (H.264, MPEG-4) of similar resolution is kept?
				foreach (var checkedItem in checkedInScope) {
					if (checkedItem.ItemInfo.IsImage) continue;
					int checkedRank = MainWindowVM.CodecRank(checkedItem.ItemInfo.Format);

					foreach (var keptItem in keptInScope) {
						if (keptItem.ItemInfo.IsImage) continue;
						int keptRank = MainWindowVM.CodecRank(keptItem.ItemInfo.Format);

						if (checkedRank > keptRank && checkedItem.ItemInfo.FrameSizeInt >= keptItem.ItemInfo.FrameSizeInt) {
							violations.Add(new AuditViolationDto {
								GroupId = group.Key,
								Path = checkedItem.ItemInfo.Path,
								Type = "CodecInversion",
								Severity = "Warning",
								Message = $"Codec Inversion in group {group.Key}: Superior codec '{checkedItem.ItemInfo.Path}' ({checkedItem.ItemInfo.Format}, rank {checkedRank}) is checked for deletion while inferior codec '{keptItem.ItemInfo.Path}' ({keptItem.ItemInfo.Format}, rank {keptRank}) at {keptItem.ItemInfo.FrameSize} is kept!"
							});
							break;
						}
					}
				}
			}

			bool isSafe = !violations.Any(v => string.Equals(v.Severity, "Error", StringComparison.OrdinalIgnoreCase));

			string summary;
			if (violations.Count == 0) {
				summary = $"Selection is healthy. {totalChecked} items checked across {auditedGroupsCount} groups ({totalWastedSpace / 1024 / 1024:N0} MB). No rule violations detected.";
			}
			else {
				int errors = violations.Count(v => string.Equals(v.Severity, "Error", StringComparison.OrdinalIgnoreCase));
				int warnings = violations.Count(v => string.Equals(v.Severity, "Warning", StringComparison.OrdinalIgnoreCase));
				summary = $"Audit completed with {errors} critical errors and {warnings} warnings. Checked: {totalChecked}, Kept: {totalKept}.";
			}

			return new AuditResultDto {
				IsSafe = isSafe,
				TotalGroups = auditedGroupsCount,
				TotalItems = auditedItemsCount,
				CheckedCount = totalChecked,
				KeptCount = totalKept,
				WastedSpaceBytes = totalWastedSpace,
				HasPartiallyFilteredGroups = hasPartiallyFiltered,
				Violations = violations,
				Summary = summary
			};
		}
	}
}
