using System.Diagnostics;
using System.Text.Json;

namespace SonosFlow.App.Infrastructure;

/// <summary>Opt-in snapshot built from a fixed allow-list, without raw logs, paths or device identities.</summary>
public static class DiagnosticReport
{
    public static void Write(string outputPath)
    {
        using var process = Process.GetCurrentProcess();
        var report = new
        {
            Schema = 1,
            CreatedUtc = DateTime.UtcNow,
            Version = typeof(DiagnosticReport).Assembly.GetName().Version?.ToString(),
            Runtime = Environment.Version.ToString(),
            Windows = Environment.OSVersion.Version.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            ProcessPrivateBytes = process.PrivateMemorySize64,
            ProcessWorkingSetBytes = process.WorkingSet64,
            ProcessCpuSeconds = process.TotalProcessorTime.TotalSeconds,
            PendingLocalRestore = File.Exists(AppPaths.GuardStateFile),
            PendingApplicationRestore = File.Exists(Path.Combine(AppPaths.Local, "app-output-guard.json")),
            Note = "Instantánea del proceso exportador; no demuestra estabilidad de una transmisión. Los CSV locales contienen las métricas de sesión."
        };
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
    }
}
