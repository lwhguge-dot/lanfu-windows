using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ChannelConfigPatchBuilderTests
{
    /// <summary>
    /// Mirror production usage: callers always pass a Clone()'d element so
    /// the JsonDocument's lifetime doesn't matter. The earlier form
    /// (JsonDocument.Parse(raw).RootElement) left the document undisposed,
    /// which worked only because BuildPatch internally clones — fragile if
    /// internals ever change. Hanselman review LOW-9.
    /// </summary>
    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static List<(string, object)> Updates(params (string, object)[] vs) => vs.ToList();

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public void BuildPatch_FreshChannel_CreatesChannelBlock_AndSetsEnabled()
    {
        var existing = Json("""{ "agents": [] }""");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "feishu",
            Updates(("channels.feishu.appId", "cli_abc123")));

        Assert.Null(result.BlockedReason);
        Assert.NotNull(result.Patch);
        var patch = result.Patch!.Value;
        Assert.Equal("cli_abc123", patch.GetProperty("channels").GetProperty("feishu").GetProperty("appId").GetString());
        Assert.True(patch.GetProperty("channels").GetProperty("feishu").GetProperty("enabled").GetBoolean());
        // Existing top-level keys are preserved verbatim.
        Assert.Equal(JsonValueKind.Array, patch.GetProperty("agents").ValueKind);
    }

    [Fact]
    public void BuildPatch_UpdatesTargetChannel_LeavesOtherChannelsAlone()
    {
        var existing = Json("""
            {
              "channels": {
                "wecom":   { "secret": "wecom-secret", "enabled": true },
                "feishu": { "appId": "old-app-id", "enabled": false }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "feishu",
            Updates(("channels.feishu.appId", "new-app-id")));

        Assert.Null(result.BlockedReason);
        var patch = result.Patch!.Value;
        var channels = patch.GetProperty("channels");
        Assert.Equal("new-app-id", channels.GetProperty("feishu").GetProperty("appId").GetString());
        Assert.True(channels.GetProperty("feishu").GetProperty("enabled").GetBoolean()); // forced to true
        Assert.Equal("wecom-secret",
            channels.GetProperty("wecom").GetProperty("secret").GetString());
        Assert.True(channels.GetProperty("wecom").GetProperty("enabled").GetBoolean()); // untouched
    }

    [Fact]
    public void BuildPatch_MultilinePath_IsSplitIntoTrimmedArray()
    {
        var existing = Json("""{ "channels": {} }""");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "nostr",
            Updates(
                ("channels.nostr.nsec", "nsec1abc"),
                ("channels.nostr.relays", "  wss://relay.damus.io \n\n wss://relay.nostr.band ")),
            multilineDotPaths: new HashSet<string> { "channels.nostr.relays" });

        Assert.Null(result.BlockedReason);
        var relays = result.Patch!.Value.GetProperty("channels").GetProperty("nostr").GetProperty("relays");
        Assert.Equal(JsonValueKind.Array, relays.ValueKind);
        var arr = relays.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "wss://relay.damus.io", "wss://relay.nostr.band" }, arr);
    }

    [Fact]
    public void BuildPatch_EnabledField_IsBoolNotString()
    {
        var existing = Json("{}");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "feishu",
            Updates(("channels.feishu.appId", "cli_abc123")));

        var enabled = result.Patch!.Value.GetProperty("channels").GetProperty("feishu").GetProperty("enabled");
        Assert.Equal(JsonValueKind.True, enabled.ValueKind);
    }

    [Fact]
    public void BuildPatch_EmptyCachedConfig_StartsFresh()
    {
        var empty = Json("{}");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            empty, "wecom",
            Updates(("channels.wecom.secret", "wecom-secret")));

        Assert.Null(result.BlockedReason);
        var channels = result.Patch!.Value.GetProperty("channels");
        Assert.Equal("wecom-secret",
            channels.GetProperty("wecom").GetProperty("secret").GetString());
    }

    [Fact]
    public void BuildPatch_NonObjectCachedConfig_StartsFresh()
    {
        // Null root, array root, etc. — be tolerant.
        var notObject = Json("null");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            notObject, "feishu",
            Updates(("channels.feishu.appId", "cli_abc")));

        Assert.NotNull(result.Patch);
        Assert.Equal("cli_abc", result.Patch!.Value.GetProperty("channels").GetProperty("feishu").GetProperty("appId").GetString());
    }

    [Fact]
    public void BuildPatch_DeepPath_CreatesIntermediateObjects()
    {
        var existing = Json("{}");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "feishu",
            Updates(("channels.feishu.advanced.proxy.url", "http://localhost:8080")));

        Assert.Equal("http://localhost:8080",
            result.Patch!.Value.GetProperty("channels").GetProperty("feishu")
                .GetProperty("advanced").GetProperty("proxy").GetProperty("url").GetString());
    }

    // ─── Redaction-sentinel safety rail ──────────────────────────────────

    [Fact]
    public void BuildPatch_BlocksWhenOtherChannelHasRedactionSentinel()
    {
        var existing = Json("""
            {
              "channels": {
                "matrix": { "accessToken": "[REDACTED]", "enabled": true },
                "feishu": { "appId": "old", "enabled": false }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "feishu",
            Updates(("channels.feishu.appId", "new")));

        Assert.Null(result.Patch);
        Assert.NotNull(result.BlockedReason);
        Assert.Equal("channels.matrix.accessToken", result.BlockedPath);
        Assert.Contains("redacted", result.BlockedReason!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPatch_DoesNotBlockOnSentinelInsideTargetChannel()
    {
        // The sentinel is in the channel we're about to overwrite — that's
        // the whole point of this save, so don't refuse.
        var existing = Json("""
            {
              "channels": {
                "feishu": { "appId": "<redacted>", "enabled": true }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "feishu",
            Updates(("channels.feishu.appId", "real-new-app-id")));

        Assert.Null(result.BlockedReason);
        Assert.Equal("real-new-app-id",
            result.Patch!.Value.GetProperty("channels").GetProperty("feishu").GetProperty("appId").GetString());
    }

    [Fact]
    public void BuildPatch_BlocksRedactedSiblingInsideTargetChannel()
    {
        // Only the edited leaf and channels.{id}.enabled are replaced.
        // An untouched placeholder in the same channel must not be sent back.
        var existing = Json("""
            {
              "channels": {
                "matrix": {
                  "accessToken": "syt_old",
                  "refreshToken": "[REDACTED]",
                  "enabled": true
                }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "matrix",
            Updates(("channels.matrix.accessToken", "syt_new")));

        Assert.Null(result.Patch);
        Assert.NotNull(result.BlockedReason);
        Assert.Equal("channels.matrix.refreshToken", result.BlockedPath);
    }

    [Fact]
    public void BuildPatch_BlocksRedactedAccountBotTokenInsideTargetChannel()
    {
        var existing = Json("""
            {
              "channels": {
                "matrix": {
                  "accessToken": "syt_old",
                  "accounts": { "work": { "accessToken": "<redacted>" } },
                  "enabled": true
                }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "matrix",
            Updates(("channels.matrix.accessToken", "syt_new")));

        Assert.Null(result.Patch);
        Assert.NotNull(result.BlockedReason);
        Assert.Equal("channels.matrix.accounts.work.accessToken", result.BlockedPath);
    }

    [Theory]
    [InlineData("***")]
    [InlineData("<redacted>")]
    [InlineData("[redacted]")]
    [InlineData("  [REDACTED]  ")] // trimmed before matching
    public void BuildPatch_DetectsCommonRedactionSentinels(string sentinel)
    {
        var existing = Json($$"""
            {
              "channels": {
                "matrix": { "accessToken": "{{sentinel.Replace("\\", "\\\\")}}", "enabled": true }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "feishu",
            Updates(("channels.feishu.appId", "cli_abc")));

        Assert.NotNull(result.BlockedReason);
        Assert.Equal("channels.matrix.accessToken", result.BlockedPath);
    }

    [Theory]
    [InlineData("REDACTED")]         // bracket-less
    [InlineData("<hidden>")]         // gateway-of-Theseus alternative
    [InlineData("●●●●●●")]           // bullet characters
    [InlineData("....")]             // dots
    public void BuildPatch_DoesNotCatch_NonListedSentinelVariants(string variant)
    {
        // Documents the *current* limitation of the redaction safety rail:
        // values outside the hardcoded sentinel set silently pass through
        // (Hanselman review LOW-13). If the sentinel matcher is widened in
        // the future, the matching InlineData rows here should flip to
        // BlockedReason != null and be moved into the Detects test above.
        var existing = Json($$"""
            {
              "channels": {
                "matrix": { "accessToken": "{{variant}}", "enabled": true }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "feishu",
            Updates(("channels.feishu.appId", "cli_abc")));

        Assert.Null(result.BlockedReason);
        Assert.NotNull(result.Patch);
    }

    [Fact]
    public void BuildPatch_IgnoresPartialSentinelMatches()
    {
        // A value that *contains* the word "redacted" as part of legit
        // text should NOT be treated as a sentinel.
        var existing = Json("""
            {
              "channels": {
                "matrix": { "label": "no redacted secrets here", "enabled": true }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "feishu",
            Updates(("channels.feishu.appId", "cli_abc")));

        Assert.Null(result.BlockedReason);
        Assert.NotNull(result.Patch);
    }

    // ─── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public void BuildPatch_BlocksOnSentinelInsideArrayInOtherChannel()
    {
        // FindRedactionSentinel handles arrays — confirm a sentinel buried
        // inside an array value in a non-target channel is still detected
        // (Hanselman review LOW-10). Without this test the array-traversal
        // branch could regress silently.
        var existing = Json("""
            {
              "channels": {
                "matrix": { "webhookUrls": ["https://real.example/", "[REDACTED]"], "enabled": true }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "feishu",
            Updates(("channels.feishu.appId", "cli_abc")));

        Assert.NotNull(result.BlockedReason);
        Assert.Equal("channels.matrix.webhookUrls[1]", result.BlockedPath);
    }

    [Fact]
    public void BuildPatch_PrefixChannelId_DoesNotSkipSentinelInLongerChannelName()
    {
        // The exclude prefix is `channels.{id}.` with a trailing dot — that
        // dot is what stops `channelId = "fei"` from skipping sentinels in
        // `channels.feishu.*`. Documenting that boundary so a refactor
        // can't silently drop the dot (Hanselman review LOW-11).
        var existing = Json("""
            {
              "channels": {
                "feishu": { "appId": "[REDACTED]", "enabled": true }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "fei",
            Updates(("channels.fei.apiKey", "abc")));

        Assert.NotNull(result.BlockedReason);
        Assert.Equal("channels.feishu.appId", result.BlockedPath);
    }

    [Fact]
    public void BuildPatch_RejectsEmptyDotPathSegments()
    {
        var existing = Json("{}");
        Assert.Throws<System.ArgumentException>(() =>
            ChannelConfigPatchBuilder.BuildPatch(
                existing, "feishu",
                Updates(("channels..appId", "abc"))));
    }

    [Fact]
    public void BuildPatch_RequiresChannelId()
    {
        Assert.Throws<System.ArgumentException>(() =>
            ChannelConfigPatchBuilder.BuildPatch(
                Json("{}"), "",
                Updates(("channels.feishu.appId", "abc"))));
    }
}
