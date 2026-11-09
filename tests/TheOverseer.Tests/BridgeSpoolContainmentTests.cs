using TheOverseer.App.Monitoring;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class BridgeSpoolContainmentTests
{
    [Fact]
    public async Task DiscoveryAndPollingRejectAJunctionWhileKeepingAHealthyPeer()
    {
        using var home = new SyntheticCodexHome();
        var spool = Path.Combine(home.Root, "spool", "claude");
        var linked = Path.Combine(spool, "linked");
        var peer = Path.Combine(spool, "peer");
        var outside = Path.Combine(home.Root, "outside");
        Directory.CreateDirectory(peer);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "events.jsonl"), "outside-canary\n");
        File.WriteAllText(Path.Combine(peer, "events.jsonl"), "peer\n");
        try
        {
            await WindowsTestLinks.CreateJunctionAsync(linked, outside);
            var discovery = HarnessSources.DiscoverDetailed(home.Root);
            Assert.Equal("peer", Assert.Single(discovery.Sources).SourceInstanceId);
            Assert.True(discovery.Capped);
            Assert.Empty(new BridgeSpoolReader(linked).ReadNewLines(out var reason, out _));
            Assert.Equal("bridge-spool-linked", reason);
            Assert.Equal("peer", Assert.Single(new BridgeSpoolReader(peer).ReadNewLines(out _, out _)));
        }
        finally
        {
            if (Directory.Exists(linked) && (File.GetAttributes(linked) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(linked);
        }
    }

    [Fact]
    public void AHardLinkedEventFileCannotFeedRecordsAndAReplacementRecovers()
    {
        using var home = new SyntheticCodexHome();
        var directory = Path.Combine(home.Root, "source");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(home.Root, "canary.txt");
        File.WriteAllText(target, "outside-canary\n");
        var reader = new BridgeSpoolReader(directory, tailOnAttach: true);
        WindowsTestLinks.CreateHardLink(reader.EventsPath, target);
        Assert.Empty(reader.ReadNewLines(out var reason, out _));
        Assert.Equal("bridge-spool-linked", reason);
        File.Delete(reader.EventsPath);
        File.WriteAllText(reader.EventsPath, "real-record\n");
        Assert.Equal("real-record", Assert.Single(reader.ReadNewLines(out reason, out _)));
        Assert.Equal("ok", reason);
        Assert.Equal("outside-canary\n", File.ReadAllText(target));
    }
}
