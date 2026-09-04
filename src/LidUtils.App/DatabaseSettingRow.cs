using System.ComponentModel;
using System.Runtime.CompilerServices;
using LidUtils.Core;

namespace LidUtils.App;

/// <summary>
/// Presentation state for one database setting in the inline editor.
/// </summary>
public sealed class DatabaseSettingRow : INotifyPropertyChanged
{
    private string _draftValue;
    private string _validationError = string.Empty;
    private string _warning = string.Empty;
    private bool _isFavorite;
    private bool _isStaged;
    private bool _isValidating;
    private long _draftVersion;

    public DatabaseSettingRow(SettingEntry entry, bool isFavorite)
    {
        Entry = entry;
        _draftValue = entry.RawValue;
        _isFavorite = isFavorite;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<DatabaseSettingRow>? DraftChanged;

    public SettingEntry Entry { get; }
    public string Label => Entry.Label;
    public string Key => Entry.Key;
    public string RawValue => Entry.RawValue;
    public string TypeLabel => Entry.TypeLabel;
    public string SourceTable => Entry.SourceTable;
    public string DetailsToolTip => string.Join(Environment.NewLine,
        $"Database key: {Entry.Key}",
        Entry.Description,
        $"Raw units: {Entry.RawUnits}",
        $"Type: {Entry.TypeLabel} · Source: {Entry.SourceTable}");

    public string DraftValue
    {
        get => _draftValue;
        set
        {
            if (!SetField(ref _draftValue, value)) return;
            _draftVersion++;
            ValidationError = string.Empty;
            Warning = string.Empty;
            DraftChanged?.Invoke(this);
        }
    }

    public string ValidationError { get => _validationError; set => SetField(ref _validationError, value); }
    public string Warning { get => _warning; set => SetField(ref _warning, value); }
    public bool IsFavorite { get => _isFavorite; set => SetField(ref _isFavorite, value); }
    public bool IsStaged { get => _isStaged; set => SetField(ref _isStaged, value); }
    public bool IsValidating { get => _isValidating; set => SetField(ref _isValidating, value); }
    public long DraftVersion => _draftVersion;

    public void SetDraftWithoutStaging(string value)
    {
        _draftVersion++;
        SetField(ref _draftValue, value, nameof(DraftValue));
    }

    public void ResetEditState()
    {
        SetDraftWithoutStaging(RawValue);
        ValidationError = string.Empty;
        Warning = string.Empty;
        IsStaged = false;
        IsValidating = false;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
