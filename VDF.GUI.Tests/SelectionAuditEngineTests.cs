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
using VDF.Core.ViewModels;
using VDF.GUI.Automation;
using VDF.GUI.ViewModels;
using Xunit;

namespace VDF.GUI.Tests {
	public class SelectionAuditEngineTests {

		static DuplicateItemVM CreateItem(
			Guid groupId,
			string path,
			long size,
			int width,
			int height,
			int durationMinutes,
			decimal bitrateKbs,
			bool isChecked = false,
			string format = "h264",
			bool isVisibleInFilter = true) {

			var item = new DuplicateItem {
				GroupId = groupId,
				Path = path,
				Folder = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
				SizeLong = size,
				FrameSize = $"{width}x{height}",
				FrameSizeInt = width + height,
				Duration = TimeSpan.FromMinutes(durationMinutes),
				BitRateKbs = bitrateKbs,
				Format = format,
				Similarity = 99f,
				IsImage = false
			};

			return new DuplicateItemVM(item) {
				Checked = isChecked,
				IsVisibleInFilter = isVisibleInFilter
			};
		}

		[Fact]
		public void EvaluateMatches_ExpressionWithSafetyGuard_SkipsFullGroups() {
			var group1 = Guid.NewGuid();
			var group2 = Guid.NewGuid();

			var items = new List<DuplicateItemVM> {
				// Group 1: 1 matches, 1 does not
				CreateItem(group1, @"C:\videos\1_small.mp4", size: 1000, 1280, 720, 10, 1500),
				CreateItem(group1, @"C:\videos\1_large.mp4", size: 5000, 1920, 1080, 10, 3000),

				// Group 2: BOTH match expression (size < 4000)
				CreateItem(group2, @"C:\videos\2_a.mp4", size: 2000, 1280, 720, 10, 1500),
				CreateItem(group2, @"C:\videos\2_b.mp4", size: 3000, 1280, 720, 10, 1800),
			};

			// Without allowAllInGroup
			var (partial, full, skipped) = SelectionAuditEngine.EvaluateMatches(
				items,
				expression: "item.SizeLong < 4000",
				strategy: null,
				allowAllInGroup: false);

			Assert.Single(partial);
			Assert.Equal(@"C:\videos\1_small.mp4", partial[0].ItemInfo.Path);
			Assert.Empty(full);
			Assert.Equal(1, skipped);

			// With allowAllInGroup
			var (partial2, full2, skipped2) = SelectionAuditEngine.EvaluateMatches(
				items,
				expression: "item.SizeLong < 4000",
				strategy: null,
				allowAllInGroup: true);

			Assert.Single(partial2);
			Assert.Single(full2);
			Assert.Equal(2, full2[0].Count);
			Assert.Equal(0, skipped2);
		}

		[Fact]
		public void EvaluateMatches_StrategyLowestQuality_PicksInferiorItem() {
			var groupId = Guid.NewGuid();
			var items = new List<DuplicateItemVM> {
				CreateItem(groupId, @"C:\videos\720p.mp4", size: 1000, 1280, 720, 10, 1500),
				CreateItem(groupId, @"C:\videos\1080p.mp4", size: 3000, 1920, 1080, 10, 4000),
			};

			var (partial, _, _) = SelectionAuditEngine.EvaluateMatches(
				items,
				expression: null,
				strategy: "lowest-quality",
				allowAllInGroup: false);

			Assert.Single(partial);
			Assert.Equal(@"C:\videos\720p.mp4", partial[0].ItemInfo.Path);
		}

		[Fact]
		public void EvaluateMatches_StrategyBestCodec_MarksInferiorCodec() {
			var groupId = Guid.NewGuid();
			var items = new List<DuplicateItemVM> {
				// Both are 1080p, but one is modern AV1, the other is older H.264
				CreateItem(groupId, @"C:\videos\movie_av1.mkv", size: 2000, 1920, 1080, 10, 2500, format: "av1"),
				CreateItem(groupId, @"C:\videos\movie_h264.mp4", size: 3500, 1920, 1080, 10, 4500, format: "h264"),
			};

			var (partial, _, skipped) = SelectionAuditEngine.EvaluateMatches(
				items,
				expression: null,
				strategy: "best-codec",
				allowAllInGroup: false);

			Assert.Single(partial);
			Assert.Equal(@"C:\videos\movie_h264.mp4", partial[0].ItemInfo.Path);
			Assert.Equal(0, skipped);
		}

		[Fact]
		public void EvaluateMatches_CurrentFilterOnly_RespectsVisibleItemsOnly() {
			var groupId = Guid.NewGuid();
			var items = new List<DuplicateItemVM> {
				// Visible item
				CreateItem(groupId, @"C:\videos\folderA\match.mp4", size: 1000, 1280, 720, 10, 1500, isVisibleInFilter: true),
				// Hidden item in different folder
				CreateItem(groupId, @"C:\videos\folderB\hidden.mp4", size: 1000, 1280, 720, 10, 1500, isVisibleInFilter: false),
			};

			var (partial, _, skipped) = SelectionAuditEngine.EvaluateMatches(
				items,
				expression: "item.SizeLong == 1000",
				strategy: null,
				allowAllInGroup: false,
				currentFilterOnly: true);

			Assert.Single(partial);
			Assert.Equal(@"C:\videos\folderA\match.mp4", partial[0].ItemInfo.Path);
			Assert.Equal(0, skipped);
		}

		[Fact]
		public void AuditSelection_SafeSelection_ReturnsNoViolations() {
			var groupId = Guid.NewGuid();
			var items = new List<DuplicateItemVM> {
				CreateItem(groupId, @"C:\videos\720p.mp4", size: 1000, 1280, 720, 10, 1500, isChecked: true),
				CreateItem(groupId, @"C:\videos\1080p.mp4", size: 3000, 1920, 1080, 10, 4000, isChecked: false),
			};

			var report = SelectionAuditEngine.AuditSelection(items, criteriaExpression: "item.FrameSizeInt <= 1280 + 720");

			Assert.True(report.IsSafe);
			Assert.Empty(report.Violations);
			Assert.Equal(1, report.CheckedCount);
			Assert.Equal(1, report.KeptCount);
		}

		[Fact]
		public void AuditSelection_AllCheckedInGroup_FlagsCriticalError() {
			var groupId = Guid.NewGuid();
			var items = new List<DuplicateItemVM> {
				CreateItem(groupId, @"C:\videos\copy1.mp4", size: 1000, 1280, 720, 10, 1500, isChecked: true),
				CreateItem(groupId, @"C:\videos\copy2.mp4", size: 1000, 1280, 720, 10, 1500, isChecked: true),
			};

			var report = SelectionAuditEngine.AuditSelection(items);

			Assert.False(report.IsSafe);
			var violation = Assert.Single(report.Violations);
			Assert.Equal("AllCheckedInGroup", violation.Type);
			Assert.Equal("Error", violation.Severity);
		}

		[Fact]
		public void AuditSelection_PartiallyFilteredGroup_NotesHiddenKept() {
			var groupId = Guid.NewGuid();
			var items = new List<DuplicateItemVM> {
				// 2 visible items in filter, both checked
				CreateItem(groupId, @"C:\videos\folderA\item1.mp4", size: 1000, 1280, 720, 10, 1500, isChecked: true, isVisibleInFilter: true),
				CreateItem(groupId, @"C:\videos\folderA\item2.mp4", size: 1000, 1280, 720, 10, 1500, isChecked: true, isVisibleInFilter: true),
				// 1 hidden item, kept (unchecked)
				CreateItem(groupId, @"C:\videos\folderB\item3.mp4", size: 1000, 1280, 720, 10, 1500, isChecked: false, isVisibleInFilter: false),
			};

			var report = SelectionAuditEngine.AuditSelection(items, currentFilterOnly: true);

			Assert.True(report.IsSafe); // Not an error because 1 copy is kept outside filter!
			Assert.True(report.HasPartiallyFilteredGroups);
			Assert.Contains(report.Violations, v => v.Type == "PartiallyFilteredGroupKeep" && v.Severity == "Info");
		}

		[Fact]
		public void AuditSelection_CodecInversion_FlagsWarning() {
			var groupId = Guid.NewGuid();
			var items = new List<DuplicateItemVM> {
				// Superior AV1 is checked for deletion while inferior H.264 at same 1080p resolution is kept!
				CreateItem(groupId, @"C:\videos\movie_av1.mkv", size: 2500, 1920, 1080, 10, 3000, isChecked: true, format: "av1"),
				CreateItem(groupId, @"C:\videos\movie_h264.mp4", size: 4000, 1920, 1080, 10, 4500, isChecked: false, format: "h264"),
			};

			var report = SelectionAuditEngine.AuditSelection(items);

			Assert.Contains(report.Violations, v => v.Type == "CodecInversion" && v.Severity == "Warning");
		}

		[Fact]
		public void AuditSelection_CriteriaMismatch_FlagsWarning() {
			var groupId = Guid.NewGuid();
			var items = new List<DuplicateItemVM> {
				CreateItem(groupId, @"C:\videos\1080p.mp4", size: 4000, 1920, 1080, 10, 4000, isChecked: true),
				CreateItem(groupId, @"C:\videos\720p.mp4", size: 1000, 1280, 720, 10, 1500, isChecked: false),
			};

			// We require checked items to be 720p or lower
			var report = SelectionAuditEngine.AuditSelection(items, criteriaExpression: "item.FrameSizeInt <= 1280 + 720");

			Assert.Contains(report.Violations, v => v.Type == "CriteriaMismatch" && v.Severity == "Warning");
		}

		[Fact]
		public void AuditSelection_QualityInversion_FlagsWarning() {
			var groupId = Guid.NewGuid();
			var items = new List<DuplicateItemVM> {
				// Best item (1080p, 4000kbps) checked for deletion, lower quality item (720p, 1500kbps) kept!
				CreateItem(groupId, @"C:\videos\best_1080p.mp4", size: 4000, 1920, 1080, 10, 4000, isChecked: true),
				CreateItem(groupId, @"C:\videos\worst_720p.mp4", size: 1000, 1280, 720, 10, 1500, isChecked: false),
			};

			var report = SelectionAuditEngine.AuditSelection(items);

			Assert.Contains(report.Violations, v => v.Type == "QualityInversion" && v.Severity == "Warning");
		}
	}
}
