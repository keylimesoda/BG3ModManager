using DivinityModManager.Models;
using DivinityModManager.Models.Cache;
using DivinityModManager.Util;

using DynamicData;

using Newtonsoft.Json;

using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivinityModManager.ModUpdater.Cache;

public class NexusModsCacheHandler : IExternalModCacheHandler<NexusModsCachedData>, IDisposable
{
	private const string NexusLocalStateFileName = "nexuslocalstate.json";
	private readonly SourceCache<NexusLocalState, Guid> _localState = new(x => x.UUID);
	private readonly Subject<Unit> _localStateSaveRequests = new();
	private readonly IDisposable _localStateChangeSubscription;
	private readonly IDisposable _localStateSampleSaveSubscription;
	private readonly IDisposable _localStateFinalSaveSubscription;
	private readonly SemaphoreSlim _localStateSaveLock = new(1, 1);

	private readonly JsonSerializerOptions _localStateSerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};

	public ModSourceType SourceType => ModSourceType.NEXUSMODS;
	public string FileName => "nexusmodsdata.json";
	public JsonSerializerSettings SerializerSettings => ModUpdateHandler.DefaultSerializerSettings;
	public bool IsEnabled { get; set; } = false;
	public NexusModsCachedData CacheData { get; set; }

	public SourceCache<NexusLocalState, Guid> LocalState => _localState;

	public string APIKey { get; set; }
	public string AppName { get; set; }
	public string AppVersion { get; set; }

	public NexusModsCacheHandler() : base()
	{
		CacheData = new NexusModsCachedData();

		LoadLocalState();

		_localStateChangeSubscription = _localState.Connect().Subscribe(_ =>
		{
			_localStateSaveRequests.OnNext(Unit.Default);
		});

		_localStateSampleSaveSubscription = _localStateSaveRequests
			.Sample(TimeSpan.FromSeconds(2))
			.Subscribe(async _ =>
			{
				await SaveLocalStateAsync(CancellationToken.None);
			});

		_localStateFinalSaveSubscription = _localStateSaveRequests
			.Throttle(TimeSpan.FromSeconds(2))
			.Subscribe(async _ =>
			{
				await SaveLocalStateAsync(CancellationToken.None);
			});
	}

	public bool TryGetLocalState(Guid uuid, out NexusLocalState state)
	{
		return _localState.Lookup(uuid).TryGetValue(out state);
	}

	public void SetLocalState(NexusLocalState state)
	{
		if (state != null)
		{
			_localState.AddOrUpdate(state);
		}
	}

	public void RemoveLocalState(Guid uuid)
	{
		_localState.Remove(uuid);
	}

	public async Task FlushLocalStateAsync(CancellationToken cts)
	{
		await SaveLocalStateAsync(cts);
	}

	public async Task<bool> Update(IEnumerable<DivinityModData> mods, CancellationToken cts)
	{
		if (!NexusModsDataLoader.IsInitialized && !string.IsNullOrEmpty(APIKey))
		{
			NexusModsDataLoader.Init(APIKey, AppName, AppVersion);
		}

		if (NexusModsDataLoader.CanFetchData)
		{
			var result = await NexusModsDataLoader.LoadAllModsDataAsync(mods, cts);

			if (result.Success)
			{
				DivinityApp.Log($"Fetched NexusMods mod info for {result.UpdatedMods.Count} mod(s).");

				foreach (var mod in mods.Where(x => x.NexusModsData.ModId >= DivinityApp.NEXUSMODS_MOD_ID_START).Select(x => x.NexusModsData))
				{
					CacheData.Mods[mod.UUID] = mod;
				}

				await SaveLocalStateAsync(cts);
				return true;
			}
			else
			{
				DivinityApp.Log($"Failed to update NexusMods mod info:\n{result.FailureMessage}");
			}
		}
		else
		{
			DivinityApp.Log("NexusModsAPIKey not set, or daily/hourly limit reached. Skipping.");
		}
		return false;
	}

	private string GetLocalStateFilePath()
	{
		return DivinityApp.GetAppDirectory("Data", NexusLocalStateFileName);
	}

	private void LoadLocalState()
	{
		var filePath = GetLocalStateFilePath();
		if (!File.Exists(filePath)) return;

		try
		{
			var json = File.ReadAllText(filePath);
			var data = JsonSerializer.Deserialize<NexusLocalStateCacheFile>(json, _localStateSerializerOptions);
			if (data?.Mods != null && data.Mods.Count > 0)
			{
				_localState.Edit(innerCache =>
				{
					innerCache.Clear();
					innerCache.AddOrUpdate(data.Mods.Select(kvp =>
					{
						kvp.Value.UUID = kvp.Key;
						return kvp.Value;
					}));
				});
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"[Nexus] Failed to load local Nexus state cache '{filePath}'. Starting with an empty cache.\n{ex}");
			BackupCorruptLocalStateFile(filePath);
			_localState.Clear();
		}
	}

	private void BackupCorruptLocalStateFile(string filePath)
	{
		try
		{
			if (File.Exists(filePath))
			{
				var backupPath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
				File.Move(filePath, backupPath, true);
				DivinityApp.Log($"[Nexus] Backed up corrupt local Nexus state cache to '{backupPath}'.");
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"[Nexus] Failed to back up corrupt local Nexus state cache '{filePath}'.\n{ex}");
		}
	}

	private async Task SaveLocalStateAsync(CancellationToken cts)
	{
		var filePath = GetLocalStateFilePath();
		await _localStateSaveLock.WaitAsync(cts);
		try
		{
			var parentDir = Path.GetDirectoryName(filePath);
			if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
			{
				Directory.CreateDirectory(parentDir);
			}

			var data = new NexusLocalStateCacheFile()
			{
				Version = 1,
				Mods = _localState.Items.ToDictionary(x => x.UUID, x => x)
			};

			var json = JsonSerializer.Serialize(data, _localStateSerializerOptions);
			var tempPath = $"{filePath}.tmp";
			await File.WriteAllTextAsync(tempPath, json, cts);
			File.Move(tempPath, filePath, true);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"[Nexus] Failed to save local Nexus state cache '{filePath}'.\n{ex}");
		}
		finally
		{
			_localStateSaveLock.Release();
		}
	}

	public void Dispose()
	{
		_localStateChangeSubscription.Dispose();
		_localStateSampleSaveSubscription.Dispose();
		_localStateFinalSaveSubscription.Dispose();
		_localStateSaveRequests.Dispose();
		_localState.Dispose();
		_localStateSaveLock.Dispose();
	}

	private sealed class NexusLocalStateCacheFile
	{
		[JsonPropertyName("version")]
		public int Version { get; set; } = 1;

		[JsonPropertyName("mods")]
		public Dictionary<Guid, NexusLocalState> Mods { get; set; } = [];
	}
}
