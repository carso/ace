using System.Diagnostics;
using Ace.Core.Configuration;
using Ace.Core.Discovery;
using Ace.Core.Platform;

namespace Ace.Core.Tests.Discovery;

public sealed class RepositoryDiscoveryTests
{
    private static RepositoryDiscovery CreateDiscovery(AceOptions? options = null)
        => new(new FileSystemService(), options ?? new AceOptions());

    [Fact]
    public void Discover_SampleRepo_DetectsLanguagesBuildSystemsAndTestProjects()
    {
        var result = CreateDiscovery().Discover(TestPaths.SampleRepo);
        var context = result.Context;

        Assert.Equal(TestPaths.SampleRepo, context.RepositoryPath);
        Assert.Equal(18, context.FileCount);
        Assert.Equal(11, context.SourceFileCount); // 8 source + 3 test .cs files
        Assert.Equal(4, context.ProjectCount);

        Assert.Contains("C#", context.Languages);
        Assert.Contains("MSBuild", context.BuildSystems);
        Assert.Contains("NuGet", context.DependencySystems);
        Assert.Contains("ASP.NET Core", context.Frameworks);

        var testProject = Assert.Single(context.TestProjects);
        Assert.Equal("Customer.Services.Tests", testProject);
    }

    [Fact]
    public void Discover_SampleRepo_ClassifiesFilesIntoBuckets()
    {
        var result = CreateDiscovery().Discover(TestPaths.SampleRepo);

        Assert.Single(result.ByCategory(FileCategory.Solution));
        Assert.Equal(4, result.ByCategory(FileCategory.Project).Count);
        Assert.Equal(8, result.ByCategory(FileCategory.Source).Count);
        Assert.Equal(3, result.ByCategory(FileCategory.Test).Count);
        Assert.Single(result.ByCategory(FileCategory.Config)); // appsettings.json

        Assert.Contains(result.Files, f => f.RelativePath == "tests/Customer.Services.Tests/CustomerServiceTests.cs" && f.Category == FileCategory.Test);
        Assert.Contains(result.Files, f => f.RelativePath == "src/Customer.Services/CustomerService.cs" && f.Category == FileCategory.Source);
        Assert.Contains(result.Files, f => f.RelativePath == ".gitignore" && f.Category == FileCategory.Other);
    }

    [Fact]
    public void Discover_PrunesExcludedDirectoriesAndSensitiveFiles()
    {
        var temp = TestPaths.CreateTempCopyOfSampleRepo();
        try
        {
            // Pollute the copy with things discovery must never return.
            Directory.CreateDirectory(Path.Combine(temp, "bin", "Debug"));
            Directory.CreateDirectory(Path.Combine(temp, "obj"));
            Directory.CreateDirectory(Path.Combine(temp, ".git"));
            Directory.CreateDirectory(Path.Combine(temp, ".ace"));
            Directory.CreateDirectory(Path.Combine(temp, "node_modules", "some-package"));
            File.WriteAllText(Path.Combine(temp, "bin", "Debug", "Output.dll.txt"), "build output");
            File.WriteAllText(Path.Combine(temp, "obj", "generated.cs"), "class X {}");
            File.WriteAllText(Path.Combine(temp, ".git", "HEAD"), "ref: refs/heads/main");
            File.WriteAllText(Path.Combine(temp, ".ace", "index.json"), "{}");
            File.WriteAllText(Path.Combine(temp, "node_modules", "some-package", "index.js"), "// vendored");
            File.WriteAllText(Path.Combine(temp, ".env"), "SECRET=1");
            File.WriteAllText(Path.Combine(temp, "src", "private.key"), "key material");

            var result = CreateDiscovery().Discover(temp);

            Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith("bin/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith("obj/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith(".ace/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Files, f => Path.GetFileName(f.RelativePath) == ".env");
            Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith(".key", StringComparison.OrdinalIgnoreCase));

            // The pristine SampleRepo content is still there.
            Assert.Equal(18, result.Context.FileCount);
        }
        finally
        {
            TestPaths.DeleteDirectoryQuietly(temp);
        }
    }

    [Fact]
    public void Discover_UsesCustomExclusionPatterns()
    {
        var temp = TestPaths.CreateTempCopyOfSampleRepo();
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "docs-output"));
            File.WriteAllText(Path.Combine(temp, "docs-output", "notes.md"), "generated docs");

            var options = new AceOptions();
            options.ExclusionPatterns.Add("docs-output");
            var result = CreateDiscovery(options).Discover(temp);

            Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith("docs-output/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TestPaths.DeleteDirectoryQuietly(temp);
        }
    }

    [Fact]
    public void Discover_MissingRoot_Throws()
    {
        var discovery = CreateDiscovery();
        Assert.Throws<DirectoryNotFoundException>(() => discovery.Discover(Path.Combine(TestPaths.RepoRoot, "does-not-exist")));
    }

    [Fact]
    public void Discover_DoesNotIndexFilesThroughJunctionEscapingRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // mklink /J is a Windows mechanism.
        }

        var repo = TestPaths.CreateTempCopyOfSampleRepo();
        var outsideBase = Path.Combine(Path.GetTempPath(), "ace-junction-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(outsideBase, "target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "EvilLeak.cs"), "public class EvilLeak {}");

        var link = Path.Combine(repo, "linked");
        using (var process = Process.Start(new ProcessStartInfo
               {
                   FileName = "cmd.exe",
                   Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
                   UseShellExecute = false,
                   RedirectStandardOutput = true,
                   RedirectStandardError = true,
               }))
        {
            Assert.NotNull(process);
            process!.WaitForExit(30_000);
            Assert.True(process.ExitCode == 0 && Directory.Exists(link), "mklink /J failed; junction not created");
        }

        try
        {
            var result = CreateDiscovery().Discover(repo);

            // Discovery must never index through a reparse point escaping the root (SR-002/005).
            Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith("linked/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("EvilLeak", StringComparison.OrdinalIgnoreCase));

            // The pristine SampleRepo content is still fully discovered.
            Assert.Equal(18, result.Context.FileCount);
        }
        finally
        {
            Directory.Delete(link); // removes the junction, not its target
            TestPaths.DeleteDirectoryQuietly(outsideBase);
            TestPaths.DeleteDirectoryQuietly(repo);
        }
    }
}
