using System.IO;
using System.Diagnostics;
using System.Text.Json;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Runtime;

[TestClass]
public sealed class StablePackageSafetyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [TestMethod]
    public void StableMaintenance_UsesExactIdentityAndNeverForcesTermination()
    {
        string combined = string.Join("\n", PackageScripts().Select(File.ReadAllText));

        foreach (string forbidden in new[]
                 {
                     "Stop-Process",
                     "taskkill",
                     ".Kill(",
                     "GetProcessesByName",
                     "Win32_Process",
                     "TerminateProcess"
                 })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("GetProcessById", combined, StringComparison.Ordinal);
        Assert.Contains("ProcessStartUtcTicks", combined, StringComparison.Ordinal);
        Assert.Contains("ExecutablePath", combined, StringComparison.Ordinal);
        Assert.Contains("application-shutdown-request-{0}.json", combined, StringComparison.Ordinal);
        Assert.Contains("Release(64)", combined, StringComparison.Ordinal);
    }

    [TestMethod]
    public void StableProcessWait_UsesCapturedPidAndStartTimeWithoutReopeningMainModule()
    {
        using var directory = new TestTempDirectory();
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            $current = [System.Diagnostics.Process]::GetProcessById($PID)
            try {
                $identity = [pscustomobject]@{
                    ProcessId = $current.Id
                    ProcessStartUtcTicks = $current.StartTime.ToUniversalTime().Ticks
                    ExecutablePath = 'Z:\captured-path-must-not-be-reopened.exe'
                }
                $alive = Test-AgenTallyStableProcessAlive -Identity $identity
                $reused = Test-AgenTallyStableProcessAlive -Identity ([pscustomobject]@{
                    ProcessId = $identity.ProcessId
                    ProcessStartUtcTicks = $identity.ProcessStartUtcTicks + 1
                    ExecutablePath = $identity.ExecutablePath
                })
            }
            finally {
                $current.Dispose()
            }

            [ordered]@{
                alive = $alive
                reused = $reused
            } | ConvertTo-Json -Compress
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.IsTrue(document.RootElement.GetProperty("alive").GetBoolean());
        Assert.IsFalse(document.RootElement.GetProperty("reused").GetBoolean());
    }

    [TestMethod]
    public void StableProcessInspection_ExitedSnapshotWithMissingIdentityIsIgnored()
    {
        using var directory = new TestTempDirectory();
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            function Get-Process {
                $process = [pscustomobject]@{
                    ProcessName = 'AgenTally.Core'
                    Id = 42
                    HasExited = $true
                    Path = $null
                    StartTime = $null
                }
                $process | Add-Member -MemberType ScriptMethod -Name Dispose -Value {}
                return $process
            }

            @(Get-AgenTallyStableProcess `
                -InstallRoot '{{PowerShellLiteral(directory.File("installed"))}}').Count |
                ConvertTo-Json -Compress
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(0, JsonSerializer.Deserialize<int>(result.StandardOutput));
    }

    [TestMethod]
    public void InnoInstaller_IsCurrentUserX64WithOneUserEntry()
    {
        string installer = File.ReadAllText(Package("AgenTally.iss"));
        string runtime = File.ReadAllText(Package("StableMaintenance.ps1"));

        Assert.AreEqual(
            1,
            CountOccurrences(installer, "Name: \"{group}\\AgenTally\""));
        Assert.AreEqual(1, CountOccurrences(installer, "AppId="));
        Assert.Contains("PrivilegesRequired=lowest", installer, StringComparison.Ordinal);
        Assert.Contains("ArchitecturesAllowed=x64compatible", installer, StringComparison.Ordinal);
        Assert.Contains("DefaultDirName={code:GetDefaultInstallDir}", installer, StringComparison.Ordinal);
        Assert.Contains("DisableDirPage=auto", installer, StringComparison.Ordinal);
        Assert.Contains("UsePreviousAppDir=yes", installer, StringComparison.Ordinal);
        Assert.Contains("AlwaysShowDirOnReadyPage=yes", installer, StringComparison.Ordinal);
        Assert.Contains("UsePreviousTasks=yes", installer, StringComparison.Ordinal);
        Assert.Contains("AllowNetworkDrive=no", installer, StringComparison.Ordinal);
        Assert.Contains("AllowUNCPath=no", installer, StringComparison.Ordinal);
        Assert.Contains("AllowRootDirectory=no", installer, StringComparison.Ordinal);
        Assert.Contains("Name: \"desktopicon\"", installer, StringComparison.Ordinal);
        Assert.Contains("Name: \"{userdesktop}\\AgenTally\"", installer, StringComparison.Ordinal);
        Assert.Contains("Uninstallable=yes", installer, StringComparison.Ordinal);
        Assert.AreEqual(1, CountOccurrences(installer, "[Registry]"));
        Assert.Contains(
            "Root: HKCU; Subkey: \"Software\\AgenTally\\Stable\"; ValueType: string; ValueName: \"InstallLocation\"; ValueData: \"{app}\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains("Software\\AgenTally\\Stable", installer, StringComparison.Ordinal);
        Assert.Contains("RegDeleteValue", installer, StringComparison.Ordinal);
        Assert.Contains(
            "AgenTally.InstallIdentity.json",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "{A59B3C1C-D735-4D8E-9357-4DF501455822}",
            runtime,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void StablePublisher_UsesExplicitCleanVersionedInputAndContainedOutput()
    {
        string wrapper = File.ReadAllText(Script("Publish-AgenTallyStablePackage.ps1"));
        string publisher = File.ReadAllText(Script("Build-AgenTallyStableInstaller.ps1"));
        string verifier = File.ReadAllText(Script("Test-AgenTallyStableReleaseArtifact.ps1"));
        string publicBoundary = File.ReadAllText(Script("Test-AgenTallyPublicBoundary.ps1"));

        Assert.Contains("Build-AgenTallyStableInstaller.ps1", wrapper, StringComparison.Ordinal);
        Assert.Contains("status --porcelain=v1 --untracked-files=all", publisher, StringComparison.Ordinal);
        Assert.Contains("Test-AgenTallyPrepackageSecurity.ps1", publisher, StringComparison.Ordinal);
        Assert.Contains("AgenTallyChannel=Stable", publisher, StringComparison.Ordinal);
        Assert.Contains("--runtime', 'win-x64", publisher, StringComparison.Ordinal);
        Assert.Contains("--self-contained', 'true", publisher, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", publisher, StringComparison.Ordinal);
        Assert.Contains("IncludeNativeLibrariesForSelfExtract=false", publisher, StringComparison.Ordinal);
        Assert.Contains("--no-restore", publisher, StringComparison.Ordinal);
        Assert.Contains("Stop-AgenTallyStableGracefully", publisher, StringComparison.Ordinal);
        Assert.Contains("Get-AgenTallyStableProcess", publisher, StringComparison.Ordinal);
        Assert.DoesNotContain("AgenTally.Runtime.ps1", publisher, StringComparison.Ordinal);
        Assert.Contains("artifacts\\stable-package", publisher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Get-FileHash", publisher, StringComparison.Ordinal);
        Assert.Contains("Inno Setup did not produce the expected single EXE installer", publisher, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", publisher, StringComparison.Ordinal);
        Assert.Contains("CN=Pyrsys B.V.", publisher, StringComparison.Ordinal);
        Assert.DoesNotContain("Compress-Archive", publisher, StringComparison.Ordinal);
        Assert.Contains(".agentally-public", verifier, StringComparison.Ordinal);
        Assert.Contains("https://github.com/syoosch/AgenTally", verifier, StringComparison.Ordinal);
        Assert.Contains("status', '--porcelain=v1", verifier, StringComparison.Ordinal);
        Assert.Contains("rev-parse', '--verify", verifier, StringComparison.Ordinal);
        Assert.Contains("releaseCandidate", verifier, StringComparison.Ordinal);
        Assert.Contains("UNCOMMITTED", verifier, StringComparison.Ordinal);
        Assert.Contains("System.Security.Cryptography.SHA256", verifier, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", verifier, StringComparison.Ordinal);
        Assert.Contains("staging-*", verifier, StringComparison.Ordinal);
        Assert.AreEqual(
            2,
            CountOccurrences(
                publicBoundary,
                "scripts/Test-AgenTallyStableReleaseArtifact.ps1"));
    }

    [TestMethod]
    public void StableReleaseArtifactVerifier_AcceptsExactFormalArtifactAndRejectsNonCandidate()
    {
        using var directory = new TestTempDirectory();
        string publicRepository = directory.File("public");
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $repository = '{{PowerShellLiteral(publicRepository)}}'
            $scripts = Join-Path $repository 'scripts'
            $packageRoot = Join-Path $repository 'artifacts\stable-package'
            New-Item -ItemType Directory -Path $scripts -Force | Out-Null
            New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
            Copy-Item `
                -LiteralPath '{{PowerShellLiteral(Script("Test-AgenTallyStableReleaseArtifact.ps1"))}}' `
                -Destination (Join-Path $scripts 'Test-AgenTallyStableReleaseArtifact.ps1')
            [System.IO.File]::WriteAllText((Join-Path $repository 'AgenTally.sln'), '')
            [System.IO.File]::WriteAllText((Join-Path $repository '.agentally-root'), '')
            [System.IO.File]::WriteAllText((Join-Path $repository '.agentally-public'), '')
            [System.IO.File]::WriteAllText((Join-Path $repository '.gitignore'), "/artifacts/`r`n")

            & git -C $repository init -b main | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }
            & git -C $repository config user.name 'AgenTally Test' | Out-Null
            & git -C $repository config user.email 'agentally-test@example.invalid' | Out-Null
            & git -C $repository remote add origin 'https://github.com/syoosch/AgenTally.git' | Out-Null
            & git -C $repository add -- `.agentally-public `.agentally-root `.gitignore AgenTally.sln scripts | Out-Null
            & git -C $repository commit -m 'test: create public fixture' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
            $commit = (& git -C $repository rev-parse HEAD).Trim()
            $shortCommit = (& git -C $repository rev-parse --short=12 HEAD).Trim()

            $version = '1.2.3'
            $baseName = "AgenTally-$version-win-x64-setup"
            $installerName = "$baseName.exe"
            $installerPath = Join-Path $packageRoot $installerName
            $bytes = New-Object byte[] 1024
            $bytes[0] = [byte] [char] 'M'
            $bytes[1] = [byte] [char] 'Z'
            [System.IO.File]::WriteAllBytes($installerPath, $bytes)
            $stream = [System.IO.File]::OpenRead($installerPath)
            $algorithm = [System.Security.Cryptography.SHA256]::Create()
            try {
                $hash = ([System.BitConverter]::ToString(
                    $algorithm.ComputeHash($stream))).Replace('-', '')
            }
            finally {
                $algorithm.Dispose()
                $stream.Dispose()
            }
            $report = [ordered]@{
                schemaVersion = 1
                product = 'AgenTally'
                channel = 'Stable'
                version = $version
                commit = $shortCommit
                releaseCandidate = $true
                architecture = 'win-x64'
                selfContained = $true
                uiAndCoreSingleFile = $true
                installer = $installerName
                sha256 = $hash
                bytes = $bytes.Length
                signed = $false
                innoSetupCompilerMajorVersion = 7
                innoSetupCompilerSigner = 'CN=Pyrsys B.V.'
            }
            $reportPath = Join-Path $packageRoot "$baseName.json"
            $report | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
            "$hash  $installerName" |
                Set-Content `
                    -LiteralPath (Join-Path $packageRoot "$baseName.sha256") `
                    -Encoding ASCII

            $verifier = Join-Path $scripts 'Test-AgenTallyStableReleaseArtifact.ps1'
            function Get-AuthenticodeSignature {
                [pscustomobject]@{
                    Status = [System.Management.Automation.SignatureStatus]::NotSigned
                }
            }
            & $verifier -Version $version -ExpectedCommit $commit

            $report.releaseCandidate = $false
            $report | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
            $rejected = $false
            try {
                & $verifier -Version $version -ExpectedCommit $commit
            }
            catch {
                if ($_.Exception.Message -notlike '*not a formal self-contained release candidate*') {
                    throw
                }
                $rejected = $true
            }
            if (-not $rejected) { throw 'The non-candidate artifact was accepted.' }
            Write-Output 'formal artifact accepted; non-candidate rejected'
            """;

        PowerShellResult result;
        try
        {
            result = RunPowerShell(directory, script);
        }
        finally
        {
            ClearReadOnlyFileAttributes(publicRepository);
        }

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains(
            "formal artifact accepted; non-candidate rejected",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void StableUninstall_IsAllowListedAndDataRemovalIsExplicit()
    {
        string maintenance = File.ReadAllText(Package("Invoke-AgenTallyStableMaintenance.ps1"));
        string installer = File.ReadAllText(Package("AgenTally.iss"));
        string documentation = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "PACKAGING.md"));
        string combinedScripts = string.Join("\n", PackageScripts().Select(File.ReadAllText));

        Assert.Contains("[switch] $RemoveData", maintenance, StringComparison.Ordinal);
        Assert.Contains("Stop-AgenTallyStableGracefully", maintenance, StringComparison.Ordinal);
        Assert.Contains("[string] $InstallRoot", maintenance, StringComparison.Ordinal);
        Assert.Contains("Assert-AgenTallyStableNoReparsePoint", maintenance, StringComparison.Ordinal);
        Assert.Contains("$actualRunCommand.Equals", maintenance, StringComparison.Ordinal);
        Assert.Contains("Remove-AgenTallyStableOwnedData", maintenance, StringComparison.Ordinal);
        Assert.Contains("unexpected residual content", combinedScripts, StringComparison.Ordinal);
        Assert.Contains("-ErrorAction Stop", maintenance, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts\\development", maintenance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProtectedDataRoots", combinedScripts, StringComparison.Ordinal);
        Assert.DoesNotContain("agentSourceCandidates", combinedScripts, StringComparison.Ordinal);
        Assert.Contains("InstallRootDisjointFromOwnedData", combinedScripts, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Remove-Item -LiteralPath $paths.StableRoot -Recurse",
            maintenance,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UninstallSilent", installer, StringComparison.Ordinal);
        Assert.Contains("MB_YESNOCANCEL", installer, StringComparison.Ordinal);
        Assert.Contains("MB_DEFBUTTON2", installer, StringComparison.Ordinal);
        Assert.Contains("只会处理上面列出的 AgenTally 本地应用数据", installer, StringComparison.Ordinal);
        Assert.Contains("Abort;", installer, StringComparison.Ordinal);
        Assert.IsTrue(
            documentation.Contains("silent uninstall keeps", StringComparison.OrdinalIgnoreCase) ||
            documentation.Contains("静默卸载同样保留数据库和设置", StringComparison.Ordinal));
        Assert.IsTrue(
            documentation.Contains("outside the ownership set", StringComparison.Ordinal) ||
            documentation.Contains("不属于上述正向所有权集合", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InnoInstaller_StopsExactStableIdentityBeforeInstallAndUninstall()
    {
        string installer = File.ReadAllText(Package("AgenTally.iss"));

        Assert.Contains("CloseApplications=no", installer, StringComparison.Ordinal);
        Assert.Contains("function PrepareToInstall", installer, StringComparison.Ordinal);
        Assert.Contains("'InspectInstall'", installer, StringComparison.Ordinal);
        Assert.Contains("'PrepareInstall'", installer, StringComparison.Ordinal);
        Assert.Contains("ExpandConstant('{app}')", installer, StringComparison.Ordinal);
        Assert.Contains("-InstallRoot", installer, StringComparison.Ordinal);
        Assert.Contains(
            "{sysnative}\\WindowsPowerShell\\v1.0\\powershell.exe",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "NormalizedInstallRoot := RemoveBackslashUnlessRoot(InstallRoot)",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddQuotes(NormalizedInstallRoot)",
            installer,
            StringComparison.Ordinal);
        Assert.Contains("procedure CurUninstallStepChanged", installer, StringComparison.Ordinal);
        Assert.Contains("'InspectUninstall'", installer, StringComparison.Ordinal);
        Assert.Contains("'PrepareUninstall'", installer, StringComparison.Ordinal);
        Assert.Contains("ewWaitUntilTerminated", installer, StringComparison.Ordinal);
        Assert.Contains("最多等待 20 秒", installer, StringComparison.Ordinal);
        Assert.Contains("IsRunStateValid", installer, StringComparison.Ordinal);
        Assert.Contains("IsShortcutStateValid", installer, StringComparison.Ordinal);
        Assert.Contains("InstallInspectionCompleted := False", installer, StringComparison.Ordinal);
        Assert.Contains("Parameters: \"{code:GetPostInstallParameters}\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("skipifsilent unchecked", installer, StringComparison.Ordinal);
    }

    [TestMethod]
    public void StableUpgradeVersion_ComparesBinaryAndRegistryEvidenceAndFailsClosed()
    {
        using var directory = new TestTempDirectory();
        string script = UpgradeVersionFixture(directory) + """
            function Check-Version([string] $Incoming, [bool] $Blocked) {
                $rejected = $false
                try {
                    Assert-AgenTallyStableUpgradeVersion -Paths $paths `
                        -IncomingVersion $Incoming -InstallMode upgrade
                }
                catch { $rejected = $true }
                if ($rejected -ne $Blocked) { throw "Unexpected result for $Incoming." }
            }
            Check-Version '0.9.99' $true
            Check-Version '0.10.0' $false
            Check-Version '0.11.0' $false
            Check-Version '1.0.0' $false
            foreach ($invalid in @('', '0.10', '0.10.0.0', '0.10.0-beta', '65536.0.0')) {
                Check-Version $invalid $true
            }
            # Missing uninstall metadata still uses both actual PE file versions.
            $script:registeredVersion = '0.9.0'
            Check-Version '0.9.0' $true
            $script:registeredVersion = '0.11.0'
            Check-Version '0.10.0' $true
            Check-Version '0.11.0' $false
            $script:registeredVersion = 'broken-version'
            Check-Version '1.0.0' $true
            $script:registeredVersion = $null

            # A partially replaced Core cannot be hidden by the older UI.
            Add-Type -TypeDefinition @'
            using System.Reflection;
            [assembly: AssemblyFileVersion("0.12.0.0")]
            public sealed class NewerCoreFixture {}
            '@ -OutputAssembly (Join-Path $paths.InstallRoot 'newer.dll')
            Copy-Item -LiteralPath (Join-Path $paths.InstallRoot 'newer.dll') `
                -Destination (Join-Path $paths.InstallRoot 'AgenTally.Core.exe') -Force
            Check-Version '0.11.0' $true
            Check-Version '0.12.0' $false
            [IO.File]::WriteAllText((Join-Path $paths.InstallRoot 'AgenTally.Core.exe'), 'no version')
            Check-Version '1.0.0' $true

            Assert-AgenTallyStableUpgradeVersion -Paths ([pscustomobject]@{}) `
                -IncomingVersion '0.1.0' -InstallMode first
            'version cases passed'
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains("version cases passed", result.StandardOutput, StringComparison.Ordinal);
    }

    [TestMethod]
    public void StableUpgradeVersion_InstallEntryRejectsBeforeShutdownOrWrites()
    {
        using var directory = new TestTempDirectory();
        string script = UpgradeVersionFixture(directory) + $$"""
            # Load the real entry-point functions, without executing its top-level
            # invocation against this machine's installed Stable application.
            $tokens = $null
            $parseErrors = $null
            $entry = [Management.Automation.Language.Parser]::ParseFile(
                '{{PowerShellLiteral(Package("Invoke-AgenTallyStableMaintenance.ps1"))}}',
                [ref] $tokens, [ref] $parseErrors)
            if ($parseErrors.Count -ne 0) { throw 'Maintenance parse failed.' }
            foreach ($statement in $entry.EndBlock.Statements) {
                if ($statement -is [Management.Automation.Language.FunctionDefinitionAst]) {
                    . ([scriptblock]::Create($statement.Extent.Text))
                }
            }
            function Get-AgenTallyStablePaths { return $script:paths }
            function Stop-AgenTallyStableGracefully { $script:shutdowns++ }
            $script:shutdowns = 0
            $InstallRoot = $paths.InstallRoot
            $IncomingVersion = '0.9.99'
            $StatePath = Join-Path $paths.TempRoot 'install-state.txt'
            $DesktopShortcutRequested = $false
            $StopTimeoutSeconds = 1
            $data = Join-Path $paths.DataRoot 'agentally.db'
            New-Item -ItemType Directory -Path $paths.DataRoot -Force | Out-Null
            [IO.File]::WriteAllText($data, 'existing-ledger-must-remain')
            $protected = @($data, (Join-Path $InstallRoot 'AgenTally.UI.exe'),
                (Join-Path $InstallRoot 'AgenTally.Core.exe'))
            function Fingerprint([string] $Path) {
                $algorithm = [Security.Cryptography.SHA256]::Create()
                try { [Convert]::ToBase64String($algorithm.ComputeHash([IO.File]::ReadAllBytes($Path))) }
                finally { $algorithm.Dispose() }
            }
            $before = @($protected | ForEach-Object { Fingerprint $_ })
            foreach ($Mode in @('InspectInstall', 'PrepareInstall')) {
                $rejected = $false
                try { Invoke-AgenTallyStableMaintenance }
                catch { $rejected = $_.Exception.Message -like '*downgrade refused*' }
                if (-not $rejected) { throw "Downgrade reached $Mode." }
            }
            if ($shutdowns -ne 0 -or (Test-Path -LiteralPath $StatePath)) {
                throw 'Rejected downgrade stopped the application or wrote install state.'
            }
            $after = @($protected | ForEach-Object { Fingerprint $_ })
            if (($before -join ',') -ne ($after -join ',')) { throw 'Existing files changed.' }
            $Mode = 'PrepareInstall'
            $IncomingVersion = '0.10.0'
            Invoke-AgenTallyStableMaintenance
            if ($shutdowns -ne 1) { throw 'Same-version repair did not reach normal preparation.' }
            'entry guard passed'
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains("entry guard passed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "-IncomingVersion ' + AddQuotes('{#MyAppVersion}')",
            File.ReadAllText(Package("AgenTally.iss")),
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void StableOwnedDataCleanup_RemovesOnlyAllowListedTreesAndPreservesExternalData()
    {
        using var directory = new TestTempDirectory();
        string stableRoot = directory.File("local-app-data/AgenTally/Stable");
        string externalRoot = directory.File("external-data");
        string sentinelPath = Path.Combine(externalRoot, "sentinel.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinelPath)!);
        File.WriteAllText(sentinelPath, "external-data-must-remain");
        string sentinelBefore = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sentinelPath)));
        foreach (string child in new[] { "data", "runtime", "logs", "temp" })
        {
            string owned = Path.Combine(stableRoot, child);
            Directory.CreateDirectory(owned);
            File.WriteAllText(Path.Combine(owned, "owned.txt"), child);
        }

        string script = CleanupScript(stableRoot, removeData: true);
        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.IsFalse(Directory.Exists(stableRoot));
        Assert.IsTrue(File.Exists(sentinelPath));
        Assert.AreEqual(
            sentinelBefore,
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sentinelPath))));
    }

    [TestMethod]
    public void StableOwnedDataCleanup_LockedFileFailsWithoutFalseSuccess()
    {
        using var directory = new TestTempDirectory();
        string stableRoot = directory.File("local-app-data/AgenTally/Stable");
        string lockedPath = Path.Combine(stableRoot, "data", "agentally.db");
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        File.WriteAllText(lockedPath, "locked");
        foreach (string child in new[] { "runtime", "logs", "temp" })
        {
            Directory.CreateDirectory(Path.Combine(stableRoot, child));
        }

        using (var locked = new FileStream(
                   lockedPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            PowerShellResult result = RunPowerShell(
                directory,
                CleanupScript(stableRoot, removeData: true));

            Assert.AreNotEqual(0, result.ExitCode);
            Assert.IsTrue(File.Exists(lockedPath));
        }
    }

    [TestMethod]
    public void StableOwnedDataCleanup_KeepDataRemovesOnlyTransientTrees()
    {
        using var directory = new TestTempDirectory();
        string stableRoot = directory.File("local-app-data/AgenTally/Stable");
        string dataPath = Path.Combine(stableRoot, "data", "agentally.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        File.WriteAllText(dataPath, "preserved-data");
        foreach (string child in new[] { "runtime", "logs", "temp" })
        {
            string owned = Path.Combine(stableRoot, child);
            Directory.CreateDirectory(owned);
            File.WriteAllText(Path.Combine(owned, "owned.txt"), child);
        }

        PowerShellResult result = RunPowerShell(
            directory,
            CleanupScript(stableRoot, removeData: false));

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.IsTrue(File.Exists(dataPath));
        Assert.AreEqual("preserved-data", File.ReadAllText(dataPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(stableRoot, "runtime")));
        Assert.IsFalse(Directory.Exists(Path.Combine(stableRoot, "logs")));
        Assert.IsFalse(Directory.Exists(Path.Combine(stableRoot, "temp")));
    }

    [TestMethod]
    public void StablePathBoundary_VolumeRootDoesNotDependOnWorkingDirectory()
    {
        using var directory = new TestTempDirectory();
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            $candidate = Join-Path `
                ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) `
                'AgenTally-volume-root-boundary-test'
            $boundary = [System.IO.Path]::GetPathRoot($candidate)
            Assert-AgenTallyStableNoReparsePoint -Path $candidate -Boundary $boundary
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
    }

    [TestMethod]
    public void StableRelativePath_VolumeRootDoesNotDependOnWorkingDirectory()
    {
        using var directory = new TestTempDirectory();
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            $candidate = [System.IO.Path]::GetFullPath(
                [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile))
            $boundary = [System.IO.Path]::GetPathRoot($candidate)
            $relative = Get-AgenTallyStableRelativePath `
                -Path $candidate `
                -Root $boundary
            if ([string]::IsNullOrWhiteSpace($relative) -or
                [System.IO.Path]::IsPathRooted($relative)) {
                throw "Unexpected relative path: $relative"
            }
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
    }

    [TestMethod]
    public void StableInstallTarget_RecognizesStrictLegacyDefaultOrphan()
    {
        using var directory = new TestTempDirectory();
        string installRoot = directory.File("legacy-default-install");
        Directory.CreateDirectory(installRoot);
        foreach (string fileName in new[]
                 {
                     "AgenTally.UI.exe",
                     "AgenTally.Core.exe",
                     "unins000.exe",
                     "unins000.dat",
                     "StableMaintenance.ps1",
                     "Invoke-AgenTallyStableMaintenance.ps1"
                 })
        {
            File.WriteAllText(Path.Combine(installRoot, fileName), fileName);
        }

        string missingRegistryBase = $"HKCU:\\Software\\AgenTally-Tests\\{Guid.NewGuid():N}";
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            $installRoot = '{{PowerShellLiteral(installRoot)}}'
            $paths = [pscustomobject]@{
                InstallRoot = $installRoot
                DefaultInstallRoot = $installRoot
                InnoUninstallRegistryPath = '{{PowerShellLiteral(missingRegistryBase + "\\Inno")}}'
                UninstallRegistryPath = '{{PowerShellLiteral(missingRegistryBase + "\\Legacy")}}'
                InstallRecordRegistryPath = '{{PowerShellLiteral(missingRegistryBase + "\\Record")}}'
                StableRoot = '{{PowerShellLiteral(directory.File("stable-data"))}}'
            }
            $mode = Assert-AgenTallyStableInstallTarget -Paths $paths
            if ($mode -ne 'upgrade') { throw "Unexpected install mode: $mode" }
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
    }

    [TestMethod]
    public void StableInstallTarget_EmptyDirectoryIsFirstInstall()
    {
        using var directory = new TestTempDirectory();
        string installRoot = directory.File("empty-first-install");
        Directory.CreateDirectory(installRoot);

        PowerShellResult result = RunPowerShell(
            directory,
            InstallTargetScript(installRoot, directory.File("default-install")));

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual("first", result.StandardOutput.Trim());
    }

    [TestMethod]
    public void StableInstallTarget_ForeignNonEmptyDirectoryFailsClosed()
    {
        using var directory = new TestTempDirectory();
        string installRoot = directory.File("foreign-non-empty");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "foreign.txt"), "foreign");

        PowerShellResult result = RunPowerShell(
            directory,
            InstallTargetScript(installRoot, directory.File("default-install")));

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains(
            "requires a new or empty directory",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void StableInstallTarget_MarkedCustomOrphanIsUpgrade()
    {
        using var directory = new TestTempDirectory();
        string installRoot = directory.File("custom-marked-orphan");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "AgenTally.UI.exe"), "ui");
        File.WriteAllText(Path.Combine(installRoot, "AgenTally.Core.exe"), "core");
        File.WriteAllText(
            Path.Combine(installRoot, "AgenTally.InstallIdentity.json"),
            """
            {
              "schemaVersion": 1,
              "channel": "Stable",
              "appId": "{A59B3C1C-D735-4D8E-9357-4DF501455822}"
            }
            """);

        PowerShellResult result = RunPowerShell(
            directory,
            InstallTargetScript(installRoot, directory.File("default-install")));

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual("upgrade", result.StandardOutput.Trim());
    }

    [TestMethod]
    public void StableInstallTarget_InsideOwnedDataFailsClosed()
    {
        using var directory = new TestTempDirectory();
        string stableRoot = directory.File("stable-data");
        string installRoot = Path.Combine(stableRoot, "program");
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            Assert-AgenTallyStableInstallRootDisjointFromOwnedData `
                -InstallRoot '{{PowerShellLiteral(installRoot)}}' `
                -StableRoot '{{PowerShellLiteral(stableRoot)}}'
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains(
            "must not contain or be contained by",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(stableRoot));
    }

    [TestMethod]
    public void StableInstallTarget_SiblingOfOwnedDataIsAllowed()
    {
        using var directory = new TestTempDirectory();
        string stableRoot = directory.File("stable-data");
        string installRoot = directory.File("program");
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            Assert-AgenTallyStableInstallRootDisjointFromOwnedData `
                -InstallRoot '{{PowerShellLiteral(installRoot)}}' `
                -StableRoot '{{PowerShellLiteral(stableRoot)}}'
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.IsFalse(Directory.Exists(stableRoot));
    }

    [TestMethod]
    public void StableRegistryValue_MissingOptionalNameReturnsNull()
    {
        using var directory = new TestTempDirectory();
        string valueName = $"AgenTally-missing-{Guid.NewGuid():N}";
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            $value = Get-AgenTallyStableRegistryValue `
                -RegistryPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
                -ValueName '{{valueName}}'
            if ($null -ne $value) { throw 'Unexpected registry value.' }
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
    }

    [TestMethod]
    public void StableInstallRecord_MissingLocationFailsWithControlledIdentityError()
    {
        using var directory = new TestTempDirectory();
        string missingRegistryBase = $"HKCU:\\Software\\AgenTally-Tests\\{Guid.NewGuid():N}";
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            function Get-AgenTallyStableRegistryValue { return $null }
            $paths = [pscustomobject]@{
                InnoUninstallRegistryPath = 'HKCU:\Software'
                UninstallRegistryPath = '{{PowerShellLiteral(missingRegistryBase + "\\Legacy")}}'
                InstallRecordRegistryPath = '{{PowerShellLiteral(missingRegistryBase + "\\Record")}}'
            }
            Get-AgenTallyStableRegisteredInstallRoot -Paths $paths
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains(
            "no valid absolute local InstallLocation",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void StableInstallRecords_DisagreementFailsClosed()
    {
        using var directory = new TestTempDirectory();
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            function Get-AgenTallyStableRegistryValue {
                param([string] $RegistryPath, [string] $ValueName)
                if ($RegistryPath -eq 'HKCU:\Software') { return 'C:\AgenTally-A' }
                return 'C:\AgenTally-B'
            }
            $paths = [pscustomobject]@{
                InnoUninstallRegistryPath = 'HKCU:\Software'
                UninstallRegistryPath = 'HKCU:\Software\Microsoft'
                InstallRecordRegistryPath = 'HKCU:\Software\Microsoft\Windows'
            }
            Get-AgenTallyStableRegisteredInstallRoot -Paths $paths
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains(
            "records disagree",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void StableShutdownRequest_UsesUtf8WithoutBom()
    {
        using var directory = new TestTempDirectory();
        string requestPath = directory.File("shutdown-request.json");
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            Write-AgenTallyStableUtf8JsonFile `
                -Path '{{PowerShellLiteral(requestPath)}}' `
                -Value ([ordered]@{
                    profileId = 'TESTPROFILE'
                    requestedAtUtcTicks = [DateTime]::UtcNow.Ticks
                })
            """;

        PowerShellResult result = RunPowerShell(directory, script);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        byte[] bytes = File.ReadAllBytes(requestPath);
        Assert.IsGreaterThan(3, bytes.Length);
        CollectionAssert.AreNotEqual(
            new byte[] { 0xEF, 0xBB, 0xBF },
            bytes.Take(3).ToArray());
        Assert.AreEqual((byte)'{', bytes[0]);
    }

    [TestMethod]
    public void StablePublisher_MergesOneSelfContainedPayloadAndPrunesDebugAssets()
    {
        string publisher = File.ReadAllText(Script("Build-AgenTallyStableInstaller.ps1"));

        Assert.Contains("Merge-PublishOutput -Source $uiPublish", publisher, StringComparison.Ordinal);
        Assert.Contains("Merge-PublishOutput -Source $corePublish", publisher, StringComparison.Ordinal);
        Assert.Contains("UI/Core publish collision is not byte-identical", publisher, StringComparison.Ordinal);
        Assert.Contains("'AgenTally.Core.exe'", publisher, StringComparison.Ordinal);
        Assert.DoesNotContain("'Core\\AgenTally.Core.exe'", publisher, StringComparison.Ordinal);
        Assert.Contains("Stable payload must contain exactly one win-x64 SQLite native library", publisher, StringComparison.Ordinal);
        Assert.Contains("'AgenTally.InstallIdentity.json'", publisher, StringComparison.Ordinal);
        Assert.Contains("@('.pdb', '.xml')", publisher, StringComparison.Ordinal);
    }

    private static string[] PackageScripts() =>
    [
        Package("StableMaintenance.ps1"),
        Package("Invoke-AgenTallyStableMaintenance.ps1"),
        Script("Build-AgenTallyStableInstaller.ps1")
    ];

    private static int CountOccurrences(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;

    private static string Package(string name) =>
        Path.Combine(RepositoryRoot, "packaging", "stable", name);

    private static string Script(string name) =>
        Path.Combine(RepositoryRoot, "scripts", name);

    private static string CleanupScript(string stableRoot, bool removeData)
    {
        string literalRoot = PowerShellLiteral(stableRoot);
        return $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            $paths = [pscustomobject]@{
                StableRoot = '{{literalRoot}}'
                DataRoot = '{{PowerShellLiteral(Path.Combine(stableRoot, "data"))}}'
                RuntimeRoot = '{{PowerShellLiteral(Path.Combine(stableRoot, "runtime"))}}'
                LogRoot = '{{PowerShellLiteral(Path.Combine(stableRoot, "logs"))}}'
                TempRoot = '{{PowerShellLiteral(Path.Combine(stableRoot, "temp"))}}'
            }
            Remove-AgenTallyStableOwnedData -Paths $paths{{(removeData ? " -RemoveData" : string.Empty)}}
            """;
    }

    private static string InstallTargetScript(
        string installRoot,
        string defaultInstallRoot)
    {
        string missingRegistryBase =
            $"HKCU:\\Software\\AgenTally-Tests\\{Guid.NewGuid():N}";
        return $$"""
            $ErrorActionPreference = 'Stop'
            . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
            $paths = [pscustomobject]@{
                InstallRoot = '{{PowerShellLiteral(installRoot)}}'
                DefaultInstallRoot = '{{PowerShellLiteral(defaultInstallRoot)}}'
                InnoUninstallRegistryPath = '{{PowerShellLiteral(missingRegistryBase + "\\Inno")}}'
                UninstallRegistryPath = '{{PowerShellLiteral(missingRegistryBase + "\\Legacy")}}'
                InstallRecordRegistryPath = '{{PowerShellLiteral(missingRegistryBase + "\\Record")}}'
                StableRoot = '{{PowerShellLiteral(Path.Combine(Path.GetDirectoryName(installRoot)!, "stable-data"))}}'
            }
            Assert-AgenTallyStableInstallTarget -Paths $paths
            """;
    }

    private static string UpgradeVersionFixture(TestTempDirectory directory) =>
        $$"""
        $ErrorActionPreference = 'Stop'
        . '{{PowerShellLiteral(Package("StableMaintenance.ps1"))}}'
        $root = '{{PowerShellLiteral(directory.File("program"))}}'
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        Add-Type -TypeDefinition @'
        using System.Reflection;
        [assembly: AssemblyFileVersion("0.10.0.0")]
        public sealed class InstalledVersionFixture {}
        '@ -OutputAssembly (Join-Path $root 'fixture.dll')
        foreach ($name in @('AgenTally.UI.exe', 'AgenTally.Core.exe')) {
            Copy-Item -LiteralPath (Join-Path $root 'fixture.dll') -Destination (Join-Path $root $name)
        }
        Copy-Item -LiteralPath '{{PowerShellLiteral(Package("AgenTally.InstallIdentity.json"))}}' -Destination $root
        $stable = '{{PowerShellLiteral(directory.File("stable"))}}'
        $script:paths = [pscustomobject]@{
            InstallRoot = $root
            DefaultInstallRoot = $root
            StableRoot = $stable
            DataRoot = Join-Path $stable 'data'
            RuntimeRoot = Join-Path $stable 'runtime'
            LogRoot = Join-Path $stable 'logs'
            TempRoot = Join-Path $stable 'temp'
            InnoUninstallRegistryPath = Join-Path $stable 'missing-inno-record'
            UninstallRegistryPath = Join-Path $stable 'missing-legacy-record'
            InstallRecordRegistryPath = Join-Path $stable 'missing-location-record'
            DesktopShortcutPath = Join-Path $stable 'desktop.lnk'
            InnoStartMenuShortcutPath = Join-Path $stable 'menu.lnk'
        }
        $script:registeredVersion = $null
        function Get-AgenTallyStableRegistryValue {
            param([string] $RegistryPath, [string] $ValueName)
            if ($ValueName -eq 'DisplayVersion') { return $script:registeredVersion }
            throw 'Unexpected registry read in isolated version fixture.'
        }

        """;

    private static string PowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static void ClearReadOnlyFileAttributes(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static PowerShellResult RunPowerShell(
        TestTempDirectory directory,
        string script)
    {
        string scriptPath = directory.File($"test-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, script);
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        using Process process = Process.Start(startInfo) ??
            throw new AssertFailedException("PowerShell test process did not start.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        Assert.IsTrue(
            process.WaitForExit(10_000),
            "PowerShell test process did not exit within 10 seconds.");
        return new PowerShellResult(
            process.ExitCode,
            standardOutput,
            standardError);
    }

    private sealed record PowerShellResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AgenTally.sln")) &&
                File.Exists(Path.Combine(current.FullName, ".agentally-root")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("AgenTally repository root not found.");
    }
}
