using SonosFlow.App.Infrastructure;
using Windows.Networking.NetworkOperators;

namespace SonosFlow.App.Tests;

public sealed class HotspotAdvisorTests
{
    private sealed class FakeHotspot(HotspotState state, bool canEnable = true) : IHotspotController
    {
        public int StateQueries, EnableCalls, SettingsOpened;
        public HotspotState State { get; set; } = state;
        public Task<HotspotState> GetStateAsync() { StateQueries++; return Task.FromResult(State); }
        public Task<bool> EnableAsync() { EnableCalls++; return Task.FromResult(canEnable); }
        public void OpenSettings() => SettingsOpened++;
    }

    [Theory]
    [InlineData(HotspotState.Off, HotspotPrompt.Offer)]
    [InlineData(HotspotState.On, HotspotPrompt.AlreadyOn)]
    [InlineData(HotspotState.Unknown, HotspotPrompt.Unknown)] // not knowing is never reported as "off"
    public async Task Empty_search_maps_the_hotspot_state_to_the_right_notice(HotspotState state, HotspotPrompt expected)
    {
        var advisor = new HotspotAdvisor(new FakeHotspot(state));
        Assert.Equal(expected, await advisor.CheckAfterEmptySearchAsync());
    }

    [Fact]
    public async Task Only_one_notice_per_no_speakers_episode_and_again_after_speakers_were_found()
    {
        var fake = new FakeHotspot(HotspotState.Off);
        var advisor = new HotspotAdvisor(fake);
        Assert.Equal(HotspotPrompt.Offer, await advisor.CheckAfterEmptySearchAsync());
        Assert.Null(await advisor.CheckAfterEmptySearchAsync());
        Assert.Null(await advisor.CheckAfterEmptySearchAsync());
        Assert.Equal(1, fake.StateQueries); // no repeated queries either

        advisor.Reset(); // a search found speakers
        Assert.Equal(HotspotPrompt.Offer, await advisor.CheckAfterEmptySearchAsync());
    }

    private sealed class SlowHotspot : IHotspotController
    {
        public readonly TaskCompletionSource<HotspotState> Gate = new();
        public Task<HotspotState> GetStateAsync() => Gate.Task;
        public Task<bool> EnableAsync() => Task.FromResult(true);
        public void OpenSettings() { }
    }

    [Fact]
    public async Task Speakers_found_while_a_check_is_in_flight_drop_it_and_re_arm_the_notice()
    {
        var slow = new SlowHotspot();
        var advisor = new HotspotAdvisor(slow);
        var pending = advisor.CheckAfterEmptySearchAsync();
        var overlapping = await advisor.CheckAfterEmptySearchAsync(); // a second, overlapping check does not notify twice
        Assert.Null(overlapping);

        advisor.Reset(); // a search found speakers while the first check was running
        slow.Gate.SetResult(HotspotState.Off);
        Assert.Null(await pending); // stale result is dropped

        // the next "no speakers" episode notifies normally
        Assert.Equal(HotspotPrompt.Offer, await advisor.CheckAfterEmptySearchAsync());
    }

    [Fact]
    public async Task A_hotspot_that_is_switching_state_gives_no_notice_and_is_checked_again_next_time()
    {
        var fake = new FakeHotspot(HotspotState.Changing);
        var advisor = new HotspotAdvisor(fake);
        Assert.Null(await advisor.CheckAfterEmptySearchAsync());
        fake.State = HotspotState.On;
        Assert.Equal(HotspotPrompt.AlreadyOn, await advisor.CheckAfterEmptySearchAsync());
    }

    [Fact]
    public async Task Querying_never_switches_anything_on()
    {
        var fake = new FakeHotspot(HotspotState.Off);
        await new HotspotAdvisor(fake).CheckAfterEmptySearchAsync();
        Assert.Equal(0, fake.EnableCalls);
        Assert.Equal(0, fake.SettingsOpened);
    }

    [Theory]
    [InlineData(true, HotspotPrompt.Enabled)]
    [InlineData(false, HotspotPrompt.Failed)]
    public async Task Enable_reports_the_outcome(bool works, HotspotPrompt expected)
    {
        var fake = new FakeHotspot(HotspotState.Off, canEnable: works);
        Assert.Equal(expected, await new HotspotAdvisor(fake).EnableAsync());
        Assert.Equal(1, fake.EnableCalls);
    }

    [Theory]
    [InlineData(TetheringOperationalState.On, HotspotState.On)]
    [InlineData(TetheringOperationalState.Off, HotspotState.Off)]
    [InlineData(TetheringOperationalState.InTransition, HotspotState.Changing)]
    [InlineData(TetheringOperationalState.Unknown, HotspotState.Unknown)]
    public void Windows_states_are_mapped(TetheringOperationalState windows, HotspotState expected) =>
        Assert.Equal(expected, WindowsHotspot.Map(windows));

    [Fact]
    public async Task Real_windows_query_is_read_only_and_never_throws()
    {
        // Reads the real state on this PC (any answer is valid); proves the WinRT call works from an unpackaged app.
        var state = await new WindowsHotspot().GetStateAsync();
        Assert.True(Enum.IsDefined(state));
    }
}
