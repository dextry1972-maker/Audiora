using System.Net;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace SonosFlow.Core.Sonos;

public sealed class SonosSoapException(string action, int? upnpErrorCode, string body)
    : Exception($"{action} falló (UPnP error {upnpErrorCode?.ToString() ?? "?"})")
{
    public int? UpnpErrorCode { get; } = upnpErrorCode;
    public string Body { get; } = body;
}

/// <summary>Minimal UPnP SOAP client for a Sonos player (local port 1400). Control only — audio is pulled by the player over HTTP.</summary>
public sealed class SonosClient : IDisposable
{
    private const string AvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string RenderingControl = "urn:schemas-upnp-org:service:RenderingControl:1";

    private readonly HttpClient _http;

    public SonosClient(IPAddress address)
        : this(new HttpClient { BaseAddress = new Uri($"http://{address}:1400"), Timeout = TimeSpan.FromSeconds(5) }) { }

    internal SonosClient(HttpClient http) => _http = http;

    public Task SetStreamAsync(string streamUrl, string mimeType, string title, CancellationToken ct = default)
    {
        var didl =
            "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
            "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
            "<item id=\"-1\" parentID=\"-1\" restricted=\"1\">" +
            $"<dc:title>{SecurityElement.Escape(title)}</dc:title>" +
            "<upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
            $"<res protocolInfo=\"http-get:*:{mimeType}:*\">{SecurityElement.Escape(streamUrl)}</res>" +
            "</item></DIDL-Lite>";

        return InvokeAsync("/MediaRenderer/AVTransport/Control", AvTransport, "SetAVTransportURI",
            $"<InstanceID>0</InstanceID><CurrentURI>{SecurityElement.Escape(streamUrl)}</CurrentURI>" +
            $"<CurrentURIMetaData>{SecurityElement.Escape(didl)}</CurrentURIMetaData>", ct);
    }

    public Task PlayAsync(CancellationToken ct = default) =>
        InvokeAsync("/MediaRenderer/AVTransport/Control", AvTransport, "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>", ct);

    public Task PauseAsync(CancellationToken ct = default) =>
        InvokeAsync("/MediaRenderer/AVTransport/Control", AvTransport, "Pause", "<InstanceID>0</InstanceID>", ct);

    public Task StopAsync(CancellationToken ct = default) =>
        InvokeAsync("/MediaRenderer/AVTransport/Control", AvTransport, "Stop", "<InstanceID>0</InstanceID>", ct);

    public async Task<string> GetTransportStateAsync(CancellationToken ct = default)
    {
        var xml = await InvokeAsync("/MediaRenderer/AVTransport/Control", AvTransport, "GetTransportInfo", "<InstanceID>0</InstanceID>", ct);
        return xml.Descendants("CurrentTransportState").FirstOrDefault()?.Value ?? "?";
    }

    /// <summary>Playback position Sonos reports for the current stream (1 s resolution), or null if unparseable.</summary>
    public async Task<TimeSpan?> GetRelTimeAsync(CancellationToken ct = default)
    {
        var xml = await InvokeAsync("/MediaRenderer/AVTransport/Control", AvTransport, "GetPositionInfo", "<InstanceID>0</InstanceID>", ct);
        return TimeSpan.TryParse(xml.Descendants("RelTime").FirstOrDefault()?.Value, out var t) ? t : null;
    }

    /// <summary>URI the player is currently set to — used to tell whether our stream is still the active source.</summary>
    public async Task<string> GetCurrentUriAsync(CancellationToken ct = default)
    {
        var xml = await InvokeAsync("/MediaRenderer/AVTransport/Control", AvTransport, "GetMediaInfo", "<InstanceID>0</InstanceID>", ct);
        return xml.Descendants("CurrentURI").FirstOrDefault()?.Value ?? "";
    }

    public async Task<int> GetVolumeAsync(CancellationToken ct = default)
    {
        var xml = await InvokeAsync("/MediaRenderer/RenderingControl/Control", RenderingControl, "GetVolume",
            "<InstanceID>0</InstanceID><Channel>Master</Channel>", ct);
        return int.TryParse(xml.Descendants("CurrentVolume").FirstOrDefault()?.Value, out var v) ? v : -1;
    }

    public Task SetVolumeAsync(int volume, CancellationToken ct = default) =>
        InvokeAsync("/MediaRenderer/RenderingControl/Control", RenderingControl, "SetVolume",
            $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>{Math.Clamp(volume, 0, 100)}</DesiredVolume>", ct);

    // Speaker-side tone controls (Sonos range −10..+10). Applied by the Sonos itself, not by our DSP.
    public async Task<int> GetBassAsync(CancellationToken ct = default) =>
        ParseInt(await InvokeAsync("/MediaRenderer/RenderingControl/Control", RenderingControl, "GetBass", "<InstanceID>0</InstanceID>", ct), "CurrentBass");

    public Task SetBassAsync(int value, CancellationToken ct = default) =>
        InvokeAsync("/MediaRenderer/RenderingControl/Control", RenderingControl, "SetBass",
            $"<InstanceID>0</InstanceID><DesiredBass>{Math.Clamp(value, -10, 10)}</DesiredBass>", ct);

    public async Task<int> GetTrebleAsync(CancellationToken ct = default) =>
        ParseInt(await InvokeAsync("/MediaRenderer/RenderingControl/Control", RenderingControl, "GetTreble", "<InstanceID>0</InstanceID>", ct), "CurrentTreble");

    public Task SetTrebleAsync(int value, CancellationToken ct = default) =>
        InvokeAsync("/MediaRenderer/RenderingControl/Control", RenderingControl, "SetTreble",
            $"<InstanceID>0</InstanceID><DesiredTreble>{Math.Clamp(value, -10, 10)}</DesiredTreble>", ct);

    public async Task<bool> GetLoudnessAsync(CancellationToken ct = default) =>
        ParseInt(await InvokeAsync("/MediaRenderer/RenderingControl/Control", RenderingControl, "GetLoudness",
            "<InstanceID>0</InstanceID><Channel>Master</Channel>", ct), "CurrentLoudness") == 1;

    public Task SetLoudnessAsync(bool on, CancellationToken ct = default) =>
        InvokeAsync("/MediaRenderer/RenderingControl/Control", RenderingControl, "SetLoudness",
            $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredLoudness>{(on ? 1 : 0)}</DesiredLoudness>", ct);

    private static int ParseInt(XDocument xml, string element) =>
        int.TryParse(xml.Descendants(element).FirstOrDefault()?.Value, out var v) ? v : 0;

    private async Task<XDocument> InvokeAsync(string path, string service, string action, string args, CancellationToken ct)
    {
        var envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            $"<s:Body><u:{action} xmlns:u=\"{service}\">{args}</u:{action}></s:Body></s:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("SOAPACTION", $"\"{service}#{action}\"");

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            int? code = null;
            try
            {
                var err = XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
                if (int.TryParse(err, out var c)) code = c;
            }
            catch (System.Xml.XmlException) { }
            throw new SonosSoapException(action, code, body);
        }

        // Strip namespaces from the response so callers can query by local name.
        var doc = XDocument.Parse(body);
        foreach (var e in doc.Descendants()) e.Name = e.Name.LocalName;
        return doc;
    }

    public void Dispose() => _http.Dispose();
}
