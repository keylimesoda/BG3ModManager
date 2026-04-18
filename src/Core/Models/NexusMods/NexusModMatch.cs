namespace DivinityModManager.Models.NexusMods;

public enum NexusMatchSource
{
	None,
	ManualOverride,
	Cached,
	FilenameId,
	AuthorSearch,
	NameSearch
}

public sealed record NexusMatchResult(
	NexusMatchSource Source,
	long? AcceptedModId,
	double Score,
	IReadOnlyList<NexusScoredCandidate> Candidates)
{
	public static NexusMatchResult Empty { get; } = new(NexusMatchSource.None, null, 0, []);
}

public sealed record NexusScoredCandidate(
	long ModId,
	string Name,
	string Author,
	string Version,
	string Summary,
	string Url,
	long Endorsements,
	long UniqueDownloads,
	double Score,
	NexusScoreBreakdown Breakdown);

public sealed record NexusScoreBreakdown(
	double NameScore,
	double AuthorScore,
	double VersionScore,
	double DescriptionScore,
	double PopularityScore,
	double BaseTier,
	bool TranslationPenaltyApplied);

public sealed record NexusSearchResult(
	long ModId,
	string Name,
	string Author,
	string Version,
	string Summary,
	string Url,
	long Endorsements,
	long UniqueDownloads);