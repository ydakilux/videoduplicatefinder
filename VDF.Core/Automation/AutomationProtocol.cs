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
using System.Text.Json.Serialization;

namespace VDF.Core.Automation {

	internal static class AutomationConstants {
		public const string DefaultPipeName = "vdf-gui-automation";
	}

	internal sealed class AutomationRequest {
		[JsonPropertyName("command")]
		public string Command { get; set; } = string.Empty;

		[JsonPropertyName("parameters")]
		public Dictionary<string, object?>? Parameters { get; set; }
	}

	internal sealed class AutomationResponse {
		[JsonPropertyName("success")]
		public bool Success { get; set; }

		[JsonPropertyName("error")]
		public string? Error { get; set; }

		[JsonPropertyName("data")]
		public object? Data { get; set; }

		public static AutomationResponse Ok(object? data = null) => new() {
			Success = true,
			Data = data
		};

		public static AutomationResponse Fail(string error) => new() {
			Success = false,
			Error = error
		};
	}

	internal sealed class ActiveFilterDto {
		[JsonPropertyName("filterByPath")]
		public string FilterByPath { get; set; } = string.Empty;

		[JsonPropertyName("fileType")]
		public string FileType { get; set; } = "All";

		[JsonPropertyName("similarityFrom")]
		public int SimilarityFrom { get; set; } = 0;

		[JsonPropertyName("similarityTo")]
		public int SimilarityTo { get; set; } = 100;

		[JsonPropertyName("onlyGroupsWithCheckedItems")]
		public bool OnlyGroupsWithCheckedItems { get; set; }

		[JsonPropertyName("hideGroupsWithOneFileLeft")]
		public bool HideGroupsWithOneFileLeft { get; set; }

		[JsonPropertyName("onlyAiMatches")]
		public bool OnlyAiMatches { get; set; }
	}

	internal sealed class ScanStatusDto {
		[JsonPropertyName("isScanning")]
		public bool IsScanning { get; set; }

		[JsonPropertyName("isPaused")]
		public bool IsPaused { get; set; }

		[JsonPropertyName("isReviewState")]
		public bool IsReviewState { get; set; }

		[JsonPropertyName("totalDuplicates")]
		public int TotalDuplicates { get; set; }

		[JsonPropertyName("visibleDuplicates")]
		public int VisibleDuplicates { get; set; }

		[JsonPropertyName("totalGroups")]
		public int TotalGroups { get; set; }

		[JsonPropertyName("visibleGroups")]
		public int VisibleGroups { get; set; }

		[JsonPropertyName("checkedCount")]
		public int CheckedCount { get; set; }

		[JsonPropertyName("visibleCheckedCount")]
		public int VisibleCheckedCount { get; set; }

		[JsonPropertyName("totalWastedSpaceBytes")]
		public long TotalWastedSpaceBytes { get; set; }

		[JsonPropertyName("visibleWastedSpaceBytes")]
		public long VisibleWastedSpaceBytes { get; set; }

		[JsonPropertyName("statusMessage")]
		public string? StatusMessage { get; set; }

		[JsonPropertyName("activeFilter")]
		public ActiveFilterDto? ActiveFilter { get; set; }
	}

	internal sealed class DuplicateGroupSummaryDto {
		[JsonPropertyName("groupId")]
		public Guid GroupId { get; set; }

		[JsonPropertyName("itemCount")]
		public int ItemCount { get; set; }

		[JsonPropertyName("visibleItemCount")]
		public int VisibleItemCount { get; set; }

		[JsonPropertyName("hiddenItemCount")]
		public int HiddenItemCount { get; set; }

		[JsonPropertyName("checkedCount")]
		public int CheckedCount { get; set; }

		[JsonPropertyName("visibleCheckedCount")]
		public int VisibleCheckedCount { get; set; }

		[JsonPropertyName("hiddenCheckedCount")]
		public int HiddenCheckedCount { get; set; }

		[JsonPropertyName("maxSimilarity")]
		public float MaxSimilarity { get; set; }

		[JsonPropertyName("wastedSpaceBytes")]
		public long WastedSpaceBytes { get; set; }

		[JsonPropertyName("bestItemPath")]
		public string? BestItemPath { get; set; }

		[JsonPropertyName("bestReason")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? BestReason { get; set; }

		[JsonPropertyName("formats")]
		public List<string> Formats { get; set; } = new();

		[JsonPropertyName("resolutions")]
		public List<string> Resolutions { get; set; } = new();

		[JsonPropertyName("hasMixedFormats")]
		public bool HasMixedFormats { get; set; }

		[JsonPropertyName("hasMixedResolutions")]
		public bool HasMixedResolutions { get; set; }

		[JsonPropertyName("items")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public List<DuplicateItemDetailDto>? Items { get; set; }
	}

	internal sealed class DuplicateItemDetailDto {
		[JsonPropertyName("groupId")]
		public Guid GroupId { get; set; }

		[JsonPropertyName("path")]
		public string Path { get; set; } = string.Empty;

		[JsonPropertyName("folder")]
		public string Folder { get; set; } = string.Empty;

		[JsonPropertyName("checked")]
		public bool Checked { get; set; }

		[JsonPropertyName("isVisibleInFilter")]
		public bool IsVisibleInFilter { get; set; }

		[JsonPropertyName("isBest")]
		public bool IsBest { get; set; }

		[JsonPropertyName("bestReason")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? BestReason { get; set; }

		[JsonPropertyName("isBestSize")]
		public bool IsBestSize { get; set; }

		[JsonPropertyName("sizeBytes")]
		public long SizeBytes { get; set; }

		[JsonPropertyName("duration")]
		public TimeSpan Duration { get; set; }

		[JsonPropertyName("resolution")]
		public string Resolution { get; set; } = string.Empty;

		[JsonPropertyName("codecRank")]
		public int CodecRank { get; set; }

		[JsonPropertyName("bitRateKbs")]
		public decimal BitRateKbs { get; set; }

		[JsonPropertyName("fps")]
		public float Fps { get; set; }

		[JsonPropertyName("format")]
		public string Format { get; set; } = string.Empty;

		[JsonPropertyName("hdrFormat")]
		public string HdrFormat { get; set; } = string.Empty;

		[JsonPropertyName("audioFormat")]
		public string AudioFormat { get; set; } = string.Empty;

		[JsonPropertyName("audioChannel")]
		public string AudioChannel { get; set; } = string.Empty;

		[JsonPropertyName("audioSampleRate")]
		public int AudioSampleRate { get; set; }

		[JsonPropertyName("audioBitRateKbs")]
		public decimal AudioBitRateKbs { get; set; }

		[JsonPropertyName("audioLanguages")]
		public string AudioLanguages { get; set; } = string.Empty;

		[JsonPropertyName("subtitleLanguages")]
		public string SubtitleLanguages { get; set; } = string.Empty;

		[JsonPropertyName("dateCreated")]
		public DateTime DateCreated { get; set; }

		[JsonPropertyName("dateModified")]
		public DateTime DateModified { get; set; }

		[JsonPropertyName("similarity")]
		public float Similarity { get; set; }

		[JsonPropertyName("isImage")]
		public bool IsImage { get; set; }

		[JsonPropertyName("isAiMatched")]
		public bool IsAiMatched { get; set; }

		[JsonPropertyName("isTombstone")]
		public bool IsTombstone { get; set; }

		[JsonPropertyName("isOffline")]
		public bool IsOffline { get; set; }

		[JsonPropertyName("partialClipOffset")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? PartialClipOffset { get; set; }
	}

	internal sealed class AuditViolationDto {
		[JsonPropertyName("groupId")]
		public Guid GroupId { get; set; }

		[JsonPropertyName("path")]
		public string? Path { get; set; }

		[JsonPropertyName("type")]
		public string Type { get; set; } = string.Empty;

		[JsonPropertyName("severity")]
		public string Severity { get; set; } = "Warning";

		[JsonPropertyName("message")]
		public string Message { get; set; } = string.Empty;
	}

	internal sealed class AuditResultDto {
		[JsonPropertyName("isSafe")]
		public bool IsSafe { get; set; }

		[JsonPropertyName("totalGroups")]
		public int TotalGroups { get; set; }

		[JsonPropertyName("totalItems")]
		public int TotalItems { get; set; }

		[JsonPropertyName("checkedCount")]
		public int CheckedCount { get; set; }

		[JsonPropertyName("keptCount")]
		public int KeptCount { get; set; }

		[JsonPropertyName("wastedSpaceBytes")]
		public long WastedSpaceBytes { get; set; }

		[JsonPropertyName("hasPartiallyFilteredGroups")]
		public bool HasPartiallyFilteredGroups { get; set; }

		[JsonPropertyName("violations")]
		public List<AuditViolationDto> Violations { get; set; } = new();

		[JsonPropertyName("summary")]
		public string Summary { get; set; } = string.Empty;
	}

	internal sealed class SelectResultDto {
		[JsonPropertyName("modifiedCount")]
		public int ModifiedCount { get; set; }

		[JsonPropertyName("totalMatches")]
		public int TotalMatches { get; set; }

		[JsonPropertyName("skippedFullGroupsCount")]
		public int SkippedFullGroupsCount { get; set; }

		[JsonPropertyName("message")]
		public string Message { get; set; } = string.Empty;
	}

	internal sealed class ScrollResultDto {
		[JsonPropertyName("found")]
		public bool Found { get; set; }

		[JsonPropertyName("path")]
		public string? Path { get; set; }

		[JsonPropertyName("groupId")]
		public Guid? GroupId { get; set; }

		[JsonPropertyName("message")]
		public string Message { get; set; } = string.Empty;
	}

	internal sealed class GroupThumbnailFrameDto {
		[JsonPropertyName("mimeType")]
		public string MimeType { get; set; } = "image/jpeg";

		[JsonPropertyName("base64Data")]
		public string Base64Data { get; set; } = string.Empty;

		[JsonPropertyName("timestampSeconds")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public double? TimestampSeconds { get; set; }
	}

	internal sealed class GroupThumbnailItemDto {
		[JsonPropertyName("path")]
		public string Path { get; set; } = string.Empty;

		[JsonPropertyName("checked")]
		public bool Checked { get; set; }

		[JsonPropertyName("isBest")]
		public bool IsBest { get; set; }

		[JsonPropertyName("resolution")]
		public string Resolution { get; set; } = string.Empty;

		[JsonPropertyName("format")]
		public string Format { get; set; } = string.Empty;

		[JsonPropertyName("similarity")]
		public float Similarity { get; set; }

		[JsonPropertyName("frames")]
		public List<GroupThumbnailFrameDto> Frames { get; set; } = new();
	}

	internal sealed class GroupThumbnailsResultDto {
		[JsonPropertyName("groupId")]
		public Guid GroupId { get; set; }

		[JsonPropertyName("items")]
		public List<GroupThumbnailItemDto> Items { get; set; } = new();
	}
}
