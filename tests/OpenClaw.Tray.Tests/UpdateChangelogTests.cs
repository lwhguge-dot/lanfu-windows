using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class UpdateChangelogTests
{
    [Fact]
    public void StripsUpdatumMetadataHeaderAndRule()
    {
        var input = "## LanFu 0.4.23\n\n> Release date: 2026/10/18 1:55:59 +00:00\n> Release diff: 1\n\n- 第一条\n- 第二条\n\n---\n";
        Assert.Equal("- 第一条\n- 第二条", UpdateChangelog.Normalize(input));
    }

    [Fact]
    public void KeepsPlainBodyUnchanged()
    {
        Assert.Equal("- 第一条\n- 第二条", UpdateChangelog.Normalize("- 第一条\n- 第二条"));
    }

    [Fact]
    public void KeepsNonMetadataBlockquotes()
    {
        Assert.Equal("> 注意：x\n- a", UpdateChangelog.Normalize("> 注意：x\n- a"));
    }

    [Fact]
    public void HandlesNullAndWhitespace()
    {
        Assert.Equal(string.Empty, UpdateChangelog.Normalize(null));
        Assert.Equal(string.Empty, UpdateChangelog.Normalize("   \n\n"));
    }

    [Fact]
    public void CollapsesBlankRunsAndTrims()
    {
        Assert.Equal("a\n\nb", UpdateChangelog.Normalize("\n\na\n\n\n\nb\n\n"));
    }

    [Fact]
    public void HandlesCrLf()
    {
        var input = "## LanFu 0.4.23\r\n\r\n> Release date: x\r\n\r\n- a\r\n";
        Assert.Equal("- a", UpdateChangelog.Normalize(input));
    }
}
