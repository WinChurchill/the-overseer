using System.Text.Json;
using TheOverseer.App.Monitoring;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class BridgeHealthAccountingTests
{
    [Fact]
    public void PendingDropsAreVisibleBeforeAnySummaryExists()
    {
        WithSpool(root =>
        {
            WriteReceipt(root, 0, Guid.NewGuid().ToString(), "droppedLockTimeout");
            var reader = new BridgeSpoolReader(root);
            reader.ReadNewLines(out var reason, out var drops);
            Assert.Equal("ok", reason);
            Assert.Equal(1, drops);
        });
    }

    [Fact]
    public void FoldedReceiptsAndReusedSlotsDoNotDoubleCountDrops()
    {
        WithSpool(root =>
        {
            var committed = Guid.NewGuid().ToString();
            var pending = Guid.NewGuid().ToString();
            var reused = Guid.NewGuid().ToString();
            WriteSummary(root, 2, new() { ["0"] = committed });
            WriteReceipt(root, 0, committed, "droppedLockTimeout");
            WriteReceipt(root, 1, pending, "droppedLockTimeout");
            var reader = new BridgeSpoolReader(root);
            reader.ReadNewLines(out var reason, out var drops);
            Assert.Equal("ok", reason);
            Assert.Equal(3, drops);

            WriteSummary(root, 3, new() { ["0"] = committed, ["1"] = pending });
            File.Delete(ReceiptPath(root, 1));
            reader.ReadNewLines(out reason, out drops);
            Assert.Equal("ok", reason);
            Assert.Equal(3, drops);

            WriteReceipt(root, 0, reused, "droppedLockTimeout");
            reader.ReadNewLines(out reason, out drops);
            Assert.Equal("ok", reason);
            Assert.Equal(4, drops);
            WriteSummary(root, 4, new() { ["0"] = reused, ["1"] = pending });
            reader.ReadNewLines(out reason, out drops);
            Assert.Equal("ok", reason);
            Assert.Equal(4, drops);
        });
    }

    [Fact]
    public void AReceiptMayShareOnlyItsExpectedStagingFile()
    {
        WithSpool(root =>
        {
            var receipt = ReceiptPath(root, 0);
            var staging = receipt + ".tmp";
            WriteReceipt(root, 0, Guid.NewGuid().ToString(), "droppedLockTimeout");
            File.Move(receipt, staging);
            WindowsTestLinks.CreateHardLink(receipt, staging);
            var reader = new BridgeSpoolReader(root);
            reader.ReadNewLines(out var reason, out var drops);
            Assert.Equal("ok", reason);
            Assert.Equal(1, drops);

            File.Delete(staging);
            WindowsTestLinks.CreateHardLink(Path.Combine(root, "foreign.json"), receipt);
            reader.ReadNewLines(out reason, out drops);
            Assert.Equal("bridge-spool-linked", reason);
            Assert.Equal(1, drops);
        });
    }

    [Fact]
    public void AnAbandonedIntentMakesCoverageIncompleteWithoutInventingDrops()
    {
        WithSpool(root =>
        {
            var intent = ReceiptPath(root, 0) + ".intent";
            File.WriteAllText(intent, "1");
            File.SetLastWriteTimeUtc(intent, DateTime.UtcNow.AddMinutes(-1));
            var reader = new BridgeSpoolReader(root);
            reader.ReadNewLines(out var reason, out var drops);
            Assert.Equal("bridge-health-accounting-incomplete", reason);
            Assert.Equal(0, drops);
        });
    }

    [Fact]
    public void AccountingOverflowIsPartialCoverageRatherThanHealthyZero()
    {
        WithSpool(root =>
        {
            WriteSummary(root, 0, new());
            File.WriteAllText(Path.Combine(root, "health-incomplete"), "");
            var reader = new BridgeSpoolReader(root);
            reader.ReadNewLines(out var reason, out var drops);
            Assert.Equal("bridge-health-accounting-incomplete", reason);
            Assert.Equal(0, drops);
        });
    }

    [Fact]
    public void AMarkerBeingCommittedIsStillIncompleteThenBecomesOneCountedDrop()
    {
        WithSpool(root =>
        {
            WriteSummary(root, 0, new());
            var folding = Path.Combine(root, "health-incomplete.folding");
            File.WriteAllText(folding, "");
            var reader = new BridgeSpoolReader(root);
            reader.ReadNewLines(out var reason, out var drops);
            Assert.Equal("bridge-health-accounting-incomplete", reason);
            Assert.Equal(0, drops);

            // The producer commits the episode in its summary, then removes the marker.
            File.WriteAllText(Path.Combine(root, "health.json"), JsonSerializer.Serialize(new
            {
                counters = new { droppedUnaccounted = 1 },
                healthRevision = Guid.NewGuid().ToString(),
                accountedReceipts = new Dictionary<string, string>(),
            }));
            File.Delete(folding);
            reader.ReadNewLines(out reason, out drops);
            Assert.Equal("ok", reason);
            Assert.Equal(1, drops);
        });
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"id\":1,\"counters\":[]}")]
    [InlineData("{\"id\":\"\\ud83d\",\"counters\":[\"droppedLockTimeout\"]}")]
    [InlineData("{\"id\":\"00000000-0000-0000-0000-000000000001\",\"counters\":[\"unlisted\"]}")]
    public void MalformedReceiptsReduceCoverage(string contents)
    {
        WithSpool(root =>
        {
            File.WriteAllText(ReceiptPath(root, 0), contents);
            var reader = new BridgeSpoolReader(root);
            reader.ReadNewLines(out var reason, out _);
            Assert.Equal("bridge-health-malformed", reason);
        });
    }

    [Fact]
    public void ReceiptsRespectTheSourceReadBudgetAndRetainKnownDrops()
    {
        WithSpool(root =>
        {
            WriteReceipt(root, 0, Guid.NewGuid().ToString(), "droppedLockTimeout");
            var reader = new BridgeSpoolReader(root);
            reader.ReadNewLines(out _, out var first);
            Assert.Equal(1, first);
            reader.MaximumBytesPerPass = 32;
            reader.ReadNewLines(out var reason, out var retained);
            Assert.Equal("bridge-health-size", reason);
            Assert.Equal(1, retained);
            Assert.InRange(reader.BytesReadThisPass, 0, 32);
        });
    }

    private static string ReceiptPath(string root, int slot) =>
        Path.Combine(root, "health-pending", slot.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".json");

    private static void WriteReceipt(string root, int slot, string id, params string[] counters) =>
        File.WriteAllText(ReceiptPath(root, slot), JsonSerializer.Serialize(new { id, counters }));

    private static void WriteSummary(string root, int drops, Dictionary<string, string> accounted) =>
        File.WriteAllText(Path.Combine(root, "health.json"), JsonSerializer.Serialize(new
        {
            counters = new { droppedLockTimeout = drops },
            healthRevision = Guid.NewGuid().ToString(),
            accountedReceipts = accounted,
        }));

    private static void WithSpool(Action<string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-health-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(root, "health-pending"));
        try { check(root); }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }
}
