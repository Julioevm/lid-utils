using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using LidUtils.Core;

namespace LidUtils.App;

/// <summary>Editable presentation state for one row loaded by the Advanced table browser.</summary>
public sealed class AdvancedTableRow : INotifyPropertyChanged
{
    private readonly IReadOnlyList<string> _columnNames;
    private readonly IReadOnlyList<string> _primaryKeyColumns;

    public AdvancedTableRow(
        TablePreviewRow row,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<string> primaryKeyColumns,
        bool canEdit)
    {
        _columnNames = columnNames;
        _primaryKeyColumns = primaryKeyColumns;
        Cells = row.Cells.Select(cell => new AdvancedTableCell(cell, canEdit && !cell.IsBlob)).ToArray();
        foreach (var cell in Cells) cell.DraftChanged += (_, _) => OnRowChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? DraftChanged;
    public IReadOnlyList<AdvancedTableCell> Cells { get; }
    public bool IsStaged => Cells.Any(cell => cell.HasDraft);
    public bool HasInvalidDraft => Cells.Any(cell => !string.IsNullOrEmpty(cell.ValidationError));
    public bool HasChanges => Cells.Any(cell => cell.HasChanged);

    public StagedTableRowChange? BuildChange(string tableName)
    {
        if (!HasChanges || HasInvalidDraft) return null;
        var keys = _primaryKeyColumns.Select(column =>
        {
            var index = IndexOf(column);
            return new TableKeyValue(column, Cells[index].OriginalValue);
        }).ToArray();
        var changedCells = Cells
            .Select((cell, index) => (cell, index))
            .Where(item => item.cell.HasChanged)
            .Select(item => new StagedTableCellChange(
                _columnNames[item.index], item.cell.OriginalValue, item.cell.ProposedValue))
            .ToArray();
        return new StagedTableRowChange(tableName, keys, changedCells);
    }

    public void Undo()
    {
        foreach (var cell in Cells) cell.Undo();
    }

    private int IndexOf(string columnName)
    {
        for (var index = 0; index < _columnNames.Count; index++)
        {
            if (string.Equals(_columnNames[index], columnName, StringComparison.Ordinal)) return index;
        }
        throw new InvalidOperationException($"The primary-key column '{columnName}' is not in this preview.");
    }

    private void OnRowChanged()
    {
        OnPropertyChanged(nameof(IsStaged));
        OnPropertyChanged(nameof(HasInvalidDraft));
        OnPropertyChanged(nameof(HasChanges));
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AdvancedTableCell : INotifyPropertyChanged
{
    private string _draftValue;
    private string _validationError = string.Empty;
    private object? _proposedValue;

    public AdvancedTableCell(TablePreviewCell cell, bool isEditable)
    {
        OriginalValue = cell.Value;
        OriginalDisplayValue = cell.DisplayValue;
        IsEditable = isEditable;
        _draftValue = cell.Value is null ? "<NULL>" : cell.DisplayValue;
        _proposedValue = cell.Value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? DraftChanged;
    public object? OriginalValue { get; }
    public object? ProposedValue => _proposedValue;
    public string OriginalDisplayValue { get; }
    public bool IsEditable { get; }
    public string ValidationError { get => _validationError; private set => SetField(ref _validationError, value); }
    public bool HasChanged => string.IsNullOrEmpty(ValidationError) && !ValuesEqual(OriginalValue, ProposedValue);
    public bool HasDraft => !string.Equals(DraftValue, OriginalValue is null ? "<NULL>" : OriginalDisplayValue, StringComparison.Ordinal);

    public string DraftValue
    {
        get => _draftValue;
        set
        {
            if (!SetField(ref _draftValue, value)) return;
            Validate();
            OnPropertyChanged(nameof(HasChanged));
            OnPropertyChanged(nameof(HasDraft));
            DraftChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Undo()
    {
        _draftValue = OriginalValue is null ? "<NULL>" : OriginalDisplayValue;
        _proposedValue = OriginalValue;
        ValidationError = string.Empty;
        OnPropertyChanged(nameof(DraftValue));
        OnPropertyChanged(nameof(HasChanged));
        OnPropertyChanged(nameof(HasDraft));
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Validate()
    {
        if (!IsEditable)
        {
            _proposedValue = OriginalValue;
            ValidationError = "BLOB values cannot be edited here.";
            return;
        }

        if (string.Equals(DraftValue, "<NULL>", StringComparison.OrdinalIgnoreCase))
        {
            _proposedValue = null;
            ValidationError = string.Empty;
            return;
        }

        switch (OriginalValue)
        {
            case long:
                if (long.TryParse(DraftValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    _proposedValue = integer;
                    ValidationError = string.Empty;
                }
                else ValidationError = "Enter a whole-number integer, or <NULL>.";
                break;
            case double:
                if (double.TryParse(DraftValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                {
                    _proposedValue = number;
                    ValidationError = string.Empty;
                }
                else ValidationError = "Enter a finite number using '.' as the decimal separator, or <NULL>.";
                break;
            case byte[]:
                _proposedValue = OriginalValue;
                ValidationError = "BLOB values cannot be edited here.";
                break;
            default:
                _proposedValue = DraftValue;
                ValidationError = string.Empty;
                break;
        }
    }

    private static bool ValuesEqual(object? left, object? right) => left switch
    {
        null => right is null,
        byte[] bytes when right is byte[] other => bytes.SequenceEqual(other),
        _ => Equals(left, right)
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
