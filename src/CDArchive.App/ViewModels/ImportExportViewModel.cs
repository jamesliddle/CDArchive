using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CDArchive.App.ViewModels;

public partial class ImportExportViewModel : ObservableObject
{
    private readonly ICanonDataService _svc;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;

    public ImportExportViewModel(ICanonDataService svc)
    {
        _svc = svc;
    }

    // ── Export ───────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task ExportComposersAsync()
    {
        var path = PickSaveFile("Export Composers",
            Path.GetFileName(_svc.ComposersFilePath),
            Path.GetDirectoryName(_svc.ComposersFilePath));
        if (path == null) return;

        await RunAsync(async () =>
        {
            var data = await _svc.LoadComposersAsync();
            var sorted = data.OrderBy(c => !string.IsNullOrEmpty(c.SortName) ? c.SortName : c.Name,
                StringComparer.OrdinalIgnoreCase).ToList();
            var json = JsonSerializer.Serialize(sorted, WriteOptions);
            await File.WriteAllTextAsync(path, json);
            return $"Exported {sorted.Count} composers to {Path.GetFileName(path)}.";
        });
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task ExportPiecesAsync()
    {
        var path = PickSaveFile("Export Pieces",
            Path.GetFileName(_svc.PiecesFilePath),
            Path.GetDirectoryName(_svc.PiecesFilePath));
        if (path == null) return;

        await RunAsync(async () =>
        {
            var data = await _svc.LoadPiecesAsync();
            var sorted = data
                .OrderBy(p => p.Composer ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.FormatCatalog(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var json = JsonSerializer.Serialize(sorted, WriteOptions);
            await File.WriteAllTextAsync(path, json);
            return $"Exported {sorted.Count} pieces to {Path.GetFileName(path)}.";
        });
    }

    /// <summary>
    /// Normalises the canonical JSON files: reloads and re-saves them, applying
    /// the standard sort order and stripping any null fields.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task NormalizeJsonAsync()
    {
        await RunAsync(async () =>
        {
            var composers = await _svc.LoadComposersAsync();
            await _svc.SaveComposersAsync(composers);

            var pieces = await _svc.LoadPiecesAsync();
            await _svc.SavePiecesAsync(pieces);

            var pickLists = await _svc.LoadPickListsAsync();
            await _svc.SavePickListsAsync(pickLists);

            return $"Normalised {composers.Count} composers and {pieces.Count} pieces.";
        });
    }

    // ── Import (merge — only adds records not already present) ───────────────

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task ImportComposersAsync()
    {
        var path = PickOpenFile("Import Composers JSON");
        if (path == null) return;

        await RunAsync(async () =>
        {
            var json = await File.ReadAllTextAsync(path);

            // Accept either a single composer object { } or an array [ ]
            List<CanonComposer> incoming;
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                incoming = JsonSerializer.Deserialize<List<CanonComposer>>(json, ReadOptions) ?? [];
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                incoming = [JsonSerializer.Deserialize<CanonComposer>(json, ReadOptions)!];
            else
                return "Unrecognised JSON format — expected an object or array of composers.";

            var existing = await _svc.LoadComposersAsync();
            var existingNames = existing
                .Select(c => c.Name ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toAdd = incoming.Where(c => !existingNames.Contains(c.Name ?? "")).ToList();
            if (toAdd.Count == 0)
                return "No new composers found — nothing imported.";

            existing.AddRange(toAdd);
            await _svc.SaveComposersAsync(existing);
            return $"Imported {toAdd.Count} new composer(s) from {Path.GetFileName(path)}.";
        });
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task ImportPiecesAsync()
    {
        var path = PickOpenFile("Import Pieces JSON");
        if (path == null) return;

        await RunAsync(async () =>
        {
            var json = await File.ReadAllTextAsync(path);

            // Accept either a single piece object { } or an array of pieces [ ]
            List<CanonPiece> incoming;
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                incoming = JsonSerializer.Deserialize<List<CanonPiece>>(json, ReadOptions) ?? [];
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                incoming = [JsonSerializer.Deserialize<CanonPiece>(json, ReadOptions)!];
            else
                return "Unrecognised JSON format — expected an object or array of pieces.";

            var existing = await _svc.LoadPiecesAsync();

            // Key: composer + title (case-insensitive). Pieces without either are always added.
            var existingKeys = existing
                .Where(p => p.Composer != null && p.Title != null)
                .Select(PieceKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toAdd = incoming
                .Where(p => p.Composer == null || p.Title == null || !existingKeys.Contains(PieceKey(p)))
                .ToList();

            if (toAdd.Count == 0)
                return "No new pieces found — nothing imported.";

            existing.AddRange(toAdd);
            await _svc.SavePiecesAsync(existing);
            return $"Imported {toAdd.Count} new piece(s) from {Path.GetFileName(path)}.";
        });
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lets the user pick replacement JSON files and overwrites the canonical
    /// data files with them. Useful for restoring from a backup.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task RestoreFromJsonAsync()
    {
        var composersPath = PickOpenFile("Select Composers JSON (cancel to keep current)");
        var piecesPath    = PickOpenFile("Select Pieces JSON (cancel to keep current)");

        if (composersPath == null && piecesPath == null)
        {
            StatusMessage = "No files selected — nothing changed.";
            return;
        }

        var lines = new List<string>();
        if (composersPath != null) lines.Add($"Composers: {Path.GetFileName(composersPath)}");
        if (piecesPath != null)    lines.Add($"Pieces: {Path.GetFileName(piecesPath)}");

        var result = MessageBox.Show(
            $"This will overwrite the canonical data files with:\n\n{string.Join("\n", lines)}\n\nContinue?",
            "Confirm Restore", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        await RunAsync(async () =>
        {
            var composerCount = 0;
            var pieceCount    = 0;

            if (composersPath != null)
            {
                var json = await File.ReadAllTextAsync(composersPath);
                var composers = JsonSerializer.Deserialize<List<CanonComposer>>(json, ReadOptions) ?? [];
                await _svc.SaveComposersAsync(composers);
                composerCount = composers.Count;
            }

            if (piecesPath != null)
            {
                var json = await File.ReadAllTextAsync(piecesPath);
                var pieces = JsonSerializer.Deserialize<List<CanonPiece>>(json, ReadOptions) ?? [];
                await _svc.SavePiecesAsync(pieces);
                pieceCount = pieces.Count;
            }

            return composersPath != null && piecesPath != null
                ? $"Restored {composerCount} composers and {pieceCount} pieces."
                : composersPath != null
                    ? $"Restored {composerCount} composers."
                    : $"Restored {pieceCount} pieces.";
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool IsNotBusy => !IsBusy;

    private async Task RunAsync(Func<Task<string>> action)
    {
        IsBusy = true;
        StatusMessage = "Working…";
        NotifyCommandsCanExecuteChanged();
        try
        {
            StatusMessage = await action();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyCommandsCanExecuteChanged();
        }
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        ExportComposersCommand.NotifyCanExecuteChanged();
        ExportPiecesCommand.NotifyCanExecuteChanged();
        NormalizeJsonCommand.NotifyCanExecuteChanged();
        ImportComposersCommand.NotifyCanExecuteChanged();
        ImportPiecesCommand.NotifyCanExecuteChanged();
        RestoreFromJsonCommand.NotifyCanExecuteChanged();
    }

    private static string PieceKey(CanonPiece p) =>
        $"{p.Composer?.Trim()}|{p.Title?.Trim()}";

    private static string? PickSaveFile(string title, string? defaultFileName, string? initialDirectory)
    {
        var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            FileName = defaultFileName ?? "",
            InitialDirectory = initialDirectory ?? "",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string? PickOpenFile(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
