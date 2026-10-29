using System.Net;
using SonosFlow.Core.Sonos;
using SonosFlow.Core.Streaming;

namespace SonosFlow.Core.Tests;

/// <summary>A session that never started can't be guarded: the live toggle must refuse and touch nothing.</summary>
public sealed class SetMuteLocalTests
{
    [Fact]
    public async Task A_session_that_is_not_streaming_does_not_apply_the_toggle()
    {
        var target = new SonosDevice(IPAddress.Loopback, "Test", "Sonos Test", "S0", "uuid:test");
        var spec = CaptureSpec.SystemOutput(null);
        var file = Path.Combine(Path.GetTempPath(), $"sf-mute-{Guid.NewGuid():N}.json");
        await using var session = new StreamingSession(target, [], spec, new StreamOptions(MuteLocal: false), file);

        // State is still Starting (never started): turning it on must not mute anything, so it stays false-safe.
        await session.StopAsync();
        Assert.False(await session.SetMuteLocalAsync(true)); // stopped session
        Assert.False(File.Exists(file));                     // nothing was recorded or muted
    }

    [Fact]
    public async Task Restoring_for_sign_out_is_synchronous_idempotent_and_final()
    {
        var target = new SonosDevice(IPAddress.Loopback, "Test", "Sonos Test", "S0", "uuid:test");
        var file = Path.Combine(Path.GetTempPath(), $"sf-mute-{Guid.NewGuid():N}.json");
        await using var session = new StreamingSession(target, [], CaptureSpec.SystemOutput(null), new StreamOptions(MuteLocal: true), file);

        session.RestoreLocalNow(TimeSpan.FromSeconds(1));
        session.RestoreLocalNow(TimeSpan.FromSeconds(1)); // twice: sign-out may call it again
        Assert.False(session.IsActive);                   // nothing may guard the PC afterwards
        Assert.False(await session.SetMuteLocalAsync(true));
        Assert.False(File.Exists(file));
    }
}
