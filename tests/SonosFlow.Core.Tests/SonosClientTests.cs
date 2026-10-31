using System.Net;
using System.Xml;
using SonosFlow.Core.Sonos;
using SonosFlow.Core.Streaming;

namespace SonosFlow.Core.Tests;

public sealed class SonosClientTests
{
    private sealed class SimulatedSonos : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } = (_, _) =>
            Task.FromResult(Response("<CurrentTransportState>PLAYING</CurrentTransportState>"));
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Requests); return Reply(request, cancellationToken); }
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent($"<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body>{body}</s:Body></s:Envelope>") };

    private static SonosClient Client(SimulatedSonos handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1400"), Timeout = TimeSpan.FromSeconds(2) });

    [Fact]
    public async Task Transient_network_loss_then_speaker_restart_recovers_with_existing_backoff()
    {
        using var handler = new SimulatedSonos();
        using var client = Client(handler);
        handler.Reply = (_, _) => throw new HttpRequestException("simulated Wi-Fi loss");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTransportStateAsync());
        var policy = new RecoveryPolicy();
        Assert.Equal(RecoveryDecision.None, policy.Observe(TimeSpan.Zero, healthy: false));
        Assert.Equal(RecoveryDecision.Reconnect, policy.Observe(TimeSpan.FromSeconds(5), healthy: false));
        handler.Reply = (_, _) => Task.FromResult(Response("<CurrentTransportState>TRANSITIONING</CurrentTransportState>"));
        Assert.Equal("TRANSITIONING", await client.GetTransportStateAsync());
        handler.Reply = (_, _) => Task.FromResult(Response("<CurrentTransportState>PLAYING</CurrentTransportState>"));
        Assert.Equal("PLAYING", await client.GetTransportStateAsync());
        policy.Observe(TimeSpan.FromSeconds(6), healthy: true);
        policy.Observe(TimeSpan.FromSeconds(36), healthy: true);
        Assert.Equal(0, policy.Attempts);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task Unreachable_speaker_exhausts_policy_without_unlimited_retries()
    {
        using var handler = new SimulatedSonos { Reply = (_, _) => throw new HttpRequestException("offline") };
        using var client = Client(handler);
        var policy = new RecoveryPolicy();
        RecoveryDecision decision = RecoveryDecision.None;
        for (int second = 0; second <= 600; second++)
        {
            decision = policy.Observe(TimeSpan.FromSeconds(second), healthy: false);
            if (decision == RecoveryDecision.Reconnect)
                await Assert.ThrowsAsync<HttpRequestException>(() => client.PlayAsync());
            if (decision == RecoveryDecision.GiveUp) break;
        }
        Assert.Equal(RecoveryDecision.GiveUp, decision);
        Assert.Equal(RecoveryPolicy.MaxAttempts, handler.Requests);
    }

    [Fact]
    public async Task Cancellation_releases_a_speaker_request_that_never_replies()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SimulatedSonos
        {
            Reply = async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Response(""); }
        };
        using var client = Client(handler);
        using var cancel = new CancellationTokenSource();
        var pending = client.GetTransportStateAsync(cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Soap_failure_preserves_its_error_code()
    {
        using var handler = new SimulatedSonos
        {
            Reply = (_, _) => Task.FromResult(Response("<UPnPError><errorCode>701</errorCode></UPnPError>", HttpStatusCode.InternalServerError))
        };
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<SonosSoapException>(() => client.PlayAsync());
        Assert.Equal(701, error.UpnpErrorCode);
    }

    [Fact]
    public async Task Malformed_reply_is_reported_and_next_probe_can_succeed()
    {
        using var handler = new SimulatedSonos { Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<broken") }) };
        using var client = Client(handler);
        await Assert.ThrowsAsync<XmlException>(() => client.GetTransportStateAsync());
        handler.Reply = (_, _) => Task.FromResult(Response("<RelTime>00:00:42</RelTime>"));
        Assert.Equal(TimeSpan.FromSeconds(42), await client.GetRelTimeAsync());
    }
}
