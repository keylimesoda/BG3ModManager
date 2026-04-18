using DivinityModManager.Models.NexusMods;

using System.Text.Json.Serialization;

namespace DivinityModManager.ModUpdater.Cache;

public class NexusLocalState
{
	[JsonIgnore]
	public Guid UUID { get; set; }

	[JsonPropertyName("modIdOverride")]
	public int? ModIdOverride { get; set; }

	[JsonPropertyName("updateState")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public NexusUpdateState NexusUpdateState { get; set; } = NexusUpdateState.Unknown;

	[JsonPropertyName("latestVersion")]
	public string NexusLatestVersion { get; set; }

	[JsonPropertyName("lastChecked")]
	public DateTime? NexusLastChecked { get; set; }

	[JsonPropertyName("notOnNexus")]
	public bool NotOnNexus { get; set; }

	[JsonPropertyName("lastSearchAttempt")]
	public DateTime? LastSearchAttempt { get; set; }

	[JsonPropertyName("lastError")]
	public string LastError { get; set; }
}