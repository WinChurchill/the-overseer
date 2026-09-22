using TheOverseer.App.Desktop;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class StartupRegistrationTests
{
    private const string AppPath = @"C:\Program Files\OVERSEER\TheOverseer.App.exe";

    [Fact]
    public void CommandLineQuotesPathsWithSpacesAndAddsTheHiddenFlag()
    {
        var command = StartupRegistration.BuildCommand(AppPath);
        Assert.Equal("\"" + AppPath + "\" --start-in-tray", command);

        var already = StartupRegistration.BuildCommand("\"" + AppPath + "\"");
        Assert.Equal("\"" + AppPath + "\" --start-in-tray", already);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\tools\dotnet.exe")]
    [InlineData(@"C:\tools\dotnet")]
    public void UnusableExecutablePathsAreRefused(string path)
    {
        Assert.False(StartupRegistration.IsUsableExecutablePath(path));
    }

    [Fact]
    public void EnablingWritesOnlyTheOwnedValueAndDisablingRemovesIt()
    {
        var store = new FakeStartupRegistry();
        store.Seed("SomeOtherApp", "other.exe");
        var registration = new StartupRegistration(store, AppPath);

        var enabled = registration.Set(true);
        Assert.Equal(StartupRegistrationState.Enabled, enabled.State);
        Assert.Equal(1, store.WriteCount);
        Assert.Equal("\"" + AppPath + "\" --start-in-tray", store.Values[StartupRegistration.ValueName]);
        Assert.Equal("other.exe", store.Values["SomeOtherApp"]);

        var disabled = registration.Set(false);
        Assert.Equal(StartupRegistrationState.Disabled, disabled.State);
        Assert.False(store.Values.ContainsKey(StartupRegistration.ValueName));
        Assert.Equal("other.exe", store.Values["SomeOtherApp"]);
    }

    [Fact]
    public void ReadingReportsEnabledDisabledAndStaleStates()
    {
        var store = new FakeStartupRegistry();
        var registration = new StartupRegistration(store, AppPath);
        Assert.Equal(StartupRegistrationState.Disabled, registration.Read().State);

        store.Seed(StartupRegistration.ValueName, "\"C:\\old\\TheOverseer.App.exe\" --start-in-tray");
        var stale = registration.Read();
        Assert.Equal(StartupRegistrationState.StalePath, stale.State);
        Assert.Equal("stale-path", stale.ReasonCode);

        store.Seed(StartupRegistration.ValueName, StartupRegistration.BuildCommand(AppPath));
        Assert.Equal(StartupRegistrationState.Enabled, registration.Read().State);
    }

    [Fact]
    public void UnusableExecutableNeverTouchesTheRegistry()
    {
        var store = new FakeStartupRegistry();
        var registration = new StartupRegistration(store, "dotnet.exe");

        Assert.Equal(StartupRegistrationState.Unavailable, registration.Read().State);
        Assert.Equal(StartupRegistrationState.Unavailable, registration.Set(true).State);
        Assert.Equal(0, store.WriteCount);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public void DeniedRegistryAccessIsReportedHonestly()
    {
        var registration = new StartupRegistration(new DenyingRegistry(), AppPath);
        Assert.Equal(StartupRegistrationState.Unavailable, registration.Read().State);
        Assert.Equal("registry-denied", registration.Read().ReasonCode);
        Assert.Equal(StartupRegistrationState.Unavailable, registration.Set(true).State);
        Assert.Equal("registry-denied", registration.Set(true).ReasonCode);
    }

    private sealed class DenyingRegistry : IStartupRegistry
    {
        public string? Read(string valueName) => throw new UnauthorizedAccessException();

        public void Write(string valueName, string value) => throw new UnauthorizedAccessException();

        public void Delete(string valueName) => throw new UnauthorizedAccessException();
    }
}
