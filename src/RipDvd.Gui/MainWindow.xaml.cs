using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RipDvd.Gui.ViewModels;

namespace RipDvd.Gui;

/// <summary>
/// The main window. All state lives in <see cref="MainViewModel"/>; this code only connects Windows events to it:
/// disc insert/remove messages (WM_DEVICECHANGE) trigger a re-scan, and the --screenshot test mode saves a picture of the window.
/// </summary>
public partial class MainWindow : Window {
	private const int WmDeviceChange = 0x0219;
	private const int DbtDeviceArrival = 0x8000, DbtDeviceRemoveComplete = 0x8004;
	private const int DbtDevtypVolume = 2;
	private const short DbtfMedia = 0x0001;   // the volume change is a disc (media), not a drive

	private readonly MainViewModel _vm = new();
	private readonly string? _screenshotPath;
	private readonly (int[] Titles, string Folder)? _testRip;

	public MainWindow(string? screenshotPath = null, string? startDrive = null, (int[] Titles, string Folder)? testRip = null) {
		_testRip = testRip;
		InitializeComponent();
		DataContext = _vm;
		_vm.PickFolder = start => {
			var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save the MKV files to", InitialDirectory = Directory.Exists(start) ? start : "" };
			return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
		};
		_vm.ShowMessage = text => MessageBox.Show(this, text, "rip-dvd", MessageBoxButton.OK, MessageBoxImage.Warning);
		_vm.ConfirmCancel = () => MessageBox.Show(this, "Stop ripping? The title being ripped is not saved; titles already finished are kept.",
			"rip-dvd", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
		_vm.AskAboutExistingFiles = files => {
			string list = string.Join("\n", files.Take(10)) + (files.Count > 10 ? $"\n… and {files.Count - 10} more" : "");
			var answer = MessageBox.Show(this,
				$"{files.Count} file(s) already exist:\n\n{list}\n\nYes: replace them.   No: skip those titles.   Cancel: don't start.",
				"rip-dvd", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			return answer switch {
				MessageBoxResult.Yes => ExistingFilesChoice.Overwrite,
				MessageBoxResult.No => ExistingFilesChoice.Skip,
				_ => ExistingFilesChoice.Cancel,
			};
		};
		_screenshotPath = screenshotPath;
		if (startDrive is not null) _vm.SelectDrive(startDrive);
		SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
		if (_screenshotPath is not null) ContentRendered += (_, _) => _ = SaveScreenshotWhenScannedAsync();
	}

	/// <summary>Windows broadcasts WM_DEVICECHANGE when a disc is inserted or removed; the volume's drive letters are in a bit mask.</summary>
	private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
		if (msg != WmDeviceChange || lParam == IntPtr.Zero) return IntPtr.Zero;
		int ev = wParam.ToInt32();
		if (ev != DbtDeviceArrival && ev != DbtDeviceRemoveComplete) return IntPtr.Zero;
		// DEV_BROADCAST_VOLUME: size (4), devicetype (4), reserved (4), unitmask (4), flags (2).
		if (Marshal.ReadInt32(lParam, 4) != DbtDevtypVolume) return IntPtr.Zero;
		int unitMask = Marshal.ReadInt32(lParam, 12);
		short flags = Marshal.ReadInt16(lParam, 16);
		if ((flags & DbtfMedia) == 0) return IntPtr.Zero;
		for (int i = 0; i < 26; i++)
			if ((unitMask & (1 << i)) != 0) _vm.OnMediaChanged((char)('A' + i), arrived: ev == DbtDeviceArrival);
		return IntPtr.Zero;
	}

	/// <summary>Test mode: wait for the scan, let the window lay out, save it as PNG, and close.</summary>
	private async Task SaveScreenshotWhenScannedAsync() {
		if (_vm.IsScanning) {
			var done = new TaskCompletionSource();
			_vm.ScanFinished += () => done.TrySetResult();
			if (_vm.IsScanning) await done.Task;
		}
		await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
		if (_vm.Titles.Count > 0) _vm.SelectedTitleNumber = _vm.Titles[0].Number;   // show the selection highlighting
		if (_testRip is { } rip) {
			// Test mode: rip the given titles as if Start had been clicked; existing files are replaced.
			_vm.AskAboutExistingFiles = _ => ExistingFilesChoice.Overwrite;
			_vm.PrepareTestRip(rip.Titles, rip.Folder);
			await _vm.StartAsync();
		}
		await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

		// The content has a margin; include it so nothing at the edges is cut off.
		var content = (FrameworkElement)Content;
		double width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
		double height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
		var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
		var background = new DrawingVisual();
		using (var dc = background.RenderOpen())
			dc.DrawRectangle(Background ?? Brushes.White, null, new Rect(0, 0, width, height));
		bitmap.Render(background);
		bitmap.Render(content);
		var encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using (var file = File.Create(_screenshotPath!)) encoder.Save(file);
		Close();
	}
}
