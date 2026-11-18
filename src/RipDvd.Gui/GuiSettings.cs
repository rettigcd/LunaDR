using System.IO;
using System.Text.Json;

namespace RipDvd.Gui;

/// <summary>
/// Settings remembered between runs, stored as JSON in %APPDATA%\rip-dvd\gui-settings.json.
/// Reading or writing problems are ignored: the defaults are used instead.
/// </summary>
public sealed class GuiSettings {
	public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "rip-dvd");
	public bool PlaySoundWhenDone { get; set; } = true;

	private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rip-dvd", "gui-settings.json");

	public static GuiSettings Load() {
		try {
			if (File.Exists(FilePath)) return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(FilePath)) ?? new GuiSettings();
		} catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
			// Unreadable settings: start from the defaults.
		}
		return new GuiSettings();
	}

	public void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Not being able to remember settings is not worth interrupting the user for.
		}
	}
}
