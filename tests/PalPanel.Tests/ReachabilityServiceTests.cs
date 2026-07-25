using PalPanel.Control;

public class ReachabilityServiceTests
{
    [Theory]
    [InlineData("-port=8211 -players=32", 8211)]
    [InlineData("-players=32 -port=7777 -publiclobby", 7777)]
    [InlineData("-players=32 -publiclobby", 8211)]     // no -port => default
    [InlineData("", 8211)]
    [InlineData(null, 8211)]
    [InlineData("-port=99999", 8211)]                   // out of range => fallback
    public void ParseGamePort_ExtractsOrDefaults(string? args, int expected) =>
        Assert.Equal(expected, ReachabilityService.ParseGamePort(args));

    [Fact]
    public void GamePortListening_UnusedHighPort_IsFalse()
    {
        // A port nothing binds should not be reported as listening (also proves it never throws).
        Assert.False(ReachabilityService.GamePortListening(59321));
    }
}
