using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using LidUtils.Core;

namespace LidUtils.App.Tests;

/// <summary>
/// Parses the XAML that the Map feature adds (MapView and the Game Database tab in MainWindow)
/// on an STA thread so markup or binding-construction errors fail in CI instead of only at runtime.
/// </summary>
public sealed class MapViewXamlSmokeTests
{
    [Fact]
    public void MainWindowAndMapView_ParseAndDrawWithoutExceptions()
    {
        RunOnSta(() =>
        {
            var application = Application.Current as App ?? new App();
            application.InitializeComponent();

            var mapView = new MapView();
            var mapViewModel = new MapViewModel(new StubMapDataService());
            mapView.DataContext = mapViewModel;
            mapViewModel.SetResult(Result());
            Assert.True(mapViewModel.HasData);
            Assert.NotEmpty(mapViewModel.Nodes);

            var viewModel = new MainWindowViewModel(
                new StubDiscovery(),
                new StubValidator(),
                new StubPreferencesStore(),
                new StubBrowser(),
                new StubMaintenance(),
                SettingsCatalog.Empty,
                new SaveEditorViewModel(new StubSaveFileService()),
                mapDataService: new StubMapDataService());
            var window = new MainWindow(viewModel);
            Assert.NotNull(window.Title);
        });
    }

    private static TowerMapLoadResult Result() => new(
        Templates:
        [
            new TowerMapTemplate("4HMA",
                [
                    new MapNode("4HMA", "S_MET", "MET_FLR_01", 1, "MET_AREA_010", "AREA_NAME.TXT_MET_0001", "IMA OKA", true, "ELV_MAIN_MET_FLR_01", TowerMapCatalog.MainElevatorCarId, "MAIN ELEVATOR", 0),
                    new MapNode("4HMA", "S_MET", "MET_FLR_02", 2, "MET_AREA_020", "AREA_NAME.TXT_MET_0002", "WANOKI", true, "", "", "", 0)
                ],
                [
                    new MapEdge("4HMA", "MET_FLR_01", "MET_AREA_010", "MET_FLR_02", "MET_AREA_020", 0, "", "")
                ])
        ],
        ActiveTemplateId: "4HMA",
        ActiveTermStartUtc: DateTimeOffset.UnixEpoch,
        ActiveTermExpiresUtc: DateTimeOffset.FromUnixTimeSeconds(2_000_000),
        RecentTerms: [],
        UpcomingTerms: [],
        Warnings: []);

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class StubMapDataService : IMapDataService
    {
        public Task<TowerMapLoadResult> LoadAsync(string databasePath, string? language = null, DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result());
    }

    private sealed class StubDiscovery : IDatabaseDiscoveryService
    {
        public Task<IReadOnlyList<DatabaseCandidate>> GetCandidatesAsync(string? rememberedPath, string? gameInstallPath = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatabaseCandidate>>([]);

        public Task<DatabaseCandidate?> FindFirstExistingAsync(string? rememberedPath, string? gameInstallPath = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<DatabaseCandidate?>(null);
    }

    private sealed class StubValidator : IDatabaseValidator
    {
        public Task<DatabaseValidationResult> ValidateAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(DatabaseValidationResult.Success(
                new DatabaseFileMetadata(path, 1, DateTime.UnixEpoch, "db", "schema", 3, 0, 0)));
    }

    private sealed class StubPreferencesStore : IPreferencesStore
    {
        public Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppPreferences());
        public Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubBrowser : IReadOnlyDatabaseBrowser
    {
        public Task<SettingsLoadResult> LoadSettingsAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SettingsLoadResult([], []));

        public Task<IReadOnlyList<SchemaTable>> LoadSchemaAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchemaTable>>([]);

        public Task<TablePreview> LoadTablePreviewAsync(string path, string tableName, int maximumRows = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TablePreview(tableName, [], [], false));
    }

    private sealed class StubMaintenance : IDatabaseMaintenanceService
    {
        public Task<DatabaseApplyResult> ApplyAsync(DatabaseFileMetadata loadedSource, IReadOnlyCollection<StagedSettingChange> changes, int backupRetentionCount, CancellationToken cancellationToken = default) =>
            Task.FromException<DatabaseApplyResult>(new NotSupportedException());

        public Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(string sourcePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatabaseBackupInfo>>([]);

        public Task<DatabaseRestoreResult> RestoreAsync(string sourcePath, Guid backupId, int backupRetentionCount, CancellationToken cancellationToken = default) =>
            Task.FromException<DatabaseRestoreResult>(new NotSupportedException());
    }

    private sealed class StubSaveFileService : ISaveFileService
    {
        public Task<IReadOnlyList<string>> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<SaveFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromException<SaveFileSnapshot>(new NotSupportedException());

        public Task ExportJsonAsync(SaveFileSnapshot snapshot, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot snapshot, IReadOnlyCollection<StagedSaveChange> changes, CancellationToken cancellationToken = default) =>
            Task.FromException<SaveApplyResult>(new NotSupportedException());
    }
}
