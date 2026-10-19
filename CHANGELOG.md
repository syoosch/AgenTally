# Changelog

All notable user-visible changes to AgenTally are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Target version: **0.2.1**. This release is being prepared and has not been
published.

### Fixed

- Upgrade rescans now accept complete, bounded Codex compaction snapshots
  larger than the normal log-line limit without counting repeated context as
  new usage. Malformed, unsupported, incomplete, and over-bound records remain
  rejected, while existing statistics and frozen prices are preserved.
- Background status retains the specific failed statistics-update message
  after Core exits, so the cause remains visible and explicitly retryable.

## [0.2.0] - 2026-10-19

### Added

- Read-only DeepSeek Harness Desktop/CLI collection, including source-proven
  token categories, Prompt previews, tools, and child-session attribution.
- Exact model recognition for GPT-6.1 Sol and GPT-6 Astra/Sol/Luna identifiers.
- Call-time peak/off-peak pricing for DeepSeek Flash and V4 Pro, using an
  offline Beijing-time holiday calendar.
- Effective-date built-in and custom price histories. Late-imported unpriced
  calls use the applicable historical rule; already priced records stay frozen.
- Verified official historical price transitions and pre-change baselines,
  with explicit gaps where official dates or rates cannot be proved.

### Changed

- Reworked the user-facing README, support entry points, and issue forms.
- Made English the default README language and added Simplified Chinese README
  and support translations.
- Added concise Release, CI, license, and Windows platform badges.
- Disclosed that the current application UI is Simplified Chinese and added
  exact Chinese UI labels to English instructions.
- Added paired English and Simplified Chinese user guides and privacy notices.
- Added a copyable installer SHA-256 verification procedure without pinning
  evergreen documentation to one release version.
- Added a pull request template, source-compatibility triage in Bug reports,
  and automated public-document validation.

### Fixed

- Codex Prompt previews for reliably identified messages received from another
  chat, without treating internal context or ordinary Agent replies as user input.
- Claude Code default/custom CLI and Desktop source compatibility, including
  meta input and oversized records during safe rescans.
- Trend-chart guide/detail cards jumping back during rapid mouse movement;
  the normal pointer remains visible inside the plot.

### Security

- New installers reject downgrades before requesting application shutdown or
  replacing files, while allowing same-version repair and newer upgrades.
  Previously published installers are unchanged.
- Network update checking remains disabled/unconfigured. No telemetry,
  source-write access, or additional background network feature is introduced.

### Known limitations

- DeepSeek peak/off-peak holiday coverage currently includes 2026 only.
- Historical prices and cache categories remain partial where official
  evidence or source fields are insufficient; existing frozen amounts are not
  retrospectively recalculated.
- Existing Agent compatibility limitations, unsigned packaging, and deferred
  online update checking remain unchanged.

## [0.1.0] - 2026-08-14

### Added

- First public AgenTally release for per-user installation on Windows x64.
- Read-only collection of supported AI Agent usage records already stored on
  the local computer.
- Token views by Agent, model, project, session, and time range.
- Trends, heatmap, Prompt timeline, and API-equivalent price views.
- Local SQLite history with manual backup, restore, rescan, and clear controls.
- User-controlled startup with Windows sign-in.
- Self-contained single-EXE installer with no administrator rights or separate
  .NET installation required.
- Continued collection through the Core and system tray after the main window
  is closed.

### Security

- Agent logs and databases remain read-only; AgenTally does not modify Agent
  configuration, files, or processes.
- No credential, cookie, login-token, or subscription-quota access.
- No telemetry or automatic upload of statistics, crash logs, or work files.
- Public-boundary, dependency-vulnerability, sensitive-content, local-input,
  and installer-ownership checks in the release workflow.

[Unreleased]: https://github.com/syoosch/AgenTally/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/syoosch/AgenTally/releases/tag/v0.2.0
[0.1.0]: https://github.com/syoosch/AgenTally/releases/tag/v0.1.0
