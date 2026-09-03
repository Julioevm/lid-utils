using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using LidUtils.Core;
using LidUtils.Data;

namespace LidUtils.App;

public sealed class SaveEditorViewModel : INotifyPropertyChanged
{
    private readonly ISaveFileService _saveFileService;
    private readonly SaveChangeStagingService _staging = new();
    private IReadOnlyList<SaveValueEntry> _allValues = [];
    private IReadOnlyList<SaveValueEntry> _displayedValues = [];
    private SaveFileSnapshot? _snapshot;
    private SaveValueEntry? _selectedValue;
    private string _savePath = "No save selected";
    private string _statusTitle = "Ready to inspect saves";
    private string _statusDetails = "The editor will look for .sav files in the game's Savedata folder.";
    private string _metadata = "No save loaded.";
    private string _searchText = string.Empty;
    private string _proposedValue = string.Empty;
    private string _editorError = string.Empty;
    private string _editorWarning = string.Empty;
    private bool _isBusy;
    private bool _isApplying;

    public SaveEditorViewModel(ISaveFileService saveFileService)
    {
        _saveFileService = saveFileService;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SaveValueEntry> DisplayedValues
    {
        get => _displayedValues;
        private set => SetField(ref _displayedValues, value);
    }
    public ObservableCollection<StagedSaveChange> PendingChanges { get; } = [];

    public string SavePath { get => _savePath; private set => SetField(ref _savePath, value); }
    public string StatusTitle { get => _statusTitle; private set => SetField(ref _statusTitle, value); }
    public string StatusDetails { get => _statusDetails; private set => SetField(ref _statusDetails, value); }
    public string Metadata { get => _metadata; private set => SetField(ref _metadata, value); }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                ApplyFilter();
                OnPropertyChanged(nameof(HasSearchText));
            }
        }
    }

    public SaveValueEntry? SelectedValue
    {
        get => _selectedValue;
        set
        {
            if (!SetField(ref _selectedValue, value)) return;
            LoadSelectedEditState();
            OnPropertyChanged(nameof(HasSelectedValue));
            OnPropertyChanged(nameof(CanStage));
        }
    }

    public string ProposedValue
    {
        get => _proposedValue;
        set
        {
            if (!SetField(ref _proposedValue, value)) return;
            EditorError = string.Empty;
            EditorWarning = string.Empty;
        }
    }

    public string EditorError { get => _editorError; private set => SetField(ref _editorError, value); }
    public string EditorWarning { get => _editorWarning; private set => SetField(ref _editorWarning, value); }
    public bool HasSelectedValue => SelectedValue is not null;
    public bool HasPendingChanges => _staging.HasPendingChanges;
    public bool CanStage => HasSelectedValue && !IsBusy;
    public bool CanApply => HasPendingChanges && !IsBusy;
    public bool CanInteract => !IsBusy;
    public bool IsApplying { get => _isApplying; private set => SetField(ref _isApplying, value); }
    public bool HasSnapshot => _snapshot is not null;
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public string ValuesSummary => HasSearchText
        ? $"Showing {DisplayedValues.Count:N0} of {_allValues.Count:N0} entries"
        : $"Showing all {_allValues.Count:N0} entries";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanInteract));
            OnPropertyChanged(nameof(CanStage));
            OnPropertyChanged(nameof(CanApply));
        }
    }

    public async Task DiscoverAsync()
    {
        await RunBusyAsync(async cancellationToken =>
        {
            StatusTitle = "Searching for saves…";
            StatusDetails = $"Checking {SaveFileService.DefaultSaveDirectory}.";
            var paths = await _saveFileService.DiscoverAsync(cancellationToken);
            if (paths.Count == 0)
            {
                Clear("No save selected");
                StatusTitle = "No save found";
                StatusDetails = "No .sav file was found automatically. Use Browse to select one manually.";
                return;
            }

            await LoadCoreAsync(paths[0], cancellationToken);
        });
    }

    public Task ReloadAsync() => !File.Exists(SavePath)
        ? Task.CompletedTask
        : RunBusyAsync(token => LoadCoreAsync(SavePath, token));

    public Task SelectPathAsync(string path) => RunBusyAsync(token => LoadCoreAsync(path, token));

    public void ClearSearch() => SearchText = string.Empty;

    public void StageSelectedChange()
    {
        if (SelectedValue is null) return;
        var outcome = _staging.Stage(SelectedValue, ProposedValue);
        EditorError = outcome.Error ?? string.Empty;
        EditorWarning = outcome.Change?.Warning ??
            (outcome.WasReverted ? "Matches the original value; the pending change was removed." : string.Empty);
        RefreshPendingChanges();
    }

    public void ResetSelectedChange()
    {
        if (SelectedValue is null) return;
        _staging.Reset(SelectedValue.Pointer);
        ProposedValue = SelectedValue.Value;
        EditorError = string.Empty;
        EditorWarning = "Pending change removed.";
        RefreshPendingChanges();
    }

    public void ResetAllChanges()
    {
        _staging.ResetAll();
        LoadSelectedEditState();
        RefreshPendingChanges();
    }

    public Task ApplyAsync() => RunBusyAsync(async cancellationToken =>
    {
        if (_snapshot is null || !_staging.HasPendingChanges) return;
        IsApplying = true;
        try
        {
            StatusTitle = "Backing up and applying…";
            StatusDetails = "Rechecking the source, creating and verifying a backup, then atomically replacing the save.";
            var result = await _saveFileService.ApplyAsync(_snapshot, _staging.PendingChanges, cancellationToken);
            _staging.ResetAll();
            SetSnapshot(result.UpdatedSnapshot);
            StatusTitle = "Save updated safely";
            StatusDetails = $"Applied the staged changes. Verified backup: {result.BackupPath}";
        }
        finally
        {
            IsApplying = false;
        }
    });

    private async Task LoadCoreAsync(string path, CancellationToken cancellationToken)
    {
        Clear(path);
        StatusTitle = "Loading save…";
        StatusDetails = "Validating the BRG container, decompressing its JSON, and indexing editable values.";
        var snapshot = await _saveFileService.LoadAsync(path, cancellationToken);
        SetSnapshot(snapshot);
        StatusTitle = "Save ready";
        StatusDetails = $"All {snapshot.Entries.Count:N0} scalar entries are shown below. Type in the filter to narrow the list; nothing is written until Apply is confirmed.";
    }

    private void SetSnapshot(SaveFileSnapshot snapshot)
    {
        _snapshot = snapshot;
        SavePath = snapshot.Path;
        _allValues = snapshot.Entries
            .OrderBy(value => value.DisplayPath, StringComparer.Ordinal)
            .ToArray();
        SearchText = string.Empty;
        ApplyFilter();
        PendingChanges.Clear();
        SelectedValue = null;
        Metadata = string.Join(Environment.NewLine,
            $"BRG version {snapshot.Version} · {snapshot.ChunkCount} zlib chunks · {snapshot.Entries.Count:N0} scalar values",
            $"{FormatBytes(snapshot.FileLength)} compressed · {FormatBytes(snapshot.UncompressedLength)} JSON · modified {snapshot.LastWriteTimeUtc.ToLocalTime():g}",
            $"SHA-256: {snapshot.Sha256[..12]}…");
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(ValuesSummary));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanApply));
    }

    private void Clear(string path)
    {
        _snapshot = null;
        SavePath = path;
        _allValues = [];
        DisplayedValues = [];
        _staging.ResetAll();
        PendingChanges.Clear();
        SelectedValue = null;
        Metadata = "No save loaded.";
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(ValuesSummary));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanApply));
    }

    private void LoadSelectedEditState()
    {
        ProposedValue = SelectedValue is null
            ? string.Empty
            : _staging.Get(SelectedValue.Pointer)?.ProposedValue ?? SelectedValue.Value;
        EditorError = string.Empty;
        EditorWarning = _staging.Get(SelectedValue?.Pointer)?.Warning ?? string.Empty;
    }

    private void RefreshPendingChanges()
    {
        PendingChanges.Clear();
        foreach (var change in _staging.PendingChanges) PendingChanges.Add(change);
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanApply));
    }

    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        DisplayedValues = term.Length == 0
            ? _allValues
            : _allValues.Where(value =>
                    value.DisplayPath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    value.Value.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    value.TypeLabel.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (SelectedValue is not null && !DisplayedValues.Contains(SelectedValue))
        {
            SelectedValue = null;
        }

        OnPropertyChanged(nameof(ValuesSummary));
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception exception)
        {
            StatusTitle = "Save operation failed";
            StatusDetails = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatBytes(long bytes) => bytes < 1024 * 1024
        ? (bytes / 1024d).ToString("N1", CultureInfo.CurrentCulture) + " KB"
        : (bytes / (1024d * 1024d)).ToString("N1", CultureInfo.CurrentCulture) + " MB";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
