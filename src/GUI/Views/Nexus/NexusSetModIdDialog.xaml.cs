using AdonisUI.Controls;

using System.Text.RegularExpressions;
using System.Windows;

namespace DivinityModManager.Views.Nexus;

public partial class NexusSetModIdDialog : AdonisWindow
{
	private static readonly Regex ModIdRegex = new(@"(?:/mods/|^)(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public int ParsedModId { get; private set; }

	public NexusSetModIdDialog(string modName)
	{
		InitializeComponent();
		PromptText.Text = $"Paste a Nexus Mod ID or full URL for '{modName}':";
		Loaded += (_, _) => InputTextBox.Focus();
	}

	private void OnOkClicked(object sender, RoutedEventArgs e)
	{
		var value = (InputTextBox.Text ?? string.Empty).Trim();
		if (TryParseModId(value, out var modId) && modId > 0)
		{
			ParsedModId = modId;
			DialogResult = true;
			Close();
			return;
		}

		ErrorText.Text = "Enter a valid positive Nexus Mod ID or mod URL.";
	}

	private void OnCancelClicked(object sender, RoutedEventArgs e)
	{
		DialogResult = false;
		Close();
	}

	private static bool TryParseModId(string value, out int modId)
	{
		modId = 0;
		if (string.IsNullOrWhiteSpace(value)) return false;

		if (int.TryParse(value, out modId))
		{
			return modId > 0;
		}

		var match = ModIdRegex.Match(value);
		if (match.Success && int.TryParse(match.Groups[1].Value, out modId))
		{
			return modId > 0;
		}

		return false;
	}
}
