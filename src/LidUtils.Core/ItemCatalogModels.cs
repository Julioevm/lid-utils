namespace LidUtils.Core;

/// <summary>Inventory families which can occupy an account-storage slot.</summary>
public enum ItemCatalogCategory
{
    Equipment,
    Item,
    Mushroom,
    Beast
}

/// <summary>
/// A read-only definition from the installed game database. A definition can be
/// shown even when this version of LidUtils cannot safely create an instance for it.
/// </summary>
public sealed record ItemCatalogEntry(
    string DefinitionId,
    string DisplayName,
    ItemCatalogCategory Category,
    string SourceTable,
    bool IsTemplateSupported,
    string? TemplateUnsupportedReason = null)
{
    public string CategoryLabel => Category switch
    {
        ItemCatalogCategory.Equipment => "Equipment",
        ItemCatalogCategory.Item => "Items & materials",
        ItemCatalogCategory.Mushroom => "Mushrooms",
        ItemCatalogCategory.Beast => "Beasts",
        _ => Category.ToString()
    };

    public string SupportLabel => IsTemplateSupported
        ? "Template supported"
        : "Template unsupported";

    public string SearchText => $"{DefinitionId} {DisplayName} {CategoryLabel} {SourceTable}";
}

public sealed record ItemCatalogLoadResult(
    IReadOnlyList<ItemCatalogEntry> Entries,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<ItemCatalogEntry> Search(
        string? query,
        ItemCatalogCategory? category = null)
    {
        var terms = query?.Trim();
        return Entries
            .Where(entry => category is null || entry.Category == category)
            .Where(entry => string.IsNullOrEmpty(terms) ||
                entry.SearchText.Contains(terms, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.DefinitionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed record ItemCatalogTemplateResult(
    StorageItemTemplate? Template,
    string? Error)
{
    public bool IsSuccess => Template is not null && string.IsNullOrEmpty(Error);
}

public interface IItemCatalogService
{
    /// <summary>
    /// Reads item definitions from <paramref name="databasePath"/> without modifying it.
    /// Language is a LET IT DIE master-text language code (for example, <c>int</c>).
    /// </summary>
    Task<ItemCatalogLoadResult> LoadAsync(
        string databasePath,
        string? language = null,
        CancellationToken cancellationToken = default);

    ItemCatalogTemplateResult CreateTemplate(ItemCatalogEntry entry);
}
