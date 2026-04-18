using DivinityModManager.Models.NexusMods;

using AdonisUI.Controls;

using System.Windows;

namespace DivinityModManager.Views.Nexus;

public partial class NexusManualMatchDialog : AdonisWindow
{
	public NexusManualMatchViewModel ViewModel { get; }

	public NexusManualMatchDialog(string modName, IEnumerable<NexusScoredCandidate> candidates)
	{
		InitializeComponent();

		ViewModel = new NexusManualMatchViewModel(modName, candidates);
		ViewModel.RequestClose = () =>
		{
			DialogResult = true;
			Close();
		};
		ViewModel.RequestSetModId = () =>
		{
			var dialog = new NexusSetModIdDialog(modName)
			{
				Owner = this
			};
			if (dialog.ShowDialog() == true && dialog.ParsedModId > 0)
			{
				ViewModel.SelectedCandidate = new NexusScoredCandidate(
					dialog.ParsedModId,
					modName,
					string.Empty,
					string.Empty,
					string.Empty,
					string.Empty,
					0,
					0,
					1.0,
					new NexusScoreBreakdown(1, 1, 1, 0, 0, 1, false));
				DialogResult = true;
				Close();
			}
		};
		DataContext = ViewModel;
	}
}
