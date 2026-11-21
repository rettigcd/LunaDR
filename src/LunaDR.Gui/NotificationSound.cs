using System.IO;
using System.Media;
using Microsoft.Win32;

namespace LunaDR.Gui;

/// <summary>
/// Plays the user's Windows "Notification" sound (owner's choice for "rip finished").
/// The sound is whatever is set in Windows' Sound settings (registry: HKCU\AppEvents\Schemes\Apps\.Default\Notification.Default\.Current);
/// if that is empty or missing, Windows' standard notification sound file; if that is missing too, the system "Asterisk" sound.
/// </summary>
public static class NotificationSound {
	public static void Play() {
		try {
			string? file = UserNotificationSoundFile();
			if (file is not null) {
				using var player = new SoundPlayer(file);
				player.Play();   // asynchronous: returns at once
				return;
			}
		} catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) {
			// Fall through to the system sound.
		}
		SystemSounds.Asterisk.Play();
	}

	private static string? UserNotificationSoundFile() {
		using var key = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps\.Default\Notification.Default\.Current");
		string? configured = key?.GetValue(null) as string;
		if (!string.IsNullOrWhiteSpace(configured)) {
			string path = Environment.ExpandEnvironmentVariables(configured);
			if (File.Exists(path)) return path;
		}
		string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify System Generic.wav");
		return File.Exists(standard) ? standard : null;
	}
}
