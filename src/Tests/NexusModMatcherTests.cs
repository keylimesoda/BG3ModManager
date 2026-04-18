using DivinityModManager.Models;
using DivinityModManager.Models.NexusMods;
using DivinityModManager.Util;

namespace DivinityModManager.Tests;

public class NexusModMatcherTests
{
	[Theory]
	[InlineData("Better Hotbar 2", "Better Hotbar II", 0.50, 1.00)]
	[InlineData("5eSpells", "5e Spells", 0.80, 1.00)]
	[InlineData("CombatExtender", "Combat Extender", 0.80, 1.00)]
	[InlineData("Unlocked Level Curve", "Unlocked Level Curve (BG3)", 0.80, 1.00)]
	[InlineData("Totally unrelated", "Unrelated chew toy", 0.00, 0.40)]
	public void NameScore_Range(string local, string nexus, double min, double max)
	{
		var score = NexusModMatcher.ComputeNameScore(local, nexus);
		Assert.InRange(score, min, max);
	}

	[Theory]
	[InlineData("1.0.0", "1.0.0", 1.00)]
	[InlineData("1.0.0", "1.0.1", 0.77)]
	[InlineData("36028797018963968", "1.3.0", 0.15)]
	[InlineData("1.3.0", "36028797018963968", 0.15)]
	[InlineData("", "1.3.0", 0.00)]
	public void VersionSimilarity_ExactValues(string local, string nexus, double expected)
	{
		var score = NexusModMatcher.ComputeVersionSimilarity(local, nexus);
		Assert.Equal(expected, Math.Round(score, 2));
	}

	[Theory]
	[InlineData("CombatExtender", "CombatExtender TCN", true)]
	[InlineData("CombatExtender TCN", "CombatExtender TCN", false)]
	[InlineData("CombatExtender", "CombatExtender Translation", true)]
	public void LooksLikeTranslation(string local, string nexus, bool expected)
	{
		Assert.Equal(expected, NexusModMatcher.LooksLikeTranslation(local, nexus));
	}

	[Fact]
	public void Cascade_AuthorExact_NameHigh_BaseIs_092()
	{
		var c = Score("Combat Extender", "LaughingLeader", "Adds combat options", "1.0.0", "Combat Extender", "LaughingLeader");
		Assert.Equal(0.92, c.Breakdown.BaseTier, 3);
	}

	[Fact]
	public void Cascade_AuthorFuzzy_NameHigh_BaseIs_082()
	{
		var c = Score("Combat Extender", "Laughing", "Adds combat options", "1.0.0", "Combat Extender", "LaughingLeader");
		Assert.Equal(0.82, c.Breakdown.BaseTier, 3);
	}

	[Fact]
	public void Cascade_NoAuthor_NameVeryHigh_BaseIs_075()
	{
		var c = Score("Combat Extender", "Unknown", "Adds combat options", "1.0.0", "Combat Extender", "AnotherAuthor");
		Assert.Equal(0.75, c.Breakdown.BaseTier, 3);
	}

	[Fact]
	public void Cascade_NoAuthor_NameMedium_BaseIs_060()
	{
		var c = Score("Combat Tools", "Unknown", "Adds combat options", "1.0.0", "Combat Options", "AnotherAuthor");
		Assert.Equal(0.60, c.Breakdown.BaseTier, 3);
	}

	[Fact]
	public void Cascade_LowEverything_BaseIsNameTimes_055()
	{
		var c = Score("Alpha", "Unknown", "A", "1.0.0", "Beta", "AnotherAuthor");
		Assert.Equal(Math.Round(c.Breakdown.NameScore * 0.55, 3), c.Breakdown.BaseTier, 3);
	}

	[Fact]
	public async Task NearTie_BlocksAutoAccept()
	{
		var mod = new DivinityModData { Name = "Combat Extender", Author = "Author", Description = "desc" };
		var context = new NexusModMatcher.NexusMatchContext(null, false, -1, string.Empty);
		var options = new NexusModMatcher.NexusMatcherOptions(0.75, 0.2);

		var result = await NexusModMatcher.ResolveAsync(
			mod,
			context,
			options,
			(id, _) => Task.FromResult(false),
			(_, _) => Task.FromResult<IReadOnlyList<NexusSearchResult>>([]),
			(_, _) => Task.FromResult<IReadOnlyList<NexusSearchResult>>([
				new NexusSearchResult(1, "Combat Extender", "Author", "1.0.0", "desc", "", 10, 10),
				new NexusSearchResult(2, "Combat Extender Plus", "Author", "1.0.0", "desc", "", 10, 10)
			]),
			CancellationToken.None);

		Assert.Null(result.AcceptedModId);
	}

	[Fact]
	public async Task ClearWinner_AboveThreshold_AutoAccepts()
	{
		var mod = new DivinityModData { Name = "Combat Extender", Author = "Author", Description = "desc" };
		var context = new NexusModMatcher.NexusMatchContext(null, false, -1, string.Empty);
		var options = new NexusModMatcher.NexusMatcherOptions(0.75, 0.2);

		var result = await NexusModMatcher.ResolveAsync(
			mod,
			context,
			options,
			(id, _) => Task.FromResult(false),
			(_, _) => Task.FromResult<IReadOnlyList<NexusSearchResult>>([]),
			(_, _) => Task.FromResult<IReadOnlyList<NexusSearchResult>>([
				new NexusSearchResult(1, "Combat Extender", "Author", "1.0.0", "desc", "", 5000, 10000),
				new NexusSearchResult(2, "Completely Different", "Other", "1.0.0", "desc", "", 5, 10)
			]),
			CancellationToken.None);

		Assert.Equal(1, result.AcceptedModId);
	}

	[Fact]
	public async Task ManualOverride_WinsOverCachedAndSearch()
	{
		var mod = new DivinityModData { Name = "Anything", Author = "Unknown" };
		var context = new NexusModMatcher.NexusMatchContext(12345, false, 54321, "mod-9999-1-1-1.pak");
		var result = await NexusModMatcher.ResolveAsync(
			mod,
			context,
			NexusModMatcher.NexusMatcherOptions.Balanced,
			(id, _) => Task.FromResult(true),
			(_, _) => Task.FromResult<IReadOnlyList<NexusSearchResult>>([]),
			(_, _) => Task.FromResult<IReadOnlyList<NexusSearchResult>>([]),
			CancellationToken.None);

		Assert.Equal(NexusMatchSource.ManualOverride, result.Source);
		Assert.Equal(12345, result.AcceptedModId);
	}

	private static NexusScoredCandidate Score(string localName, string localAuthor, string localDesc, string localVersion, string nexusName, string nexusAuthor)
		=> NexusModMatcher.Score(localName, localAuthor, localDesc, localVersion,
			new NexusSearchResult(1, nexusName, nexusAuthor, "1.0.0", "Adds combat options", "", 100, 200));
}