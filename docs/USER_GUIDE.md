# AgenTally User Guide

**English** | [简体中文](USER_GUIDE.zh-CN.md)

This guide explains the current Stable application. The application UI is
available in Simplified Chinese only, so every English instruction includes the
matching Chinese label.

## Install and start

1. Open the [latest GitHub Release](https://github.com/syoosch/AgenTally/releases/latest).
2. Download the Windows x64 setup executable and its matching `.sha256` file.
3. Follow the checksum procedure in the [README](../README.md), then run the
   installer. Administrator rights and a separate .NET installation are not
   required.
4. If SmartScreen shows “Unknown publisher”, confirm that the file came from
   this repository and that its SHA-256 matches before choosing whether to run
   it. The checksum does not prove publisher identity.
5. Start AgenTally from the Start menu. Supported local Agent records are
   discovered automatically and remain read-only.

Normal source changes should appear within 30–60 seconds. If nothing appears,
see [Support](../SUPPORT.md).

## Navigate the application

The left navigation contains:

- Overview（概览）: total usage, estimated price, trend, Agent/model shares,
  and the usage heatmap.
- Analysis（分析）: detailed Agent, model, and project breakdowns.
- Projects（项目）: logical projects, activity, trends, sessions, and usage
  composition.
- Sessions（会话）: root sessions, Prompt timelines, model tabs, and reliable
  per-call details.
- Data Sources（数据来源）: detected source instances and their compatibility
  status.
- Settings（设置）: local preferences and maintenance.

Settings（设置）has five categories:

- General Settings（常规设置）: startup with Windows and UI refresh interval.
- Data & Backup（数据与备份）: data overview, backup, restore, rescan, clear,
  and the advanced database-path display.
- Models & Pricing（模型与计价）: current built-in or custom prices for models
  that have local usage.
- Privacy & Security（隐私与安全）: the app's local-read and local-storage
  boundaries.
- About & Updates（关于与更新）: version information and the update check.

## Filters and time ranges

Overview（概览）, Analysis（分析）, Projects（项目）, and Sessions（会话）
share Agent, model, project, and time filters where applicable.

Available periods are All Time（全部时间）, Today（今天）, Last 7 Days（近 7
天）, Last 30 Days（近 30 天）, Last 90 Days（近 90 天）, and Custom（自定义）.
All Time starts at the first valid record within the current filters and ends
today.

Custom（自定义）uses the Windows system time zone captured when the UI starts.
Choose a start date and whole hour, then an end date and whole hour. The range
includes the start and excludes the end: `[start, end)`. The end must be later
than the start. The selection is applied after the complete end time is chosen;
cancelling restores the previously active range.

## Understand the numbers

- Total Token（总 Token）is the total supported token usage recorded by the
  source.
- Input is source-reported prompt/input usage.
- Cache read or cache hit is input served from a cache when the source exposes
  it reliably.
- Uncached input（未缓存输入）is derived only when the source's exact totals
  prove the relationship.
- Cache write or cache creation is shown only when the source exposes it
  reliably.
- Output is source-reported generated/output usage.
- API-equivalent price（等效 API 价格）is an estimate from explicit original-
  provider public API pricing rules. It is not a provider bill, subscription
  quota, or amount paid.

The UI deliberately keeps these states distinct:

- Zero: the source reliably reported or proved a value of zero.
- Unknown（未知）: the state cannot currently be determined.
- Unavailable（不可取得）: the local source does not provide a reliable field
  or unique relationship. AgenTally does not guess it.
- No data（暂无数据）: no matching records exist for the current view or filters.
- Unpriced（未计价）: token usage exists, but no exact price rule matched.

## Projects, sessions, and Prompt timelines

A logical project is based on a reliable normalized Git repository identity
when available. Otherwise, AgenTally uses the complete working directory and
does not merge different paths by a similar name.

Sessions（会话）retain the source, time range, actual working directory, models,
token categories, total token, and estimated price. Related child sessions join
a root session only when the source provides reliable causal evidence.

When a source reliably provides Prompt turns, AgenTally shows one bounded
preview for the first user message in the turn and the reliable per-call
details. It does not store or show complete Prompts, responses, tool arguments,
tool output, or attachment paths/content. If attribution is not reliable, the
field remains unavailable instead of being guessed.

## Data-source compatibility

Open Data Sources（数据来源）to inspect each detected source.

- Fully compatible（完全兼容）means the core and classified metrics have
  reliable semantics.
- Partially compatible（部分兼容）means reliable metrics continue to work while
  missing or ambiguous metrics remain unavailable.
- Not currently compatible（暂不兼容）means core token semantics cannot be
  determined safely, so AgenTally stops statistics for that source.
- Structure only means session or Prompt structure can be found, but a reliable
  per-call token contract is not available.

An Agent version change alone does not make a source incompatible. AgenTally
stops only when the required data meaning cannot be proved.

## Models and pricing

Open Settings（设置）→ Models & Pricing（模型与计价）. The list contains models
that have produced local usage plus retained custom overrides.

Built-in prices are offline snapshots shipped with AgenTally. A custom price is
local to this installation and has the highest priority for future or currently
unpriced events. Changing or restoring a price does not rewrite already priced
history. If no exact price is available, token usage remains visible and the
price stays Unpriced（未计价）.

Use Effective Date（生效日期）to schedule a custom price from local midnight on
that date. Price History（价格历史）shows retained dated rules, including future
ones. For a model, Restore Default（恢复默认）ends its custom override from the
chosen date and cancels later planned overrides. Earlier rules remain available
for late-imported calls. Already priced amounts stay frozen after price edits,
catalog upgrades, and rescans.

DeepSeek Flash and V4 Pro built-in rates choose peak/off-peak prices from the
call's Beijing time and the bundled holiday calendar, currently covering 2026.
An explicit custom price stays a fixed override. A missing holiday year or
unproved historical rate can leave an estimate partial or unpriced.

## Backup and restore

Open Settings（设置）→ Data & Backup（数据与备份）.

### Create a backup

1. Choose Back Up Now（立即备份）.
2. Select the destination when prompted.
3. Keep the resulting backup as private local data.

A backup is a consistent snapshot created by the Core. It includes statistics,
sessions, bounded Prompt previews, source compatibility/cursors, frozen prices,
and custom prices. It does not include Agent source logs, complete Prompts or
responses, tool payloads, attachments, credentials, runtime files, or UI-only
preferences. It may contain complete local project paths and session names.
AgenTally does not upload backups.

### Restore a backup

1. Choose Restore Backup（恢复备份）.
2. Select an AgenTally backup.
3. Review the warning and confirm only if replacing the current statistics is
   intended.

Restore validates the archive, database, schema, integrity, and compatible
channel before replacing the current database. It is a whole-database
replacement, not a merge. A failed validation or replacement keeps the
pre-restore database and current system settings.

## Rescan and clear statistics

Open Settings（设置）→ Data & Backup（数据与备份）.

- Rescan（重新扫描）re-reads all supported sources without modifying them. It
  safely adds facts that can still be proved and retains database-only history.
- Clear All Statistics（清除全部统计）is under Dangerous Actions（危险操作）
  and requires confirmation. AgenTally first records a baseline at the end of
  every current source, then clears local statistics and existing event price
  bindings. Collection continues only for new source content after the
  baseline. Custom model prices are retained.

A later rescan can recover history that still exists in the source logs. It
cannot recover history already deleted from the source and removed from
AgenTally. Neither operation modifies Agent source records.

## Window, tray, and startup behavior

- Minimize keeps the UI in the Windows taskbar.
- Closing the main window or pressing Alt+F4 exits the UI process, while the
  Core and tray continue collecting.
- Double-click the tray icon or choose Open AgenTally（打开 AgenTally）to open a
  new UI at Overview（概览）.
- Choose Exit AgenTally（退出 AgenTally）from the tray to stop both the UI and
  Core for the current Profile.

Startup with Windows（开机自启）is in Settings（设置）→ General Settings（常规
设置）and is off by default. When enabled, Windows sign-in starts the Core and
tray for the current user without automatically opening the main window.

## Updates and uninstall

The production version channel is currently not configured, so automatic and
manual checks make no version request. Use
[GitHub Releases](https://github.com/syoosch/AgenTally/releases/latest) to check
for updates. AgenTally does not download or install an update in the app.

New installers refuse to replace a newer installed version before closing the
application or copying files. The same version can repair an installation and
a newer version can upgrade it. Previously published installers are unchanged.

Uninstall keeps the Stable database and settings by default. Select the
explicit delete-data option only if you intend to remove AgenTally-owned Stable
data, logs, and runtime state. Backups saved outside the application data
directory and all Agent source records remain outside uninstall scope.

## More help

- [Support and troubleshooting](../SUPPORT.md)
- [Privacy notice](../PRIVACY.md)
- [Security policy](../SECURITY.md)
- [Latest Release](https://github.com/syoosch/AgenTally/releases/latest)
