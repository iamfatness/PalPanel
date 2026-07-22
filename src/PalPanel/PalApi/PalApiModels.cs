using System.Text.Json.Serialization;

namespace PalPanel.PalApi;

internal class InfoDto
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("servername")]
    public string ServerName { get; set; } = string.Empty;
}

internal class PlayerDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("playerId")]
    public string PlayerId { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("ping")]
    public double Ping { get; set; }
}

internal class PlayersDto
{
    [JsonPropertyName("players")]
    public List<PlayerDto> Players { get; set; } = [];
}

internal class MetricsDto
{
    [JsonPropertyName("serverfps")]
    public double ServerFps { get; set; }

    [JsonPropertyName("currentplayernum")]
    public int CurrentPlayerNum { get; set; }

    [JsonPropertyName("serverframetime")]
    public double ServerFrameTime { get; set; }

    [JsonPropertyName("maxplayernum")]
    public int MaxPlayerNum { get; set; }

    [JsonPropertyName("uptime")]
    public int Uptime { get; set; }
}
