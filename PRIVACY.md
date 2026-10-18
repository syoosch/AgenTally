# AgenTally Privacy Notice

**English** | [简体中文](PRIVACY.zh-CN.md)

AgenTally is a local-first Windows desktop application. Its normal collection,
statistics, pricing, backup, and crash handling stay on this computer.

## What AgenTally reads

AgenTally opens supported local Agent records as read-only inputs. It extracts
only the fields needed for local usage statistics, such as:

- timestamps, Agent/source identity, model identity, and token categories;
- logical project identity and the actual complete working directory;
- session identifiers, source-generated session names, and reliable
  parent/child relationships;
- source-proven Prompt turn identity and tool names;
- a bounded preview of the first user message in a reliable Prompt turn.

The bounded Prompt preview is normalized locally and limited to 120 Unicode
scalar values. Later user additions in the same turn increase only the message
count and are not stored as text.

## What AgenTally does not collect or store

AgenTally does not store, index, search, display, or export:

- complete Prompts or responses;
- tool arguments, tool output, or source excerpts;
- attachment contents or attachment paths;
- credentials, cookies, login tokens, or API keys;
- subscription quotas or account credit;
- Agent configuration contents used to infer private data.

It does not install hooks, inject into or proxy an Agent, wrap Agent commands,
or modify Agent files, configuration, environment variables, or processes.

## Local storage and retention

Extracted statistics are stored in AgenTally's own local SQLite database. The
database may contain token and pricing facts, complete local project paths,
source-generated session names, bounded Prompt previews, source compatibility
state, and collection cursors.

Deleting an Agent source log does not automatically delete statistics already
extracted into AgenTally. Those local statistics remain until the user clears
them or explicitly removes AgenTally-owned data during uninstall.

Clear All Statistics（清除全部统计）removes local usage, sessions, bounded
Prompt previews, and existing event price bindings after establishing a new
end-of-source baseline. Custom model prices remain. A later rescan can recover
only history that still exists in the source records.

## Backups

Backups are created only when the user asks for one and chooses a destination.
A backup contains a consistent snapshot of statistics, sessions, bounded
Prompt previews, source state, frozen prices, and custom prices. It may contain
complete local project paths and session names.

Backups do not contain Agent source logs, complete Prompts or responses, tool
payloads, attachments, credentials, runtime files, or UI-only preferences.
AgenTally does not upload backups and does not delete backups saved outside its
application data directory during uninstall. Treat backup files as private.

## Networking

Normal Agent collection, statistics, price estimation, telemetry, and crash
handling use no network connection. AgenTally does not provide anonymous
telemetry and does not automatically upload statistics, crash logs, backups, or
work files.

Version checking is the only permitted network surface. The production version
channel is currently not configured, so automatic and manual checks make no
version request. Opening the GitHub Release page happens only after a user
action. The app does not download or install updates.

## Uninstall and deletion

Uninstall keeps the Stable database and settings by default so that a later
installation can continue using them. The installer removes AgenTally-owned
Stable data, logs, and runtime state only when the user explicitly selects the
delete-data option. Agent records and backups outside the application data
directory are never part of the uninstall scope.

## Reporting problems safely

Do not post Agent source records, the AgenTally database, backups, Prompts,
responses, complete local paths, session/project names, credentials, or private
screenshots in a public issue. Use a minimized, sanitized, or synthetic example
and follow [SUPPORT.md](SUPPORT.md).

Report a vulnerability through the private channel in
[SECURITY.md](SECURITY.md), without publishing exploit details or real user
data.
