using PalPanel.Control;

public class PalGameSettingsTests
{
    private const string Ini =
        "[/Script/Pal.PalGameWorldSettings]\r\n" +
        "OptionSettings=(Difficulty=None,ExpRate=1.500000,DeathPenalty=Item," +
        "bEnablePlayerToPlayerDamage=True,PublicPort=8211," +
        "ServerName=\"The Fat Shack, a place\",ServerDescription=\"\")\r\n";

    [Fact]
    public void Parse_ThenSerialize_IsLossless()
    {
        Assert.Equal(Ini, PalGameSettings.Parse(Ini).ToIniText());
    }

    [Fact]
    public void Reads_TypedValues()
    {
        var s = PalGameSettings.Parse(Ini);
        Assert.Equal("None", s.Get("Difficulty"));
        Assert.Equal("1.500000", s.Get("ExpRate"));
        Assert.Equal("True", s.Get("bEnablePlayerToPlayerDamage"));
        Assert.Equal("8211", s.Get("PublicPort"));
    }

    [Fact]
    public void QuotedValue_WithComma_IsNotSplit()
    {
        var s = PalGameSettings.Parse(Ini);
        Assert.Equal("\"The Fat Shack, a place\"", s.Get("ServerName"));
    }

    [Fact]
    public void Set_UpdatesValue_AndOnlyThatValue()
    {
        var s = PalGameSettings.Parse(Ini);
        s.Set("ExpRate", "3.000000");
        s.Set("bEnablePlayerToPlayerDamage", "False");
        var reparsed = PalGameSettings.Parse(s.ToIniText());
        Assert.Equal("3.000000", reparsed.Get("ExpRate"));
        Assert.Equal("False", reparsed.Get("bEnablePlayerToPlayerDamage"));
        Assert.Equal("None", reparsed.Get("Difficulty")); // untouched
        Assert.Equal("\"The Fat Shack, a place\"", reparsed.Get("ServerName"));
    }

    [Fact]
    public void MissingOptionSettings_Throws()
    {
        Assert.Throws<FormatException>(() => PalGameSettings.Parse("[/Script/Pal.PalGameWorldSettings]\r\n"));
    }
}
