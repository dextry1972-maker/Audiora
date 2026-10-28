using SonosFlow.Core.Streaming;

namespace SonosFlow.Core.Tests;

public sealed class StallTrackerTests
{
    private static StreamStats Second(int t, double rate = 192, double backlog = 10, int clients = 1) =>
        new(SessionState.Streaming, "PLAYING", TimeSpan.FromSeconds(t), rate, 0, 1.2, 0, 0, clients, 0, null, BacklogMaxMs: backlog);

    [Fact]
    public void Normal_streaming_has_no_stalls()
    {
        var t = new StallTracker(192, 1000);
        for (int i = 1; i < 600; i++) Assert.Null(t.Observe(Second(i, rate: i % 7 == 0 ? 160 : 194, backlog: 60)));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Connection_warmup_is_ignored()
    {
        var t = new StallTracker(192, 1000);
        t.Observe(Second(1, rate: 40, backlog: 900));
        t.Observe(Second(2, rate: 90));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Consecutive_bad_seconds_are_one_episode_and_the_worst_is_kept()
    {
        var t = new StallTracker(192, 1000);
        Assert.Null(t.Observe(Second(10)));
        Assert.Equal(400, t.Observe(Second(11, backlog: 400)));
        Assert.Null(t.Observe(Second(12, rate: 0, backlog: 1400))); // same episode
        Assert.Null(t.Observe(Second(13, rate: 380)));               // catch-up burst ends it
        Assert.Equal(1, t.Count);
        Assert.Equal(1400, t.WorstMs);
        Assert.Equal(1, t.OverMarginCount);

        Assert.NotNull(t.Observe(Second(20, rate: 96))); // half a second missing: a second, shorter episode
        Assert.Equal(2, t.Count);
        Assert.Equal(1, t.OverMarginCount);
    }

    [Fact]
    public void A_reconnect_and_its_margin_burst_are_not_counted()
    {
        var t = new StallTracker(192, 1000);
        t.RestartWarmup(TimeSpan.FromSeconds(100));
        Assert.Null(t.Observe(Second(101, rate: 20, backlog: 1000)));
        Assert.Null(t.Observe(Second(103, rate: 30)));
        Assert.Equal(0, t.Count);
        Assert.NotNull(t.Observe(Second(110, rate: 20)));
    }

    [Fact]
    public void Without_a_connected_player_nothing_is_counted()
    {
        var t = new StallTracker(192, 0);
        Assert.Null(t.Observe(Second(10, rate: 0, clients: 0)));
        Assert.Equal(0, t.Count);
    }
}
