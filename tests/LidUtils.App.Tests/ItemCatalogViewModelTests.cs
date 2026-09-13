namespace LidUtils.App.Tests;

public sealed class ItemCatalogViewModelTests
{
    private static readonly ItemCatalogEntry Hammer =
        new("ARM_IRON", "Iron Hammer", ItemCatalogCategory.Equipment, "master_part", true);

    private static readonly ItemCatalogEntry Potion =
        new("ITHEAL", "Healing Potion", ItemCatalogCategory.Item, "master_item", true);

    private static readonly ItemCatalogEntry Fungus =
        new("MSR_FUN", "Fun Fungus", ItemCatalogCategory.Mushroom, "master_mushroom", true);

    private static readonly ItemCatalogEntry Scorpion =
        new("BST_SCORP", "Scorpion", ItemCatalogCategory.Beast, "master_beast", true);

    private static readonly ItemCatalogEntry UnknownRandomMapItem =
        new("IT_UNKNOWN", "RMAP.UNKNOWN_RMAP", ItemCatalogCategory.Item, "master_item", true);

    private static readonly ItemCatalogEntry RandomMapItem =
        new("IT_RMAP", "RMAP", ItemCatalogCategory.Item, "master_item", true);

    [Fact]
    public void SetResult_PopulatesAllEntriesAndSummary()
    {
        var viewModel = new ItemCatalogViewModel();

        viewModel.SetResult(LoadResult(), "C:\\masters.db");

        Assert.True(viewModel.HasCatalog);
        Assert.Equal(4, viewModel.Items.Count);
        Assert.Equal("4 of 4 item definition(s)", viewModel.Summary);
        Assert.Contains("masters.db", viewModel.Status);
    }

    [Fact]
    public void SearchText_FiltersByNameOrDefinitionId()
    {
        var viewModel = Catalog();

        viewModel.SearchText = "hammer";

        Assert.Equal([Hammer], viewModel.Items.ToArray());
        Assert.Equal("1 of 4 item definition(s)", viewModel.Summary);

        viewModel.SearchText = "ITHEAL";

        Assert.Equal([Potion], viewModel.Items.ToArray());
    }

    [Fact]
    public void SelectedCategory_FiltersByCatalogCategory()
    {
        var viewModel = Catalog();

        viewModel.SelectedCategory = "Mushrooms";

        Assert.Equal([Fungus], viewModel.Items.ToArray());

        viewModel.SelectedCategory = ItemCatalogViewModel.AllCategories;

        Assert.Equal(4, viewModel.Items.Count);
    }

    [Fact]
    public void SearchAndCategory_CombineAndSelectedItemIsClearedWhenFilteredOut()
    {
        var viewModel = Catalog();
        viewModel.SelectedItem = Hammer;

        viewModel.SelectedCategory = "Equipment";
        viewModel.SearchText = "scorpion";

        Assert.Empty(viewModel.Items);
        Assert.Null(viewModel.SelectedItem);
        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public void SetResult_HidesRandomMapPlaceholdersFromLists()
    {
        var viewModel = new ItemCatalogViewModel();
        var result = new ItemCatalogLoadResult([Hammer, Potion, Fungus, Scorpion, UnknownRandomMapItem, RandomMapItem], []);

        viewModel.SetResult(result, "C:\\masters.db");

        Assert.DoesNotContain(UnknownRandomMapItem, viewModel.Items);
        Assert.DoesNotContain(RandomMapItem, viewModel.Items);
        Assert.Contains(UnknownRandomMapItem, viewModel.AllEntries);
        Assert.Contains(RandomMapItem, viewModel.AllEntries);
        Assert.Equal("4 of 4 item definition(s)", viewModel.Summary);

        viewModel.SearchText = "RMAP";

        Assert.Empty(viewModel.Items);
        Assert.Equal("0 of 4 item definition(s)", viewModel.Summary);
    }

    [Fact]
    public void Reset_ClearsCatalogAndRestoresDefaults()
    {
        var viewModel = Catalog();
        viewModel.SearchText = "hammer";
        viewModel.SelectedCategory = "Equipment";
        viewModel.SelectedItem = Hammer;

        viewModel.Reset();

        Assert.False(viewModel.HasCatalog);
        Assert.Empty(viewModel.Items);
        Assert.Equal(ItemCatalogViewModel.AllCategories, viewModel.SelectedCategory);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Null(viewModel.SelectedItem);
        Assert.Contains("Validate masters.db", viewModel.Status);
    }

    private static ItemCatalogViewModel Catalog()
    {
        var viewModel = new ItemCatalogViewModel();
        viewModel.SetResult(LoadResult(), "C:\\masters.db");
        return viewModel;
    }

    private static ItemCatalogLoadResult LoadResult() =>
        new([Hammer, Potion, Fungus, Scorpion], []);
}
