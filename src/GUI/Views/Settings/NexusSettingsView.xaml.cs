using DivinityModManager.Util;
using DivinityModManager.ViewModels;

using Newtonsoft.Json.Linq;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DivinityModManager.Views.Settings;

public partial class NexusSettingsView : UserControl
{
	private static readonly HttpClient _httpClient = new();
	private readonly Brush _defaultBorderBrush;
	private readonly Thickness _defaultBorderThickness;

	public NexusSettingsView()
	{
		InitializeComponent();
		_defaultBorderBrush = ApiKeyTextBox.BorderBrush;
		_defaultBorderThickness = ApiKeyTextBox.BorderThickness;
	}

	private async void ValidateApiKeyButton_Click(object sender, RoutedEventArgs e)
	{
		ClearError();

		if (DataContext is not MainWindowViewModel vm)
		{
			SetError("Nexus settings are not bound to the main view model.");
			return;
		}

		var key = vm.Settings.NexusModsAPIKey?.Trim();
		if (string.IsNullOrWhiteSpace(key))
		{
			SetError("Enter a Nexus API key before validating.");
			return;
		}

		ValidateApiKeyButton.IsEnabled = false;
		ValidationStatusText.Text = "Validating...";

		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.nexusmods.com/v1/users/validate.json");
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
			request.Headers.Add("apikey", key);

			var response = await _httpClient.SendAsync(request);
			var responseText = await response.Content.ReadAsStringAsync();

			if (response.StatusCode == HttpStatusCode.Unauthorized)
			{
				SetError("Key rejected by Nexus.");
				ValidationStatusText.Text = "";
				return;
			}

			if (!response.IsSuccessStatusCode)
			{
				SetError("Could not reach Nexus. Check your connection.");
				ValidationStatusText.Text = "";
				return;
			}

			var json = JObject.Parse(responseText);
			var name = json.Value<string>("name") ?? "Unknown";
			var isPremium = json.Value<bool?>("is_premium") == true;
			ValidationStatusText.Text = isPremium
				? $"✓ Connected as {name} (Premium)"
				: $"✓ Connected as {name}";
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"[Nexus] API key validation failed:\n{ex}");
			SetError("Could not reach Nexus. Check your connection.");
			ValidationStatusText.Text = "";
		}
		finally
		{
			ValidateApiKeyButton.IsEnabled = true;
		}
	}

	private void ClearError()
	{
		ApiKeyTextBox.BorderBrush = _defaultBorderBrush;
		ApiKeyTextBox.BorderThickness = _defaultBorderThickness;
		ValidationErrorText.Text = "";
		ValidationErrorText.Visibility = Visibility.Collapsed;
	}

	private void SetError(string message)
	{
		ApiKeyTextBox.BorderBrush = Brushes.IndianRed;
		ApiKeyTextBox.BorderThickness = new Thickness(2);
		ValidationErrorText.Text = message;
		ValidationErrorText.Visibility = Visibility.Visible;
	}
}
