using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LidUtils.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.DiscoverAsync();
    }

    private async void OnDiscover(object sender, RoutedEventArgs e)
    {
        await _viewModel.DiscoverAsync();
    }

    private async void OnValidate(object sender, RoutedEventArgs e)
    {
        await _viewModel.ValidateCurrentAsync();
    }

    private void OnShowDatabaseInfo(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"{_viewModel.StatusTitle}{Environment.NewLine}{Environment.NewLine}" +
            $"{_viewModel.StatusDetails}{Environment.NewLine}{Environment.NewLine}" +
            _viewModel.MetadataDetails,
            "Game database details",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Let It Die masters.db",
            Filter = "Let It Die database (masters.db)|masters.db|SQLite databases (*.db)|*.db|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            FileName = "masters.db"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SelectManualPathAsync(dialog.FileName);
        }
    }

    private async void OnSchemaSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await _viewModel.LoadSelectedTablePreviewAsync();
    }

    private async void OnForgetRememberedDatabase(object sender, RoutedEventArgs e)
    {
        await _viewModel.ForgetRememberedDatabaseAsync();
    }

    private async void OnChangeGameInstallPath(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the LET IT DIE installation folder"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SetGameInstallPathAsync(dialog.FolderName);
        }
    }

    private async void OnClearFavorites(object sender, RoutedEventArgs e)
    {
        await _viewModel.ClearFavoritePreferencesAsync();
    }

    private async void OnToggleDatabaseFavorite(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DatabaseSettingRow row }) await _viewModel.ToggleFavoriteAsync(row);
    }

    private void OnUndoDatabaseChange(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DatabaseSettingRow row }) _viewModel.UndoRowChange(row);
    }

    private void OnResetAllChanges(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetAllChanges();
    }

    private async void OnApplyDatabaseChanges(object sender, RoutedEventArgs e)
    {
        var count = _viewModel.PendingChanges.Count;
        if (count == 0) return;
        var answer = MessageBox.Show(
            $"Apply {count:N0} staged database change(s)?\n\n" +
            "LET IT DIE must be closed. A verified full-database backup will be created before all changes are applied in one transaction.",
            "Apply database changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.ApplyDatabaseChangesAsync();
        }
    }

    private async void OnRestoreDatabaseBackup(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedDatabaseBackup;
        if (selected is null || !selected.IsEligible) return;
        var answer = MessageBox.Show(
            $"Restore the database backup from {selected.Created}?\n\n" +
            "LET IT DIE must be closed. The current database will be backed up and verified before it is replaced.",
            "Restore database backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.RestoreSelectedDatabaseBackupAsync();
        }
    }

    private async void OnSaveDatabaseBackupLimit(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DatabaseBackupRetentionText.Text, out var count) ||
            !await _viewModel.SetDatabaseBackupRetentionAsync(count))
        {
            DatabaseBackupRetentionText.Text = _viewModel.DatabaseBackupRetentionCount.ToString();
        }
    }

    private async void OnBrowseSave(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select LET IT DIE save file",
            Filter = "LET IT DIE saves (*.sav)|*.sav|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SaveEditor.SelectPathAsync(dialog.FileName);
        }
    }

    private async void OnReloadSave(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveEditor.ReloadAsync();
    }

    private async void OnExportSaveJson(object sender, RoutedEventArgs e)
    {
        var savePath = _viewModel.SaveEditor.SavePath;
        if (!File.Exists(savePath)) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export decoded save JSON",
            Filter = "JSON files (*.json)|*.json|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(savePath),
            FileName = $"{Path.GetFileNameWithoutExtension(savePath)}.json"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SaveEditor.ExportJsonAsync(dialog.FileName);
        }
    }

    private void OnShowSaveInfo(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            _viewModel.SaveEditor.Metadata,
            "Save file details",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnClearSaveSearch(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveEditor.ClearSearch();
    }

    private void OnUndoSaveChange(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SaveValueRow row }) _viewModel.SaveEditor.UndoChange(row);
    }

    private void OnToggleSaveFavorite(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SaveValueRow row }) _viewModel.SaveEditor.ToggleFavorite(row);
    }

    private async void OnApplySaveChanges(object sender, RoutedEventArgs e)
    {
        var count = _viewModel.SaveEditor.PendingChanges.Count;
        if (count == 0) return;
        var answer = MessageBox.Show(
            $"Apply {count:N0} staged save change(s)?\n\n" +
            "LET IT DIE must be closed. A verified timestamped backup will be created before the save is replaced.",
            "Apply save changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.SaveEditor.ApplyAsync();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.SaveEditor.IsApplying || _viewModel.IsDatabaseMaintenanceActive)
        {
            e.Cancel = true;
            MessageBox.Show(
                "A backup or write is still being verified. Wait for it to finish before closing the utility.",
                "Update in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _viewModel.CancelPendingOperations();
    }
}
