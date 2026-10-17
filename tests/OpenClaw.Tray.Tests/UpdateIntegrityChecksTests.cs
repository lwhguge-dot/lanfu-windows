using System.IO;
using System.Threading.Tasks;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class UpdateIntegrityChecksTests
{
    private const string FileName = "LanFu-Setup-x64.exe";
    private static readonly string HashA = new string('a', 64);
    private static readonly string HashB = new string('b', 64);

    [Fact]
    public void ParseExpectedHash_FindsMatchingEntry()
    {
        var text = HashA + "  other.exe\n" + HashB + "  " + FileName + "\n";
        Assert.Equal(HashB, UpdateIntegrityChecks.ParseExpectedHash(text, FileName));
    }

    [Fact]
    public void ParseExpectedHash_ToleratesBinaryMarkerAndCrLf()
    {
        var text = HashA + " *" + FileName + "\r\n";
        Assert.Equal(HashA, UpdateIntegrityChecks.ParseExpectedHash(text, FileName));
    }

    [Fact]
    public void ParseExpectedHash_ReturnsNullWhenMissing()
    {
        var text = HashA + "  other.exe\n";
        Assert.Null(UpdateIntegrityChecks.ParseExpectedHash(text, FileName));
        Assert.Null(UpdateIntegrityChecks.ParseExpectedHash("", FileName));
    }

    [Fact]
    public void Evaluate_SizeMismatch_WinsOverHash()
    {
        var result = UpdateIntegrityChecks.Evaluate(300, 299, HashA, HashA);
        Assert.Equal(UpdateIntegrityChecks.Verdict.SizeMismatch, result.Verdict);
    }

    [Fact]
    public void Evaluate_MatchingHashes_Verified()
    {
        var result = UpdateIntegrityChecks.Evaluate(300, 300, HashA, HashA);
        Assert.Equal(UpdateIntegrityChecks.Verdict.Verified, result.Verdict);
    }

    [Fact]
    public void Evaluate_DifferentHashes_ChecksumMismatch()
    {
        var result = UpdateIntegrityChecks.Evaluate(300, 300, HashA, HashB);
        Assert.Equal(UpdateIntegrityChecks.Verdict.ChecksumMismatch, result.Verdict);
        Assert.Equal(HashA, result.Expected);
        Assert.Equal(HashB, result.Actual);
    }

    [Fact]
    public void Evaluate_NoExpectedHash_ChecksumUnavailable()
    {
        var result = UpdateIntegrityChecks.Evaluate(300, 300, null, null);
        Assert.Equal(UpdateIntegrityChecks.Verdict.ChecksumUnavailable, result.Verdict);
    }

    [Fact]
    public void Evaluate_MissingActualHash_Error()
    {
        var result = UpdateIntegrityChecks.Evaluate(300, 300, HashA, null);
        Assert.Equal(UpdateIntegrityChecks.Verdict.Error, result.Verdict);
    }

    [Fact]
    public async Task ComputeSha256Async_KnownValue()
    {
        var path = Path.Combine(Path.GetTempPath(), "lanfu-integrity-" + System.Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllTextAsync(path, "hello");
            var hash = await UpdateIntegrityChecks.ComputeSha256Async(path);
            Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
