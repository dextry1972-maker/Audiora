using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using SonosFlow.App.Infrastructure;
using SonosFlow.App.Localization;
using SonosFlow.Backup;

namespace SonosFlow.App.ViewModels;

public sealed class BackupRow(BackupInfo info) : ObservableObject
{
    // A key rather than text, so the row follows language changes. Unreadable backups show the library's reason.
    private string? _statusKey = info.IsReadable ? "backup.row.status.unverified" : null;

    public BackupInfo Info { get; } = info;
    public string Id => Info.Id;
    public string DateText => Info.Manifest?.CreatedLocal.ToString(Loc.Instance.T("backup.row.dateFormat"), Loc.Instance.Culture) ?? "—";
    public string Details => Info.Manifest is { } m
        ? Loc.Instance.Plural("backup.row.details", m.FileCount, m.FileCount, m.TotalBytes / 1024.0)
        : "—";
    public string Reason => Info.Manifest?.Reason ?? "";
    public string Status => _statusKey is { } key ? Loc.Instance.T(key) : Info.Problem ?? Loc.Instance.T("backup.row.status.unreadable");

    public void SetStatus(string key)
    {
        _statusKey = key;
        OnPropertyChanged(nameof(Status));
    }

    public void RefreshText() => OnPropertyChanged(string.Empty);
}

/// <summary>"Copias de seguridad" in Settings: backups of the Sonos Flow project source (not of the audio settings).</summary>
public sealed class BackupViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public BackupViewModel(AppSettings settings)
    {
        _settings = settings;
        _backupRoot = settings.BackupRoot ?? BackupService.DefaultBackupRoot;
        _projectPath = BackupService.FindProjectRoot(AppContext.BaseDirectory)
                       ?? (settings.ProjectPath is { } p && File.Exists(Path.Combine(p, "SonosFlow.sln")) ? p : null);

        CreateCommand = new RelayCommand(async () => await CreateAsync(), () => CanAct);
        VerifyCommand = new RelayCommand(async () => await VerifyAsync(), () => CanAct && Selected is not null);
        RestoreCommand = new RelayCommand(async () => await RestoreAsync(), () => CanAct && Selected is { Info.IsReadable: true });
        ChooseProjectCommand = new RelayCommand(ChooseProject, () => !Busy);
        OpenFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(BackupRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{BackupRoot}\"") { UseShellExecute = true });
        });
        RefreshCommand = new RelayCommand(Refresh, () => !Busy);
        Refresh();
        // Lives as long as the app (owned by MainViewModel), so the subscription is never removed.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<BackupRow> Items { get; } = [];
    public ICommand CreateCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ChooseProjectCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RefreshCommand { get; }

    private string? _projectPath;
    public string? ProjectPath
    {
        get => _projectPath;
        private set { if (Set(ref _projectPath, value)) { OnPropertyChanged(nameof(ProjectText)); OnPropertyChanged(nameof(CanAct)); } }
    }
    public string ProjectText => ProjectPath ?? Loc.Instance.T("backup.project.notFound");

    private string _backupRoot;
    public string BackupRoot { get => _backupRoot; private set => Set(ref _backupRoot, value); }

    private BackupRow? _selected;
    public BackupRow? Selected { get => _selected; set { Set(ref _selected, value); CommandManager.InvalidateRequerySuggested(); } }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) { OnPropertyChanged(nameof(CanAct)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool CanAct => !Busy && ProjectPath is not null;

    // Kept as a function so the last message is re-rendered in the new language.
    private Func<string>? _status;
    public string StatusText => _status?.Invoke() ?? "";

    private void SetStatus(Func<string> text)
    {
        _status = text;
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(string.Empty);
        foreach (var row in Items) row.RefreshText();
    }

    private BackupService? Service => ProjectPath is null ? null : new BackupService(ProjectPath, BackupRoot);

    private void Refresh()
    {
        var keep = Selected?.Id;
        Items.Clear();
        var list = (Service ?? new BackupService(Environment.CurrentDirectory, BackupRoot)).List();
        foreach (var b in list) Items.Add(new BackupRow(b));
        Selected = Items.FirstOrDefault(i => i.Id == keep) ?? Items.FirstOrDefault();
        if (_status is null)
        {
            var count = list.Count;
            SetStatus(() => count == 0
                ? Loc.Instance.T("backup.status.none")
                : Loc.Instance.Plural("backup.status.count", count, count, BackupRoot));
        }
    }

    private void ChooseProject()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Instance.T("backup.dialog.chooseProject") };
        if (dialog.ShowDialog() != true) return;
        if (!File.Exists(Path.Combine(dialog.FolderName, "SonosFlow.sln")))
        {
            Show(Loc.Instance.T("backup.dialog.notProject"), MessageBoxImage.Warning);
            return;
        }
        ProjectPath = dialog.FolderName;
        _settings.ProjectPath = ProjectPath;
        _settings.Save();
    }

    private async Task CreateAsync()
    {
        var service = Service!;
        Busy = true;
        SetStatus(() => Loc.Instance.T("backup.status.creating"));
        // The reason is stored in the backup manifest (shared with the CLI), so it stays language-independent.
        var r = await Task.Run(() => service.Create("Copia manual desde Ajustes"));
        Busy = false;
        SetStatus(() => r.Success
            ? Loc.Instance.Plural("backup.status.created", r.Manifest!.FileCount, r.Id, r.Manifest!.FileCount, r.Directory)
              + (r.Error is { } w ? Loc.Instance.F("backup.status.createdWarning", w) : "")
            : Loc.Instance.F("backup.status.createFailed", r.Error));
        Refresh();
        if (r.Success && Items.FirstOrDefault(i => i.Id == r.Id) is { } row)
        {
            row.SetStatus("backup.row.status.intactOnCreate");
            Selected = row;
        }
        if (!r.Success) Show(StatusText, MessageBoxImage.Error);
    }

    private async Task VerifyAsync()
    {
        var row = Selected!;
        var service = Service!;
        Busy = true;
        SetStatus(() => Loc.Instance.F("backup.status.verifying", row.Id));
        var v = await Task.Run(() => service.Verify(row.Id));
        Busy = false;
        row.SetStatus(v.IsIntact ? "backup.row.status.intact" : "backup.row.status.damaged");
        SetStatus(() => Loc.Instance.F("backup.status.verified", row.Id, Summary(v)));
    }

    private async Task RestoreAsync()
    {
        var row = Selected!;
        var service = Service!;
        Busy = true;
        SetStatus(() => Loc.Instance.F("backup.status.checking", row.Id));
        try
        {
            var v = await Task.Run(() => service.Verify(row.Id));
            row.SetStatus(v.IsIntact ? "backup.row.status.intact" : "backup.row.status.damaged");
            if (!v.IsIntact)
            {
                SetStatus(() => Loc.Instance.F("backup.status.cannotRestore", row.Id, Summary(v)));
                Show(StatusText, MessageBoxImage.Error);
                return;
            }

            var plan = await Task.Run(() => service.Plan(row.Id));
            if (plan.Changes == 0)
            {
                SetStatus(() => Loc.Instance.T("backup.status.nothingToRestore"));
                Show(StatusText, MessageBoxImage.Information);
                return;
            }

            if (!Confirm(plan)) { SetStatus(() => Loc.Instance.T("backup.status.restoreCancelled")); return; }

            SetStatus(() => Loc.Instance.T("backup.status.restoring"));
            var r = await Task.Run(() => service.Restore(row.Id));
            SetStatus(() => r.Success
                ? Loc.Instance.Plural("backup.status.restored", r.Written, row.Id, r.Written, r.SafetyBackupId)
                : Loc.Instance.F("backup.status.restoreFailed", r.Error)
                  + (r.SafetyBackupId is null ? "" : Loc.Instance.F("backup.status.restoreFailedSafety", r.SafetyBackupId)));
            Show(StatusText, r.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    /// <summary>Localized counterpart of <see cref="VerifyResult.Summary"/> (which stays Spanish for the backup log).</summary>
    private static string Summary(VerifyResult v) =>
        !v.ManifestReadable ? Loc.Instance.T("backup.summary.unreadable")
        : v.IsIntact ? Loc.Instance.Plural("backup.summary.intact", v.Checked)
        : Loc.Instance.F("backup.summary.damaged", v.Missing.Count, v.Corrupted.Count, v.MissingEssentials.Count);

    private static bool Confirm(RestorePlan plan)
    {
        var loc = Loc.Instance;
        var sb = new StringBuilder();
        sb.AppendLine(loc.F("backup.confirm.target", plan.BackupId));
        sb.AppendLine(plan.TargetRoot);
        sb.AppendLine();
        sb.AppendLine(loc.F("backup.confirm.toAdd", plan.ToAdd.Count));
        sb.AppendLine(loc.F("backup.confirm.toOverwrite", plan.ToOverwrite.Count));
        sb.AppendLine(loc.F("backup.confirm.unchanged", plan.Unchanged.Count));
        sb.AppendLine(loc.F("backup.confirm.notInBackup", plan.NotInBackup.Count));
        sb.AppendLine();
        foreach (var f in plan.ToOverwrite.Take(12)) sb.AppendLine($"   ~ {f}");
        foreach (var f in plan.ToAdd.Take(Math.Max(0, 12 - plan.ToOverwrite.Count))) sb.AppendLine($"   + {f}");
        if (plan.Changes > 12) sb.AppendLine("   " + loc.F("backup.confirm.more", plan.Changes - 12));
        sb.AppendLine();
        sb.AppendLine(loc.T("backup.confirm.safety"));
        sb.AppendLine();
        sb.AppendLine(loc.T("backup.confirm.question"));
        return MessageBox.Show(Application.Current.MainWindow, sb.ToString(), loc.T("backup.confirm.title"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static void Show(string text, MessageBoxImage icon) =>
        MessageBox.Show(Application.Current.MainWindow, text, Loc.Instance.T("backup.dialog.title"), MessageBoxButton.OK, icon);
}
