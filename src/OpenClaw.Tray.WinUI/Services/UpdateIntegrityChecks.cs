using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Pure, side-effect-free pieces of the installer integrity check. Kept separate
/// from <see cref="UpdateIntegrityVerifier"/> so the size/checksum decision table
/// is unit-testable without network or WinUI dependencies.
/// </summary>
internal static class UpdateIntegrityChecks
{
    private static readonly Regex ChecksumLine =
        new(@"^([0-9a-fA-F]{64})\s+\*?(?<name>\S+)\s*$", RegexOptions.Compiled);

    internal enum Verdict
    {
        Verified,
        SizeMismatch,
        ChecksumMismatch,
        ChecksumUnavailable,
        Error,
    }

    internal readonly record struct Result(
        Verdict Verdict,
        string? Expected,
        string? Actual,
        string? Detail);

    /// <summary>
    /// Finds the expected SHA256 for <paramref name="fileName"/> in a
    /// SHA256SUMS-style file. Accepted line form: 64 hex chars, whitespace, then
    /// the file name (an optional leading '*' binary marker is tolerated).
    /// Returns null when no line matches.
    /// </summary>
    internal static string? ParseExpectedHash(string checksumsText, string fileName)
    {
        if (string.IsNullOrEmpty(checksumsText) || string.IsNullOrWhiteSpace(fileName))
            return null;
        foreach (var rawLine in checksumsText.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var match = ChecksumLine.Match(line);
            if (!match.Success)
                continue;
            if (string.Equals(match.Groups["name"].Value, fileName, StringComparison.OrdinalIgnoreCase))
                return match.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    internal static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Decides whether a downloaded installer is trustworthy. Size is checked
    /// first (works even without a checksum asset); a missing expected hash
    /// degrades to <see cref="Verdict.ChecksumUnavailable"/> so callers can fall
    /// back to size-only behavior instead of blocking legitimate updates.
    /// </summary>
    internal static Result Evaluate(
        long expectedSize,
        long actualSize,
        string? expectedHash,
        string? actualHash)
    {
        if (expectedSize > 0 && actualSize != expectedSize)
        {
            return new(
                Verdict.SizeMismatch,
                expectedSize.ToString(),
                actualSize.ToString(),
                "downloaded file size does not match the release asset size");
        }
        if (string.IsNullOrEmpty(expectedHash))
        {
            return new(
                Verdict.ChecksumUnavailable,
                null,
                null,
                "checksum entry is missing");
        }
        if (string.IsNullOrEmpty(actualHash))
        {
            return new(Verdict.Error, expectedHash, null, "downloaded file hash is missing");
        }
        return string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase)
            ? new(Verdict.Verified, expectedHash, actualHash, null)
            : new(Verdict.ChecksumMismatch, expectedHash, actualHash, "sha256 does not match SHA256SUMS.txt");
    }
}
