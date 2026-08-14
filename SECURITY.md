# Security Policy

## Supported versions

Security fixes are provided for the latest published release line.

| Version | Supported |
| --- | --- |
| Latest published release | Yes |
| Older releases | No |

## Reporting a vulnerability

If GitHub private vulnerability reporting is enabled for this repository, use
that channel:

1. Open the repository's **Security** tab.
2. Choose **Advisories** and **Report a vulnerability**.
3. Include affected versions, impact, reproduction conditions, and a minimal
   proof of concept that contains no real user data.

If private vulnerability reporting is unavailable, open a minimal public issue
requesting a private contact channel. Do not include exploit details, secrets,
real Agent records, databases, prompts, responses, file paths, or screenshots
containing private information in that issue.

Reports are handled on a best-effort basis; no fixed response-time commitment
is currently offered. Please allow time to reproduce, correct, and validate a
fix before public disclosure.

## Security-sensitive areas

Reports are especially useful when they involve:

- Modification or deletion outside AgenTally-owned paths.
- Credential, cookie, login-token, or subscription-quota access.
- Network activity outside the explicitly approved version-check surface.
- Exposure of complete prompts, responses, tool payloads, attachments, or
  local project paths beyond the documented local-only behavior.
- Installer, upgrade, uninstall, path traversal, reparse-point, ACL, registry,
  or process-identity boundaries.
- SQLite integrity, backup/restore validation, IPC authorization, or source
  parsing of malicious local input.

## Safe testing

Use synthetic data and environments you own or are authorized to test. Do not
access another person's records, disrupt services, retain private data, or
publish an exploit before a fix is available.
