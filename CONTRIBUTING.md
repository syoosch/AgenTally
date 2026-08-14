# Contributing to AgenTally

Thank you for helping improve AgenTally. This public repository contains the
product source, general tests, Stable packaging support, and durable user or
contributor documentation.

## Product and privacy boundaries

AgenTally is a Windows-only, local-first Agent token usage application. Changes
must preserve these boundaries:

- Agent logs and databases are read-only inputs.
- Do not add hooks, injection, proxies, Agent command wrappers, plugins, or
  modifications to Agent configuration, environment, files, or processes.
- Do not read credentials, cookies, login tokens, or subscription quotas.
- Do not add telemetry or automatic crash, statistics, prompt, response, or
  work-file uploads.
- Normal collection, statistics, and pricing remain offline. Version checking
  is the only approved networking surface and must remain isolated and
  explicitly configured.
- Do not persist or display complete prompts, responses, tool arguments or
  results, attachment paths, or attachment contents.

Changes that alter these boundaries require an explicit product decision before
implementation.

## Development setup

Requirements:

- A supported Windows environment.
- The .NET SDK selected by `global.json`.
- PowerShell 7 or Windows PowerShell 5.1 for repository scripts.

Restore locked dependencies and run a focused test group while iterating:

```powershell
dotnet restore AgenTally.sln --locked-mode
.\scripts\Test-AgenTallyFocused.ps1 -Filter "FullyQualifiedName~TargetTests"
```

Tests that show a real WPF window must use the repository's isolated-desktop
test host and the `WindowedDesktop` category. Do not automate the user's real
mouse, keyboard, or input desktop.

Before submitting a pull request, run the applicable checks. For a release or
cross-cutting change, run the complete gate:

```powershell
.\scripts\Test-AgenTallyPublicBoundary.ps1
.\scripts\Test-AgenTallyPrepackageSecurity.ps1
dotnet test --project tests/AgenTally.Tests/AgenTally.Tests.csproj --configuration Release --no-restore
dotnet test --project tests/AgenTally.Tests/AgenTally.Tests.csproj --configuration Release --no-restore -p:AgenTallyIncludeWindowedDesktopTests=true --filter "TestCategory=WindowedDesktop"
dotnet build AgenTally.sln --configuration Release --no-restore --no-incremental -warnaserror
```

## Fixtures and local data

- Use synthetic or explicitly sanitized fixtures only.
- Never commit real Agent logs, prompts, responses, databases, credentials,
  cookies, attachment paths, runtime state, backups, installer output, or local
  diagnostic artifacts.
- Keep generated files under ignored output directories.
- If a source format cannot be proved reliably, fail closed and report the
  field as unavailable rather than guessing.

## Pull requests

Keep each pull request focused and include:

- The user-visible or technical problem being solved.
- The affected product and privacy boundaries.
- The exact checks that were run and their results.
- Any platform, sample, GUI, installer, or release validation that remains
  unverified.

Update public documentation when setup, behavior, privacy, or packaging
changes. Internal work records, prompts, screenshots, and acceptance logs do
not belong in this repository.
