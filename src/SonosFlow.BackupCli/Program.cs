using SonosFlow.Backup;

// Sonos Flow — copias de seguridad del proyecto.
// Uso (desde la raíz del proyecto):  sfbackup <comando> [opciones]
//   create ["motivo"]          crea una copia verificada con fecha y hora
//   list                       enumera las copias (más reciente primero)
//   verify <id>                comprueba la integridad (SHA-256) de una copia
//   plan <id> [--target DIR]   muestra qué archivos cambiarían al restaurar (no modifica nada)
//   restore <id> [--target DIR] [--yes]
//                              restaura tras confirmación; antes copia el estado actual
//   log                        muestra el registro de operaciones
// Opciones: --project DIR (por defecto: se busca SonosFlow.sln), --backups DIR (por defecto: %USERPROFILE%\SonosFlow-Backups)

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string[] valueOptions = ["--project", "--backups", "--target"];
var positional = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (valueOptions.Contains(args[i])) { i++; continue; } // skip the option and its value
    if (!args[i].StartsWith("--")) positional.Add(args[i]);
}

var project = Opt("--project") ?? BackupService.FindProjectRoot(Environment.CurrentDirectory) ?? BackupService.FindProjectRoot(AppContext.BaseDirectory);
if (project is null)
{
    Console.Error.WriteLine("No se encontró SonosFlow.sln. Ejecuta desde el proyecto o usa --project <carpeta>.");
    return 2;
}
var service = new BackupService(project, Opt("--backups") ?? BackupService.DefaultBackupRoot);

switch (args[0])
{
    case "create":
    {
        var reason = positional.FirstOrDefault() ?? "Copia manual";
        Console.WriteLine($"Proyecto: {service.SourceRoot}");
        Console.WriteLine($"Creando copia en {service.BackupRoot} …");
        var r = service.Create(reason);
        if (!r.Success)
        {
            Console.Error.WriteLine($"ERROR: {r.Error}");
            return 1;
        }
        Console.WriteLine($"OK  {r.Id}");
        Console.WriteLine($"    Ubicación: {r.Directory}");
        Console.WriteLine($"    Archivos:  {r.Manifest!.FileCount} ({r.Manifest.TotalBytes / 1024.0:0.#} KB), verificados con SHA-256");
        Console.WriteLine($"    Excluido:  {service.Rules.Describe()}");
        if (r.Error is { } warning) Console.WriteLine($"    AVISO:     {warning}");
        if (r.Manifest.SkippedSecrets.Count > 0)
            Console.WriteLine($"    Omitidos por posibles secretos: {string.Join(", ", r.Manifest.SkippedSecrets)}");
        return 0;
    }

    case "list":
    {
        var all = service.List();
        if (all.Count == 0) { Console.WriteLine($"No hay copias en {service.BackupRoot}"); return 0; }
        Console.WriteLine($"Copias en {service.BackupRoot}:");
        foreach (var b in all)
        {
            if (b.Manifest is { } m)
                Console.WriteLine($"  {b.Id,-36} {m.CreatedLocal:yyyy-MM-dd HH:mm:ss}  {m.FileCount,4} arch.  {m.TotalBytes / 1024.0,8:0.#} KB  {m.Reason}");
            else
                Console.WriteLine($"  {b.Id,-36} ({b.Problem})");
        }
        return 0;
    }

    case "verify":
    {
        if (positional.FirstOrDefault() is not { } id) return Usage("verify <id>");
        var v = service.Verify(id);
        Console.WriteLine($"{id}: {v.Summary}");
        foreach (var f in v.Missing) Console.WriteLine($"  ausente:   {f}");
        foreach (var f in v.Corrupted) Console.WriteLine($"  alterado:  {f}");
        foreach (var f in v.MissingEssentials) Console.WriteLine($"  esencial no incluido: {f}");
        return v.IsIntact ? 0 : 1;
    }

    case "plan":
    {
        if (positional.FirstOrDefault() is not { } id) return Usage("plan <id>");
        PrintPlan(service.Plan(id, Opt("--target")));
        return 0;
    }

    case "restore":
    {
        if (positional.FirstOrDefault() is not { } id) return Usage("restore <id>");
        var target = Opt("--target");
        var v = service.Verify(id);
        Console.WriteLine($"Integridad de {id}: {v.Summary}");
        if (!v.IsIntact) return 1;
        var plan = service.Plan(id, target);
        PrintPlan(plan);
        if (plan.Changes == 0) { Console.WriteLine("Nada que restaurar: el destino ya coincide con la copia."); return 0; }

        if (!args.Contains("--yes"))
        {
            Console.WriteLine();
            Console.WriteLine("Antes de escribir se creará automáticamente una copia del estado actual del destino.");
            Console.Write("Escribe RESTAURAR para confirmar: ");
            if (Console.ReadLine()?.Trim() != "RESTAURAR") { Console.WriteLine("Cancelado. No se ha modificado nada."); return 3; }
        }

        var r = service.Restore(id, target);
        if (r.SafetyBackupId is not null) Console.WriteLine($"Copia previa del estado actual: {r.SafetyBackupId}");
        if (!r.Success)
        {
            Console.Error.WriteLine($"ERROR: {r.Error}");
            return 1;
        }
        Console.WriteLine($"OK: {r.Written} archivos restaurados y verificados; estructura del proyecto coherente.");
        return 0;
    }

    case "log":
        foreach (var line in service.ReadLog()) Console.WriteLine(line);
        return 0;

    default:
        PrintHelp();
        return 2;
}

static void PrintPlan(RestorePlan p)
{
    Console.WriteLine($"Restaurar {p.BackupId} en {p.TargetRoot}:");
    Console.WriteLine($"  se crearán:      {p.ToAdd.Count}");
    Console.WriteLine($"  se sobrescribirán (el contenido actual queda en la copia previa): {p.ToOverwrite.Count}");
    Console.WriteLine($"  sin cambios:     {p.Unchanged.Count}");
    Console.WriteLine($"  no están en la copia (NO se tocan): {p.NotInBackup.Count}");
    foreach (var f in p.ToAdd) Console.WriteLine($"    + {f}");
    foreach (var f in p.ToOverwrite) Console.WriteLine($"    ~ {f}");
    foreach (var f in p.NotInBackup.Take(20)) Console.WriteLine($"    = {f} (se conserva)");
}

static int Usage(string text)
{
    Console.Error.WriteLine($"Uso: sfbackup {text}");
    return 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Sonos Flow — copias de seguridad del proyecto

          sfbackup create ["motivo"]         Crear una copia verificada (fecha y hora, nunca sobrescribe)
          sfbackup list                      Enumerar copias
          sfbackup verify <id>               Verificar integridad (SHA-256)
          sfbackup plan <id> [--target DIR]  Ver qué cambiaría al restaurar (no modifica nada)
          sfbackup restore <id> [--target DIR]
                                             Restaurar con confirmación; copia antes el estado actual
          sfbackup log                       Ver el registro de operaciones

        Opciones: --project DIR  --backups DIR
        """);
}
