using DivinityModManager.ModUpdater.Cache;
using DivinityModManager.Models;
using DivinityModManager.Models.NexusMods;
using DivinityModManager.Models.Updates;

using NexusModsNET;
using NexusModsNET.DataModels;

using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DivinityModManager.Util;

public class NexusModsRateLimitsUpdatedEventArgs : EventArgs
{
	public NexusApiLimits Limits { get; set; }

	public NexusModsRateLimitsUpdatedEventArgs(NexusApiLimits limits)
	{
		Limits = limits;
	}
}

public delegate void NexusModsRateLimitsUpdatedEventHandler(object sender, NexusModsRateLimitsUpdatedEventArgs e);

public sealed record NexusCheckProgress(int Checked, int Total, string CurrentModName, string StatusMessage);

public static class NexusModsDataLoader
{
	private static INexusModsClient _client;
	private static bool _isActive = false;
	private static bool _pendingDispose = false;

	private static string _lastApiKey = "";
	private static readonly Random _jitterRandom = new();
	private static readonly object _rateLimitPauseLock = new();
	private static DateTime _rateLimitPauseUntil = DateTime.MinValue;

	public static INexusModsClient Client => _client;

	public static event NexusModsRateLimitsUpdatedEventHandler RateLimitsUpdated;

	public static void Init(string apiKey, string appName, string appVersion)
	{
		if (!String.IsNullOrEmpty(apiKey) && apiKey != _lastApiKey)
		{
			if (Dispose())
			{
				_lastApiKey = apiKey;
				_client = NexusModsClient.Create(apiKey, appName, appVersion);
				//_client = new NexusModsCustomClient(apiKey, appName, appVersion);
				//RateLimitsUpdated ?.Invoke(_client, new NexusModsRateLimitsUpdatedEventArgs(_client.RateLimitsManagement.APILimits));
			}
		}
	}

	public static void EmitLimitsChanged(NexusApiLimits limits)
	{
		RateLimitsUpdated?.Invoke(_client, new NexusModsRateLimitsUpdatedEventArgs(limits));
	}

	public static bool Dispose()
	{
		if (!_isActive)
		{
			_client?.Dispose();
			_pendingDispose = false;
			return true;
		}
		_pendingDispose = true;
		return false;
	}

	public static bool CanFetchData => _client != null && !_client.RateLimitsManagement.ApiDailyLimitExceeded() && !_client.RateLimitsManagement.ApiHourlyLimitExceeded();
	public static bool LimitExceeded => _client != null && (_client.RateLimitsManagement.ApiDailyLimitExceeded() || _client.RateLimitsManagement.ApiHourlyLimitExceeded());
	public static bool IsInitialized => _client != null;

	private static bool LimitExceededCheck()
	{
		if (_client != null)
		{
			var daily = _client.RateLimitsManagement.ApiDailyLimitExceeded();
			var hourly = _client.RateLimitsManagement.ApiHourlyLimitExceeded();

			if (daily)
			{
				DivinityApp.Log($"Daily limit exceeded ({_client.RateLimitsManagement.APILimits.DailyLimit})");
				return true;
			}
			else if (hourly)
			{
				DivinityApp.Log($"Hourly limit exceeded ({_client.RateLimitsManagement.APILimits.HourlyLimit})");
				return true;
			}
		}
		return false;
	}

	public static bool CanDoTask(int apiCalls)
	{
		if (_client != null)
		{
			var currentLimit = Math.Min(_client.RateLimitsManagement.APILimits.HourlyRemaining, _client.RateLimitsManagement.APILimits.DailyRemaining);
			if (currentLimit > apiCalls)
			{
				return true;
			}
		}
		return false;
	}

	private static void OnTaskDone()
	{
		_isActive = false;
		if (_pendingDispose) Dispose();
	}

	public static async Task<List<NexusModsModDownloadLink>> GetLatestDownloadsForMods(List<DivinityModData> mods, CancellationToken t)
	{
		var links = new List<NexusModsModDownloadLink>();
		if (!CanFetchData || mods.Count <= 0) return links;
		_isActive = true;

		try
		{
			var apiCallAmount = mods.Count(x => x.NexusModsData.ModId >= DivinityApp.NEXUSMODS_MOD_ID_START) * 2;
			if (!CanDoTask(apiCallAmount))
			{
				var apiAmounts = _client.RateLimitsManagement.APILimits;

				DivinityApp.Log($"Task would exceed hourly or daily API limits. ExpectedCalls({apiCallAmount}) HourlyRemaining({apiAmounts.HourlyRemaining}/{apiAmounts.HourlyLimit}) DailyRemaining({apiAmounts.DailyRemaining}/{apiAmounts.DailyLimit})");
				OnTaskDone();
				return links;
			}
			using var dataLoader = new InfosInquirer(_client);
			foreach (var mod in mods)
			{
				if (mod.NexusModsData.ModId >= DivinityApp.NEXUSMODS_MOD_ID_START)
				{
					var result = await dataLoader.ModFiles.GetModFilesAsync(DivinityApp.NEXUSMODS_GAME_DOMAIN, mod.NexusModsData.ModId, t);
					if (result != null)
					{
						var file = result.ModFiles.FirstOrDefault(x => x.IsPrimary);
						if (file != null)
						{
							var fileId = file.FileId;
							var linkResult = await dataLoader.ModFiles.GetModFileDownloadLinksAsync(DivinityApp.NEXUSMODS_GAME_DOMAIN, mod.NexusModsData.ModId, fileId, t);
							if (linkResult != null && linkResult.Count() > 0)
							{
								var primaryLink = linkResult.FirstOrDefault();
								links.Add(new NexusModsModDownloadLink(mod, primaryLink));
							}
						}
					}
				}

				if (t.IsCancellationRequested) break;
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error fetching NexusMods data:\n{ex}");
		}

		OnTaskDone();

		return links;
	}

	public static async Task<UpdateResult> LoadAllModsDataAsync(IEnumerable<DivinityModData> mods, CancellationToken t)
	{
		var taskResult = new UpdateResult();
		if (!CanFetchData)
		{
			taskResult.Success = false;
			if (_client == null)
			{
				taskResult.FailureMessage = "API Client not initialized.";
			}
			else
			{
				var rateLimits = _client.RateLimitsManagement.APILimits;
				taskResult.FailureMessage = $"API limit exceeded. Hourly({rateLimits.HourlyRemaining}/{rateLimits.HourlyLimit}) Daily({rateLimits.DailyRemaining}/{rateLimits.DailyLimit})";
			}
			return taskResult;
		}
		var totalLoaded = 0;

		_isActive = true;

		try
		{
			var targetMods = mods.Where(mod => mod.NexusModsData.ModId >= DivinityApp.NEXUSMODS_MOD_ID_START).ToList();
			var total = targetMods.Count;
			if (total == 0)
			{
				taskResult.Success = false;
				taskResult.FailureMessage = "Skipping. No mods to check (no NexusMods ID set in the loaded mods).";
				return taskResult;
			}

			var apiCallAmount = total; // 1 call for 1 mod
			if (!CanDoTask(total))
			{
				var apiAmounts = _client.RateLimitsManagement.APILimits;

				DivinityApp.Log($"Task would exceed hourly or daily API limits. ExpectedCalls({apiCallAmount}) HourlyRemaining({apiAmounts.HourlyRemaining}/{apiAmounts.HourlyLimit}) DailyRemaining({apiAmounts.DailyRemaining}/{apiAmounts.DailyLimit})");
				OnTaskDone();
				return taskResult;
			}

			DivinityApp.Log($"Using NexusMods API to update {total} mods");

			using var dataLoader = new InfosInquirer(_client);
			foreach (var mod in targetMods)
			{
				var result = await dataLoader.Mods.GetMod(DivinityApp.NEXUSMODS_GAME_DOMAIN, mod.NexusModsData.ModId, t);
				if (result != null)
				{
					mod.NexusModsData.Update(result);
					taskResult.UpdatedMods.Add(mod);
					totalLoaded++;
				}

				if (t.IsCancellationRequested) break;
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error fetching NexusMods data:\n{ex}");
		}

		OnTaskDone();

		return taskResult;
	}

	public static async Task CheckForUpdatesAsync(
		IEnumerable<DivinityModData> mods,
		NexusModsCacheHandler cacheHandler,
		NexusModMatcher.NexusMatcherOptions options,
		IProgress<NexusCheckProgress> progress,
		CancellationToken ct)
	{
		if (mods == null) return;
		if (!CanFetchData || _client == null)
		{
			DivinityApp.Log("[Nexus] API client unavailable. Skipping update check.");
			return;
		}

		var modList = mods.ToList();
		if (modList.Count <= 0) return;

		_isActive = true;
		var checkedCount = 0;
		var total = modList.Count;
		using var dataLoader = new InfosInquirer(_client);
		using var gate = new SemaphoreSlim(4, 4);

		try
		{
			var tasks = modList.Select(async mod =>
			{
				await gate.WaitAsync(ct);
				try
				{
					await PauseIfRateLimitedAsync(progress, ct);
					await CheckSingleModAsync(mod, cacheHandler, dataLoader, options, progress, total, () => Interlocked.Increment(ref checkedCount), ct);
				}
				finally
				{
					gate.Release();
				}
			});

			await Task.WhenAll(tasks);
		}
		finally
		{
			await cacheHandler.FlushLocalStateAsync(ct);
			OnTaskDone();
		}
	}

	private static async Task CheckSingleModAsync(
		DivinityModData mod,
		NexusModsCacheHandler cacheHandler,
		InfosInquirer dataLoader,
		NexusModMatcher.NexusMatcherOptions options,
		IProgress<NexusCheckProgress> progress,
		int total,
		Func<int> incrementChecked,
		CancellationToken ct)
	{
		var modName = mod?.Name ?? "Unknown mod";
		try
		{
			var localState = GetOrCreateLocalState(cacheHandler, mod);

			var cachedModId = mod.NexusModsData?.ModId ?? -1;
			var sourceFileName = Path.GetFileNameWithoutExtension(mod.FilePath ?? mod.FileName ?? string.Empty);
			var context = new NexusModMatcher.NexusMatchContext(
				ModIdOverride: localState.ModIdOverride,
				NotOnNexus: localState.NotOnNexus,
				CachedModId: cachedModId,
				SourceFileName: sourceFileName);

			var matcherOptions = options;
			var match = await NexusModMatcher.ResolveAsync(
				mod,
				context,
				matcherOptions,
				validateModIdAsync: async (modId, token) =>
				{
					var info = await ExecuteWithRateLimitHandlingAsync(
						() => dataLoader.Mods.GetMod(DivinityApp.NEXUSMODS_GAME_DOMAIN, modId, token),
						progress,
						total,
						modName,
						token);
					return info != null;
				},
				searchByAuthorAsync: async (author, token) =>
				{
					var results = await NexusModMatcher.SearchGraphQlByNameAsync(_lastApiKey, author, token);
					return results.Where(x => string.Equals(x.Author, author, StringComparison.OrdinalIgnoreCase)).ToList();
				},
				searchByNameAsync: (query, token) => NexusModMatcher.SearchGraphQlByNameAsync(_lastApiKey, query, token),
				cancellationToken: ct);

			if (!match.AcceptedModId.HasValue || match.AcceptedModId.Value < DivinityApp.NEXUSMODS_MOD_ID_START)
			{
				SetUpdateState(mod, NexusUpdateState.NeedsReview);
				localState.NexusUpdateState = NexusUpdateState.NeedsReview;
				localState.LastSearchAttempt = DateTime.UtcNow;
				localState.NexusLastChecked = DateTime.UtcNow;
				localState.LastError = null;
				EmitProgress(progress, incrementChecked(), total, modName, "Needs review");
				return;
			}

			var resolvedModId = match.AcceptedModId.Value;
			if (mod.NexusModsData.ModId < DivinityApp.NEXUSMODS_MOD_ID_START)
			{
				mod.NexusModsData.SetModVersion(resolvedModId);
			}

			var modInfo = await ExecuteWithRateLimitHandlingAsync(
				() => dataLoader.Mods.GetMod(DivinityApp.NEXUSMODS_GAME_DOMAIN, resolvedModId, ct),
				progress,
				total,
				modName,
				ct);

			if (modInfo != null)
			{
				mod.NexusModsData.Update(modInfo);
			}

			var filesResult = await ExecuteWithRateLimitHandlingAsync(
				() => dataLoader.ModFiles.GetModFilesAsync(DivinityApp.NEXUSMODS_GAME_DOMAIN, resolvedModId, ct),
				progress,
				total,
				modName,
				ct);

			var latestFile = SelectLatestFile(filesResult?.ModFiles);
			var nexusVersion = TryGetStringProperty(latestFile, "Version")
				?? modInfo?.Version
				?? mod.NexusModsData.Version
				?? string.Empty;
			var localVersion = mod.Version?.Version ?? string.Empty;
			var updateState = ComputeUpdateState(localVersion, nexusVersion);

			SetUpdateState(mod, updateState);
			mod.NexusLatestVersion = nexusVersion ?? string.Empty;
			mod.NexusCheckError = string.Empty;
			localState.NexusUpdateState = updateState;
			localState.NexusLatestVersion = nexusVersion;
			localState.NexusLastChecked = DateTime.UtcNow;
			localState.LastError = null;

			if (latestFile != null)
			{
				var latestFileId = GetFileId(latestFile);
				if (latestFileId > 0)
				{
					mod.NexusModsData.LastFileId = latestFileId;
				}
			}

			EmitProgress(progress, incrementChecked(), total, modName, updateState == NexusUpdateState.UpdateAvailable ? "Update available" : "Up to date");
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"[Nexus] Failed checking '{modName}':\n{ex}");
			var state = GetOrCreateLocalState(cacheHandler, mod);
			SetUpdateState(mod, NexusUpdateState.CheckFailed);
			mod.NexusCheckError = ex.Message ?? "Check failed.";
			state.NexusUpdateState = NexusUpdateState.CheckFailed;
			state.NexusLastChecked = DateTime.UtcNow;
			state.LastError = ex.Message;
			EmitProgress(progress, incrementChecked(), total, modName, "Check failed");
		}
	}

	private static async Task<T> ExecuteWithRateLimitHandlingAsync<T>(
		Func<Task<T>> action,
		IProgress<NexusCheckProgress> progress,
		int total,
		string modName,
		CancellationToken ct)
	{
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			await PauseIfRateLimitedAsync(progress, ct);
			try
			{
				return await action();
			}
			catch (Exception ex) when (TryGetRetryAfter(ex, out var retryAfter))
			{
				await RegisterRateLimitPauseAsync(retryAfter, progress, total, modName, ct);
			}
		}
	}

	private static async Task RegisterRateLimitPauseAsync(TimeSpan retryAfter, IProgress<NexusCheckProgress> progress, int total, string modName, CancellationToken ct)
	{
		var pause = ApplyJitter(retryAfter);
		lock (_rateLimitPauseLock)
		{
			var pauseUntil = DateTime.UtcNow.Add(pause);
			if (pauseUntil > _rateLimitPauseUntil)
			{
				_rateLimitPauseUntil = pauseUntil;
			}
		}

		DivinityApp.Log($"[Nexus] Rate-limited. Waiting {pause.TotalSeconds:0}s before retrying.");
		EmitProgress(progress, 0, total, modName, $"Rate-limited by Nexus. Resuming in {Math.Max(1, (int)Math.Round(pause.TotalSeconds))}s…");
		await PauseIfRateLimitedAsync(progress, ct);
	}

	private static async Task PauseIfRateLimitedAsync(IProgress<NexusCheckProgress> progress, CancellationToken ct)
	{
		DateTime until;
		lock (_rateLimitPauseLock)
		{
			until = _rateLimitPauseUntil;
		}

		if (until <= DateTime.UtcNow) return;
		var delay = until - DateTime.UtcNow;
		if (delay > TimeSpan.Zero)
		{
			await Task.Delay(delay, ct);
		}
	}

	private static TimeSpan ApplyJitter(TimeSpan baseDelay)
	{
		var safeBase = baseDelay <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : baseDelay;
		double factor;
		lock (_jitterRandom)
		{
			factor = 0.75 + (_jitterRandom.NextDouble() * 0.5);
		}
		var jittered = TimeSpan.FromMilliseconds(safeBase.TotalMilliseconds * factor);
		return jittered <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : jittered;
	}

	private static bool TryGetRetryAfter(Exception ex, out TimeSpan retryAfter)
	{
		retryAfter = TimeSpan.Zero;
		if (ex == null) return false;

		if (ex is HttpRequestException hre && hre.StatusCode == HttpStatusCode.TooManyRequests)
		{
			if (TryGetRetryAfterFromException(hre, out retryAfter)) return true;
			retryAfter = TimeSpan.FromSeconds(5);
			return true;
		}

		if (TryGetRetryAfterFromException(ex, out retryAfter)) return true;
		if (Regex.IsMatch(ex.Message ?? string.Empty, @"\b429\b", RegexOptions.CultureInvariant))
		{
			retryAfter = TimeSpan.FromSeconds(5);
			return true;
		}
		return false;
	}

	private static bool TryGetRetryAfterFromException(Exception ex, out TimeSpan retryAfter)
	{
		retryAfter = TimeSpan.Zero;
		var exType = ex.GetType();
		var responseProp = exType.GetProperty("ResponseMessage", BindingFlags.Instance | BindingFlags.Public)
			?? exType.GetProperty("Response", BindingFlags.Instance | BindingFlags.Public);
		if (responseProp?.GetValue(ex) is HttpResponseMessage response)
		{
			if (response.StatusCode != HttpStatusCode.TooManyRequests) return false;
			if (response.Headers?.RetryAfter?.Delta is TimeSpan delta)
			{
				retryAfter = delta;
				return true;
			}
			if (response.Headers?.RetryAfter?.Date is DateTimeOffset retryAt)
			{
				var wait = retryAt - DateTimeOffset.UtcNow;
				retryAfter = wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
				return true;
			}
			retryAfter = TimeSpan.FromSeconds(5);
			return true;
		}
		return false;
	}

	private static NexusUpdateState ComputeUpdateState(string localVersion, string nexusVersion)
	{
		if (string.IsNullOrWhiteSpace(nexusVersion)) return NexusUpdateState.Unknown;
		if (string.IsNullOrWhiteSpace(localVersion)) return NexusUpdateState.Unknown;

		var localNormalized = NexusModMatcher.NormalizeVersion(localVersion);
		var nexusNormalized = NexusModMatcher.NormalizeVersion(nexusVersion);
		if (string.IsNullOrEmpty(localNormalized) || string.IsNullOrEmpty(nexusNormalized)) return NexusUpdateState.Unknown;
		if (string.Equals(localNormalized, nexusNormalized, StringComparison.Ordinal)) return NexusUpdateState.UpToDate;

		var localParts = localNormalized.Split('.');
		var nexusParts = nexusNormalized.Split('.');
		var localBigSingle = localParts.Length == 1 && localParts[0].Length > 6;
		var nexusBigSingle = nexusParts.Length == 1 && nexusParts[0].Length > 6;
		if ((localBigSingle && nexusParts.Length > 1) || (nexusBigSingle && localParts.Length > 1))
		{
			return NexusUpdateState.UpToDate;
		}

		var cmp = CompareNormalizedVersions(localParts, nexusParts);
		return cmp < 0 ? NexusUpdateState.UpdateAvailable : NexusUpdateState.UpToDate;
	}

	private static int CompareNormalizedVersions(string[] localParts, string[] nexusParts)
	{
		var max = Math.Max(localParts.Length, nexusParts.Length);
		for (var i = 0; i < max; i++)
		{
			var l = i < localParts.Length && int.TryParse(localParts[i], out var lv) ? lv : 0;
			var n = i < nexusParts.Length && int.TryParse(nexusParts[i], out var nv) ? nv : 0;
			if (l == n) continue;
			return l.CompareTo(n);
		}
		return 0;
	}

	private static object SelectLatestFile(IEnumerable<object> files)
	{
		if (files == null) return null;
		var mainFiles = files.Where(f => GetCategoryId(f) == 1).ToList();
		var target = mainFiles.Count > 0 ? mainFiles : files.ToList();
		return target
			.OrderByDescending(GetUploadedTimestamp)
			.ThenByDescending(GetFileId)
			.FirstOrDefault();
	}

	private static long GetCategoryId(object file)
	{
		return TryGetLongProperty(file, "CategoryId")
			?? TryGetLongProperty(file, "Category")
			?? -1;
	}

	private static long GetUploadedTimestamp(object file)
	{
		return TryGetLongProperty(file, "UploadedTimestamp")
			?? TryGetLongProperty(file, "UploadedTime")
			?? TryGetLongProperty(file, "Date")
			?? 0;
	}

	private static long GetFileId(object file)
	{
		return TryGetLongProperty(file, "FileId")
			?? TryGetLongProperty(file, "Id")
			?? -1;
	}

	private static long? TryGetLongProperty(object obj, string propertyName)
	{
		if (obj == null) return null;
		var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
		if (prop == null) return null;
		var value = prop.GetValue(obj);
		if (value == null) return null;
		if (value is long l) return l;
		if (value is int i) return i;
		if (value is short s) return s;
		if (long.TryParse(value.ToString(), out var parsed)) return parsed;
		return null;
	}

	private static string TryGetStringProperty(object obj, string propertyName)
	{
		if (obj == null) return null;
		var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
		return prop?.GetValue(obj)?.ToString();
	}

	private static NexusLocalState GetOrCreateLocalState(NexusModsCacheHandler cacheHandler, DivinityModData mod)
	{
		if (!Guid.TryParse(mod.UUID, out var uuid))
		{
			uuid = Guid.NewGuid();
		}

		if (cacheHandler.TryGetLocalState(uuid, out var state) && state != null)
		{
			if (state.UUID == Guid.Empty) state.UUID = uuid;
			return state;
		}

		state = new NexusLocalState()
		{
			UUID = uuid,
			NexusUpdateState = NexusUpdateState.Unknown
		};
		cacheHandler.SetLocalState(state);
		return state;
	}

	private static void SetUpdateState(DivinityModData mod, NexusUpdateState state)
	{
		var prop = mod.GetType().GetProperty("NexusUpdateState", BindingFlags.Instance | BindingFlags.Public);
		if (prop != null && prop.CanWrite && prop.PropertyType == typeof(NexusUpdateState))
		{
			prop.SetValue(mod, state);
		}
		else
		{
			mod.NexusUpdateState = state;
		}
	}

	private static void EmitProgress(IProgress<NexusCheckProgress> progress, int checkedCount, int total, string currentModName, string status)
	{
		progress?.Report(new NexusCheckProgress(checkedCount, total, currentModName, status));
	}
}
