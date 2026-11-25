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
using System.Threading;
using VDF.Core.Automation;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.GUI.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Automation {
	internal static class SelectionAuditEngine {
		// Keep cancellation inside enumeration, including the inner loops of a single large group.
		static IEnumerable<T> WithCancellation<T>(IEnumerable<T> items, CancellationToken ct) =>
			ct.CanBeCanceled ? EnumerateWithCancellation(items, ct) : items;

		static IEnumerable<T> EnumerateWithCancellation<T>(IEnumerable<T> items, CancellationToken ct) {
			foreach (var item in items) {
				ct.ThrowIfCancellationRequested();
				yield return item;
			}
			ct.ThrowIfCancellationRequested();
		}

		static IEnumerable<QualityRanker.Criterion<DuplicateItemVM>> WithCancellableAccessors(
			IEnumerable<QualityRanker.Criterion<DuplicateItemVM>> criteria, CancellationToken ct) =>
			!ct.CanBeCanceled ? criteria : criteria.Select(criterion => new QualityRanker.Criterion<DuplicateItemVM>(
				criterion.Name, item => {
					ct.ThrowIfCancellationRequested();
					return criterion.Accessor(item);
				}, criterion.VideoOnly, criterion.Ascending, criterion.NearTie));

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
				bool currentFilterOnly = true,
				CancellationToken ct = default) {

			ct.ThrowIfCancellationRequested();
			Func<DuplicateItem, bool>? interpreter = null;
			if (!string.IsNullOrWhiteSpace(expression)) {
				interpreter = SelectionExpression.Compile(expression);
			}

			// Group by GroupId across all items to know the complete group context
			var groups = WithCancellation(allItems, ct).GroupBy(d => d.ItemInfo.GroupId);

			var partialMatches = new List<DuplicateItemVM>();
			var fullGroups = new List<List<DuplicateItemVM>>();

			foreach (var group in groups) {
				ct.ThrowIfCancellationRequested();
				var groupAllItems = WithCancellation(group, ct).ToList();
				var scopeItems = currentFilterOnly
					? WithCancellation(groupAllItems, ct).Where(d => d.IsVisibleInFilter).ToList()
					: groupAllItems;

				if (scopeItems.Count == 0) continue;

				List<DuplicateItemVM> matchedInGroup;
				if (interpreter != null) {
					matchedInGroup = WithCancellation(scopeItems, ct).Where(item => interpreter(item.ItemInfo)).ToList();
				}
				else if (!string.IsNullOrWhiteSpace(strategy)) {
					matchedInGroup = EvaluateStrategyOnGroup(groupAllItems, scopeItems, strategy, ct);
				}
				else {
					throw new ArgumentException("Either 'expression' or 'strategy' must be provided.");
				}

				if (matchedInGroup.Count == 0) continue;

				// Safety calculation: would checking these items leave ZERO kept files across the ENTIRE group?
				int hiddenKept = currentFilterOnly
					? WithCancellation(groupAllItems, ct).Count(d => !d.IsVisibleInFilter && !d.Checked)
					: 0;
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

			ct.ThrowIfCancellationRequested();
			return (partialMatches, fullGroups, skipped);
		}

		static List<DuplicateItemVM> EvaluateStrategyOnGroup(
			List<DuplicateItemVM> fullGroup,
			List<DuplicateItemVM> scope,
			string strategy,
			CancellationToken ct) {

			var allItems = WithCancellation(fullGroup, ct);
			var scopeItems = WithCancellation(scope, ct);
			var normalized = strategy.Trim().ToLowerInvariant().Replace("_", "-");
			switch (normalized) {
				case "lowest-quality":
				case "quality": {
					var activeCriteria = MainWindowVM.ResolveCriteria(
						Data.SettingsFile.Instance.QualityCriteriaOrder,
						Data.SettingsFile.Instance.QualityCriteriaDisabled);
					var keeper = QualityRanker.PickKeeper(fullGroup, WithCancellableAccessors(activeCriteria, ct), d => d.ItemInfo.IsImage);
					return scopeItems.Where(d => !ReferenceEquals(d, keeper)).ToList();
				}
				case "best-codec":
				case "codec": {
					// Mark files for deletion having an inferior codec when a modern encoding exists at equivalent or better resolution
					var matches = new List<DuplicateItemVM>();
					foreach (var item in scopeItems) {
						if (item.ItemInfo.IsImage) continue;
						int currentRank = MainWindowVM.CodecRank(item.ItemInfo.Format);
						bool hasBetter = allItems.Any(other =>
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
					long maxSize = allItems.Max(d => d.ItemInfo.SizeLong);
					return scopeItems.Where(d => d.ItemInfo.SizeLong < maxSize).ToList();
				}
				case "largest-file": {
					long minSize = allItems.Min(d => d.ItemInfo.SizeLong);
					return scopeItems.Where(d => d.ItemInfo.SizeLong > minSize).ToList();
				}
				case "worst-resolution": {
					int maxRes = allItems.Max(d => d.ItemInfo.FrameSizeInt);
					return scopeItems.Where(d => d.ItemInfo.FrameSizeInt < maxRes).ToList();
				}
				case "shortest-duration": {
					var maxDur = allItems.Max(d => d.ItemInfo.Duration);
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
					var newest = allItems.Max(d => d.ItemInfo.DateCreated);
					return scopeItems.Where(d => d.ItemInfo.DateCreated < newest).ToList();
				}
				case "newest": {
					var oldest = allItems.Min(d => d.ItemInfo.DateCreated);
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
			bool currentFilterOnly = true,
			CancellationToken ct = default) {

			ct.ThrowIfCancellationRequested();
			Func<DuplicateItem, bool>? criteriaPredicate = null;
			if (!string.IsNullOrWhiteSpace(criteriaExpression)) {
				criteriaPredicate = SelectionExpression.Compile(criteriaExpression);
			}

			var groups = WithCancellation(allItems, ct).GroupBy(d => d.ItemInfo.GroupId);

			var violations = new List<AuditViolationDto>();
			int totalChecked = 0;
			int totalKept = 0;
			long totalWastedSpace = 0;
			bool hasPartiallyFiltered = false;

			var activeCriteria = WithCancellableAccessors(MainWindowVM.ResolveCriteria(
				Data.SettingsFile.Instance.QualityCriteriaOrder,
				Data.SettingsFile.Instance.QualityCriteriaDisabled), ct);

			int auditedGroupsCount = 0;
			int auditedItemsCount = 0;

			foreach (var group in groups) {
				ct.ThrowIfCancellationRequested();
				var fullGroupItems = WithCancellation(group, ct).ToList();
				var groupItems = WithCancellation(fullGroupItems, ct);
				var visibleItems = groupItems.Where(d => d.IsVisibleInFilter).ToList();

				// If auditing filter only, skip groups that have no visible items
				if (currentFilterOnly && visibleItems.Count == 0) {
					continue;
				}

				auditedGroupsCount++;
				var scopeItems = currentFilterOnly ? visibleItems : fullGroupItems;
				auditedItemsCount += scopeItems.Count;

				var cancellableScope = WithCancellation(scopeItems, ct);
				var checkedInScope = cancellableScope.Where(d => d.Checked).ToList();
				var keptInScope = cancellableScope.Where(d => !d.Checked).ToList();
				var checkedItems = WithCancellation(checkedInScope, ct);
				var keptItems = WithCancellation(keptInScope, ct);
				int hiddenKeptCount = groupItems.Count(d => !d.IsVisibleInFilter && !d.Checked);

				totalChecked += checkedInScope.Count;
				totalKept += keptInScope.Count;
				totalWastedSpace += checkedItems.Sum(d => d.ItemInfo.SizeLong);

				if (visibleItems.Count < fullGroupItems.Count) {
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
					foreach (var checkedItem in checkedItems) {
						bool satisfiesCriteria;
						try {
							satisfiesCriteria = criteriaPredicate(checkedItem.ItemInfo);
						}
						catch (Exception ex) {
							// Cancellation aborts the audit rather than becoming an item warning.
							ct.ThrowIfCancellationRequested();
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
					if (keeper != null && keeper.Checked && cancellableScope.Contains(keeper)) {
						var keptExample = keptItems.FirstOrDefault(k => !ReferenceEquals(k, keeper));
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
				foreach (var checkedItem in checkedItems) {
					if (checkedItem.ItemInfo.IsImage) continue;
					int checkedRank = MainWindowVM.CodecRank(checkedItem.ItemInfo.Format);

					foreach (var keptItem in keptItems) {
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

			var cancellableViolations = WithCancellation(violations, ct);
			bool isSafe = !cancellableViolations.Any(v => string.Equals(v.Severity, "Error", StringComparison.OrdinalIgnoreCase));

			string summary;
			if (violations.Count == 0) {
				summary = $"Selection is healthy. {totalChecked} items checked across {auditedGroupsCount} groups ({totalWastedSpace / 1024 / 1024:N0} MB). No rule violations detected.";
			}
			else {
				int errors = cancellableViolations.Count(v => string.Equals(v.Severity, "Error", StringComparison.OrdinalIgnoreCase));
				int warnings = cancellableViolations.Count(v => string.Equals(v.Severity, "Warning", StringComparison.OrdinalIgnoreCase));
				summary = $"Audit completed with {errors} critical errors and {warnings} warnings. Checked: {totalChecked}, Kept: {totalKept}.";
			}

			ct.ThrowIfCancellationRequested();
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
