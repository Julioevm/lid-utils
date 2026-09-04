using System.Windows;
using System.IO;
using LidUtils.Data;
using LidUtils.Core;

namespace LidUtils.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SettingsCatalog catalog;
        try
        {
            catalog = SettingsCatalogLoader.Load(Path.Combine(AppContext.BaseDirectory, "settings.catalog.json"));
        }
        catch (CatalogValidationException exception)
        {
            MessageBox.Show(exception.Message + Environment.NewLine + Environment.NewLine +
                "The application will continue with all settings marked undocumented.",
                "Invalid settings catalog", MessageBoxButton.OK, MessageBoxImage.Warning);
            catalog = SettingsCatalog.Empty;
        }

        SaveValueCatalog saveCatalog;
        try
        {
            saveCatalog = SaveCatalogLoader.Load(Path.Combine(AppContext.BaseDirectory, "saves.catalog.json"));
        }
        catch (CatalogValidationException exception)
        {
            MessageBox.Show(exception.Message + Environment.NewLine + Environment.NewLine +
                "The application will continue with all save values marked undocumented.",
                "Invalid saves catalog", MessageBoxButton.OK, MessageBoxImage.Warning);
            saveCatalog = SaveValueCatalog.Empty;
        }

        var validator = new DatabaseValidator();
        var saveEditor = new SaveEditorViewModel(new SaveFileService(), saveCatalog);
        var viewModel = new MainWindowViewModel(
            new DatabaseDiscoveryService(),
            validator,
            new JsonPreferencesStore(),
            new ReadOnlyDatabaseBrowser(),
            new DatabaseMaintenanceService(validator),
            catalog,
            saveEditor);

        new MainWindow(viewModel).Show();
    }
}
