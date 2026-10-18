using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OpenClawTray.Services;

/// <summary>
/// Updatum's <c>GetChangelog</c> prepends its own English metadata block:
/// <code>
/// ## LanFu 0.4.23
/// > Release date: 2026/10/18 1:55:59 +00:00
/// > Release diff: 1
/// > 
/// - 本次更新说明…
/// ---
/// </code>
/// The update dialog already shows the release tag and a localized
/// 「更新内容」label, so the metadata block is redundant English noise.
/// Strip it and keep only the per-version notes that the release body carries.
/// </summary>
internal static class UpdateChangelog
{
    private static readonly Regex ReleaseHeading =
        new(@"^\s*#{1,6}\s*LanFu\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MetadataLine =
        new(@"^\s*>\s*Release\s+(date|diff)\s*:.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HorizontalRule =
        new(@"^\s*-{3,}\s*$", RegexOptions.Compiled);

    internal static string Normalize(string? changelog)
    {
        if (string.IsNullOrWhiteSpace(changelog))
            return string.Empty;

        var normalized = changelog!.Replace("\r\n", "\n").Replace('\r', '\n');
        var kept = new List<string>();
        var skippedHeading = false;
        foreach (var line in normalized.Split('\n'))
        {
            if (!skippedHeading && ReleaseHeading.IsMatch(line))
            {
                skippedHeading = true;
                continue;
            }
            if (MetadataLine.IsMatch(line) || HorizontalRule.IsMatch(line))
                continue;
            kept.Add(line);
        }

        // Trim leading/trailing blanks and collapse blank runs.
        var output = new List<string>(kept.Count);
        var previousBlank = true;
        foreach (var line in kept)
        {
            var blank = string.IsNullOrWhiteSpace(line);
            if (blank && previousBlank)
                continue;
            output.Add(blank ? string.Empty : line.TrimEnd());
            previousBlank = blank;
        }
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
            output.RemoveAt(output.Count - 1);
        return string.Join("\n", output).Trim();
    }
}
