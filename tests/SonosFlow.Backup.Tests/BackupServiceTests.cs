using SonosFlow.Backup;

namespace SonosFlow.Backup.Tests;

/// <summary>Each test builds a tiny fake Sonos Flow project in its own temp folder.</summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sf-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _project;
    private readonly string _backups;

    public BackupServiceTests()
    {
        _project = Path.Combine(_root, "project");
        _backups = Path.Combine(_root, "backups");
        Write("SonosFlow.sln", "solution");
        Write("README.md", "readme");
        Write("src/SonosFlow.Core/SonosFlow.Core.csproj", "<Project />");
        Write("src/SonosFlow.App/SonosFlow.App.csproj", "<Project />");
        Write("src/SonosFlow.Setup/SonosFlow.Setup.csproj", "<Project />");
        Write("src/SonosFlow.Core/Streaming/StreamingSession.cs", "class StreamingSession {}");
        Write("src/SonosFlow.App/Assets/icon.png", "\x89PNG binary-ish");
        Write("src/SonosFlow.App/bin/Release/SonosFlow.exe", "build output");
        Write("src/SonosFlow.App/obj/project.assets.json", "{}");
        Write("artifacts/SonosFlow-Setup.exe", "installer");
        Write("src/SonosFlow.App/SonosFlow.App.csproj.user", "ide state");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private BackupService Service() => new(_project, _backups);

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_project, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_project, rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Create_copies_sources_and_resources_but_not_build_output()
    {
        var r = Service().Create("prueba");

        Assert.True(r.Success, r.Error);
        var paths = r.Manifest!.Files.Select(f => f.Path).ToHashSet();
        Assert.Contains("src/SonosFlow.Core/Streaming/StreamingSession.cs", paths);
        Assert.Contains("src/SonosFlow.App/Assets/icon.png", paths);
        Assert.DoesNotContain(paths, p => p.Contains("/bin/") || p.Contains("/obj/") || p.StartsWith("artifacts/") || p.EndsWith(".user"));
        Assert.Equal(7, r.Manifest.FileCount);
        Assert.True(File.Exists(Path.Combine(r.Directory!, "manifest.json")));
        Assert.Equal(r.Manifest.Files.Single(f => f.Path == "README.md").Sha256,
            BackupService.HashFile(Path.Combine(r.Directory!, "files", "README.md")));
    }

    [Fact]
    public void Backups_never_overwrite_each_other()
    {
        var s = Service();
        var a = s.Create("uno");
        var b = s.Create("dos"); // same second → must get a different id
        var c = s.Create("tres");

        Assert.True(a.Success && b.Success && c.Success);
        Assert.Equal(3, new[] { a.Id, b.Id, c.Id }.Distinct().Count());
        Assert.Equal(3, s.List().Count);
        Assert.Equal("uno", s.List().Single(x => x.Id == a.Id).Manifest!.Reason);
    }

    [Fact]
    public void Secrets_are_not_copied_and_are_reported()
    {
        Write(".env", "API_KEY=abc");
        Write("tools/deploy.ps1", "$k = 'ghp_" + new string('a', 36) + "'");
        Write("certs/dev.pfx", "binary");

        var r = Service().Create("con secretos");

        Assert.True(r.Success, r.Error);
        Assert.Equal([".env", "certs/dev.pfx", "tools/deploy.ps1"], r.Manifest!.SkippedSecrets.Order());
        Assert.DoesNotContain(r.Manifest.Files, f => f.Path is ".env" or "tools/deploy.ps1" or "certs/dev.pfx");
        Assert.False(File.Exists(Path.Combine(r.Directory!, "files", ".env")));
    }

    [Fact]
    public void Verify_detects_altered_and_missing_files()
    {
        var s = Service();
        var r = s.Create("x");
        Assert.True(s.Verify(r.Id!).IsIntact);

        File.AppendAllText(Path.Combine(r.Directory!, "files", "README.md"), "tampered");
        File.Delete(Path.Combine(r.Directory!, "files", "src", "SonosFlow.Core", "Streaming", "StreamingSession.cs"));
        var v = s.Verify(r.Id!);

        Assert.False(v.IsIntact);
        Assert.Contains("README.md", v.Corrupted);
        Assert.Contains("src/SonosFlow.Core/Streaming/StreamingSession.cs", v.Missing);
    }

    [Fact]
    public void Verify_flags_backup_without_essential_files()
    {
        File.Delete(Path.Combine(_project, "SonosFlow.sln"));
        var s = Service();
        var r = s.Create("sin sln");

        // A broken project is still protected, but the backup is flagged and won't be restored blindly.
        Assert.True(r.Success);
        Assert.Contains("SonosFlow.sln", r.Error);
        var v = s.Verify(r.Id!);
        Assert.False(v.IsIntact);
        Assert.Equal(["SonosFlow.sln"], v.MissingEssentials);
        Assert.False(s.Restore(r.Id!, Path.Combine(_root, "sandbox")).Success);
    }

    [Fact]
    public void Plan_lists_changes_without_touching_anything()
    {
        var s = Service();
        var r = s.Create("base");
        Write("README.md", "edited");
        File.Delete(Path.Combine(_project, "src", "SonosFlow.App", "Assets", "icon.png"));
        Write("docs/new.md", "new file");

        var plan = s.Plan(r.Id!);

        Assert.Equal(["README.md"], plan.ToOverwrite);
        Assert.Equal(["src/SonosFlow.App/Assets/icon.png"], plan.ToAdd);
        Assert.Equal(["docs/new.md"], plan.NotInBackup);
        Assert.Equal("edited", Read("README.md")); // preview only
    }

    [Fact]
    public void Restore_backs_up_current_state_first_and_keeps_extra_files()
    {
        var s = Service();
        var good = s.Create("versión buena");
        Write("README.md", "versión rota");
        Write("docs/notas.md", "trabajo nuevo que no está en la copia");

        var r = s.Restore(good.Id!);

        Assert.True(r.Success, r.Error);
        Assert.Equal("readme", Read("README.md"));
        Assert.Equal("trabajo nuevo que no está en la copia", Read("docs/notas.md")); // never deleted
        Assert.NotNull(r.SafetyBackupId);

        // The broken state is recoverable from the automatic safety backup.
        var safety = s.List().Single(b => b.Id == r.SafetyBackupId);
        Assert.Equal("versión rota", File.ReadAllText(Path.Combine(safety.Directory, "files", "README.md")));
        Assert.True(s.Verify(safety.Id).IsIntact);
        // And the restored backup is untouched.
        Assert.True(s.Verify(good.Id!).IsIntact);
        Assert.Equal(2, s.List().Count);
    }

    [Fact]
    public void Restore_into_isolated_folder_leaves_project_untouched()
    {
        var s = Service();
        var b = s.Create("base");
        Write("README.md", "cambio local");
        var target = Path.Combine(_root, "sandbox");

        var r = s.Restore(b.Id!, target);

        Assert.True(r.Success, r.Error);
        Assert.Equal("readme", File.ReadAllText(Path.Combine(target, "README.md")));
        Assert.Equal("cambio local", Read("README.md"));
        Assert.Null(r.SafetyBackupId); // empty target: nothing to protect
        Assert.Empty(r.MissingEssentialsAfter);
    }

    [Fact]
    public void Restore_refuses_a_damaged_backup_and_changes_nothing()
    {
        var s = Service();
        var b = s.Create("base");
        File.AppendAllText(Path.Combine(b.Directory!, "files", "SonosFlow.sln"), "corrupt");
        Write("README.md", "actual");

        var r = s.Restore(b.Id!);

        Assert.False(r.Success);
        Assert.Equal(0, r.Written);
        Assert.Equal("actual", Read("README.md"));
        Assert.Single(s.List()); // no safety backup was needed, none created
    }

    [Fact]
    public void Every_operation_is_logged()
    {
        var s = Service();
        var b = s.Create("log");
        s.Verify(b.Id!);
        s.Restore(b.Id!, Path.Combine(_root, "sandbox"));

        var log = s.ReadLog();
        Assert.StartsWith("timestamp_utc;id;operation;result", log[0]);
        Assert.Contains(log, l => l.Contains(";create;OK;"));
        Assert.Contains(log, l => l.Contains(";verify;OK;"));
        Assert.Contains(log, l => l.Contains(";restore;OK;"));
    }

    [Fact]
    public void Backup_folder_inside_project_is_rejected()
    {
        var r = new BackupService(_project, Path.Combine(_project, "Backups")).Create("dentro");
        Assert.False(r.Success);
    }

    [Fact]
    public void Reads_manifest_written_by_the_initial_PowerShell_backup()
    {
        var s = Service();
        var b = s.Create("x");
        // The very first backup was written by PowerShell: UTF-8 with BOM, no skippedSecrets field.
        var path = Path.Combine(b.Directory!, "manifest.json");
        var json = File.ReadAllText(path).Replace("\"skippedSecrets\": [],", "");
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(true));

        Assert.True(s.Verify(b.Id!).IsIntact);
    }
}
