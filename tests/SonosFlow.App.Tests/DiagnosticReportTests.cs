using System.IO;
using System.Text.Json;
using SonosFlow.App.Infrastructure;

namespace SonosFlow.App.Tests;

public sealed class DiagnosticReportTests
{
    [Fact]
    public void Export_contains_only_allowlisted_fields_and_does_not_overwrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-report-{Guid.NewGuid():N}.json");
        try
        {
            DiagnosticReport.Write(path);
            string json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            string[] expected = ["Schema", "CreatedUtc", "Version", "Runtime", "Windows", "LogicalProcessors",
                "ProcessPrivateBytes", "ProcessWorkingSetBytes", "ProcessCpuSeconds", "PendingLocalRestore",
                "PendingApplicationRestore", "Note"];
            Assert.Equal(expected.Order(), document.RootElement.EnumerateObject().Select(p => p.Name).Order());
            Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), json);
            Assert.Throws<IOException>(() => DiagnosticReport.Write(path));
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
