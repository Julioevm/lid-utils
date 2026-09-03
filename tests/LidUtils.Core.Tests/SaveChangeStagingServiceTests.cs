using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class SaveChangeStagingServiceTests
{
    [Fact]
    public void Stage_ValidatesAndNormalizesScalarValues()
    {
        var staging = new SaveChangeStagingService();
        var number = new SaveValueEntry("/soul/free_money", "/soul/free_money", SaveValueType.Number, "10");
        var boolean = new SaveValueEntry("/flag", "/flag", SaveValueType.Boolean, "false");

        var invalid = staging.Stage(number, "1,000");
        var validNumber = staging.Stage(number, " 1250 ");
        var validBoolean = staging.Stage(boolean, "TRUE");

        Assert.NotNull(invalid.Error);
        Assert.Equal("1250", validNumber.Change?.ProposedValue);
        Assert.Equal("true", validBoolean.Change?.ProposedValue);
        Assert.Equal(2, staging.PendingChanges.Count);
    }

    [Fact]
    public void Stage_MatchingOriginalRemovesPendingChange()
    {
        var staging = new SaveChangeStagingService();
        var entry = new SaveValueEntry("/name", "/name", SaveValueType.String, "Ferdinand");

        staging.Stage(entry, "Senpai");
        var outcome = staging.Stage(entry, "Ferdinand");

        Assert.True(outcome.WasReverted);
        Assert.False(staging.HasPendingChanges);
    }
}
