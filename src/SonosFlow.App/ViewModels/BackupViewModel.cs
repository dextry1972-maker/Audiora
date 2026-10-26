using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using SonosFlow.App.Infrastructure;
using SonosFlow.Backup;

namespace SonosFlow.App.ViewModels;

public sealed class BackupRow(BackupInfo info) : ObservableObject
{
    private string _status = info.IsReadable ? "Sin verificar" : info.Problem ?? "Ilegible";

    public BackupInfo Info { get; } = info;
    public string Id => Info.Id;
    public string DateText => Info.Manifest?.CreatedLocal.ToString("dd/MM/yyyy HH:mm:ss") ?? "—";
    public string Details => Info.Manifest is { } m ? $"{m.FileCount} archivos · {m.TotalBytes / 1024.0:0.#} KB" : "—";
    public string Reason => Info.Manifest?.Reason ?? "";
    public string Status { get => _status; set => Set(ref _status, value); }
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
    public string ProjectText => ProjectPath ?? "No encontrado — elige la carpeta que contiene SonosFlow.sln";

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

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private BackupService? Service => ProjectPath is null ? null : new BackupService(ProjectPath, BackupRoot);

    private void Refresh()
    {
        var keep = Selected?.Id;
        Items.Clear();
        var list = (Service ?? new BackupService(Environment.CurrentDirectory, BackupRoot)).List();
        foreach (var b in list) Items.Add(new BackupRow(b));
        Selected = Items.FirstOrDefault(i => i.Id == keep) ?? Items.FirstOrDefault();
        if (StatusText.Length == 0)
            StatusText = list.Count switch { 0 => "Todavía no hay copias.", 1 => $"1 copia en {BackupRoot}", var n => $"{n} copias en {BackupRoot}" };
    }

    private void ChooseProject()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta del proyecto Audiora (contiene SonosFlow.sln)" };
        if (dialog.ShowDialog() != true) return;
        if (!File.Exists(Path.Combine(dialog.FolderName, "SonosFlow.sln")))
        {
            Show("Esa carpeta no contiene SonosFlow.sln.", MessageBoxImage.Warning);
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
        StatusText = "Creando copia…";
        var r = await Task.Run(() => service.Create("Copia manual desde Ajustes"));
        Busy = false;
        StatusText = r.Success
            ? $"Copia {r.Id} creada y verificada ({r.Manifest!.FileCount} archivos) en {r.Directory}" + (r.Error is { } w ? $". AVISO: {w}" : "")
            : $"Error al crear la copia: {r.Error}";
        Refresh();
        if (r.Success && Items.FirstOrDefault(i => i.Id == r.Id) is { } row)
        {
            row.Status = "Íntegra (verificada al crear)";
            Selected = row;
        }
        if (!r.Success) Show(StatusText, MessageBoxImage.Error);
    }

    private async Task VerifyAsync()
    {
        var row = Selected!;
        var service = Service!;
        Busy = true;
        StatusText = $"Verificando {row.Id}…";
        var v = await Task.Run(() => service.Verify(row.Id));
        Busy = false;
        row.Status = v.IsIntact ? "Íntegra" : "DAÑADA";
        StatusText = $"{row.Id}: {v.Summary}";
    }

    private async Task RestoreAsync()
    {
        var row = Selected!;
        var service = Service!;
        Busy = true;
        StatusText = $"Comprobando {row.Id}…";
        try
        {
            var v = await Task.Run(() => service.Verify(row.Id));
            row.Status = v.IsIntact ? "Íntegra" : "DAÑADA";
            if (!v.IsIntact)
            {
                StatusText = $"{row.Id}: {v.Summary}. No se puede restaurar.";
                Show(StatusText, MessageBoxImage.Error);
                return;
            }

            var plan = await Task.Run(() => service.Plan(row.Id));
            if (plan.Changes == 0)
            {
                StatusText = "El proyecto ya coincide con esa copia; no hay nada que restaurar.";
                Show(StatusText, MessageBoxImage.Information);
                return;
            }

            if (!Confirm(plan)) { StatusText = "Restauración cancelada. No se modificó nada."; return; }

            StatusText = "Restaurando…";
            var r = await Task.Run(() => service.Restore(row.Id));
            StatusText = r.Success
                ? $"Restaurada {row.Id}: {r.Written} archivos escritos y verificados. Estado anterior guardado en {r.SafetyBackupId}. Recompila para aplicar."
                : $"La restauración falló: {r.Error}" + (r.SafetyBackupId is null ? "" : $" El estado anterior está en {r.SafetyBackupId}.");
            Show(StatusText, r.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    private static bool Confirm(RestorePlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Restaurar la copia {plan.BackupId} en:\n{plan.TargetRoot}\n");
        sb.AppendLine($"• Se crearán: {plan.ToAdd.Count}");
        sb.AppendLine($"• Se sobrescribirán: {plan.ToOverwrite.Count}");
        sb.AppendLine($"• Sin cambios: {plan.Unchanged.Count}");
        sb.AppendLine($"• No están en la copia y NO se tocan: {plan.NotInBackup.Count}\n");
        foreach (var f in plan.ToOverwrite.Take(12)) sb.AppendLine($"   ~ {f}");
        foreach (var f in plan.ToAdd.Take(Math.Max(0, 12 - plan.ToOverwrite.Count))) sb.AppendLine($"   + {f}");
        if (plan.Changes > 12) sb.AppendLine($"   … y {plan.Changes - 12} más");
        sb.AppendLine("\nAntes de escribir se guardará automáticamente una copia del estado actual. Ninguna copia anterior se elimina.");
        sb.AppendLine("\n¿Restaurar?");
        return MessageBox.Show(Application.Current.MainWindow, sb.ToString(), "Restaurar copia de seguridad",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static void Show(string text, MessageBoxImage icon) =>
        MessageBox.Show(Application.Current.MainWindow, text, "Copias de seguridad", MessageBoxButton.OK, icon);
}
