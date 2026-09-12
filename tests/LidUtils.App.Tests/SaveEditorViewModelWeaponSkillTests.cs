using LidUtils.Core;

namespace LidUtils.App.Tests;

public sealed class SaveEditorViewModelWeaponSkillTests
{
    [Fact]
    public async Task Load_BuildsRowsFromSaveAndCatalog()
    {
        var viewModel = await LoadAsync();

        Assert.True(viewModel.HasWeaponSkillInventory);
        Assert.Equal(2, viewModel.WeaponSkills.Count);
        var machete = Assert.Single(viewModel.WeaponSkills, row => row.WeaponType == "PTARMTP_01");
        Assert.Equal("Machete", machete.DisplayName);
        Assert.Equal(3, machete.OriginalLevel);
        Assert.Equal("3", machete.CurrentLevelText);
        Assert.Equal(20, machete.MaximumLevel);
        Assert.True(machete.IsEditable);
        Assert.Contains("2 weapon skill", viewModel.WeaponSkillSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("weapon skill definitions loaded", viewModel.WeaponSkillStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageDraft_StagesLevelAndMatchingAbpThreshold()
    {
        var viewModel = await LoadAsync();
        var machete = Assert.Single(viewModel.WeaponSkills, row => row.WeaponType == "PTARMTP_01");

        machete.DraftLevel = "5";

        Assert.True(machete.IsStaged);
        Assert.Empty(machete.ValidationError);
        Assert.Equal("5", Assert.Single(viewModel.PendingChanges, change => change.Pointer == "/soul/expert/0/lvl").ProposedValue);
        Assert.Equal("500", Assert.Single(viewModel.PendingChanges, change => change.Pointer == "/soul/expert/0/abp").ProposedValue);
        Assert.True(viewModel.HasStagedWeaponSkills);
    }

    [Fact]
    public async Task StageDraft_BackToOriginal_RevertsLevelAndAbp()
    {
        var viewModel = await LoadAsync();
        var machete = Assert.Single(viewModel.WeaponSkills, row => row.WeaponType == "PTARMTP_01");

        machete.DraftLevel = "5";
        machete.DraftLevel = "3";

        Assert.False(machete.IsStaged);
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(viewModel.HasStagedWeaponSkills);
    }

    [Fact]
    public async Task StageDraft_OutOfRange_IsRejected()
    {
        var viewModel = await LoadAsync();
        var machete = Assert.Single(viewModel.WeaponSkills, row => row.WeaponType == "PTARMTP_01");

        machete.DraftLevel = "21";

        Assert.False(machete.IsStaged);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Contains("between 1 and 20", machete.ValidationError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaxAndUndoAll_SetsEveryEditableRowThenRestoresOriginals()
    {
        var viewModel = await LoadAsync();

        viewModel.MaxAllWeaponSkills();

        Assert.Equal("20", Assert.Single(viewModel.WeaponSkills, row => row.WeaponType == "PTARMTP_01").DraftLevel);
        Assert.Equal("3800", Assert.Single(viewModel.PendingChanges, change => change.Pointer == "/soul/expert/0/abp").ProposedValue);
        Assert.Equal("20", Assert.Single(viewModel.WeaponSkills, row => row.WeaponType == "PTARMTP_02").DraftLevel);
        Assert.Equal("4000", Assert.Single(viewModel.PendingChanges, change => change.Pointer == "/soul/expert/1/abp").ProposedValue);

        viewModel.UndoAllWeaponSkills();

        Assert.Empty(viewModel.PendingChanges);
        Assert.Equal("3", Assert.Single(viewModel.WeaponSkills, row => row.WeaponType == "PTARMTP_01").DraftLevel);
        Assert.Equal("1", Assert.Single(viewModel.WeaponSkills, row => row.WeaponType == "PTARMTP_02").DraftLevel);
    }

    [Fact]
    public async Task UndoWeaponSkill_ClearsTheStagedRow()
    {
        var viewModel = await LoadAsync();
        var machete = Assert.Single(viewModel.WeaponSkills, row => row.WeaponType == "PTARMTP_01");
        machete.DraftLevel = "5";

        viewModel.UndoWeaponSkill(machete);

        Assert.False(machete.IsStaged);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Equal("3", machete.DraftLevel);
    }

    [Fact]
    public async Task Load_WithoutWeaponSkillJson_MarksUnavailable()
    {
        var snapshot = new SaveFileSnapshot("C:\\save.sav", 1, 1, 1, 1, DateTime.UnixEpoch, "sha", [], "");
        var viewModel = new SaveEditorViewModel(new FakeSaveFileService(snapshot));
        await viewModel.SelectPathAsync(snapshot.Path);

        Assert.False(viewModel.HasWeaponSkillInventory);
        Assert.Empty(viewModel.WeaponSkills);
        Assert.Contains("unavailable", viewModel.WeaponSkillSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_WithoutCatalog_HidesUnbackedSkills()
    {
        var snapshot = Snapshot();
        var viewModel = new SaveEditorViewModel(new FakeSaveFileService(snapshot));
        await viewModel.SelectPathAsync(snapshot.Path);

        Assert.Empty(viewModel.WeaponSkills);
        Assert.False(viewModel.CanMaxWeaponSkills);
        Assert.Contains("Validate masters.db", viewModel.WeaponSkillStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_HidesSkillsAbsentFromTheCatalog()
    {
        var viewModel = await LoadAsync();

        Assert.Equal(2, viewModel.WeaponSkills.Count);
        Assert.DoesNotContain(viewModel.WeaponSkills, row => row.WeaponType == "PTARMTP_09");
    }

    private static async Task<SaveEditorViewModel> LoadAsync()
    {
        var snapshot = Snapshot();
        using var database = new TemporaryDatabaseFile();
        var viewModel = new SaveEditorViewModel(
            new FakeSaveFileService(snapshot),
            weaponSkillCatalogService: new FakeWeaponSkillCatalogService());
        await viewModel.SelectPathAsync(snapshot.Path);
        await viewModel.ConfigureStorageCatalogAsync(database.Path);
        return viewModel;
    }

    private static SaveFileSnapshot Snapshot()
    {
        const string json = """
        {
          "soul": {
            "expert": [
              { "ptarmtp": "PTARMTP_01", "abp": 100, "lvl": 3, "is_checked": 1 },
              { "ptarmtp": "PTARMTP_02", "abp": -1, "lvl": 1, "is_checked": 0 },
              { "ptarmtp": "PTARMTP_09", "abp": -1, "lvl": 1, "is_checked": 0 }
            ]
          }
        }
        """;
        return new SaveFileSnapshot("C:\\save.sav", 1, 1, 1, 1, DateTime.UnixEpoch, "sha", [
            new("/soul/expert/0/lvl", "/soul/expert/0/lvl", SaveValueType.Number, "3"),
            new("/soul/expert/0/abp", "/soul/expert/0/abp", SaveValueType.Number, "100"),
            new("/soul/expert/1/lvl", "/soul/expert/1/lvl", SaveValueType.Number, "1"),
            new("/soul/expert/1/abp", "/soul/expert/1/abp", SaveValueType.Number, "-1"),
            new("/soul/expert/2/lvl", "/soul/expert/2/lvl", SaveValueType.Number, "1"),
            new("/soul/expert/2/abp", "/soul/expert/2/abp", SaveValueType.Number, "-1")
        ], json);
    }

    private sealed class FakeSaveFileService(SaveFileSnapshot snapshot) : ISaveFileService
    {
        public Task<IReadOnlyList<string>> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([snapshot.Path]);

        public Task<SaveFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task ExportJsonAsync(SaveFileSnapshot value, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot value, IReadOnlyCollection<StagedSaveChange> changes, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot value, IReadOnlyCollection<StagedSaveChange> changes, IReadOnlyCollection<StorageOperation> operations, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot value, IReadOnlyCollection<StagedSaveChange> changes, IReadOnlyCollection<StorageOperation> operations, IReadOnlyCollection<GrantDecalOperation> grants, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeWeaponSkillCatalogService : IWeaponSkillCatalogService
    {
        public Task<WeaponSkillCatalogLoadResult> LoadWeaponSkillsAsync(string databasePath, string? language = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WeaponSkillCatalogLoadResult(
            [
                new("PTARMTP_01", "Machete", 20, Requirements(3800)),
                new("PTARMTP_02", "Knife", 20, Requirements(4000))
            ], []));

        private static IReadOnlyDictionary<int, long> Requirements(long maximum) =>
            Enumerable.Range(2, 19).ToDictionary(level => level, level => level == 2 ? 5L : level == 20 ? maximum : level * 100L);
    }

    private sealed class TemporaryDatabaseFile : IDisposable
    {
        public TemporaryDatabaseFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lid-weapon-skills-{Guid.NewGuid():N}.db");
            File.WriteAllText(Path, "stub");
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
