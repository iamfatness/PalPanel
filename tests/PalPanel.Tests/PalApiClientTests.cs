using PalPanel.PalApi;

public class PalApiClientTests : IAsyncLifetime
{
    private StubPalServer _stub = null!;
    private PalApiClient _client = null!;

    public Task InitializeAsync()
    {
        _stub = new StubPalServer { PlayerNames = ["Alice", "Bob"] };
        _client = new PalApiClient(new HttpClient(), new PalApiSettings(_stub.BaseUrl, "pw"));
        return Task.CompletedTask;
    }
    public async Task DisposeAsync() => await _stub.DisposeAsync();

    [Fact]
    public async Task Info_Players_Metrics_Deserialize()
    {
        var info = await _client.GetInfoAsync(default);
        Assert.Equal("v0.4.11", info!.Version);
        var players = await _client.GetPlayersAsync(default);
        Assert.Equal(2, players.Count);
        Assert.Equal("Alice", players[0].Name);
        var m = await _client.GetMetricsAsync(default);
        Assert.Equal(58, m!.ServerFps);
        Assert.Equal(2, m.CurrentPlayerNum);
    }

    [Fact]
    public async Task Actions_PostCorrectBodies()
    {
        await _client.AnnounceAsync("hello", default);
        await _client.ShutdownAsync(30, "restarting", default);
        await _client.KickAsync("steam_1", "bye", default);
        Assert.Contains(_stub.Posts, p => p.Path == "announce" && p.Body.Contains("hello"));
        Assert.Contains(_stub.Posts, p => p.Path == "shutdown" && p.Body.Contains("30"));
        Assert.Contains(_stub.Posts, p => p.Path == "kick" && p.Body.Contains("steam_1"));
    }

    [Fact]
    public async Task Unreachable_ReturnsNullNotThrow()
    {
        _stub.Healthy = false;
        Assert.Null(await _client.GetInfoAsync(default));
        Assert.Empty(await _client.GetPlayersAsync(default));
    }

    [Fact]
    public async Task Announce_ThrowsWhenServerRejects_SoSuccessIsTrustworthy()
    {
        // A rejected broadcast must surface as a failure, not a silent "sent" — this backs the
        // send-confirmation on the Messages/Overview screens.
        _stub.Healthy = false; // stub replies 503 to everything
        await Assert.ThrowsAnyAsync<Exception>(() => _client.AnnounceAsync("hello", default));
    }
}
