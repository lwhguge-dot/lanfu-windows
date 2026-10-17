using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Updatum;

namespace OpenClawTray.Services;

/// <summary>
/// Verifies a downloaded installer before handing it to Inno Setup.
/// <para>
/// Background: a partially downloaded or corrupted installer can still be
/// unpacked by the installer, which then overwrites the live install with a
/// mix of old and new files (observed on 0.4.8: the gateway refused to start
/// and the app had to be reinstalled by hand). Updatum reports a completed
/// download in that case, so the shell must check the payload itself.
/// </para>
/// <para>
/// The release publishes a SHA256SUMS.txt asset next to the installer; this
/// verifier compares the on-disk file against it and against the asset size
/// reported by GitHub. A missing sums file degrades to a size-only check (the
/// install proceeds, with a warning in the log) so older releases stay
/// updatable.
/// </para>
/// </summary>
internal static class UpdateIntegrityVerifier
{
    internal const string ChecksumAssetName = "SHA256SUMS.txt";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly Regex ChecksumLine =
        new(@"^([0-9a-fA-F]{64})\s+\*?(?<name>\S+)\s*$", RegexOptions.Compiled);

    internal enum Status
    {
        Verified,
        SizeMismatch,
        ChecksumMismatch,
        ChecksumUnavailable,
        Error,
    }

    internal readonly record struct Result(
        Status Status,
        string? Expected,
        string? Actual,
        string? Detail);

    internal static async Task<Result> VerifyAsync(
        UpdatumDownloadedAsset asset,
        CancellationToken cancellationToken = default)
    {
        if (asset is null)
            return new(Status.Error, null, null, "downloaded asset is null");

        var filePath = asset.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return new(Status.Error, null, null, "downloaded file is missing");

        // 1) Size check first: it is cheap and catches a truncated download even
        // when the release carries no checksum asset.
        long expectedSize = asset.ReleaseAsset?.Size ?? 0;
        long actualSize = new FileInfo(filePath).Length;
        if (expectedSize > 0 && actualSize != expectedSize)
        {
            return new(
                Status.SizeMismatch,
                expectedSize.ToString(),
                actualSize.ToString(),
                "downloaded file size does not match the release asset size");
        }

        // 2) SHA256 against the release's published SHA256SUMS.txt.
        var releaseAssets = asset.Release?.Assets;
        var checksumsAsset = releaseAssets?
            .FirstOrDefault(a => string.Equals(a.Name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase));
        var checksumsUrl = checksumsAsset?.BrowserDownloadUrl;
        if (string.IsNullOrWhiteSpace(checksumsUrl))
        {
            return new(
                Status.ChecksumUnavailable,
                null,
                null,
                "release has no " + ChecksumAssetName + " asset");
        }

        string checksumsText;
        try
        {
            checksumsText = await Http.GetStringAsync(checksumsUrl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(Status.ChecksumUnavailable, null, null, "checksum download failed: " + ex.Message);
        }

        var assetName = asset.ReleaseAsset?.Name;
        var fileName = Path.GetFileName(
            string.IsNullOrWhiteSpace(assetName) ? filePath : assetName);
        string? expectedHash = null;
        foreach (var line in checksumsText.Split('\n'))
        {
            var match = ChecksumLine.Match(line.Trim());
            if (match.Success &&
                string.Equals(match.Groups["name"].Value, fileName, StringComparison.OrdinalIgnoreCase))
            {
                expectedHash = match.Groups[1].Value.ToLowerInvariant();
                break;
            }
        }

        if (expectedHash is null)
        {
            return new(
                Status.ChecksumUnavailable,
                null,
                null,
                "no checksum entry for " + fileName);
        }

        string actualHash;
        try
        {
            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            actualHash = Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new(Status.Error, expectedHash, null, "hashing failed: " + ex.Message);
        }

        return string.Equals(expectedHash, actualHash, StringComparison.Ordinal)
            ? new(Status.Verified, expectedHash, actualHash, null)
            : new(Status.ChecksumMismatch, expectedHash, actualHash, "sha256 does not match " + ChecksumAssetName);
    }
}
