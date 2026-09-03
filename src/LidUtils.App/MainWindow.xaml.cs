using System.ComponentModel;
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
        await Task.WhenAll(_viewModel.DiscoverAsync(), _viewModel.SaveEditor.DiscoverAsync());
    }

    private async void OnDiscover(object sender, RoutedEventArgs e)
    {
        await _viewModel.DiscoverAsync();
    }

    private async void OnValidate(object sender, RoutedEventArgs e)
    {
        await _viewModel.ValidateCurrentAsync();
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

    private async void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        await _viewModel.ToggleSelectedFavoriteAsync();
    }

    private async void OnForgetRememberedDatabase(object sender, RoutedEventArgs e)
    {
        await _viewModel.ForgetRememberedDatabaseAsync();
    }

    private async void OnClearFavorites(object sender, RoutedEventArgs e)
    {
        await _viewModel.ClearFavoritePreferencesAsync();
    }

    private async void OnClearRecentSettings(object sender, RoutedEventArgs e)
    {
        await _viewModel.ClearRecentSettingsAsync();
    }

    private async void OnStageChange(object sender, RoutedEventArgs e)
    {
        await _viewModel.StageSelectedChangeAsync();
    }

    private void OnResetSelectedChange(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetSelectedChange();
    }

    private void OnResetAllChanges(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetAllChanges();
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

    private void OnStageSaveChange(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveEditor.StageSelectedChange();
    }

    private void OnResetSaveChange(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveEditor.ResetSelectedChange();
    }

    private void OnResetAllSaveChanges(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveEditor.ResetAllChanges();
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
        if (_viewModel.SaveEditor.IsApplying)
        {
            e.Cancel = true;
            MessageBox.Show(
                "A save backup/write is still being verified. Wait for it to finish before closing the utility.",
                "Save update in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _viewModel.CancelPendingOperations();
    }
}
