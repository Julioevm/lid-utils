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

    private static async Task<MainWindowViewModel> LoadAsync(TestValidator validator, params SettingEntry[] entries)
    {
        var viewModel = new MainWindowViewModel(
            new TestDiscovery(),
            validator,
            new TestPreferencesStore(),
            new TestBrowser(entries),
            SettingsCatalog.Empty,
            new SaveEditorViewModel(new TestSaveFileService()));
        await viewModel.SelectManualPathAsync("C:\\masters.db");
        return viewModel;
    }

    private static SettingEntry Entry(string key, string value) =>
        new(key, value, SettingValueType.Integer, "master_const_int", false);

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
}
