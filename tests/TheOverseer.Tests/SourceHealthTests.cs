using TheOverseer.Core.Models;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

public sealed class SourceHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingEvidenceStaysUnknownAcrossPublications()
    {
        var source = SourceSnapshot.Empty(new SourceInstance("dsh", "pending", "dsh"),
            DateTimeOffset.MinValue, "not-started", "test");
        Assert.Null(SourceHealth.From(source).EvidenceAt);
        Assert.Null(SourceHealth.From(source).EvidenceAt);
        var vm = new HudViewModel(new());
        vm.ApplySnapshot(MonitoringSnapshot.Empty(Now) with { Sources = new[] { SourceHealth.From(source) } });
        Assert.Equal("unknown", Assert.Single(vm.HealthDetails).Freshness);
        vm.ApplySnapshot(MonitoringSnapshot.Empty(Now.AddMinutes(3)) with { Sources = new[] { SourceHealth.From(source) } });
        Assert.Equal("unknown", Assert.Single(vm.HealthDetails).Freshness);
    }

    [Fact]
    public void OlderSourceObservationIsUsedOnlyWhenExplicitEvidenceIsMissing()
    {
        var source = SourceSnapshot.Empty(new SourceInstance("codex", "one", "Codex"),
            Now.AddMinutes(-5), "test", "test") with { EvidenceAt = default };
        Assert.Equal(Now.AddMinutes(-5), SourceHealth.From(source).EvidenceAt);
        Assert.Equal(Now.AddMinutes(-9), SourceHealth.From(source with { EvidenceAt = Now.AddMinutes(-9) }).EvidenceAt);
        var vm = new HudViewModel(new());
        vm.ApplySnapshot(MonitoringSnapshot.Empty(Now) with { Sources = new[] { SourceHealth.From(source) } });
        Assert.Equal("5m ago", Assert.Single(vm.HealthDetails).Freshness);
        vm.ApplySnapshot(MonitoringSnapshot.Empty(Now.AddMinutes(3)) with { Sources = new[] { SourceHealth.From(source) } });
        Assert.Equal("8m ago", Assert.Single(vm.HealthDetails).Freshness);
    }
}
