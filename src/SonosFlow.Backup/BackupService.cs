using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonosFlow.Backup;

/// <summary>
/// Timestamped, verified, never-overwritten copies of the Sonos Flow project, plus safe restore.
/// Layout: &lt;BackupRoot&gt;/SonosFlow_yyyy-MM-dd_HH-mm-ss/{files/…, manifest.json} and &lt;BackupRoot&gt;/backup-log.csv.
/// Nothing here deletes a finished backup or a project file.
/// </summary>
public sealed class BackupService(string sourceRoot, string backupRoot, BackupRules? rules = null)
{
    public const string IdPrefix = "SonosFlow_";
    private const string PartialSuffix = ".partial";
    private const string ManifestName = "manifest.json";
    private const string FilesDir = "files";
    private const string LogName = "backup-log.csv";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string SourceRoot { get; } = Path.GetFullPath(sourceRoot);
    public string BackupRoot { get; } = Path.GetFullPath(backupRoot);
    public BackupRules Rules { get; } = rules ?? new BackupRules();
    public string LogPath => Path.Combine(BackupRoot, LogName);

    /// <summary>Default location: the user profile, outside the project and outside OneDrive-synced folders.</summary>
    public static string DefaultBackupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SonosFlow-Backups");

    /// <summary>Walks up from <paramref name="start"/> to the folder containing SonosFlow.sln.</summary>
    public static string? FindProjectRoot(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SonosFlow.sln"))) return dir.FullName;
        return null;
    }

    // ── Create ─────────────────────────────────────────────────

    public BackupResult Create(string reason, IProgress<string>? progress = null)
    {
        string? partial = null;
        try
        {
            if (!Directory.Exists(SourceRoot)) throw new DirectoryNotFoundException($"No existe el proyecto: {SourceRoot}");
            if (IsInside(BackupRoot, SourceRoot)) throw new InvalidOperationException("La carpeta de copias no puede estar dentro del proyecto.");
            Directory.CreateDirectory(BackupRoot);

            var id = NewUniqueId();
            partial = Path.Combine(BackupRoot, id + PartialSuffix);
            Directory.CreateDirectory(partial);

            var manifest = new BackupManifest
            {
                Id = id,
                CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                SourceRoot = SourceRoot,
                Reason = reason,
                Excluded = [.. Rules.ExcludedDirectories.Order().Select(d => d + "/"), .. Rules.ExcludedFilePatterns],
            };

            foreach (var file in EnumerateProjectFiles(SourceRoot))
            {
                var rel = Relative(SourceRoot, file);
                if (Rules.IsSecretFile(file))
                {
                    manifest.SkippedSecrets.Add(rel);
                    continue;
                }
                var dest = Path.Combine(partial, FilesDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var (size, hash) = CopyWithHash(file, dest);
                manifest.Files.Add(new BackupFileEntry(rel, size, hash));
                progress?.Report(rel);
            }
            manifest.FileCount = manifest.Files.Count;
            manifest.TotalBytes = manifest.Files.Sum(f => f.Size);

            // Re-read what landed on disk before declaring the backup good. A project missing essential
            // files is still backed up (that is when protection matters most), but flagged.
            var check = VerifyFiles(partial, manifest);
            if (check.Missing.Count > 0 || check.Corrupted.Count > 0) throw new IOException($"La copia no superó la verificación: {check.Summary}");
            var warning = check.MissingEssentials.Count > 0 ? $"; AVISO: el proyecto no contiene {string.Join(" ", check.MissingEssentials)}" : "";

            File.WriteAllText(Path.Combine(partial, ManifestName), JsonSerializer.Serialize(manifest, Json), new UTF8Encoding(false));
            var final = Path.Combine(BackupRoot, id);
            Directory.Move(partial, final); // fails rather than overwrite if 'final' appeared meanwhile
            partial = null;

            Log("create", id, warning.Length == 0 ? "OK" : "OK-AVISO", manifest.FileCount, manifest.TotalBytes, final,
                $"{reason} (verificada: {check.Checked} SHA-256{(manifest.SkippedSecrets.Count > 0 ? $"; omitidos por posibles secretos: {string.Join(" ", manifest.SkippedSecrets)}" : "")}){warning}");
            return new BackupResult(true, id, final, manifest, warning.Length == 0 ? null : warning.TrimStart(';', ' '));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            // Only our own unfinished '.partial' folder is removed; finished backups are never touched.
            if (partial is not null) TryDeleteDirectory(partial);
            Log("create", "-", "ERROR", 0, 0, BackupRoot, ex.Message);
            return new BackupResult(false, null, null, null, ex.Message);
        }
    }

    // ── List / verify ──────────────────────────────────────────

    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(BackupRoot)) return [];
        return Directory.EnumerateDirectories(BackupRoot, IdPrefix + "*")
            .Where(d => !d.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(d =>
            {
                var id = Path.GetFileName(d);
                try { return new BackupInfo(id, d, ReadManifest(d), null); }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    return new BackupInfo(id, d, null, "manifiesto ilegible o ausente");
                }
            })
            .OrderByDescending(b => b.Id, StringComparer.Ordinal)
            .ToList();
    }

    public VerifyResult Verify(string id)
    {
        var dir = BackupDirectory(id);
        BackupManifest manifest;
        try { manifest = ReadManifest(dir); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            var failed = new VerifyResult(id, false, 0, [], [], []);
            Log("verify", id, "ERROR", 0, 0, dir, failed.Summary);
            return failed;
        }
        var result = VerifyFiles(dir, manifest);
        Log("verify", id, result.IsIntact ? "OK" : "DAÑADA", result.Checked, manifest.TotalBytes, dir, result.Summary);
        return result;
    }

    // ── Restore ────────────────────────────────────────────────

    /// <summary>Read-only preview of what <see cref="Restore"/> would change in <paramref name="targetRoot"/>.</summary>
    public RestorePlan Plan(string id, string? targetRoot = null)
    {
        var target = Path.GetFullPath(targetRoot ?? SourceRoot);
        var manifest = ReadManifest(BackupDirectory(id));
        List<string> add = [], overwrite = [], same = [];
        var inBackup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            var rel = entry.Path.Replace('\\', '/');
            inBackup.Add(rel);
            var path = Path.Combine(target, SafeRelative(rel));
            if (!File.Exists(path)) add.Add(rel);
            else if (!string.Equals(HashFile(path), entry.Sha256, StringComparison.OrdinalIgnoreCase)) overwrite.Add(rel);
            else same.Add(rel);
        }
        var extra = Directory.Exists(target)
            ? EnumerateProjectFiles(target).Select(f => Relative(target, f)).Where(r => !inBackup.Contains(r)).ToList()
            : [];
        return new RestorePlan(id, target, add, overwrite, same, extra);
    }

    /// <summary>
    /// Restores backup <paramref name="id"/> into <paramref name="targetRoot"/> (default: the project).
    /// Verifies the backup first, backs up the current target, writes only changed files,
    /// never deletes files that are not in the backup, then re-verifies the result.
    /// The caller is responsible for asking the user to confirm the <see cref="Plan"/> first.
    /// </summary>
    public RestoreResult Restore(string id, string? targetRoot = null)
    {
        var target = Path.GetFullPath(targetRoot ?? SourceRoot);
        RestorePlan? plan = null;
        string? safetyId = null;
        int written = 0;
        try
        {
            if (IsInside(target, BackupRoot)) throw new InvalidOperationException("No se puede restaurar dentro de la carpeta de copias.");
            var verify = Verify(id);
            if (!verify.IsIntact) throw new InvalidOperationException($"La copia {id} no es íntegra ({verify.Summary}); no se restaura.");

            plan = Plan(id, target);
            var dir = BackupDirectory(id);

            if (plan.Changes > 0 && Directory.Exists(target) && EnumerateProjectFiles(target).Any())
            {
                var safety = new BackupService(target, BackupRoot, Rules).Create($"Automática antes de restaurar {id}");
                if (!safety.Success) throw new IOException($"No se pudo copiar el estado actual antes de restaurar: {safety.Error}");
                safetyId = safety.Id;
            }

            foreach (var rel in plan.ToAdd.Concat(plan.ToOverwrite))
            {
                var source = Path.Combine(dir, FilesDir, SafeRelative(rel));
                var dest = Path.Combine(target, SafeRelative(rel));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var temp = dest + ".sfrestore.tmp";
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, dest, overwrite: true); // previous content is in the safety backup
                written++;
            }

            // The restored tree must now match the backup exactly (files not in the backup were left alone).
            var manifest = ReadManifest(dir);
            var mismatched = manifest.Files.Where(e =>
            {
                var p = Path.Combine(target, SafeRelative(e.Path));
                return !File.Exists(p) || !string.Equals(HashFile(p), e.Sha256, StringComparison.OrdinalIgnoreCase);
            }).Select(e => e.Path).ToList();
            var missingEssentials = Rules.EssentialFiles.Where(e => !File.Exists(Path.Combine(target, e))).ToList();
            if (mismatched.Count > 0) throw new IOException($"Tras restaurar, {mismatched.Count} archivos no coinciden con la copia (p. ej. {mismatched[0]}).");

            bool ok = missingEssentials.Count == 0;
            Log("restore", id, ok ? "OK" : "INCOMPLETA", written, 0, target,
                $"escritos {written}, sin cambios {plan.Unchanged.Count}, no presentes en la copia (intactos) {plan.NotInBackup.Count}; copia previa {safetyId ?? "no necesaria"}"
                + (ok ? "" : $"; faltan esenciales: {string.Join(" ", missingEssentials)}"));
            return new RestoreResult(ok, plan, safetyId, written, missingEssentials,
                ok ? null : "La estructura restaurada no es coherente: faltan archivos esenciales.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException
                                       or InvalidDataException or ArgumentException)
        {
            Log("restore", id, "ERROR", written, 0, target, ex.Message + (safetyId is null ? "" : $" (estado previo en {safetyId})"));
            return new RestoreResult(false, plan, safetyId, written, [], ex.Message);
        }
    }

    // ── Log ────────────────────────────────────────────────────

    public IReadOnlyList<string> ReadLog() => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : [];

    private void Log(string operation, string id, string result, int files, long bytes, string location, string detail)
    {
        try
        {
            Directory.CreateDirectory(BackupRoot);
            if (!File.Exists(LogPath))
                File.WriteAllText(LogPath, "timestamp_utc;id;operation;result;files;bytes;location;detail" + Environment.NewLine, new UTF8Encoding(true));
            string Clean(string s) => s.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
            File.AppendAllText(LogPath, string.Join(';',
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Clean(id), operation, result,
                files.ToString(CultureInfo.InvariantCulture), bytes.ToString(CultureInfo.InvariantCulture), Clean(location), Clean(detail)) + Environment.NewLine);
        }
        catch (IOException) { /* logging must never break a backup */ }
    }

    // ── Helpers ────────────────────────────────────────────────

    private string NewUniqueId()
    {
        var baseId = IdPrefix + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var id = baseId;
        for (int n = 2; Directory.Exists(Path.Combine(BackupRoot, id)) || Directory.Exists(Path.Combine(BackupRoot, id + PartialSuffix)); n++)
            id = $"{baseId}_{n}";
        return id;
    }

    private string BackupDirectory(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
            throw new ArgumentException($"Identificador de copia no válido: {id}");
        return Path.Combine(BackupRoot, id);
    }

    private static BackupManifest ReadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestName);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), Json)
                       ?? throw new JsonException("Manifiesto vacío");
        return manifest;
    }

    private VerifyResult VerifyFiles(string dir, BackupManifest manifest)
    {
        List<string> missing = [], corrupted = [];
        foreach (var entry in manifest.Files)
        {
            var path = Path.Combine(dir, FilesDir, SafeRelative(entry.Path));
            if (!File.Exists(path)) missing.Add(entry.Path);
            else if (!string.Equals(HashFile(path), entry.Sha256, StringComparison.OrdinalIgnoreCase)) corrupted.Add(entry.Path);
        }
        var listed = new HashSet<string>(manifest.Files.Select(f => SafeRelative(f.Path)), StringComparer.OrdinalIgnoreCase);
        var missingEssentials = Rules.EssentialFiles.Where(e => !listed.Contains(SafeRelative(e))).ToList();
        return new VerifyResult(manifest.Id, true, manifest.Files.Count, missing, corrupted, missingEssentials);
    }

    private IEnumerable<string> EnumerateProjectFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (Rules.IsExcludedDirectory(name)) continue;
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; // don't follow links out of the project
                stack.Push(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (Rules.IsExcludedFile(name) || name.EndsWith(".sfrestore.tmp", StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }
    }

    private static (long Size, string Sha256) CopyWithHash(string source, string dest)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write); // CreateNew: never overwrite
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            output.Write(buffer, 0, n);
            size += n;
        }
        return (size, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Rejects manifest paths that would escape the target folder.</summary>
    private static string SafeRelative(string path)
    {
        var p = path.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(p) || p.Split(Path.DirectorySeparatorChar).Any(s => s == ".."))
            throw new InvalidDataException($"Ruta no permitida en el manifiesto: {path}");
        return p;
    }

    private static bool IsInside(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
