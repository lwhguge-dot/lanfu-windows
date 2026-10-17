using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Updatum;

namespace OpenClawTray.Services;

/// <summary>
/// Verifies a downloaded installer before handing it to Inno Setup.
/// <para>
/// Background: a partially downloaded or corrupted installer can still be
/// unpacked by the installer, which then overwrites the live install with a
/// mix of old and new files (0.4.8 shipped a mixed install this way). Updatum
/// reports a completed download in that case, so the shell must check the
/// payload itself.
/// </para>
/// <para>
/// The release publishes a SHA256SUMS.txt asset next to the installer; this
/// verifier compares the on-disk file against it and against the asset size
/// reported by GitHub. A missing sums file degrades to a size-only check (the
/// install proceeds, with a warning in the log) so older releases stay
/// updatable. The decision table itself lives in
/// <see cref="UpdateIntegrityChecks"/> and is unit-tested there.
/// </para>
/// </summary>
internal static class UpdateIntegrityVerifier
{
    internal const string ChecksumAssetName = "SHA256SUMS.txt";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal static async Task<UpdateIntegrityChecks.Result> VerifyAsync(
        UpdatumDownloadedAsset asset,
        CancellationToken cancellationToken = default)
    {
        if (asset is null)
            return new(UpdateIntegrityChecks.Verdict.Error, null, null, "downloaded asset is null");

        var filePath = asset.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return new(UpdateIntegrityChecks.Verdict.Error, null, null, "downloaded file is missing");

        long expectedSize = asset.ReleaseAsset?.Size ?? 0;
        long actualSize = new FileInfo(filePath).Length;

        // Fetch the release's checksums; a failure here only loses the hash
        // check (Evaluate still enforces the size and reports Unavailable).
        string? checksumsText = null;
        var releaseAssets = asset.Release?.Assets;
        var checksumsAsset = releaseAssets?
            .FirstOrDefault(a => string.Equals(a.Name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase));
        var checksumsUrl = checksumsAsset?.BrowserDownloadUrl;
        if (!string.IsNullOrWhiteSpace(checksumsUrl))
        {
            try
            {
                checksumsText = await Http.GetStringAsync(checksumsUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Size-only fallback; logged by the caller through the result detail.
            }
        }

        var fileName = Path.GetFileName(
            string.IsNullOrWhiteSpace(asset.ReleaseAsset?.Name)
                ? filePath
                : asset.ReleaseAsset!.Name);
        var expectedHash = checksumsText is null
            ? null
            : UpdateIntegrityChecks.ParseExpectedHash(checksumsText, fileName);

        string? actualHash = null;
        if (expectedHash is not null)
        {
            try
            {
                actualHash = await UpdateIntegrityChecks.ComputeSha256Async(filePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                return new(UpdateIntegrityChecks.Verdict.Error, expectedHash, null, "hashing failed: " + ex.Message);
            }
        }

        return UpdateIntegrityChecks.Evaluate(expectedSize, actualSize, expectedHash, actualHash);
    }
}
