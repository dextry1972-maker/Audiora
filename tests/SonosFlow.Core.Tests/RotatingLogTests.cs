using SonosFlow.Core.Diagnostics;

namespace SonosFlow.Core.Tests;

public sealed class RotatingLogTests
{
    [Fact]
    public void Rotation_bounds_disk_use_and_keeps_recent_events_in_order()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sf-logs-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "events.log");
        try
        {
            for (int i = 0; i < 10; i++) RotatingLog.Append(path, $"event-{i}\n", maxBytes: 10, archives: 2);
            Assert.Equal("event-9\n", File.ReadAllText(path));
            Assert.Equal("event-8\n", File.ReadAllText(path + ".1"));
            Assert.Equal("event-7\n", File.ReadAllText(path + ".2"));
            Assert.Equal(3, Directory.GetFiles(directory).Length);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Concurrent_writers_preserve_complete_lines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sf-logs-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "events.log");
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => RotatingLog.Append(path, $"event-{i}\n"))));
            var lines = File.ReadAllLines(path);
            Assert.Equal(50, lines.Length);
            Assert.Equal(50, lines.Distinct().Count());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
