using System.Security.AccessControl;
using System.Security.Principal;
using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The token is now persistent, so the shape check is what replaces rotation. Without
/// it, any same-user process could pre-create agent-token.txt with a value it knows and
/// have that adopted permanently as a key to scripting the user's logged-in browser.
/// </summary>
public sealed class AgentTokenTests
{
    [Fact]
    public void AFreshlyMintedTokenIsAccepted()
    {
        // Exactly what LoadOrCreateToken produces: 24 random bytes as hex.
        string minted = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        Assert.True(AgentServer.IsMintedTokenShape(minted));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plantedplantedplantedplantedplan")]              // 32 chars: the old check let this through
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDE")]  // 47, one short
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0")] // 49, one long
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEZ")]  // right length, not hex
    [InlineData("0123456789abcdef 123456789ABCDEF0123456789ABCDEF")]  // embedded space
    public void AnythingThisCodeWouldNotHaveMintedIsRejected(string planted)
        => Assert.False(AgentServer.IsMintedTokenShape(planted));

    [Fact]
    public void LowercaseHexIsStillOurOwnShape()
    {
        // Convert.ToHexString emits uppercase, but a hand-copied token should still work.
        Assert.True(AgentServer.IsMintedTokenShape(new string('a', 48)));
    }

    [Fact]
    public void TheOldThirtyTwoCharacterMinimumNoLongerAdmitsShortTokens()
    {
        // Regression guard for the exact hole the reviewer found: length >= 32 was the
        // only validation, so a 40-character planted string was trusted forever.
        Assert.False(AgentServer.IsMintedTokenShape(new string('a', 40)));
    }
}

/// <summary>
/// Persisting the token across launches, against a temp file rather than the real
/// profile. The seam mirrors the one HistoryStore already uses for its own store.
/// </summary>
public sealed class AgentTokenFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gergur-token-{Guid.NewGuid():N}", "agent-token.txt");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch { }
    }

    [Fact]
    public void TheSameTokenComesBackOnEveryLaunch()
    {
        // The whole point of persisting: a rotating secret would break the static
        // header in MCP client config on every restart.
        string first = AgentServer.LoadOrCreateToken(_path);
        string second = AgentServer.LoadOrCreateToken(_path);

        Assert.Equal(first, second);
        Assert.True(AgentServer.IsMintedTokenShape(first));
    }

    [Theory]
    [InlineData("plantedplantedplantedplantedplan")]  // 32 chars: the old check accepted this
    [InlineData("not a token at all")]
    [InlineData("")]
    public void AFileThisCodeCouldNotHaveWrittenIsReplaced(string planted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, planted);

        Assert.Null(AgentServer.TryReadTrustedToken(_path));
        string minted = AgentServer.LoadOrCreateToken(_path);
        Assert.NotEqual(planted, minted);
        Assert.True(AgentServer.IsMintedTokenShape(minted));
    }

    [Fact]
    public void SurroundingWhitespaceIsToleratedRatherThanCausingAReMint()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string token = new string('A', 48);
        File.WriteAllText(_path, $"  {token}\r\n");

        Assert.Equal(token, AgentServer.LoadOrCreateToken(_path));
    }

    [Fact]
    public void ASameUserFileOfTheRightShapeIsAdoptedWhichIsTheKnownLimit()
    {
        // Documenting the honest boundary rather than implying it is closed: the shape
        // and owner checks stop another account, but a process running as this user can
        // create this file itself and have its own value adopted. Against same-user
        // code the agent API is exposed by design, since that code can read the token
        // anyway. Rotation used to bound a stolen token to one session; it no longer does.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string chosen = new string('B', 48);
        File.WriteAllText(_path, chosen);

        Assert.Equal(chosen, AgentServer.LoadOrCreateToken(_path));
    }

    [Fact]
    public void AdoptingATokenReHardensAPermissiveAcl()
    {
        // Regression guard for the ACL fix. A file left by an older build, or one whose
        // single rule grants Everyone, must not be mistaken for "already locked down"
        // just because it is protected and has exactly one rule.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string token = new string('C', 48);
        File.WriteAllText(_path, token);

        var info = new FileInfo(_path);
        var loose = info.GetAccessControl();
        loose.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in loose.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            loose.RemoveAccessRule(rule);
        loose.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(loose);

        Assert.Equal(token, AgentServer.LoadOrCreateToken(_path));

        var rules = new FileInfo(_path).GetAccessControl()
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        Assert.Single(rules);
        Assert.Equal(WindowsIdentity.GetCurrent().User, rules[0].IdentityReference);
    }
}
