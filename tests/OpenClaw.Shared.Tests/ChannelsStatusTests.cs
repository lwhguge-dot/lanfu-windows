using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ChannelsStatusParserTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void Parse_ReturnsEmptySnapshot_ForNonObjectInput()
    {
        var snap = ChannelsStatusParser.Parse(Json("[]"));
        Assert.Empty(snap.ChannelOrder);
        Assert.Empty(snap.Channels);
    }

    [Fact]
    public void Parse_ExtractsChannelOrderAndLabels()
    {
        var json = """
            {
              "ts": 1716000000.5,
              "channelOrder": ["feishu", "matrix"],
              "channelLabels": { "feishu": "Feishu", "matrix": "Matrix" },
              "channels": {}
            }
            """;
        var snap = ChannelsStatusParser.Parse(Json(json));
        Assert.Equal(new[] { "feishu", "matrix" }, snap.ChannelOrder);
        Assert.Equal("Feishu", snap.ChannelLabels["feishu"]);
        Assert.Equal(1716000000.5, snap.Ts);
    }

    [Fact]
    public void Parse_PrefersChannelMetaOverLabels()
    {
        var json = """
            {
              "channelOrder": ["feishu"],
              "channelLabels": { "feishu": "Legacy Label" },
              "channelMeta": [
                { "id": "feishu", "label": "Feishu", "detailLabel": "Feishu Bot", "systemImage": "message.fill" }
              ],
              "channels": {}
            }
            """;
        var snap = ChannelsStatusParser.Parse(Json(json));
        Assert.Equal("Feishu", snap.ResolveLabel("feishu"));
        Assert.Equal("Feishu Bot", snap.ResolveDetailLabel("feishu"));
        Assert.Equal("message.fill", snap.ResolveSystemImage("feishu"));
    }

    [Fact]
    public void Parse_FallsBackToIdWhenLabelMissing()
    {
        var snap = ChannelsStatusParser.Parse(Json("""{ "channelOrder": ["custom-plugin"], "channelLabels": {} }"""));
        Assert.Equal("custom-plugin", snap.ResolveLabel("custom-plugin"));
        Assert.Equal("custom-plugin", snap.ResolveDetailLabel("custom-plugin"));
        Assert.Null(snap.ResolveSystemImage("custom-plugin"));
    }

    [Fact]
    public void Parse_ExtractsChannelsAndKeepsRawJson()
    {
        var json = """
            {
              "channelOrder": ["feishu"],
              "channels": {
                "feishu": { "configured": true, "running": true, "botUsername": "lanfu_bot" }
              }
            }
            """;
        var snap = ChannelsStatusParser.Parse(Json(json));
        Assert.True(snap.Channels.ContainsKey("feishu"));
        var raw = snap.Channels["feishu"];
        Assert.Equal(JsonValueKind.True, raw.GetProperty("configured").ValueKind);
        Assert.Equal("lanfu_bot", raw.GetProperty("botUsername").GetString());
    }

    [Fact]
    public void Parse_ExtractsChannelAccounts()
    {
        var json = """
            {
              "channelOrder": ["feishu"],
              "channels": {},
              "channelAccounts": {
                "feishu": [
                  { "id": "primary", "configured": true, "running": true, "lastInboundAt": 1716000000000 },
                  { "id": "secondary", "configured": false }
                ]
              },
              "channelDefaultAccountId": { "feishu": "primary" }
            }
            """;
        var snap = ChannelsStatusParser.Parse(Json(json));
        var accs = snap.ChannelAccounts["feishu"];
        Assert.Equal(2, accs.Count);
        Assert.Equal("primary", accs[0].Id);
        Assert.True(accs[0].Configured);
        Assert.Equal(1716000000000d, accs[0].LastInboundAt);
        Assert.False(accs[1].Configured);
        Assert.Equal("primary", snap.ChannelDefaultAccountId["feishu"]);
    }

    [Fact]
    public void ExtractGeneric_ParsesProbe()
    {
        var json = """
            { "configured": true, "running": true, "probe": { "ok": true, "status": 200, "elapsedMs": 91, "version": "1.2" } }
            """;
        var generic = ChannelsStatusParser.ExtractGeneric(Json(json));
        Assert.NotNull(generic);
        Assert.True(generic!.Configured);
        Assert.True(generic.Probe!.Ok);
        Assert.Equal(200, generic.Probe.Status);
        Assert.Equal(91d, generic.Probe.ElapsedMs);
        Assert.Equal("1.2", generic.Probe.Version);
    }

    [Fact]
    public void ExtractGeneric_FallsBackToErrorWhenLastErrorMissing()
    {
        var json = """{ "configured": false, "error": "boom" }""";
        var generic = ChannelsStatusParser.ExtractGeneric(Json(json));
        Assert.Equal("boom", generic!.LastError);
    }

    [Fact]
    public void ExtractGeneric_PopulatesActivityAndModeFields()
    {
        // Mirrors what the live gateway returns for a running polling channel.
        var json = """
            {
              "configured": true, "running": true, "connected": true,
              "mode": "polling",
              "lastStartAt": 1778990651099,
              "lastEventAt": 1778997012450,
              "lastTransportActivityAt": 1778997012450,
              "reconnectAttempts": 3,
              "restartPending": false
            }
            """;
        var generic = ChannelsStatusParser.ExtractGeneric(Json(json));
        Assert.NotNull(generic);
        Assert.Equal("polling", generic!.Mode);
        Assert.Equal(1778990651099d, generic.LastStartAt);
        Assert.Equal(1778997012450d, generic.LastEventAt);
        Assert.Equal(1778997012450d, generic.LastTransportActivityAt);
        Assert.Equal(3, generic.ReconnectAttempts);
        Assert.False(generic.RestartPending);
    }

    [Fact]
    public void ExtractGeneric_DefaultsActivityFields_WhenMissing()
    {
        var json = """{ "configured": true, "running": true }""";
        var generic = ChannelsStatusParser.ExtractGeneric(Json(json));
        Assert.NotNull(generic);
        Assert.Null(generic!.Mode);
        Assert.Null(generic.LastStartAt);
        Assert.Null(generic.LastEventAt);
        Assert.Null(generic.LastTransportActivityAt);
        Assert.Equal(0, generic.ReconnectAttempts);
        Assert.False(generic.RestartPending);
    }
}

public class ChannelsAggregatorTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    private static ChannelsStatusSnapshot SnapshotWith(string order, params (string id, string statusJson)[] channels)
    {
        var ids = order.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var map = new Dictionary<string, JsonElement>();
        foreach (var (id, statusJson) in channels)
            map[id] = Json(statusJson);
        return new ChannelsStatusSnapshot { ChannelOrder = ids, Channels = map };
    }

    [Fact]
    public void Aggregate_FallsBackToBuiltInOrder_WhenSnapshotEmpty()
    {
        var records = ChannelsAggregator.Aggregate(null, DateTime.UtcNow);
        Assert.Equal(ChannelsAggregator.BuiltInChannelOrder.Count, records.Count);
        var ids = records.Select(r => r.Id).ToHashSet();
        Assert.Contains("nostr", ids);
    }

    [Fact]
    public void Aggregate_PutsConfiguredFirst()
    {
        var snap = SnapshotWith(
            "feishu,matrix,nostr",
            ("feishu", """{ "configured": false }"""),
            ("matrix", """{ "configured": true, "running": true }"""),
            ("nostr", """{ "configured": false }"""));
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        Assert.Equal("matrix", records[0].Id);
        Assert.True(records[0].IsConfigured);
        Assert.False(records[1].IsConfigured);
        Assert.False(records[2].IsConfigured);
    }

    [Fact]
    public void Aggregate_MultiAccount_OneActiveCountsAsConfigured()
    {
        var snap = new ChannelsStatusSnapshot
        {
            ChannelOrder = new[] { "feishu" },
            Channels = new Dictionary<string, JsonElement> { ["feishu"] = Json("""{ "configured": false }""") },
            ChannelAccounts = new Dictionary<string, IReadOnlyList<ChannelAccountSnapshot>>
            {
                ["feishu"] = new[]
                {
                    new ChannelAccountSnapshot { Id = "primary", Configured = true, Running = true },
                }
            },
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        Assert.True(records[0].IsConfigured);
    }

    [Fact]
    public void Aggregate_AlwaysUnionsBuiltInCatalog_SoUserCanDiscoverMoreChannels()
    {
        // Gateway reports only the channel the user has configured.
        // The page should still surface the other built-in channels as
        // AVAILABLE entries so the user can discover and add more.
        var snap = new ChannelsStatusSnapshot
        {
            ChannelOrder = new[] { "feishu" },
            Channels = new Dictionary<string, JsonElement>
            {
                ["feishu"] = Json("""{ "configured": true, "running": true }"""),
            },
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);

        // Configured (feishu) appears first, then every other built-in
        // catalog channel as unconfigured "preview" entries.
        Assert.Equal("feishu", records[0].Id);
        Assert.True(records[0].IsConfigured);

        var allIds = records.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var builtIn in ChannelsAggregator.BuiltInChannelOrder)
            Assert.Contains(builtIn, allIds);

        // No duplicate ids (built-in entries dedupe against gateway-reported).
        Assert.Equal(records.Count, records.Select(r => r.Id.ToLowerInvariant()).Distinct().Count());

        // All non-target built-ins are unconfigured.
        Assert.All(records.Where(r => !string.Equals(r.Id, "feishu", System.StringComparison.OrdinalIgnoreCase)),
            r => Assert.False(r.IsConfigured));
    }

    [Fact]
    public void Aggregate_BuiltInExtras_GetPrettyLabels()
    {
        // Gateway provides no labels for preview channels; the aggregator
        // should fall back to the built-in pretty-name catalog so the
        // AVAILABLE list never shows raw lowercase ids.
        var records = ChannelsAggregator.Aggregate(null, DateTime.UtcNow);
        var byId = records.ToDictionary(r => r.Id.ToLowerInvariant());
        Assert.Equal("Nostr", byId["nostr"].Label);
    }

    [Fact]
    public void Aggregate_UsesGatewayLabelsWhenProvided()
    {
        var snap = new ChannelsStatusSnapshot
        {
            ChannelOrder = new[] { "custom-plugin" },
            ChannelLabels = new Dictionary<string, string> { ["custom-plugin"] = "Custom Plugin" },
            Channels = new Dictionary<string, JsonElement>(),
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        Assert.Equal("Custom Plugin", records[0].Label);
    }

    [Fact]
    public void IsChannelConfigured_TrueWhenRunningOrConnected()
    {
        Assert.True(ChannelsAggregator.IsChannelConfigured(Json("""{ "running": true }"""), null));
        Assert.True(ChannelsAggregator.IsChannelConfigured(Json("""{ "connected": true }"""), null));
        Assert.True(ChannelsAggregator.IsChannelConfigured(Json("""{ "configured": true }"""), null));
        Assert.False(ChannelsAggregator.IsChannelConfigured(Json("""{ }"""), null));
        Assert.False(ChannelsAggregator.IsChannelConfigured(Json("""{ "configured": false }"""), null));
    }

    [Fact]
    public void IsChannelRunning_TrueOnlyOnRunningOrConnected()
    {
        // Running is a strict subset of Configured: configured-without-running
        // returns false so the page can offer "Start channel" as recovery.
        Assert.True(ChannelsAggregator.IsChannelRunning(Json("""{ "running": true }"""), null));
        Assert.True(ChannelsAggregator.IsChannelRunning(Json("""{ "connected": true }"""), null));
        Assert.False(ChannelsAggregator.IsChannelRunning(Json("""{ "configured": true }"""), null));
        Assert.False(ChannelsAggregator.IsChannelRunning(Json("""{ }"""), null));
    }

    [Fact]
    public void Aggregate_CanStart_OnlyWhenConfiguredButNotRunning()
    {
        // 'feishu': configured but stopped — this is the canonical "Start
        // channel" scenario.
        // 'matrix': configured AND running — Start would be a no-op, no flag.
        // 'nostr':  not configured at all — no Start until credentials exist.
        var snap = SnapshotWith(
            "feishu,matrix,nostr",
            ("feishu", """{ "configured": true }"""),
            ("matrix", """{ "configured": true, "running": true }"""),
            ("nostr",  """{ }"""));
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow).ToDictionary(r => r.Id);

        Assert.True(records["feishu"].IsConfigured);
        Assert.False(records["feishu"].IsRunning);
        Assert.True(records["feishu"].Capabilities.HasFlag(ChannelCapabilities.CanStart));

        Assert.True(records["matrix"].IsConfigured);
        Assert.True(records["matrix"].IsRunning);
        Assert.False(records["matrix"].Capabilities.HasFlag(ChannelCapabilities.CanStart));

        Assert.False(records["nostr"].IsConfigured);
        Assert.False(records["nostr"].IsRunning);
        Assert.False(records["nostr"].Capabilities.HasFlag(ChannelCapabilities.CanStart));
    }

    [Fact]
    public void Aggregate_SurfacesChannelsMissingFromChannelOrder()
    {
        // Older gateways or plugin-only setups may omit channelOrder while still
        // populating channels/channelMeta. The aggregator must still surface
        // those channels — dropping them silently is a regression.
        var snap = new ChannelsStatusSnapshot
        {
            ChannelOrder = new[] { "feishu" }, // only one in order
            Channels = new Dictionary<string, JsonElement>
            {
                ["feishu"] = Json("""{ "configured": true, "running": true }"""),
                ["custom-plugin"] = Json("""{ "configured": true, "running": true }"""),
            },
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        var byId = records.ToDictionary(r => r.Id);
        Assert.Contains("feishu", byId.Keys);
        Assert.Contains("custom-plugin", byId.Keys);
        // Both gateway-reported channels are configured. (Built-in extras
        // appear unconfigured per the discoverability union; see
        // Aggregate_AlwaysUnionsBuiltInCatalog.)
        Assert.True(byId["feishu"].IsConfigured);
        Assert.True(byId["custom-plugin"].IsConfigured);
    }

    [Fact]
    public void Aggregate_UnionsChannelsAndMetaWhenChannelOrderEmpty()
    {
        var snap = new ChannelsStatusSnapshot
        {
            // No channelOrder at all
            Channels = new Dictionary<string, JsonElement>
            {
                ["plugin-a"] = Json("""{ "configured": true }"""),
            },
            ChannelMeta = new[] { new ChannelUiMetaEntry { Id = "plugin-b", Label = "Plugin B" } },
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        var ids = records.Select(r => r.Id).ToHashSet();
        // Custom plugin-a / plugin-b surface — and the built-in catalog
        // ALWAYS unions in for discoverability (per
        // Aggregate_AlwaysUnionsBuiltInCatalog).
        Assert.Contains("plugin-a", ids);
        Assert.Contains("plugin-b", ids);
        Assert.Contains("nostr", ids);
    }

    [Fact]
    public void Aggregate_AccountsKeysAlsoCountAsKnownIds()
    {
        var snap = new ChannelsStatusSnapshot
        {
            // No channelOrder, no channels — only multi-account info present.
            ChannelAccounts = new Dictionary<string, IReadOnlyList<ChannelAccountSnapshot>>
            {
                ["acct-only"] = new[] { new ChannelAccountSnapshot { Id = "primary", Configured = true } }
            },
        };
        var records = ChannelsAggregator.Aggregate(snap, DateTime.UtcNow);
        // acct-only is surfaced (the assertion this test was originally
        // protecting); built-in extras append for discoverability.
        var byId = records.ToDictionary(r => r.Id);
        Assert.Contains("acct-only", byId.Keys);
        Assert.True(byId["acct-only"].IsConfigured);
    }
}
