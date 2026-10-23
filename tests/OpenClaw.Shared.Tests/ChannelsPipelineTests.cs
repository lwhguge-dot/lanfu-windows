using System;
using System.Linq;
using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Cross-cutting smoke tests that exercise the full Phase 0/1 pipeline:
/// raw <c>channels.status</c> JSON → <see cref="ChannelsStatusParser"/> →
/// <see cref="ChannelsAggregator"/> → <see cref="ChannelRecord"/>s.
/// </summary>
public class ChannelsPipelineTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void Pipeline_ProducesExpectedRecordsForRealisticSnapshot()
    {
        // Approximates what the gateway would return after a fresh probe
        // with feishu connected, matrix running, wecom erroring,
        // and weixin/nostr unconfigured.
        var json = """
            {
              "ts": 1716000000,
              "channelOrder": ["feishu", "matrix", "wecom", "weixin", "nostr"],
              "channelLabels": { "feishu": "Feishu", "matrix": "Matrix", "wecom": "WeCom", "weixin": "WeChat", "nostr": "Nostr" },
              "channels": {
                "feishu": { "configured": true, "running": true, "connected": true },
                "matrix": { "configured": true, "running": true, "probe": { "ok": true, "elapsedMs": 91 }, "lastProbeAt": 1716000000000 },
                "wecom":   { "configured": true, "running": false, "lastError": "AuthExpired", "probe": { "ok": false, "status": 401 }, "lastProbeAt": 1716000000000 },
                "weixin":  { "configured": false },
                "nostr":   { "configured": false }
              }
            }
            """;
        var snapshot = ChannelsStatusParser.Parse(Json(json));
        var records = ChannelsAggregator.Aggregate(snapshot, DateTime.UtcNow);

        // Order: configured first (feishu, matrix, wecom), then unconfigured
        // (weixin, nostr). No built-in extras beyond the reported channels —
        // the built-in catalog only carries channels this gateway still ships.
        var ids = records.Select(r => r.Id).ToList();
        Assert.Equal(
            new[] { "feishu", "matrix", "wecom", "weixin", "nostr" },
            ids);
        Assert.True(records[0].IsConfigured);
        Assert.True(records[1].IsConfigured);
        Assert.True(records[2].IsConfigured);
        Assert.False(records[3].IsConfigured); // weixin
        Assert.False(records[4].IsConfigured); // nostr

        // Capabilities reflect channel id: none of the currently shipped
        // channels are QR-link or logout channels.
        Assert.False(records[0].Capabilities.HasFlag(ChannelCapabilities.CanShowQr));
        Assert.False(records[0].Capabilities.HasFlag(ChannelCapabilities.CanLogout));
        Assert.True(records[0].Capabilities.HasFlag(ChannelCapabilities.CanStop));
        Assert.False(records[1].Capabilities.HasFlag(ChannelCapabilities.CanLogout));

        // Labels: the snapshot supplies them; preview channels fall back to
        // the built-in pretty-name catalog (so users don't see lowercase ids
        // in the AVAILABLE list).
        Assert.Equal("Feishu", records[0].Label);
        Assert.Equal("Nostr", records[4].Label);
    }

    [Fact]
    public void Pipeline_HandlesEmptySnapshotGracefully()
    {
        var snapshot = ChannelsStatusParser.Parse(Json("{}"));
        var records = ChannelsAggregator.Aggregate(snapshot, DateTime.UtcNow);
        // No order from gateway → built-in fallback list is used.
        Assert.Equal(ChannelsAggregator.BuiltInChannelOrder.Count, records.Count);
        Assert.All(records, r => Assert.False(r.IsConfigured));
    }
}
