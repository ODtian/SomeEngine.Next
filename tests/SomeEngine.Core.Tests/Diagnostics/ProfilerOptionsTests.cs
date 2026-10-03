using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Core.Tests.Diagnostics;

public sealed class ProfilerOptionsTests
{
    [Fact]
    public void FromCommandLine_DefaultsToDisabled()
    {
        ProfilerOptions options = Profiler.ParseOptions([]);

        Assert.False(options.EnableTracy);
        Assert.False(options.ThrowOnUnavailable);
        Assert.Null(options.TracyNativeLibraryPath);
    }

    [Fact]
    public void FromCommandLine_EnablesTracyFromSwitch()
    {
        ProfilerOptions options = Profiler.ParseOptions(
            ["--tracy", "--tracy-native", "custom-tracy.dll"]);

        Assert.True(options.EnableTracy);
        Assert.Equal("custom-tracy.dll", options.TracyNativeLibraryPath);
    }

    [Fact]
    public void FromCommandLine_ProfileSwitchEnablesTracyBridgeOnly()
    {
        ProfilerOptions options = Profiler.ParseOptions(["--profile"]);

        Assert.True(options.EnableTracy);
    }

    [Fact]
    public void FromCommandLine_ManagedProfilerSwitchThrows()
    {
        Assert.Throws<ArgumentException>(() => Profiler.ParseOptions(["--csharp-profile"]));
    }

    [Fact]
    public void FromCommandLine_ManagedProfilerOutputSwitchThrows()
    {
        Assert.Throws<ArgumentException>(() => Profiler.ParseOptions(["--profile-output", "profile.txt"]));
    }

    [Fact]
    public void FromCommandLine_ManagedProfilerDetailCountersSwitchThrows()
    {
        Assert.Throws<ArgumentException>(() => Profiler.ParseOptions(["--profile-detail-counters"]));
    }

    [Fact]
    public void FromCommandLine_DisableSwitchOverridesEarlierProfileSwitch()
    {
        ProfilerOptions options = Profiler.ParseOptions(["--profile", "--no-profile"]);

        Assert.False(options.EnableTracy);
    }

    [Fact]
    public void FromCommandLine_RequiredProfileEnablesAndRequiresNativeBackend()
    {
        ProfilerOptions options = Profiler.ParseOptions(["--profile-required"]);

        Assert.True(options.EnableTracy);
        Assert.True(options.ThrowOnUnavailable);
    }
}
