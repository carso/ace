using Ace.Core.Security;

namespace Ace.Core.Tests.Security;

/// <summary>Tests for sensitive-file pattern matching (SR-006).</summary>
public class SensitiveFileRulesTests
{
    private static readonly SensitiveFileRules Defaults = new(
    [
        ".env",
        "*.key",
        "*.pem",
        "secrets.json",
        "credentials.json",
    ]);

    [Theory]
    [InlineData(@"c:\repo\.env")]
    [InlineData(@"c:\repo\config\.env")]
    [InlineData(@"c:\repo\api.key")]
    [InlineData(@"c:\repo\certs\server.pem")]
    [InlineData(@"c:\repo\secrets.json")]
    [InlineData(@"c:\repo\deploy\credentials.json")]
    public void DefaultPatterns_MatchSensitiveFiles(string path)
    {
        Assert.True(Defaults.IsSensitive(path));
    }

    [Theory]
    [InlineData(@"c:\repo\src\Service.cs")]
    [InlineData(@"c:\repo\appsettings.json")]
    [InlineData(@"c:\repo\env.txt")]
    [InlineData(@"c:\repo\monkey.txt")]
    [InlineData(@"c:\repo\keynote.md")]
    public void DefaultPatterns_DoNotMatchRegularFiles(string path)
    {
        Assert.False(Defaults.IsSensitive(path));
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        Assert.True(Defaults.IsSensitive(@"c:\repo\API.KEY"));
        Assert.True(Defaults.IsSensitive(@"c:\repo\.ENV"));
        Assert.True(Defaults.IsSensitive(@"c:\repo\Secrets.JSON"));
    }

    [Fact]
    public void LiteralPattern_MatchesNestedPathSegments()
    {
        var rules = new SensitiveFileRules([".env"]);

        Assert.True(rules.IsSensitive("repo/.env"));
        Assert.True(rules.IsSensitive(@"repo\deep\.env"));
        Assert.False(rules.IsSensitive("repo/.env.example"));
    }

    [Fact]
    public void WildcardPattern_MatchesFileNamesAnywhere()
    {
        var rules = new SensitiveFileRules(["*.pem"]);

        Assert.True(rules.IsSensitive(@"a\b\c\cert.pem"));
        Assert.False(rules.IsSensitive(@"a\b\pem.xml"));
    }

    [Fact]
    public void QuestionMarkWildcard_MatchesSingleCharacter()
    {
        var rules = new SensitiveFileRules(["secret?.txt"]);

        Assert.True(rules.IsSensitive("secret1.txt"));
        Assert.False(rules.IsSensitive("secret12.txt"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputs_AreNotSensitive(string? path)
    {
        Assert.False(Defaults.IsSensitive(path));
    }

    [Fact]
    public void EmptyPatternSet_NeverMatches()
    {
        var rules = new SensitiveFileRules([]);

        Assert.False(rules.IsSensitive(".env"));
        Assert.Empty(rules.Patterns);
    }
}
