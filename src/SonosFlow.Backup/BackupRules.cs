using System.Text.RegularExpressions;

namespace SonosFlow.Backup;

/// <summary>What goes into a backup: source and resources yes; build output, IDE state and secrets no.</summary>
public sealed class BackupRules
{
    /// <summary>Directory names skipped anywhere in the tree (regenerable or not part of the project).</summary>
    public HashSet<string> ExcludedDirectories { get; init; } =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "artifacts", ".vs", ".vscode", ".git", "TestResults", "__pycache__", "node_modules" };

    public List<string> ExcludedFilePatterns { get; init; } = ["*.user", "*.suo", "*.pyc", "Thumbs.db", "Desktop.ini"];

    /// <summary>Names that usually hold credentials. Matching files are never copied, only reported.</summary>
    public List<string> SecretFilePatterns { get; init; } = [".env", ".env.*", "*.pfx", "*.p12", "*.pem", "*.key", "*.snk", "secrets.json", "id_rsa*", "*.kdbx"];

    /// <summary>Files a usable Sonos Flow checkout must contain; checked after backup and restore.</summary>
    public List<string> EssentialFiles { get; init; } =
    [
        "SonosFlow.sln",
        "README.md",
        "src/SonosFlow.Core/SonosFlow.Core.csproj",
        "src/SonosFlow.App/SonosFlow.App.csproj",
        "src/SonosFlow.Setup/SonosFlow.Setup.csproj",
    ];

    private static readonly Regex SecretContent = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|sk-[A-Za-z0-9]{32,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,}",
        RegexOptions.Compiled);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".xaml", ".csproj", ".sln", ".json", ".xml", ".config", ".md", ".txt", ".ps1", ".cmd", ".bat", ".py", ".manifest", ".yml", ".yaml", ".ini", ".props", ".targets" };

    public string Describe() =>
        string.Join(", ", ExcludedDirectories.Order().Select(d => d + "/").Concat(ExcludedFilePatterns));

    public bool IsExcludedDirectory(string name) => ExcludedDirectories.Contains(name);

    public bool IsExcludedFile(string name) => ExcludedFilePatterns.Any(p => Glob(name, p));

    public bool IsSecretFile(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (SecretFilePatterns.Any(p => Glob(name, p))) return true;
        if (!TextExtensions.Contains(Path.GetExtension(name))) return false;
        var info = new FileInfo(fullPath);
        if (info.Length > 2 * 1024 * 1024) return false;
        return SecretContent.IsMatch(File.ReadAllText(fullPath));
    }

    private static bool Glob(string name, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }
}
