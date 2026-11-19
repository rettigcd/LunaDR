using System.Windows;

namespace RipDvd.Gui;

/// <summary>
/// Starts the main window.
/// For testing without a person at the screen: "rip-dvd-gui --screenshot file.png [--drive E]" scans the disc,
/// saves a picture of the window and exits; adding "--rip 3,6 --to C:\folder" first rips those titles as if Start had been clicked,
/// and "--cells 23-27 --to C:\folder" selects those cells in the cell map and rips them as if "Rip selected cells" had been clicked.
/// "--midshot file.png" also saves a picture when the rip is half done (to see the progress in the cell map).
/// </summary>
public partial class App : Application {
	protected override void OnStartup(StartupEventArgs e) {
		base.OnStartup(e);
		var args = e.Args;
		string? Value(string name) {
			int i = Array.IndexOf(args, name);
			return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
		}
		string? to = Value("--to");
		(int[], string)? testRip = Value("--rip") is { } rip && to is not null ? (rip.Split(',').Select(int.Parse).ToArray(), to) : null;
		(int[], string)? testCells = Value("--cells") is { } cells && to is not null ? (Core.Ripping.CellSelection.Parse(cells).ToArray(), to) : null;
		var window = new MainWindow(screenshotPath: Value("--screenshot"), startDrive: Value("--drive"), testRip: testRip, testCells: testCells);
		if (Value("--midshot") is { } midshot) window.SaveScreenshotAtHalfway(midshot);
		window.Show();
	}
}
