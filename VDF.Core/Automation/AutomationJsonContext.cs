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

using System.Text.Json.Serialization;

namespace VDF.Core.Automation {

	[JsonSerializable(typeof(AutomationRequest))]
	[JsonSerializable(typeof(AutomationResponse))]
	[JsonSerializable(typeof(string))]
	[JsonSerializable(typeof(ActiveFilterDto))]
	[JsonSerializable(typeof(ActiveFilterResultDto))]
	[JsonSerializable(typeof(ScanStatusDto))]
	[JsonSerializable(typeof(DuplicateGroupSummaryDto))]
	[JsonSerializable(typeof(DuplicateItemDetailDto))]
	[JsonSerializable(typeof(GroupsResultDto))]
	[JsonSerializable(typeof(GroupDetailsResultDto))]
	[JsonSerializable(typeof(AuditViolationDto))]
	[JsonSerializable(typeof(AuditResultDto))]
	[JsonSerializable(typeof(SelectResultDto))]
	[JsonSerializable(typeof(ScrollResultDto))]
	[JsonSerializable(typeof(GroupThumbnailFrameDto))]
	[JsonSerializable(typeof(GroupThumbnailItemDto))]
	[JsonSerializable(typeof(GroupThumbnailsResultDto))]
	[JsonSerializable(typeof(HighlightGroupResultDto))]
	[JsonSerializable(typeof(MarkNotAMatchResultDto))]
	[JsonSerializable(typeof(RemoveFromListResultDto))]
	[JsonSerializable(typeof(RenameItemResultDto))]
	[JsonSerializable(typeof(TransferSuffixConflictDto))]
	[JsonSerializable(typeof(TransferSuffixRenameDto))]
	[JsonSerializable(typeof(TransferSuffixResultDto))]
	internal sealed partial class AutomationJsonContext : JsonSerializerContext { }
}
