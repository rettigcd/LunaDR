using System.IO;
using System.Text.Json;

namespace RipDvd.Gui;

/// <summary>
/// Settings remembered between runs, stored as JSON in %APPDATA%\LunaDR\gui-settings.json.
/// Until that file exists, the one from before the rename (%APPDATA%\rip-dvd\gui-settings.json) is read instead.
/// Reading or writing problems are ignored: the defaults are used instead.
/// </summary>
public sealed class GuiSettings {
	public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LunaDR");
	public bool PlaySoundWhenDone { get; set; } = true;

	private static string FilePath => SettingsPath("LunaDR");
	private static string OldFilePath => SettingsPath("rip-dvd");

	private static string SettingsPath(string folder) =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), folder, "gui-settings.json");

	public static GuiSettings Load() {
		try {
			string path = File.Exists(FilePath) ? FilePath : OldFilePath;
			if (File.Exists(path)) return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(path)) ?? new GuiSettings();
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
