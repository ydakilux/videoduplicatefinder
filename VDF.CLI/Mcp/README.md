# Video Duplicate Finder — Model Context Protocol (MCP) Server

This module provides a Model Context Protocol (MCP) server for **Video Duplicate Finder (VDF)**, enabling AI assistants (such as Antigravity, Claude Desktop, Gemini, or Cursor) to interact with VDF directly over standard I/O (stdio).

---

## Capabilities (18 Tools)

Every tool declares standard MCP safety `annotations` (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`) so AI hosts can auto-approve safe inspection/UI actions while gating disk renames.

1. **Filter-Aware Inspection & Status** (`readOnlyHint: true`):
   - `get_scan_status`: Current scanning state, total vs visible duplicate count, total vs visible group count, total vs visible checked count, wasted space, and full active filter descriptor.
   - `get_active_filter`: Current filter configuration (path/wildcard, file type, similarity range, secondary toggles) along with visible vs total item/group counts.
   - `set_active_filter`: Dynamically configure GUI filters (path, file type, similarity min/max, checked-only, hide single remaining, AI matches).
2. **High-Performance Duplicate & Visual Frame Inspection** (`readOnlyHint: true`):
   - `get_duplicate_groups`: List duplicate groups with summaries, `QualityRanker` `bestItemPath` + `bestReason`, synthetic metadata (`formats`, `resolutions`, `hasMixedFormats`, `hasMixedResolutions`), server-side filters (`has_checked`, `has_mixed_codecs`, `has_mixed_resolutions`, `min_similarity`), and optional `include_items: true`.
   - `get_group_details`: Complete metadata for every item in a group (paths, resolution, bitrate, fps, codec, codec rank, HDR format, audio channels/sample rate/languages, subtitle languages, AI match, tombstone/offline flags, `isBest` + `bestReason`).
   - `get_group_thumbnails`: Returns visual preview frames as native MCP `image` content blocks (extracting via FFmpeg on demand if needed) so multimodal AI models can visually inspect candidates for cropping, watermarks, quality differences, or false positives.
3. **Live GUI Control, Visual Feedback & List Curation** (`destructiveHint: false`):
   - `scroll_to_item` / `highlight_group`: Expands, scrolls to, and focuses a specific video or duplicate group on screen.
   - `set_item_checked`: Check or uncheck specific files or groups in real time (refuses to check items hidden by the active filter unless overridden).
   - `undo_selection` / `clear_selection` / `invert_selection`: Native undo (`Ctrl+Z`), clear, or invert checkboxes.
   - `mark_not_a_match`: Marks false-positive duplicate groups as "Not a Match" (`BlacklistedGroups.json`), hiding them immediately and excluding them from future scans without touching files on disk.
   - `remove_from_list`: Removes items or groups from the results list (with optional `exclude_from_future_scans`) without deleting files on disk.
4. **Smart Selection, Codec Intelligence & Auditing**:
   - `select_by_criteria`: Select videos using preset strategies (`best-codec`, `lowest-quality`, `smallest-file`, `largest-file`, `worst-resolution`, `shortest-duration`, `identical`, `identical-except-size`, `oldest`, `newest`) or custom C# expressions, with full-group wipeout protection aware of hidden items.
   - `audit_selection` (`readOnlyHint: true`): Verifies checked items against wipeout errors (`AllCheckedInGroup`), `PartiallyFilteredGroupKeep`, `CodecInversion`, `QualityInversion`, and custom criteria compliance.
5. **Safe Disk Renaming & Tag Transfer** (`destructiveHint: true`):
   - `rename_item`: Renames a duplicate file on disk, in VDF memory, and in `ScannedFiles.db` (including companion `.jpg` cover images) without overwriting existing files.
   - `transfer_suffix_and_rename`: Transfers tags/suffixes (e.g. `___tag`) from checked items onto kept items across groups, with `preview` mode, conflict detection (never overwrites), and single batched DB persistence.

---

## Configuration

### For Antigravity / Gemini IDE (`mcp_config.json`)
```json
{
  "mcpServers": {
    "videoduplicatefinder": {
      "command": "D:\\Repos\\videoduplicatefinder\\VDF.CLI\\bin\\Release\\net11.0\\win-x64\\publish\\vdf-cli.exe",
      "args": [
        "mcp"
      ]
    }
  }
}
```

### For Claude Desktop (`claude_desktop_config.json`)
```json
{
  "mcpServers": {
    "videoduplicatefinder": {
      "command": "D:\\Repos\\videoduplicatefinder\\VDF.CLI\\bin\\Release\\net11.0\\win-x64\\publish\\vdf-cli.exe",
      "args": [
        "mcp"
      ]
    }
  }
}
```

---

## Architecture & Security

- **Same-user Named Pipe isolation**: Uses `PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly` on `\\.\pipe\vdf-gui-automation`, restricting local IPC strictly to the current OS user.
- **Concurrent multi-client sessions**: Multiple MCP clients can stay connected simultaneously; UI mutations are marshaled onto Avalonia's `Dispatcher.UIThread` and wrapped in undo batches.
- **Transparent GUI-restart reconnect**: `vdf-cli mcp` automatically reconnects if `VDF.GUI` is restarted mid-session.
- **Settings UI Toggle**: Can be toggled live at runtime under **Settings → Processing → MCP / CLI automation server**.
