using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using RipDvd.Core.Ripping;

namespace RipDvd.Gui.ViewModels;

/// <summary>An optical drive in the drive picker.</summary>
public sealed record DriveItem(string Letter, string Label, bool HasDisc) {
	public string Display => HasDisc ? $"{Letter}  {Label}" : $"{Letter}  (no disc)";
}

/// <summary>What to do when some output files already exist.</summary>
public enum ExistingFilesChoice { Overwrite, Skip, Cancel }

/// <summary>
/// The main window's state: phase 1 (scan and present), phase 2 (select titles and start) and the rip itself.
/// Scans and rips run on background threads; the window updates through bindings.
/// Dialogs are supplied by the window (<see cref="PickFolder"/>, <see cref="AskAboutExistingFiles"/>, <see cref="ShowMessage"/>),
/// so this class has no window code in it.
/// </summary>
public sealed class MainViewModel : ViewModelBase {
	private readonly GuiSettings _settings = GuiSettings.Load();
	private DriveItem? _selectedDrive;
	private bool _isScanning;
	private bool _isRipping;
	private string _status = "Insert a DVD.";
	private string _discName = "";
	private string _fileName = "";
	private string _outputFolder;
	private string _ripStatus = "";
	private CellMap? _cellMap;
	private TitleRow? _selectedTitle;
	private int? _selectedTitleNumber;
	private string _hoverInfo = "";
	private string? _videoTs;
	private CancellationTokenSource? _cancel;

	public MainViewModel() {
		_outputFolder = _settings.OutputFolder;
		ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsScanning && !IsRipping && SelectedDrive is { HasDisc: true });
		SelectAllCommand = new RelayCommand(() => SetAllChecked(true), () => !IsRipping && Titles.Count > 0);
		SelectNoneCommand = new RelayCommand(() => SetAllChecked(false), () => !IsRipping && Titles.Count > 0);
		BrowseCommand = new RelayCommand(() => {
			if (PickFolder?.Invoke(OutputFolder) is { } folder) OutputFolder = folder;
		}, () => !IsRipping);
		StartCommand = new RelayCommand(() => _ = StartAsync(),
			() => !IsScanning && !IsRipping && Titles.Any(t => t.IsChecked) && !string.IsNullOrWhiteSpace(OutputFolder));
		CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsRipping);
		RefreshDrives();
	}

	public ObservableCollection<DriveItem> Drives { get; } = [];
	public ObservableCollection<TitleRow> Titles { get; } = [];
	public ICommand ScanCommand { get; }
	public ICommand SelectAllCommand { get; }
	public ICommand SelectNoneCommand { get; }
	public ICommand BrowseCommand { get; }
	public ICommand StartCommand { get; }
	public ICommand CancelCommand { get; }

	/// <summary>Supplied by the window: shows a folder picker starting at the given folder; null if cancelled.</summary>
	public Func<string, string?>? PickFolder { get; set; }

	/// <summary>Supplied by the window: asks what to do about output files that already exist.</summary>
	public Func<IReadOnlyList<string>, ExistingFilesChoice>? AskAboutExistingFiles { get; set; }

	/// <summary>Supplied by the window: shows a message (problems that stop Start).</summary>
	public Action<string>? ShowMessage { get; set; }

	/// <summary>Folder the MKV files are written to; remembered between runs.</summary>
	public string OutputFolder {
		get => _outputFolder;
		set {
			if (!Set(ref _outputFolder, value)) return;
			_settings.OutputFolder = value;
			_settings.Save();
		}
	}

	/// <summary>Name used at the start of the file names ("&lt;name&gt; - title NN.mkv"); starts as the disc name, can be edited.</summary>
	public string FileName { get => _fileName; set => Set(ref _fileName, value); }

	public bool IsRipping {
		get => _isRipping;
		private set {
			if (Set(ref _isRipping, value)) RefreshButtons();
		}
	}

	/// <summary>
	/// WPF re-checks whether buttons are enabled only after user input; a scan or rip finishing in the background isn't input,
	/// so ask for a re-check whenever those states change.
	/// </summary>
	private static void RefreshButtons() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

	/// <summary>One-line progress of the rip (phase 3 shows more).</summary>
	public string RipStatus { get => _ripStatus; private set => Set(ref _ripStatus, value); }

	/// <summary>Titles shorter than this are dimmed (MakeMKV's default minimum).</summary>
	public TimeSpan MinimumLength { get; } = DiscScanner.DefaultMinimumLength;

	/// <summary>Raised when a scan finishes (successfully or not); used by the --screenshot test mode.</summary>
	public event Action? ScanFinished;

	public DriveItem? SelectedDrive {
		get => _selectedDrive;
		set {
			if (!Set(ref _selectedDrive, value)) return;
			if (value is { HasDisc: true }) _ = ScanAsync();
			else ClearDisc(value is null ? "No optical drive found." : "Insert a DVD.");
		}
	}

	public bool IsScanning {
		get => _isScanning;
		private set {
			if (Set(ref _isScanning, value)) RefreshButtons();
		}
	}
	public string Status { get => _status; private set => Set(ref _status, value); }
	public string DiscName { get => _discName; private set => Set(ref _discName, value); }
	public CellMap? CellMap { get => _cellMap; private set => Set(ref _cellMap, value); }

	/// <summary>Text about the cell under the mouse in the cell map.</summary>
	public string HoverInfo { get => _hoverInfo; set => Set(ref _hoverInfo, value); }

	/// <summary>The title selected in the list; kept in step with <see cref="SelectedTitleNumber"/> (the cell map's selection).</summary>
	public TitleRow? SelectedTitle {
		get => _selectedTitle;
		set {
			if (!Set(ref _selectedTitle, value)) return;
			SelectedTitleNumber = value?.Number;
		}
	}

	public int? SelectedTitleNumber {
		get => _selectedTitleNumber;
		set {
			if (!Set(ref _selectedTitleNumber, value)) return;
			SelectedTitle = Titles.FirstOrDefault(t => t.Number == value);
		}
	}

	/// <summary>Re-reads the list of optical drives, keeping the selection when possible; selects a drive with a disc otherwise.</summary>
	public void RefreshDrives() {
		string? keep = SelectedDrive?.Letter;
		LoadDrives();
		_selectedDrive = null;
		SelectedDrive = Drives.FirstOrDefault(d => d.Letter == keep) ?? Drives.FirstOrDefault(d => d.HasDisc) ?? Drives.FirstOrDefault();
	}

	/// <summary>Selects a drive by letter (e.g. from the command line).</summary>
	public void SelectDrive(string letter) {
		var drive = Drives.FirstOrDefault(d => d.Letter.StartsWith(letter.TrimEnd(':', '\\'), StringComparison.OrdinalIgnoreCase));
		if (drive is not null) SelectedDrive = drive;
	}

	/// <summary>Windows reported a disc inserted in (or removed from) a drive: refresh, and scan if it is the selected drive or none had a disc.</summary>
	public void OnMediaChanged(char letter, bool arrived) {
		if (IsRipping) return;   // don't rescan or switch drives under a running rip
		bool wasSelected = SelectedDrive?.Letter[0] == letter;
		bool noneHadDisc = SelectedDrive is not { HasDisc: true };
		string? keep = SelectedDrive?.Letter;
		LoadDrives();
		var target = arrived && (wasSelected || noneHadDisc)
			? Drives.FirstOrDefault(d => d.Letter[0] == letter)
			: Drives.FirstOrDefault(d => d.Letter == keep);
		_selectedDrive = null;
		SelectedDrive = target ?? Drives.FirstOrDefault();
	}

	/// <summary>Fills <see cref="Drives"/> with the computer's optical drives and their disc names.</summary>
	private void LoadDrives() {
		Drives.Clear();
		foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom)) {
			bool ready = d.IsReady;
			Drives.Add(new DriveItem(d.Name[..2], ready ? d.VolumeLabel : "", ready));
		}
	}

	private void ClearDisc(string status) {
		Titles.Clear();
		CellMap = null;
		DiscName = "";
		FileName = "";
		_videoTs = null;
		SelectedTitle = null;
		Status = status;
	}

	/// <summary>Reads the disc's IFO files on a background thread and fills the title list and cell map.</summary>
	public async Task ScanAsync() {
		if (SelectedDrive is not { HasDisc: true } drive || IsScanning) return;
		IsScanning = true;
		ClearDisc($"Scanning {drive.Letter} …");
		try {
			string videoTs = DiscScanner.ResolveVideoTs(drive.Letter);
			var disc = await Task.Run(() => DiscScanner.Scan(videoTs));
			if (SelectedDrive != drive) return;   // the user switched drives meanwhile
			_videoTs = videoTs;
			DiscName = OutputNaming.DiscName(disc.VolumeName);
			FileName = DiscName;
			// Initially checked: long enough, and not a duplicate or compilation (the user can change any of them).
			foreach (var t in disc.Titles) {
				var row = new TitleRow(t, MinimumLength);
				row.IsChecked = !row.IsShort && !row.IsDuplicateOrCompilation;
				Titles.Add(row);
			}
			CellMap = disc.CellMap;
			int shortCount = Titles.Count(t => t.IsShort);
			int flagged = Titles.Count(t => t.IsDuplicateOrCompilation);
			Status = $"{Titles.Count} titles, {disc.CellMap.Cells.Count} cells" +
					 (shortCount > 0 ? $"; {shortCount} shorter than {MinimumLength.TotalSeconds:0} s (dimmed)" : "") +
					 (flagged > 0 ? $"; {flagged} duplicate or compilation" : "") + ".";
		} catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) {
			ClearDisc($"Could not read the disc in {drive.Letter}: {ex.Message}");
		} finally {
			IsScanning = false;
			ScanFinished?.Invoke();
		}
	}

	/// <summary>Test mode: check exactly these titles and use this output folder without saving it in the settings.</summary>
	public void PrepareTestRip(IReadOnlyCollection<int> titles, string folder) {
		foreach (var t in Titles) t.IsChecked = titles.Contains(t.Number);
		_outputFolder = folder;
		Raise(nameof(OutputFolder));
	}

	private void SetAllChecked(bool value) {
		foreach (var t in Titles) t.IsChecked = value;
	}

	/// <summary>
	/// Phase 2 → 3: checks the selection and the output folder, asks about existing files, then rips the checked titles in the background.
	/// </summary>
	public async Task StartAsync() {
		if (_videoTs is null || IsRipping) return;
		var chosen = Titles.Where(t => t.IsChecked).ToList();
		if (chosen.Count == 0) {
			ShowMessage?.Invoke("Check at least one title to rip.");
			return;
		}

		// The folder must exist (or be creatable) and be writable.
		string folder = OutputFolder.Trim();
		try {
			Directory.CreateDirectory(folder);
			string probe = Path.Combine(folder, $".rip-dvd-write-test-{Guid.NewGuid():N}");
			File.WriteAllText(probe, "");
			File.Delete(probe);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			ShowMessage?.Invoke($"Can't write to the output folder \"{folder}\":\n{ex.Message}");
			return;
		}

		string name = OutputNaming.Sanitize(string.IsNullOrWhiteSpace(FileName) ? DiscName : FileName.Trim());
		var planned = chosen.Select(t => (Row: t, Path: Path.Combine(folder, OutputNaming.FileName(name, t.Number)))).ToList();
		var existing = planned.Where(p => File.Exists(p.Path)).Select(p => Path.GetFileName(p.Path)).ToList();
		if (existing.Count > 0) {
			var choice = AskAboutExistingFiles?.Invoke(existing) ?? ExistingFilesChoice.Cancel;
			if (choice == ExistingFilesChoice.Cancel) return;
			if (choice == ExistingFilesChoice.Skip) planned = planned.Where(p => !File.Exists(p.Path)).ToList();
		}
		if (planned.Count == 0) {
			RipStatus = "Nothing to rip: all the files already exist.";
			return;
		}

		var items = planned.Select(p => new RipJobItem(p.Row.Number, p.Path, p.Row.Title.SizeBytes / 2048)).ToList();
		IsRipping = true;
		_cancel = new CancellationTokenSource();
		var progress = new Progress<RipJobProgress>(OnRipProgress);   // reports arrive on the UI thread
		string videoTs = _videoTs;
		try {
			var results = await Task.Run(() => new RipJob(videoTs, items).Run(p => ((IProgress<RipJobProgress>)progress).Report(p), _cancel.Token));
			int failed = results.Count(r => r.Error is not null);
			RipStatus = failed == 0
				? $"Done: {results.Count} title(s) ripped to {folder}."
				: $"Done with errors: {results.Count - failed} ripped, {failed} failed ({string.Join("; ", results.Where(r => r.Error is not null).Select(r => $"title {r.Item.TitleNumber}: {r.Error!.Message}"))}).";
		} catch (OperationCanceledException) {
			RipStatus = "Cancelled; the unfinished file was removed.";
		} finally {
			IsRipping = false;
			_cancel.Dispose();
			_cancel = null;
		}
	}

	private void OnRipProgress(RipJobProgress p) {
		string Time(TimeSpan? t) => t is { } r ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : "-:--:--";
		string phase = p.Title.Phase switch {
			RipPhase.Unlocking => "unlocking the drive",
			RipPhase.FindingKey => "finding the title key",
			RipPhase.Finishing => "finishing the file",
			RipPhase.Done => "done",
			_ => $"{p.TitleFraction:P0}, {Time(p.TitleRemaining)} left",
		};
		RipStatus = $"Title {p.Item.TitleNumber} ({p.Index + 1} of {p.Count}): {phase}.   All: {p.OverallFraction:P0}, {Time(p.OverallRemaining)} left.   {p.BytesPerSecond / 1e6:F1} MB/s";
	}
}
