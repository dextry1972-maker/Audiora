namespace SonosFlow.Core.Streaming;

/// <summary>
/// Problems the session reports to the user (<see cref="StreamStats.LastError"/>), as stable keys rather than text:
/// the app translates them into the chosen language, and the CSV logs stay language-independent.
/// Format: <c>key</c> or <c>key|argument</c> (the argument is an exception message or a number).
/// </summary>
public static class SessionMessages
{
    public const string SpeakerUnreachable = "core.speakerUnreachable";
    public const string CaptureStopped = "core.captureStopped";
    public const string RetryFailed = "core.retryFailed";
    public const string GaveUp = "core.gaveUp";
    public const string InternalError = "core.internalError";
    public const string OtherSource = "core.otherSource";
    public const string PausedFromSonos = "core.pausedFromSonos";
    public const string StoppedFromSonos = "core.stoppedFromSonos";

    public static string With(string key, string argument) => $"{key}|{argument.Replace('|', '/')}";
}
