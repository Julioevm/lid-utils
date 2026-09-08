using System.Windows;
using LidUtils.Core;

namespace LidUtils.App;

public partial class ItemCatalogPickerWindow : Window
{
    public ItemCatalogPickerWindow(ItemCatalogViewModel catalog)
    {
        InitializeComponent();
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        DataContext = this;
    }

    public ItemCatalogViewModel Catalog { get; }

    public ItemCatalogEntry? SelectedItem => Catalog.SelectedItem;

    private void OnSelect(object sender, RoutedEventArgs e)
    {
        if (!Catalog.HasSupportedSelection)
        {
            return;
        }

        DialogResult = true;
    }
}
