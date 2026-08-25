using System.Diagnostics;
using Ace.Core.Security;

namespace Ace.Core.Tests.Security;

/// <summary>Adversarial tests for the path containment guard (SR-002/004/005).</summary>
public class PathGuardTests
{
    private static string TempRoot()
        => Path.Combine(Path.GetTempPath(), "ace-pathguard-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates a directory junction on Windows (mklink /J needs no elevation).</summary>
    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        Assert.NotNull(process);
        process!.WaitForExit(30_000);
        Assert.True(process.ExitCode == 0 && Directory.Exists(linkPath), "mklink /J failed; junction not created");
    }

    [Fact]
    public void RelativeCandidate_IsResolvedAgainstRoot()
    {
        var root = TempRoot();

        var result = PathGuard.EnsureWithinRoot(root, @"src\Service.cs");

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "Service.cs")), result);
    }

    [Fact]
    public void NestedValidPath_IsAllowed()
    {
        var root = TempRoot();

        var result = PathGuard.EnsureWithinRoot(root, @"src\a\b\c\deep.cs");

        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateEqualToRoot_IsAllowed()
    {
        var root = TempRoot();

        var result = PathGuard.EnsureWithinRoot(root, root);

        Assert.Equal(Path.GetFullPath(root), result);
    }

    [Fact]
    public void DotDotTraversal_IsRejected()
    {
        var root = TempRoot();

        Assert.Throws<PathSecurityException>(() => PathGuard.EnsureWithinRoot(root, @"..\outside.txt"));
    }

    [Fact]
    public void DotDotNestedInsideValidPath_IsRejectedWhenItEscapes()
    {
        var root = TempRoot();

        Assert.Throws<PathSecurityException>(() => PathGuard.EnsureWithinRoot(root, @"src\..\..\outside.txt"));
    }

    [Fact]
    public void DotDotThatStaysInsideRoot_IsAllowed()
    {
        var root = TempRoot();

        var result = PathGuard.EnsureWithinRoot(root, @"src\..\other.cs");

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "other.cs")), result);
    }

    [Fact]
    public void AbsolutePathOutsideRoot_IsRejected()
    {
        var root = TempRoot();
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else.txt");

        Assert.Throws<PathSecurityException>(() => PathGuard.EnsureWithinRoot(root, outside));
    }

    [Fact]
    public void SiblingDirectoryWithSamePrefix_IsRejected()
    {
        // Classic prefix trap: root "...\Repo" must not contain "...\RepoEvil".
        var rootBase = TempRoot();
        var root = Path.Combine(rootBase, "Repo");
        var evil = Path.Combine(rootBase, "RepoEvil", "file.cs");

        Assert.Throws<PathSecurityException>(() => PathGuard.EnsureWithinRoot(root, evil));
    }

    [Fact]
    public void MixedCaseInsideRoot_IsAllowedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Case-insensitivity is a Windows file system property.
        }

        var rootBase = TempRoot();
        var root = Path.Combine(rootBase, "Repo");

        var result = PathGuard.EnsureWithinRoot(root, rootBase.ToLowerInvariant() + @"\REPO\Src\File.cs");

        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UncCandidate_IsRejectedForLocalRoot()
    {
        var root = TempRoot();

        Assert.Throws<PathSecurityException>(() => PathGuard.EnsureWithinRoot(root, @"\\attacker\share\file.txt"));
    }

    [Fact]
    public void DeviceStyleTraversal_IsRejected()
    {
        var root = TempRoot();

        Assert.Throws<PathSecurityException>(() => PathGuard.EnsureWithinRoot(root, @"src\..\.."));
    }

    [Fact]
    public void NullOrWhitespaceInputs_Throw()
    {
        Assert.ThrowsAny<ArgumentException>(() => PathGuard.EnsureWithinRoot("", "x"));
        Assert.ThrowsAny<ArgumentException>(() => PathGuard.EnsureWithinRoot("root", " "));
    }

    [Fact]
    public void NullByteInjection_IsRejected()
    {
        var root = TempRoot();

        Assert.Throws<PathSecurityException>(() => PathGuard.EnsureWithinRoot(root, "src\0evil.cs"));
    }

    [Fact]
    public void JunctionEscapingRoot_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // mklink /J is a Windows mechanism.
        }

        var baseDir = TempRoot();
        var root = Path.Combine(baseDir, "repo");
        var outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.cs"), "// outside the root");
        CreateJunction(Path.Combine(root, "link"), outside);

        try
        {
            // Logically inside the root, physically outside: the link target check must reject it.
            Assert.Throws<PathSecurityException>(() =>
                PathGuard.EnsureWithinRoot(root, Path.Combine(root, "link", "secret.cs")));
            Assert.Throws<PathSecurityException>(() =>
                PathGuard.EnsureWithinRoot(root, Path.Combine(root, "link")));
        }
        finally
        {
            Directory.Delete(Path.Combine(root, "link")); // removes the junction, not the target
        }
    }

    [Fact]
    public void JunctionStayingInsideRoot_IsAllowed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TempRoot();
        Directory.CreateDirectory(Path.Combine(root, "real"));
        CreateJunction(Path.Combine(root, "link"), Path.Combine(root, "real"));

        try
        {
            var result = PathGuard.EnsureWithinRoot(root, Path.Combine(root, "link", "file.cs"));

            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(Path.Combine(root, "link"));
        }
    }

    [Fact]
    public void VeryLongCandidate_YieldsStructuredPathSecurityRejection()
    {
        var root = TempRoot();
        var candidate = Path.Combine("src", new string('a', 40_000) + ".cs");

        // PathTooLongException during normalization must surface as path_security, not a crash.
        Assert.Throws<PathSecurityException>(() => PathGuard.EnsureWithinRoot(root, candidate));
    }
}
