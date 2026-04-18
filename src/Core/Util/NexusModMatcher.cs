using DivinityModManager.Models;
using DivinityModManager.Models.NexusMods;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DivinityModManager.Util;

internal static class NexusModMatcher
{
	private const string GraphQlUrl = "https://api.nexusmods.com/v2/graphql";
	private const string DefaultGameDomain = "baldursgate3";
	private const double WebFallbackThreshold = 0.55;
	private const double NearTieMargin = 0.05;
	private const double TranslationPenalty = 0.3;

	private static readonly string[] StripTerms =
	[
		"baldur's gate 3", "baldurs gate 3", "baldur's gate",
		"for baldur's gate 3", "for bg3", "(bg3)", "[bg3]", "bg3",
		" - "
	];

	private static readonly Regex SeparatorRegex = new(@"[_\-\.]+", RegexOptions.Compiled);
	private static readonly Regex VersionTokenRegex = new(@"\bv?\d+(?:\.\d+)+\b", RegexOptions.Compiled);
	private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
	private static readonly Regex WordRegex = new(@"\w+", RegexOptions.Compiled);
	private static readonly Regex DigitsRegex = new(@"\d+", RegexOptions.Compiled);
	private static readonly Regex FilenameIdRegex = new(@"-(\d+)-\d+-\d+-\d+", RegexOptions.Compiled);
	private static readonly Regex TranslationSuffixes = new(
		@"\b(?:"
		+ @"TCN|SCN|CN|ZH|ZHS|ZHT|CHS|CHT|TW"
		+ @"|RU|UA|Rus"
		+ @"|FR|DE|GER|ES|SPA|IT|ITA|PT|BR|PTBR"
		+ @"|JP|JA|JPN|KO|KR"
		+ @"|PL|CZ|TR|TH|VN|ID"
		+ @"|NL|FI|SV|DA|NO|HU|RO|BG|HR|SK"
		+ @"|translation|traducao|traduction|traduccion|traduzione"
		+ @"|übersetzung|tradução|перевод"
		+ @")\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private static readonly HttpClient GraphQlClient = CreateGraphQlClient();
	internal static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
	{
		"the", "a", "an", "and", "or", "is", "in", "to", "of", "for", "with", "this", "that",
		"it", "mod", "mods", "are", "on", "at", "by"
	};

	internal sealed record NexusMatcherOptions(double AutoAcceptThreshold, double MinCandidateScore)
	{
		public static NexusMatcherOptions Balanced { get; } = new(0.75, 0.20);
		public static NexusMatcherOptions Conservative { get; } = new(0.85, 0.30);
		public static NexusMatcherOptions Aggressive { get; } = new(0.65, 0.15);
	}

	internal sealed record NexusMatchContext(long? ModIdOverride, bool NotOnNexus, long CachedModId, string SourceFileName);

	private static HttpClient CreateGraphQlClient()
	{
		var client = new HttpClient();
		client.DefaultRequestHeaders.UserAgent.Clear();
		client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BG3ModManager", "1.0"));
		return client;
	}

	internal static async Task<NexusMatchResult> ResolveAsync(
		DivinityModData mod,
		NexusMatchContext context,
		NexusMatcherOptions options,
		Func<long, CancellationToken, Task<bool>> validateModIdAsync,
		Func<string, CancellationToken, Task<IReadOnlyList<NexusSearchResult>>> searchByAuthorAsync,
		Func<string, CancellationToken, Task<IReadOnlyList<NexusSearchResult>>> searchByNameAsync,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (context.ModIdOverride is long overrideId && overrideId > 0)
		{
			DivinityApp.Log($"[Nexus] Using manual override for {mod.Name}: {overrideId}");
			return new NexusMatchResult(NexusMatchSource.ManualOverride, overrideId, 1.0, []);
		}

		if (context.NotOnNexus)
		{
			DivinityApp.Log($"[Nexus] Skipping '{mod.Name}' (marked not on Nexus)");
			return NexusMatchResult.Empty;
		}

		if (context.CachedModId >= DivinityApp.NEXUSMODS_MOD_ID_START)
		{
			DivinityApp.Log($"[Nexus] Using cached mod id for {mod.Name}: {context.CachedModId}");
			return new NexusMatchResult(NexusMatchSource.Cached, context.CachedModId, 0.9, []);
		}

		if (!string.IsNullOrWhiteSpace(context.SourceFileName))
		{
			var match = FilenameIdRegex.Match(context.SourceFileName);
			if (match.Success && long.TryParse(match.Groups[1].Value, out var filenameId))
			{
				if (await validateModIdAsync(filenameId, cancellationToken))
				{
					DivinityApp.Log($"[Nexus] Matched '{mod.Name}' from filename id: {filenameId}");
					return new NexusMatchResult(NexusMatchSource.FilenameId, filenameId, 0.95, []);
				}
			}
		}

		var pool = new Dictionary<long, (NexusScoredCandidate Candidate, NexusMatchSource Source)>();
		var localAuthor = mod.Author ?? string.Empty;
		var localName = mod.Name ?? string.Empty;
		var localDesc = mod.Description ?? string.Empty;
		var localVersion = mod.Version?.Version ?? string.Empty;

		if (!string.IsNullOrWhiteSpace(localAuthor) && !IsUnknownAuthor(localAuthor))
		{
			var authorResults = await searchByAuthorAsync(localAuthor, cancellationToken);
			foreach (var entry in authorResults)
			{
				var scored = Score(localName, localAuthor, localDesc, localVersion, entry);
				pool[entry.ModId] = (scored, NexusMatchSource.AuthorSearch);
			}
		}

		var bestAuthor = pool.Values.OrderByDescending(x => x.Candidate.Score).FirstOrDefault().Candidate;
		if (bestAuthor is null || bestAuthor.Score < WebFallbackThreshold)
		{
			var queryTasks = ExpandQuery(localName).Select(async query =>
			{
				var results = await searchByNameAsync(query, cancellationToken);
				return (Query: query, Results: results);
			});

			var queryResults = await Task.WhenAll(queryTasks);
			foreach (var (_, results) in queryResults)
			{
				foreach (var entry in results)
				{
					var scored = Score(localName, localAuthor, localDesc, localVersion, entry);
					if (!pool.TryGetValue(entry.ModId, out var existing) || scored.Score > existing.Candidate.Score)
					{
						pool[entry.ModId] = (scored, NexusMatchSource.NameSearch);
					}
				}
			}
		}

		var ranked = pool.Values
			.Select(x => x.Candidate)
			.Where(x => x.Score >= options.MinCandidateScore)
			.OrderByDescending(x => x.Score)
			.ThenByDescending(x => x.Endorsements)
			.Take(5)
			.ToList();

		if (ranked.Count <= 0)
		{
			DivinityApp.Log($"[Nexus] No candidates found for '{mod.Name}'");
			return new NexusMatchResult(NexusMatchSource.None, null, 0, []);
		}

		var top = ranked[0];
		var second = ranked.Count > 1 ? ranked[1] : null;
		var margin = second is null ? top.Score : top.Score - second.Score;

		if (top.Score >= options.AutoAcceptThreshold && margin >= NearTieMargin)
		{
			var source = pool.TryGetValue(top.ModId, out var withSource) ? withSource.Source : NexusMatchSource.NameSearch;
			return new NexusMatchResult(source, top.ModId, top.Score, ranked);
		}

		DivinityApp.Log($"[Nexus] Match for '{mod.Name}' needs review. Top={top.ModId} Score={top.Score:0.000}");
		return new NexusMatchResult(NexusMatchSource.NameSearch, null, top.Score, ranked);
	}

	internal static async Task<IReadOnlyList<NexusSearchResult>> SearchGraphQlByNameAsync(string apiKey, string query, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(apiKey)) return [];

		using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl);
		request.Headers.Add("apikey", apiKey);

		var body = new JsonObject
		{
			["query"] = "query SearchMods($gameDomain: String!, $query: String!) { mods(game_domain_name: $gameDomain, filter: { name_stemmed: $query }, count: 25) { nodes { modId: mod_id name summary version author: uploader { name } endorsements: endorsement_count uniqueDownloads: unique_downloads url } } }",
			["variables"] = new JsonObject
			{
				["gameDomain"] = DefaultGameDomain,
				["query"] = query
			}
		};

		request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		using var response = await GraphQlClient.SendAsync(request, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			DivinityApp.Log($"[Nexus] GraphQL search failed ({(int)response.StatusCode}) for '{query}'");
			return [];
		}

		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
		var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
		var list = new List<NexusSearchResult>();
		if (!document.RootElement.TryGetProperty("data", out var data)
			|| !data.TryGetProperty("mods", out var mods)
			|| !mods.TryGetProperty("nodes", out var nodes)
			|| nodes.ValueKind != JsonValueKind.Array)
		{
			return list;
		}

		foreach (var node in nodes.EnumerateArray())
		{
			var author = string.Empty;
			if (node.TryGetProperty("author", out var authorNode) && authorNode.ValueKind == JsonValueKind.Object)
			{
				author = authorNode.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? string.Empty : string.Empty;
			}

			list.Add(new NexusSearchResult(
				ModId: node.TryGetProperty("modId", out var idNode) ? idNode.GetInt64() : -1,
				Name: node.TryGetProperty("name", out var nNode) ? nNode.GetString() ?? string.Empty : string.Empty,
				Author: author,
				Version: node.TryGetProperty("version", out var vNode) ? vNode.GetString() ?? string.Empty : string.Empty,
				Summary: node.TryGetProperty("summary", out var sNode) ? sNode.GetString() ?? string.Empty : string.Empty,
				Url: node.TryGetProperty("url", out var uNode) ? uNode.GetString() ?? string.Empty : string.Empty,
				Endorsements: node.TryGetProperty("endorsements", out var eNode) ? eNode.GetInt64() : 0,
				UniqueDownloads: node.TryGetProperty("uniqueDownloads", out var dNode) ? dNode.GetInt64() : 0));
		}

		return list;
	}

	internal static string NormalizeName(string name)
	{
		var value = (name ?? string.Empty).Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();
		foreach (var term in StripTerms)
		{
			value = value.Replace(term, " ", StringComparison.Ordinal);
		}
		value = SeparatorRegex.Replace(value, " ");
		value = VersionTokenRegex.Replace(value, string.Empty);
		return WhitespaceRegex.Replace(value, " ").Trim();
	}

	internal static HashSet<string> Tokenize(string text)
	{
		var tokens = new HashSet<string>(StringComparer.Ordinal);
		foreach (Match match in WordRegex.Matches((text ?? string.Empty).ToLowerInvariant()))
		{
			if (match.Value.Length > 1)
			{
				tokens.Add(match.Value);
			}
		}
		return tokens;
	}

	internal static IReadOnlyList<string> ExpandQuery(string query)
	{
		var baseQuery = (query ?? string.Empty).Trim();
		var result = new List<string> { baseQuery };
		var expanded = Regex.Replace(baseQuery, @"([a-z])([A-Z])", "$1 $2");
		expanded = Regex.Replace(expanded, @"(\d[a-z])([A-Z])", "$1 $2");
		expanded = Regex.Replace(expanded, @"([A-Za-z])(\d)", "$1 $2");
		expanded = Regex.Replace(expanded, @"[_\-]+", " ");
		expanded = Regex.Replace(expanded, @"\s+", " ").Trim();
		if (!string.Equals(expanded, baseQuery, StringComparison.Ordinal))
		{
			result.Add(expanded);
		}
		return result;
	}

	internal static bool LooksLikeTranslation(string localName, string nexusName)
	{
		if (TranslationSuffixes.IsMatch(localName ?? string.Empty)) return false;
		return TranslationSuffixes.IsMatch(nexusName ?? string.Empty);
	}

	internal static double ComputeNameScore(string localName, string nexusName)
	{
		var left = NormalizeName(localName);
		var right = NormalizeName(nexusName);
		var seq = RatcliffObershelpRatio(left, right);
		var tokensLeft = Tokenize(left);
		var tokensRight = Tokenize(right);
		var tok = 0d;
		if (tokensLeft.Count > 0 && tokensRight.Count > 0)
		{
			var overlap = tokensLeft.Intersect(tokensRight, StringComparer.Ordinal).Count();
			tok = (2d * overlap) / (tokensLeft.Count + tokensRight.Count);
		}
		return Math.Max(seq, tok);
	}

	internal static double ComputeAuthorScore(string localAuthor, string nexusAuthor)
	{
		if (string.IsNullOrWhiteSpace(localAuthor) || IsUnknownAuthor(localAuthor)) return 0;
		if (string.IsNullOrWhiteSpace(nexusAuthor) || IsUnknownAuthor(nexusAuthor)) return 0;

		var local = localAuthor.Trim().ToLowerInvariant();
		var nexus = nexusAuthor.Trim().ToLowerInvariant();
		if (string.Equals(local, nexus, StringComparison.Ordinal)) return 1.0;
		if (local.Contains(nexus, StringComparison.Ordinal) || nexus.Contains(local, StringComparison.Ordinal)) return 0.7;
		var ratio = RatcliffObershelpRatio(local, nexus);
		return ratio >= 0.4 ? ratio : 0.0;
	}

	internal static double ComputeVersionSimilarity(string localVer, string nexusVer)
	{
		if (string.IsNullOrWhiteSpace(localVer) || string.IsNullOrWhiteSpace(nexusVer)) return 0;
		var lv = NormalizeVersion(localVer);
		var nv = NormalizeVersion(nexusVer);
		if (lv.Length == 0 || nv.Length == 0) return 0;
		if (string.Equals(lv, nv, StringComparison.Ordinal)) return 1.0;

		var lParts = lv.Split('.');
		var nParts = nv.Split('.');
		var lBigSingle = lParts.Length == 1 && lParts[0].Length > 6;
		var nBigSingle = nParts.Length == 1 && nParts[0].Length > 6;
		if (lBigSingle && nParts.Length > 1) return 0.15;
		if (nBigSingle && lParts.Length > 1) return 0.15;

		var maxLen = Math.Max(lParts.Length, nParts.Length);
		if (maxLen == 0) return 0;

		var matches = 0;
		for (var i = 0; i < maxLen; i++)
		{
			var leftPart = i < lParts.Length ? lParts[i] : "0";
			var rightPart = i < nParts.Length ? nParts[i] : "0";
			if (string.Equals(leftPart, rightPart, StringComparison.Ordinal)) matches++;
		}

		var ratio = matches / (double)maxLen;
		var formatBonus = lParts.Length == nParts.Length ? 0.1 : 0.0;
		return Math.Min(ratio + formatBonus, 1.0);
	}

	internal static string NormalizeVersion(string version)
	{
		var parts = DigitsRegex.Matches(version ?? string.Empty).Select(x => x.Value).ToArray();
		return parts.Length == 0 ? string.Empty : string.Join('.', parts);
	}

	internal static NexusScoredCandidate Score(string localName, string localAuthor, string localDesc, string localVersion, NexusSearchResult result)
	{
		var nameScore = ComputeNameScore(localName, result.Name);
		var authorScore = ComputeAuthorScore(localAuthor, result.Author);

		var hasAuthor = authorScore > 0;
		var authorExact = authorScore >= 1.0;
		var authorFuzzy = authorScore >= 0.4 && authorScore < 1.0;

		double baseTier;
		if (authorExact && nameScore >= 0.45) baseTier = 0.92;
		else if (authorFuzzy && nameScore >= 0.45) baseTier = 0.82;
		else if (!hasAuthor && nameScore >= 0.85) baseTier = 0.75;
		else if (!hasAuthor && nameScore >= 0.60) baseTier = 0.60;
		else baseTier = nameScore * 0.55;

		var verScore = ComputeVersionSimilarity(localVersion, result.Version);
		var verBonus = verScore * 0.03;
		var descScore = 0d;
		if (!string.IsNullOrEmpty(localDesc) && !string.IsNullOrEmpty(result.Summary))
		{
			var localTokens = Tokenize(localDesc);
			localTokens.ExceptWith(StopWords);
			var nexusTokens = Tokenize(result.Summary);
			nexusTokens.ExceptWith(StopWords);
			if (localTokens.Count > 0 && nexusTokens.Count > 0)
			{
				var overlap = localTokens.Intersect(nexusTokens, StringComparer.Ordinal).Count();
				descScore = Math.Min(1.0, overlap / (double)Math.Min(localTokens.Count, nexusTokens.Count));
			}
		}

		var descBonus = descScore * 0.02;
		var popRaw = Math.Max(result.UniqueDownloads, result.Endorsements * 10);
		var popScore = popRaw > 0 ? Math.Min(Math.Log10(Math.Max(popRaw, 1)) / 8.0, 1.0) : 0.0;
		var popBonus = popScore * 0.03;

		var total = Math.Min(baseTier + verBonus + descBonus + popBonus, 1.0);
		var isTranslation = LooksLikeTranslation(localName, result.Name);
		if (isTranslation)
		{
			total *= TranslationPenalty;
		}

		return new NexusScoredCandidate(
			ModId: result.ModId,
			Name: result.Name,
			Author: result.Author,
			Version: result.Version,
			Summary: result.Summary,
			Url: result.Url,
			Endorsements: result.Endorsements,
			UniqueDownloads: result.UniqueDownloads,
			Score: Math.Round(total, 3),
			Breakdown: new NexusScoreBreakdown(
				NameScore: Math.Round(nameScore, 3),
				AuthorScore: Math.Round(authorScore, 3),
				VersionScore: Math.Round(verScore, 3),
				DescriptionScore: Math.Round(descScore, 3),
				PopularityScore: Math.Round(popScore, 3),
				BaseTier: Math.Round(baseTier, 3),
				TranslationPenaltyApplied: isTranslation));
	}

	private static bool IsUnknownAuthor(string value)
		=> string.Equals(value?.Trim().ToLowerInvariant(), "unknown", StringComparison.Ordinal)
		|| value?.Trim() == "—";

	internal static double RatcliffObershelpRatio(string a, string b)
	{
		if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
		if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

		var total = MatchLength(a, 0, a.Length, b, 0, b.Length);
		return (2.0 * total) / (a.Length + b.Length);

		static int MatchLength(string left, int leftStart, int leftEnd, string right, int rightStart, int rightEnd)
		{
			var bestLen = 0;
			var bestLeft = leftStart;
			var bestRight = rightStart;

			for (var i = leftStart; i < leftEnd; i++)
			{
				for (var j = rightStart; j < rightEnd; j++)
				{
					var k = 0;
					while (i + k < leftEnd && j + k < rightEnd && left[i + k] == right[j + k])
					{
						k++;
					}
					if (k > bestLen)
					{
						bestLen = k;
						bestLeft = i;
						bestRight = j;
					}
				}
			}

			if (bestLen == 0) return 0;

			return bestLen
				+ MatchLength(left, leftStart, bestLeft, right, rightStart, bestRight)
				+ MatchLength(left, bestLeft + bestLen, leftEnd, right, bestRight + bestLen, rightEnd);
		}
	}
}
