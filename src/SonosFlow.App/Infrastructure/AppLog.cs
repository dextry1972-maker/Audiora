namespace SonosFlow.App.Infrastructure;

/// <summary>One line per app event (start/stop and why) in logs\app-events.log, to diagnose unexpected stops.</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                SonosFlow.Core.Diagnostics.RotatingLog.Append(Path.Combine(AppPaths.Logs, "app-events.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
