using System.Windows;

namespace RipDvd.Gui;

/// <summary>
/// Starts the main window.
/// For testing without a person at the screen: "rip-dvd-gui --screenshot file.png [--drive E]" scans the disc,
/// saves a picture of the window and exits; adding "--rip 3,6 --to C:\folder" first rips those titles as if Start had been clicked.
/// </summary>
public partial class App : Application {
	protected override void OnStartup(StartupEventArgs e) {
		base.OnStartup(e);
		var args = e.Args;
		int shot = Array.IndexOf(args, "--screenshot");
		int drive = Array.IndexOf(args, "--drive");
		int rip = Array.IndexOf(args, "--rip");
		int to = Array.IndexOf(args, "--to");
		(int[], string)? testRip = rip >= 0 && to >= 0 && rip + 1 < args.Length && to + 1 < args.Length
			? (args[rip + 1].Split(',').Select(int.Parse).ToArray(), args[to + 1])
			: null;
		var window = new MainWindow(
			screenshotPath: shot >= 0 && shot + 1 < args.Length ? args[shot + 1] : null,
			startDrive: drive >= 0 && drive + 1 < args.Length ? args[drive + 1] : null,
			testRip: testRip);
		window.Show();
	}
}
