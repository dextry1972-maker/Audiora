using System.Text;

namespace SonosFlow.Core.Diagnostics;

/// <summary>Bounded text logs; one named mutex also serializes separate application instances.</summary>
public static class RotatingLog
{
    public static void Append(string path, string text, long maxBytes = 2 * 1024 * 1024, int archives = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(archives);
        using var mutex = new Mutex(false, @"Local\SonosFlow-TextLogs");
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return; // Diagnostics must never stall audio or shutdown.
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // A single pathological exception must not grow the file beyond its bound.
            if (Encoding.UTF8.GetByteCount(text) > maxBytes)
                text = text[..Math.Min(text.Length, (int)Math.Min(int.MaxValue, maxBytes / 4))];
            if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(text) > maxBytes)
            {
                for (int i = archives - 1; i >= 1; i--)
                    if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), overwrite: true);
                File.Move(path, path + ".1", overwrite: true);
            }
            File.AppendAllText(path, text);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
