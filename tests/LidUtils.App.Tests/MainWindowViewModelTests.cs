using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace LidUtils.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void InlineDraft_StagesTheLatestValueAndKeepsInvalidDraftsOutOfPendingChanges()
    {
        RunOnSta(async () =>
        {
            var validator = new TestValidator();
            var viewModel = await LoadAsync(validator, Entry("COUNT", "10"));
            var row = Assert.Single(viewModel.Settings);

            row.DraftValue = "11";
            row.DraftValue = "12";
            await Task.Delay(500);

            var pending = Assert.Single(viewModel.PendingChanges);
            Assert.Equal("12", pending.ProposedRawValue);
            Assert.True(row.IsStaged);
            Assert.Equal(2, validator.CallCount);

            row.DraftValue = "not an integer";
            await Task.Delay(500);

            Assert.Empty(viewModel.PendingChanges);
            Assert.False(row.IsStaged);
            Assert.Contains("whole-number", row.ValidationError);
        });
    }

    [Fact]
    public void InlineDraft_ChangedSourceDiscardsEveryRowEdit()
    {
        RunOnSta(async () =>
        {
            var validator = new TestValidator { ReturnChangedFingerprint = true };
            var viewModel = await LoadAsync(validator, Entry("COUNT", "10"));
            var row = Assert.Single(viewModel.Settings);

            row.DraftValue = "12";
            await Task.Delay(500);

            Assert.True(viewModel.SourceDatabaseChanged);
            Assert.Empty(viewModel.PendingChanges);
            Assert.Equal("10", row.DraftValue);
            Assert.Contains("database changed", row.ValidationError, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void FavoritesOnly_ShowsOnlyRowsMarkedFromTheGrid()
    {
        RunOnSta(async () =>
        {
            var viewModel = await LoadAsync(new TestValidator(), Entry("FIRST", "1"), Entry("SECOND", "2"));
            var favorite = viewModel.Settings.Single(row => row.Key == "SECOND");

            viewModel.ToggleFavoriteAsync(favorite).GetAwaiter().GetResult();
            viewModel.IsFavoritesOnly = true;

            Assert.True(favorite.IsFavorite);
            Assert.Equal([favorite], viewModel.SettingsView.Cast<DatabaseSettingRow>());
        });
    }

    [Fact]
    public void ApplyDatabaseChanges_UsesMaintenanceServiceAndReloadsCleanState()
    {
        RunOnSta(async () =>
        {
            var maintenance = new TestDatabaseMaintenanceService();
            var viewModel = await LoadAsync(new TestValidator(), maintenance, Entry("COUNT", "10"));
            viewModel.Settings.Single().DraftValue = "11";
            await Task.Delay(500);

            Assert.True(viewModel.CanApplyDatabaseChanges);
            await viewModel.ApplyDatabaseChangesAsync();

            Assert.Equal(1, maintenance.ApplyCallCount);
            Assert.Empty(viewModel.PendingChanges);
            Assert.Equal("Database updated safely", viewModel.StatusTitle);
            Assert.False(viewModel.IsDatabaseMaintenanceActive);
        });
    }

    [Fact]
    public void BackupSelection_OnlyEnablesCompatibleRestore()
    {
        RunOnSta(async () =>
        {
            var maintenance = new TestDatabaseMaintenanceService
            {
                Backups =
                [
                    Backup(Guid.NewGuid(), "schema"),
                    Backup(Guid.NewGuid(), "older-schema")
                ]
            };
            var viewModel = await LoadAsync(new TestValidator(), maintenance, Entry("COUNT", "10"));

            Assert.Equal(2, viewModel.DatabaseBackups.Count);
            viewModel.SelectedDatabaseBackup = viewModel.DatabaseBackups.Single(row => row.IsEligible);
            Assert.True(viewModel.CanRestoreDatabaseBackup);
            await viewModel.RestoreSelectedDatabaseBackupAsync();

            Assert.Equal(1, maintenance.RestoreCallCount);
            Assert.Equal("Database restored safely", viewModel.StatusTitle);
        });
    }

    private static async Task<MainWindowViewModel> LoadAsync(TestValidator validator, params SettingEntry[] entries)
        => await LoadAsync(validator, new TestDatabaseMaintenanceService(), entries);

    private static async Task<MainWindowViewModel> LoadAsync(
        TestValidator validator,
        TestDatabaseMaintenanceService maintenance,
        params SettingEntry[] entries)
    {
        var viewModel = new MainWindowViewModel(
            new TestDiscovery(),
            validator,
            new TestPreferencesStore(),
            new TestBrowser(entries),
            maintenance,
            SettingsCatalog.Empty,
            new SaveEditorViewModel(new TestSaveFileService()));
        await viewModel.SelectManualPathAsync("C:\\masters.db");
        return viewModel;
    }

    private static SettingEntry Entry(string key, string value) =>
        new(key, value, SettingValueType.Integer, "master_const_int", false);

    private static DatabaseBackupInfo Backup(Guid id, string schema) =>
        new(id, $"C:\\backups\\{id:N}.db.bak", "C:\\masters.db", DateTime.UtcNow,
            DatabaseBackupPurpose.Apply, 1, "source", schema, 1, "backup");

    private static void RunOnSta(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                PumpUntilComplete(action());
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void PumpUntilComplete(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => frame.Continue = false,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private sealed class TestValidator : IDatabaseValidator
    {
        private readonly DatabaseFileMetadata _metadata = new("C:\\masters.db", 1, DateTime.UnixEpoch, "original", "schema", 3, 0, 0);
        public int CallCount { get; private set; }
        public bool ReturnChangedFingerprint { get; set; }

        public Task<DatabaseValidationResult> ValidateAsync(string path, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var metadata = ReturnChangedFingerprint && CallCount > 1
                ? _metadata with { DatabaseSha256 = "changed" }
                : _metadata;
            return Task.FromResult(DatabaseValidationResult.Success(metadata));
        }
    }

    private sealed class TestDiscovery : IDatabaseDiscoveryService
    {
        public Task<IReadOnlyList<DatabaseCandidate>> GetCandidatesAsync(string? rememberedPath, string? gameInstallPath = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatabaseCandidate>>([]);

        public Task<DatabaseCandidate?> FindFirstExistingAsync(string? rememberedPath, string? gameInstallPath = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<DatabaseCandidate?>(null);
    }

    private sealed class TestPreferencesStore : IPreferencesStore
    {
        public Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppPreferences());
        public Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestBrowser(IReadOnlyList<SettingEntry> entries) : IReadOnlyDatabaseBrowser
    {
        public Task<SettingsLoadResult> LoadSettingsAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SettingsLoadResult(entries, []));

        public Task<IReadOnlyList<SchemaTable>> LoadSchemaAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchemaTable>>([]);

        public Task<TablePreview> LoadTablePreviewAsync(string path, string tableName, int maximumRows = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TablePreview(tableName, [], [], false));
    }

    private sealed class TestSaveFileService : ISaveFileService
    {
        public Task<IReadOnlyList<string>> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<SaveFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromException<SaveFileSnapshot>(new NotSupportedException());

        public Task ExportJsonAsync(SaveFileSnapshot snapshot, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot snapshot, IReadOnlyCollection<StagedSaveChange> changes, CancellationToken cancellationToken = default) =>
            Task.FromException<SaveApplyResult>(new NotSupportedException());
    }

    private sealed class TestDatabaseMaintenanceService : IDatabaseMaintenanceService
    {
        public IReadOnlyList<DatabaseBackupInfo> Backups { get; set; } = [];
        public int ApplyCallCount { get; private set; }
        public int RestoreCallCount { get; private set; }

        public Task<DatabaseApplyResult> ApplyAsync(DatabaseFileMetadata loadedSource, IReadOnlyCollection<StagedSettingChange> changes, int backupRetentionCount, CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            var backup = Backup(Guid.NewGuid(), loadedSource.SchemaSha256);
            return Task.FromResult(new DatabaseApplyResult(backup, loadedSource with { DatabaseSha256 = "updated" }, []));
        }

        public Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(string sourcePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Backups);

        public Task<DatabaseRestoreResult> RestoreAsync(string sourcePath, Guid backupId, int backupRetentionCount, CancellationToken cancellationToken = default)
        {
            RestoreCallCount++;
            var backup = Backup(Guid.NewGuid(), "schema");
            var metadata = new DatabaseFileMetadata(sourcePath, 1, DateTime.UnixEpoch, "restored", "schema", 3, 0, 0);
            return Task.FromResult(new DatabaseRestoreResult(backup, metadata, []));
        }
    }
}
