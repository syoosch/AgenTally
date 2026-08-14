# Changelog

All notable user-visible changes to AgenTally will be documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and releases use [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Initial Windows-only public release preparation.
- Local read-only usage collection for supported Agent records.
- Token, model, project, session, Prompt timeline, trend, heatmap, and
  equivalent API price views.
- Local SQLite history with manual backup, restore, rescan, clear-statistics,
  and opt-in Windows startup controls.
- Current-user Inno Setup packaging with isolated application identities,
  program files, data and lifecycle handling.

### Security

- Local-first privacy boundary with no telemetry or automatic data upload.
- Source, dependency, secret, installer ownership, and local-input validation
  gates for release preparation.

The first public GitHub Release will move these entries into the `0.1.0`
section with its actual publication date.
