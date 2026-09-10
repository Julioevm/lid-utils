using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class MapBossRouteTests
{
    [Theory]
    [InlineData("KGF_AMS_MIDBOSS00_CLEAR", "", MapBossRouteKind.BossClear, "Requires Max Sharp cleared")]
    [InlineData("KGF_AMS_MIDBOSS01_CLEAR", "", MapBossRouteKind.BossClear, "Requires Colonel Jackson cleared")]
    [InlineData("KGF_AMS_MIDBOSS02_CLEAR", "", MapBossRouteKind.BossClear, "Requires Mr Crowley cleared")]
    [InlineData("KGF_AMS_MIDBOSS01_02_CLEAR", "", MapBossRouteKind.BossClear, "Requires Colonel Jackson & Mr Crowley cleared")]
    [InlineData("KGF_AMS_BOSS_CLEAR", "", MapBossRouteKind.BossArenaClear, "Requires section boss cleared")]
    [InlineData("KGF_RFT_FIXED_AREA_BOSS_0002", "", MapBossRouteKind.BossArenaClear, "Requires section boss cleared")]
    [InlineData("", "KGF_AMS_BOSS_BTN_AREA_090_GOAL", MapBossRouteKind.BossTrigger, "Boss arena trigger")]
    [InlineData("", "KGF_AMS_MB00_BTN_AREA_010_GOAL", MapBossRouteKind.BossTrigger, "Boss trigger: Max Sharp")]
    [InlineData("KGF_AMS_MB02_BTN_AREA_061_GOAL", "", MapBossRouteKind.BossTrigger, "Boss trigger: Mr Crowley")]
    [InlineData("KGF_MET_GATEKEY_AREA010", "", MapBossRouteKind.KeyOrButton, "Locked route")]
    [InlineData("", "KGF_MET_BTNGT_GL_AREA010", MapBossRouteKind.KeyOrButton, "Locked route")]
    [InlineData("", "KGF_RFT_EX_AREA_BUTTON_0002", MapBossRouteKind.KeyOrButton, "Locked route")]
    [InlineData("", "", MapBossRouteKind.None, "")]
    public void Classify_RecognisesConfirmedRouteStrings(
        string key, string gate, MapBossRouteKind expectedKind, string expectedLabel)
    {
        var route = TowerBossRoutes.Classify(key, gate);

        Assert.Equal(expectedKind, route.Kind);
        Assert.Equal(expectedLabel, route.Label);
    }

    [Fact]
    public void Classify_MapEdgeExposesBossRoute()
    {
        var edge = new MapEdge("4HMA", "AMS_FLR_09", "AMS_AREA_090", "AMS_FLR_10", "AMS_AREA_101",
            0, "KGF_AMS_BOSS_CLEAR", string.Empty);

        Assert.True(edge.BossRoute.IsBoss);
        Assert.Equal(MapBossRouteKind.BossArenaClear, edge.BossRoute.Kind);
    }

    [Fact]
    public void Classify_PlainEscalatorHasNoBossRoute()
    {
        var edge = new MapEdge("4HMA", "MET_FLR_01", "MET_AREA_010", "MET_FLR_02", "MET_AREA_020",
            0, string.Empty, string.Empty);

        Assert.False(edge.BossRoute.IsBoss);
        Assert.Equal(MapBossRouteKind.None, edge.BossRoute.Kind);
        Assert.False(edge.IsGated);
    }

    [Theory]
    [InlineData("MBOSS1", "Max Sharp")]
    [InlineData("MBOSS2", "Colonel Jackson")]
    [InlineData("MBOSS3", "Mr Crowley")]
    [InlineData("MBOSS4", "Gunkanyama")]
    [InlineData("STAGE_BOSS1", "Max Sharp")]
    [InlineData("STAGE_BOSS4", "Gunkanyama")]
    public void BossType_UsesEnglishSectionBossNamesInsteadOfJapanese(string id, string expected)
    {
        // The raw master_mboss name is deliberately Japanese; the viewer must show the English name.
        var type = new MapBossType(id, "聴力強化型");

        Assert.Equal(expected, type.DisplayName);
    }

    [Fact]
    public void BossType_UnknownIdFallsBackToRawName()
    {
        Assert.Equal("mystery", new MapBossType("MBOSS9", "mystery").DisplayName);
        Assert.Equal("MBOSS9", new MapBossType("MBOSS9", string.Empty).DisplayName);
    }
}
