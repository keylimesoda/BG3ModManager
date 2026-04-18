using DivinityModManager.Models.NexusMods;

using System.Collections.ObjectModel;
using System.Windows.Input;

namespace DivinityModManager.Views.Nexus;

public class NexusManualMatchViewModel : ReactiveObject
{
	public string ModName { get; }
	public ObservableCollection<NexusScoredCandidate> Candidates { get; }

	[Reactive] public NexusScoredCandidate SelectedCandidate { get; set; }
	[Reactive] public bool MoreOptionsExpanded { get; set; }
	[Reactive] public bool MarkedNotOnNexus { get; set; }

	public ICommand UseMatchCommand { get; }
	public ICommand SkipCommand { get; }
	public ICommand SetModIdCommand { get; }
	public ICommand MarkNotOnNexusCommand { get; }

	public Action RequestClose { get; set; }
	public Action RequestSetModId { get; set; }

	public bool HasCandidates => Candidates.Count > 0;

	public NexusManualMatchViewModel(string modName, IEnumerable<NexusScoredCandidate> candidates)
	{
		ModName = modName ?? string.Empty;
		Candidates = new ObservableCollection<NexusScoredCandidate>(candidates ?? []);
		SelectedCandidate = Candidates.FirstOrDefault();

		UseMatchCommand = ReactiveCommand.Create(() =>
		{
			RequestClose?.Invoke();
		});

		SkipCommand = ReactiveCommand.Create(() =>
		{
			RequestClose?.Invoke();
		});

		SetModIdCommand = ReactiveCommand.Create(() =>
		{
			RequestSetModId?.Invoke();
		});

		MarkNotOnNexusCommand = ReactiveCommand.Create(() =>
		{
			MarkedNotOnNexus = true;
			RequestClose?.Invoke();
		});
	}
}
