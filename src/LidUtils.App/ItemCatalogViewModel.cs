using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using LidUtils.Core;

namespace LidUtils.App;

/// <summary>
/// Reusable browse/selection state for the master database item catalog. It is shared by the
/// storage picker and by the read-only catalog tab in the game database section.
/// </summary>
public sealed class ItemCatalogViewModel : INotifyPropertyChanged
{
    public const string AllCategories = "All categories";

    private readonly IItemCatalogService? _service;
    private ItemCatalogLoadResult? _catalog;
    private string? _databasePath;
    private string _searchText = string.Empty;
    private string _selectedCategory = AllCategories;
    private ItemCatalogEntry? _selectedItem;
    private string _status = "Validate masters.db to browse item definitions.";

    public ItemCatalogViewModel(IItemCatalogService? service = null)
    {
        _service = service;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ItemCatalogEntry> Items { get; } = [];

    public IReadOnlyList<string> Categories { get; } =
        [AllCategories, "Equipment", "Items & materials", "Mushrooms", "Beasts"];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value)) return;
            Refresh();
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetField(ref _selectedCategory, value)) return;
            Refresh();
        }
    }

    public ItemCatalogEntry? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetField(ref _selectedItem, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasSupportedSelection));
            OnPropertyChanged(nameof(SelectedItemName));
            OnPropertyChanged(nameof(SelectedItemDetails));
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string? DatabasePath => _databasePath;
    public bool HasCatalog => _catalog is not null;
    public bool HasSelection => SelectedItem is not null;
    public bool HasSupportedSelection => SelectedItem?.IsTemplateSupported == true;
    public ItemCatalogLoadResult? Result => _catalog;
    public IReadOnlyList<ItemCatalogEntry> AllEntries => _catalog?.Entries ?? [];

    public string Summary => _catalog is null
        ? "No item catalog loaded."
        : $"{Items.Count:N0} of {_catalog.Entries.Count:N0} item definition(s)";

    public string SelectedItemName => SelectedItem?.DisplayName ?? "No item selected";

    public string SelectedItemDetails => SelectedItem is null
        ? "Select an item to see its player-facing details."
        : string.Join(Environment.NewLine,
            $"Display name: {SelectedItem.DisplayName}",
            $"Definition ID: {SelectedItem.DefinitionId}",
            $"Category: {SelectedItem.CategoryLabel}",
            $"Source table: {SelectedItem.SourceTable}",
            SelectedItem.IsTemplateSupported
                ? "A storage template is available for this definition."
                : SelectedItem.TemplateUnsupportedReason ?? "No storage template is available for this definition.");

    /// <summary>Loads the catalog from a read-only master database connection.</summary>
    public async Task LoadAsync(string? databasePath, CancellationToken cancellationToken = default)
    {
        _databasePath = databasePath;
        _catalog = null;
        Items.Clear();
        SelectedItem = null;
        NotifyCatalogStateChanged();

        if (_service is null)
        {
            Status = "Item catalog support is unavailable in this build.";
            return;
        }

        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            Status = "Validate masters.db to browse item definitions.";
            return;
        }

        Status = "Loading item catalog from masters.db…";
        var result = await _service.LoadAsync(databasePath, cancellationToken: cancellationToken);
        SetResult(result, databasePath);
    }

    /// <summary>Installs an already loaded result, such as the one shared with the storage page.</summary>
    public void SetResult(ItemCatalogLoadResult result, string? databasePath)
    {
        ArgumentNullException.ThrowIfNull(result);
        _catalog = result;
        _databasePath = databasePath;
        SelectedItem = null;
        NotifyCatalogStateChanged();
        Refresh();
        var warnings = result.Warnings.Count == 0
            ? string.Empty
            : $" {string.Join(" ", result.Warnings)}";
        var source = string.IsNullOrWhiteSpace(databasePath)
            ? "the selected masters.db"
            : Path.GetFileName(databasePath);
        Status = $"{Items.Count:N0} matching item definition(s) from {source}.{warnings}";
    }

    /// <summary>Clears the loaded catalog and returns the view model to its empty state.</summary>
    public void Reset()
    {
        _databasePath = null;
        _catalog = null;
        _searchText = string.Empty;
        _selectedCategory = AllCategories;
        Items.Clear();
        SelectedItem = null;
        Status = "Validate masters.db to browse item definitions.";
        NotifyCatalogStateChanged();
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SelectedCategory));
    }

    public void Refresh()
    {
        Items.Clear();
        if (_catalog is not null)
        {
            var category = ParseCategory(SelectedCategory);
            foreach (var item in _catalog.Search(SearchText, category))
            {
                Items.Add(item);
            }

            if (SelectedItem is not null && !Items.Contains(SelectedItem))
            {
                SelectedItem = null;
            }
        }

        OnPropertyChanged(nameof(Summary));
    }

    public void ShowStatus(string status)
    {
        Status = status;
    }

    private static ItemCatalogCategory? ParseCategory(string value) => value switch
    {
        "Equipment" => ItemCatalogCategory.Equipment,
        "Items & materials" => ItemCatalogCategory.Item,
        "Mushrooms" => ItemCatalogCategory.Mushroom,
        "Beasts" => ItemCatalogCategory.Beast,
        _ => null
    };

    private void NotifyCatalogStateChanged()
    {
        OnPropertyChanged(nameof(HasCatalog));
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(AllEntries));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DatabasePath));
    }

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
