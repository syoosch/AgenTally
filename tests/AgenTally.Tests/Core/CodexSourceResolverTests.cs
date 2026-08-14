using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Domain.Sources;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class CodexSourceResolverTests
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    [TestMethod]
    public async Task ProbeAsync_ReturnsOnlyKnownTreesInStablePathOrder()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string active = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-2026-07-16T10-00-thread-a.jsonl");
        string archived = Path.Combine(
            codexHome,
            "archived_sessions",
            "rollout-2026-07-15T09-00-thread-b.jsonl");
        string unrelated = Path.Combine(
            codexHome,
            "other",
            "not-a-session.jsonl");
        await WriteFixtureAsync(active);
        await WriteFixtureAsync(archived);
        await WriteFixtureAsync(unrelated);
        var resolver = new CodexSourceResolver();

        SourceProbeResult result = await resolver.ProbeAsync(
            codexHome,
            CancellationToken.None);

        SourceInstanceDescriptor instance = Assert.ContainsSingle(result.Instances);
        Assert.AreEqual("codex", instance.AgentId);
        Assert.AreEqual(SourceKind.Jsonl, instance.SourceKind);
        Assert.AreEqual("Codex (Windows)", instance.DisplayName);
        Assert.AreEqual(NormalizeHome(codexHome), instance.RootPath);
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(archived), Path.GetFullPath(active) },
            result.Entities.Select(entity => entity.SourcePath).ToArray());
        Assert.IsTrue(result.Entities.All(
            entity => entity.SourceInstanceId == instance.SourceInstanceId));
        Assert.IsEmpty(result.Diagnostics);
    }

    [TestMethod]
    public async Task ProbeAsync_RepeatedProbeReturnsTheExactStableIdentities()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string active = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-2026-07-16T10-00-thread-a.jsonl");
        await WriteFixtureAsync(active);
        var resolver = new CodexSourceResolver();

        SourceProbeResult first = await resolver.ProbeAsync(
            codexHome,
            CancellationToken.None);
        SourceProbeResult second = await resolver.ProbeAsync(
            Path.GetFullPath(codexHome),
            CancellationToken.None);

        string expectedInstanceId = ExpectedInstanceId(codexHome);
        string expectedEntityId = ExpectedEntityId(active);
        Assert.AreEqual(expectedInstanceId, Assert.ContainsSingle(first.Instances).SourceInstanceId);
        Assert.AreEqual(expectedInstanceId, Assert.ContainsSingle(second.Instances).SourceInstanceId);
        Assert.AreEqual(expectedEntityId, Assert.ContainsSingle(first.Entities).SourceEntityId);
        Assert.AreEqual(expectedEntityId, Assert.ContainsSingle(second.Entities).SourceEntityId);
    }

    [TestMethod]
    public async Task ProbeAsync_ActiveToArchiveMovePreservesEntityIdentity()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        const string fileName = "rollout-2026-07-16T10-00-thread-a.jsonl";
        string active = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            fileName);
        string archived = Path.Combine(codexHome, "archived_sessions", fileName);
        await WriteFixtureAsync(active);
        var resolver = new CodexSourceResolver();

        SourceProbeResult beforeMove = await resolver.ProbeAsync(
            codexHome,
            CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.Move(active, archived);
        SourceProbeResult afterMove = await resolver.ProbeAsync(
            codexHome,
            CancellationToken.None);

        SourceEntityDescriptor beforeEntity = Assert.ContainsSingle(beforeMove.Entities);
        SourceEntityDescriptor afterEntity = Assert.ContainsSingle(afterMove.Entities);
        Assert.AreEqual(beforeEntity.SourceEntityId, afterEntity.SourceEntityId);
        Assert.AreEqual(Path.GetFullPath(archived), afterEntity.SourcePath);
        Assert.AreEqual(
            Assert.ContainsSingle(beforeMove.Instances).SourceInstanceId,
            Assert.ContainsSingle(afterMove.Instances).SourceInstanceId);
    }

    [TestMethod]
    public async Task ProbeAsync_WhenActiveAndArchiveShareIdentity_ActiveWins()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        const string activeName = "rollout-2026-07-16T10-00-thread-a.JSONL";
        const string archivedName = "ROLLOUT-2026-07-16t10-00-THREAD-A.jsonl";
        string active = Path.Combine(codexHome, "sessions", "2026", activeName);
        string archived = Path.Combine(codexHome, "archived_sessions", archivedName);
        await WriteFixtureAsync(active);
        await WriteFixtureAsync(archived);
        var resolver = new CodexSourceResolver();

        SourceProbeResult result = await resolver.ProbeAsync(
            codexHome,
            CancellationToken.None);

        SourceEntityDescriptor entity = Assert.ContainsSingle(result.Entities);
        Assert.AreEqual(Path.GetFullPath(active), entity.SourcePath);
        Assert.AreEqual(ExpectedEntityId(active), entity.SourceEntityId);
        Assert.IsEmpty(result.Diagnostics);
    }

    [TestMethod]
    public async Task ProbeAsync_AcceptsUppercaseJsonlExtension()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string active = Path.Combine(
            codexHome,
            "sessions",
            "rollout-uppercase.JSONL");
        await WriteFixtureAsync(active);
        var resolver = new CodexSourceResolver();

        SourceProbeResult result = await resolver.ProbeAsync(
            codexHome,
            CancellationToken.None);

        Assert.AreEqual(Path.GetFullPath(active), Assert.ContainsSingle(result.Entities).SourcePath);
        Assert.IsEmpty(result.Diagnostics);
    }

    [TestMethod]
    public async Task ProbeAsync_MissingKnownDirectoriesReturnsAnEmptyStableInstance()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        Directory.CreateDirectory(codexHome);
        var resolver = new CodexSourceResolver();

        SourceProbeResult result = await resolver.ProbeAsync(
            codexHome,
            CancellationToken.None);

        SourceInstanceDescriptor instance = Assert.ContainsSingle(result.Instances);
        Assert.AreEqual(ExpectedInstanceId(codexHome), instance.SourceInstanceId);
        Assert.IsEmpty(result.Entities);
        Assert.IsEmpty(result.Diagnostics);
    }

    [TestMethod]
    public async Task ProbeAsync_ObservesCancellationBeforeEnumerating()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        await WriteFixtureAsync(Path.Combine(
            codexHome,
            "sessions",
            "rollout-cancelled.jsonl"));
        var resolver = new CodexSourceResolver();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await resolver.ProbeAsync(codexHome, cancellation.Token));
    }

    [TestMethod]
    public async Task ProbeAsync_InaccessibleDescendantFailsClosedForThatKnownRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows ACL semantics are required for this test.");
        }

        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string accessibleActive = Path.Combine(
            codexHome,
            "sessions",
            "rollout-accessible.jsonl");
        string inaccessibleDirectory = Path.Combine(
            codexHome,
            "sessions",
            "inaccessible");
        string inaccessibleActive = Path.Combine(
            inaccessibleDirectory,
            "rollout-inaccessible.jsonl");
        string archived = Path.Combine(
            codexHome,
            "archived_sessions",
            "rollout-archive.jsonl");
        await WriteFixtureAsync(accessibleActive);
        await WriteFixtureAsync(inaccessibleActive);
        await WriteFixtureAsync(archived);

        var directoryInfo = new DirectoryInfo(inaccessibleDirectory);
        FileSystemAccessRule deniedRule;
        try
        {
            SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                Assert.Inconclusive("The current Windows identity has no security identifier.");
                return;
            }

            DirectorySecurity restrictedSecurity =
                directoryInfo.GetAccessControl(AccessControlSections.Access);
            deniedRule = new FileSystemAccessRule(
                currentUser,
                FileSystemRights.ListDirectory,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Deny);
            restrictedSecurity.AddAccessRule(deniedRule);
            directoryInfo.SetAccessControl(restrictedSecurity);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException or
            SecurityException or
            IdentityNotMappedException)
        {
            Assert.Inconclusive($"The test host cannot apply a directory ACL: {exception.GetType().Name}.");
            return;
        }

        try
        {
            try
            {
                _ = Directory.GetFileSystemEntries(inaccessibleDirectory);
                Assert.Inconclusive("The test host did not enforce the denied directory-listing ACL.");
            }
            catch (UnauthorizedAccessException)
            {
                // The ACL is effective; probe behavior can now be verified.
            }

            var resolver = new CodexSourceResolver();

            SourceProbeResult result = await resolver.ProbeAsync(
                codexHome,
                CancellationToken.None);

            SourceEntityDescriptor entity = Assert.ContainsSingle(result.Entities);
            Assert.AreEqual(Path.GetFullPath(archived), entity.SourcePath);
            CollectorDiagnostic diagnostic = Assert.ContainsSingle(result.Diagnostics);
            Assert.AreEqual("codex.source_root_unavailable", diagnostic.Code);
            Assert.AreEqual(
                "A known Codex source directory could not be inspected.",
                diagnostic.Message);
            Assert.IsFalse(diagnostic.Message.Contains(
                codexHome,
                StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(diagnostic.Message.Contains(
                inaccessibleDirectory,
                StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DirectorySecurity restoredSecurity =
                directoryInfo.GetAccessControl(AccessControlSections.Access);
            restoredSecurity.RemoveAccessRuleSpecific(deniedRule);
            directoryInfo.SetAccessControl(restoredSecurity);
        }
    }

    [TestMethod]
    public async Task ProbeAsync_InaccessibleKnownRootIsNotTreatedAsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows ACL semantics are required for this test.");
        }

        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string sessionsRoot = Path.Combine(codexHome, "sessions");
        string active = Path.Combine(sessionsRoot, "rollout-inaccessible-root.jsonl");
        string archived = Path.Combine(
            codexHome,
            "archived_sessions",
            "rollout-visible-archive.jsonl");
        await WriteFixtureAsync(active);
        await WriteFixtureAsync(archived);

        var directoryInfo = new DirectoryInfo(sessionsRoot);
        FileSystemAccessRule deniedRule;
        try
        {
            SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                Assert.Inconclusive("The current Windows identity has no security identifier.");
                return;
            }

            DirectorySecurity restrictedSecurity =
                directoryInfo.GetAccessControl(AccessControlSections.Access);
            deniedRule = new FileSystemAccessRule(
                currentUser,
                FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Deny);
            restrictedSecurity.AddAccessRule(deniedRule);
            directoryInfo.SetAccessControl(restrictedSecurity);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException or
            SecurityException or
            IdentityNotMappedException)
        {
            Assert.Inconclusive(
                $"The test host cannot apply a directory ACL: {exception.GetType().Name}.");
            return;
        }

        try
        {
            if (Directory.Exists(sessionsRoot))
            {
                try
                {
                    _ = Directory.GetFileSystemEntries(sessionsRoot);
                    Assert.Inconclusive(
                        "The test host did not enforce the denied root-directory ACL.");
                }
                catch (UnauthorizedAccessException)
                {
                    // The root is inaccessible even if Directory.Exists remains true.
                }
            }

            SourceProbeResult result = await new CodexSourceResolver().ProbeAsync(
                codexHome,
                CancellationToken.None);

            Assert.AreEqual(
                Path.GetFullPath(archived),
                Assert.ContainsSingle(result.Entities).SourcePath);
            CollectorDiagnostic diagnostic = Assert.ContainsSingle(result.Diagnostics);
            Assert.AreEqual("codex.source_root_unavailable", diagnostic.Code);
            Assert.AreEqual(
                "A known Codex source directory could not be inspected.",
                diagnostic.Message);
        }
        finally
        {
            DirectorySecurity restoredSecurity =
                directoryInfo.GetAccessControl(AccessControlSections.Access);
            restoredSecurity.RemoveAccessRuleSpecific(deniedRule);
            directoryInfo.SetAccessControl(restoredSecurity);
        }
    }

    [TestMethod]
    public async Task ProbeAsync_DoesNotFollowDirectoryReparsePointsWhenSupported()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string outsideRoot = directory.File("outside");
        string outsideFile = Path.Combine(outsideRoot, "rollout-outside.jsonl");
        await WriteFixtureAsync(outsideFile);
        string sessionsRoot = Path.Combine(codexHome, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        string linkPath = Path.Combine(sessionsRoot, "linked-outside");

        try
        {
            Directory.CreateSymbolicLink(linkPath, outsideRoot);
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // 创建符号链接取决于宿主权限；生产行为仍由 EnumerationOptions.AttributesToSkip 约束。
            return;
        }

        var resolver = new CodexSourceResolver();

        SourceProbeResult result = await resolver.ProbeAsync(
            codexHome,
            CancellationToken.None);

        Assert.IsEmpty(result.Entities);
        CollectorDiagnostic diagnostic = Assert.ContainsSingle(result.Diagnostics);
        Assert.AreEqual("codex.source_descendant_reparse_point", diagnostic.Code);
        Assert.AreEqual(
            "A known Codex source directory contains a reparse point and was not fully inspected.",
            diagnostic.Message);
    }

    [TestMethod]
    public async Task ProbeAsync_SkipsReparsePointRootAndContinuesWithArchiveWhenSupported()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string outsideRoot = directory.File("outside");
        await WriteFixtureAsync(Path.Combine(outsideRoot, "rollout-outside.jsonl"));
        string archive = Path.Combine(
            codexHome,
            "archived_sessions",
            "rollout-archive.jsonl");
        await WriteFixtureAsync(archive);
        Directory.CreateDirectory(codexHome);
        string sessionsRoot = Path.Combine(codexHome, "sessions");

        try
        {
            Directory.CreateSymbolicLink(sessionsRoot, outsideRoot);
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // 创建符号链接依赖宿主权限；无法创建时安全跳过此平台相关验证。
            return;
        }

        var resolver = new CodexSourceResolver();

        SourceProbeResult result = await resolver.ProbeAsync(
            codexHome,
            CancellationToken.None);

        Assert.AreEqual(Path.GetFullPath(archive), Assert.ContainsSingle(result.Entities).SourcePath);
        CollectorDiagnostic diagnostic = Assert.ContainsSingle(result.Diagnostics);
        Assert.AreEqual("codex.source_root_reparse_point", diagnostic.Code);
        Assert.AreEqual(
            "A known Codex source directory is a reparse point and was skipped.",
            diagnostic.Message);
        Assert.IsFalse(diagnostic.Message.Contains(outsideRoot, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void NormalizePath_PreservesRootTrailingSeparator()
    {
        string root = Path.GetPathRoot(Path.GetFullPath("."))!;

        string normalized = CodexSourceIdentity.NormalizePath(root);

        Assert.AreEqual(root, normalized);
        Assert.IsTrue(
            normalized.EndsWith(Path.DirectorySeparatorChar)
            || normalized.EndsWith(Path.AltDirectorySeparatorChar));
    }

    [TestMethod]
    public void NormalizePath_PreservesUncRootTrailingSeparator()
    {
        const string uncRoot = @"\\server\share";

        string normalized = CodexSourceIdentity.NormalizePath(uncRoot);

        Assert.AreEqual(uncRoot + Path.DirectorySeparatorChar, normalized);
    }

    private static async Task WriteFixtureAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{}\n", Utf8WithoutBom);
    }

    private static string NormalizeHome(string codexHome) => Path.GetFullPath(codexHome)
        .TrimEnd(Path.DirectorySeparatorChar);

    private static string ExpectedInstanceId(string codexHome)
    {
        string normalized = NormalizeHome(codexHome).ToUpperInvariant();
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $"codex:windows:{hash.ToLowerInvariant()}";
    }

    private static string ExpectedEntityId(string filePath)
    {
        string name = Path.GetFileName(filePath).ToLowerInvariant();
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..24];
        return $"codex:rollout:{hash.ToLowerInvariant()}";
    }
}
