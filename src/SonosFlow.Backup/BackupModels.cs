namespace SonosFlow.Backup;

/// <summary>One file inside a backup. Paths are relative to the project root and use '/'.</summary>
public sealed record BackupFileEntry(string Path, long Size, string Sha256);

/// <summary>manifest.json — written last, so a backup without one is incomplete.</summary>
public sealed class BackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string SourceRoot { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> Excluded { get; set; } = [];
    /// <summary>Files left out because they look like credentials (never copied).</summary>
    public List<string> SkippedSecrets { get; set; } = [];
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public List<BackupFileEntry> Files { get; set; } = [];

    public DateTime CreatedLocal =>
        DateTime.TryParse(CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : DateTime.MinValue;
}

public sealed record BackupInfo(string Id, string Directory, BackupManifest? Manifest, string? Problem)
{
    public bool IsReadable => Manifest is not null;
}

public sealed record BackupResult(bool Success, string? Id, string? Directory, BackupManifest? Manifest, string? Error);

public sealed record VerifyResult(
    string Id,
    bool ManifestReadable,
    int Checked,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Corrupted,
    IReadOnlyList<string> MissingEssentials)
{
    public bool IsIntact => ManifestReadable && Missing.Count == 0 && Corrupted.Count == 0 && MissingEssentials.Count == 0;

    public string Summary => !ManifestReadable
        ? "manifiesto ilegible o ausente"
        : IsIntact
            ? $"íntegra: {Checked} archivos verificados con SHA-256"
            : $"DAÑADA: {Missing.Count} ausentes, {Corrupted.Count} alterados, {MissingEssentials.Count} esenciales ausentes";
}

/// <summary>What a restore would do to the target folder.</summary>
public sealed record RestorePlan(
    string BackupId,
    string TargetRoot,
    IReadOnlyList<string> ToAdd,
    IReadOnlyList<string> ToOverwrite,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> NotInBackup)
{
    public int Changes => ToAdd.Count + ToOverwrite.Count;
}

public sealed record RestoreResult(
    bool Success,
    RestorePlan? Plan,
    string? SafetyBackupId,
    int Written,
    IReadOnlyList<string> MissingEssentialsAfter,
    string? Error);
