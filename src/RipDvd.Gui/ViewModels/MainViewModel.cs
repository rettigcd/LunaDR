using System.Collections.ObjectModel;
using System.IO;
using RipDvd.Gui;
using System.Windows.Input;
using RipDvd.Core.Bluray;
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

	/// <summary>The scanned disc is a Blu-ray: <see cref="_videoTs"/> is then its root folder and the title numbers are playlist numbers.</summary>
	private bool _bluray;
	private CancellationTokenSource? _cancel;
	// Phase 3: progress of the current title and of all titles, shown in the progress panel.
	private bool _showProgress;
	private double _titleProgress, _overallProgress;
	private string _titleProgressText = "", _overallProgressText = "";
	private string? _lastOutputFolder;
	private IReadOnlyList<int> _selectedCells = [];
	// G6: rip progress in the cell map.
	private IReadOnlyList<int>? _ripCells;
	private int _ripPosition = -1, _ripIndex = -1;
	private double _ripCellFraction;
	private int? _ripTitle;

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
		CancelCommand = new RelayCommand(() => {
			if (ConfirmCancel?.Invoke() ?? true) _cancel?.Cancel();
		}, () => IsRipping);
		OpenFolderCommand = new RelayCommand(() => {
			if (_lastOutputFolder is { } folder && Directory.Exists(folder))
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
		}, () => !IsRipping && _lastOutputFolder is not null);
		RipCellsCommand = new RelayCommand(() => _ = RipSelectedCellsAsync(),
			() => !IsScanning && !IsRipping && CellMap is { Bluray: false } map && CellSelection.Validate(map, SelectedCells) is null && !string.IsNullOrWhiteSpace(OutputFolder));
		ClearCellsCommand = new RelayCommand(() => SelectedCells = [], () => !IsRipping && SelectedCells.Count > 0);
		RefreshDrives();
	}

	/// <summary>G6: the playlist being ripped (cell numbers), the position being read, how far into that cell, and the title (null for chosen cells).</summary>
	public IReadOnlyList<int>? RipCells { get => _ripCells; private set => Set(ref _ripCells, value); }
	public int RipPosition { get => _ripPosition; private set => Set(ref _ripPosition, value); }
	public double RipCellFraction { get => _ripCellFraction; private set => Set(ref _ripCellFraction, value); }
	public int? RipTitle { get => _ripTitle; private set => Set(ref _ripTitle, value); }

	public ICommand RipCellsCommand { get; }
	public ICommand ClearCellsCommand { get; }

	/// <summary>Cells selected in the cell map, as cell numbers in playback order (G5: rip chosen cells into one file).</summary>
	public IReadOnlyList<int> SelectedCells {
		get => _selectedCells;
		set {
			if (!Set(ref _selectedCells, value ?? [])) return;
			Raise(nameof(SelectedCellsText));
			RefreshButtons();
		}
	}

	/// <summary>"Cells 23-27: 1:23 (title set 4)", or why the selection can't be ripped; a hint when nothing is selected.</summary>
	public string SelectedCellsText {
		get {
			if (CellMap is not { } map) return "";
			if (map.Bluray) {
				if (SelectedCells.Count == 0) return "Clip segments (Blu-ray): click a playlist to see which playlists share its video; hover for details.";
				var total = TimeSpan.FromTicks(SelectedCells.Where(n => n >= 1 && n <= map.Cells.Count).Sum(n => map.Cells[n - 1].DurationNs) / 100);
				return $"{(SelectedCells.Count == 1 ? "Segment" : "Segments")} {CellSelection.Format(SelectedCells)}: " +
					   $"{(int)total.TotalHours}:{total.Minutes:00}:{total.Seconds:00}. Ripping chosen segments works on DVDs only, for now.";
			}
			if (SelectedCells.Count == 0) return "Select cells to rip them on their own: click a cell, Ctrl+click to add, Shift+click for a range.";
			if (CellSelection.Validate(map, SelectedCells) is { } error) return error;
			var length = TimeSpan.FromTicks(CellSelection.DurationNs(map, SelectedCells) / 100);
			return $"{(SelectedCells.Count == 1 ? "Cell" : "Cells")} {CellSelection.Format(SelectedCells)}: {(int)length.TotalHours}:{length.Minutes:00}:{length.Seconds:00}" +
				   $" (title set {map.Cells[SelectedCells[0] - 1].TitleSet}){(SelectedCells.Count > 1 ? ", joined in this order, one chapter per cell" : "")}.";
		}
	}

	public ICommand OpenFolderCommand { get; }

	/// <summary>Supplied by the window: asks "Stop ripping?"; true = yes.</summary>
	public Func<bool>? ConfirmCancel { get; set; }

	/// <summary>Files written (or titles that failed) in the current/last rip, in order.</summary>
	public ObservableCollection<ResultRow> Results { get; } = [];

	/// <summary>The progress panel is shown once a rip has started (and stays to show the results).</summary>
	public bool ShowProgress { get => _showProgress; private set => Set(ref _showProgress, value); }

	/// <summary>Current title, 0..1.</summary>
	public double TitleProgress { get => _titleProgress; private set => Set(ref _titleProgress, value); }
	public string TitleProgressText { get => _titleProgressText; private set => Set(ref _titleProgressText, value); }

	/// <summary>All titles of the rip, 0..1 (weighted by size).</summary>
	public double OverallProgress { get => _overallProgress; private set => Set(ref _overallProgress, value); }
	public string OverallProgressText { get => _overallProgressText; private set => Set(ref _overallProgressText, value); }

	/// <summary>Play the Windows notification sound when a rip finishes; remembered between runs.</summary>
	public bool PlaySoundWhenDone {
		get => _settings.PlaySoundWhenDone;
		set {
			if (_settings.PlaySoundWhenDone == value) return;
			_settings.PlaySoundWhenDone = value;
			_settings.Save();
			Raise(nameof(PlaySoundWhenDone));
		}
	}

	/// <summary>Blu-ray: also write the AC3 version inside each TrueHD soundtrack as its own track (RipOptions.TrueHdCore); remembered between runs.</summary>
	public bool TrueHdCore {
		get => _settings.TrueHdCore;
		set {
			if (_settings.TrueHdCore == value) return;
			_settings.TrueHdCore = value;
			_settings.Save();
			Raise(nameof(TrueHdCore));
		}
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
	/// <summary>DVDs have a cell map; Blu-rays (and no disc) don't, and the cell area is hidden.</summary>
	public bool HasCellMap => _cellMap is not null;

	/// <summary>A Blu-ray is scanned (shows the Blu-ray-only options).</summary>
	public bool IsBluray => _bluray;

	/// <summary>Shown after the file name box: how the files will be named.</summary>
	public string FileNameSuffix => _bluray ? " - playlist NNNNN.mkv" : " - title NN.mkv";

	/// <summary>The tab shown: 0 = title list, 1 = cell map (back to the title list when there is no map).</summary>
	public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }
	private int _selectedTab;

	public CellMap? CellMap {
		get => _cellMap;
		private set {
			if (!Set(ref _cellMap, value)) return;
			Raise(nameof(HasCellMap));
			if (value is null) SelectedTab = 0;
			SelectedCells = [];
			Raise(nameof(SelectedCellsText));
		}
	}

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
		_bluray = false;
		Raise(nameof(FileNameSuffix));
		Raise(nameof(IsBluray));
		SelectedTitle = null;
		Status = status;
	}

	/// <summary>Reads the disc's IFO files on a background thread and fills the title list and cell map.</summary>
	public async Task ScanAsync() {
		if (SelectedDrive is not { HasDisc: true } drive || IsScanning) return;
		IsScanning = true;
		ClearDisc($"Scanning {drive.Letter} …");
		try {
			if (BdScanner.ResolveRoot(drive.Letter) is { } bdRoot) {
				await ScanBlurayAsync(drive, bdRoot);
				return;
			}
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

	/// <summary>
	/// Blu-ray (BR6): the playlists fill the title list (numbered by playlist), and the map shows which playlists play which clip segments.
	/// Checked at first: long enough and not a repeat of an earlier playlist.
	/// </summary>
	private async Task ScanBlurayAsync(DriveItem drive, string root) {
		var disc = await Task.Run(() => BdScanner.Scan(root));
		if (SelectedDrive != drive) return;
		_videoTs = root;
		_bluray = true;
		Raise(nameof(FileNameSuffix));
		Raise(nameof(IsBluray));
		CellMap = disc.Segments;
		DiscName = OutputNaming.DiscName(disc.VolumeName);
		FileName = DiscName;
		foreach (var t in disc.Titles.OrderByDescending(t => t.DurationNs)) {
			var summary = new TitleSummary(t.Playlist, 0, t.Chapters, t.Clips.Count, t.DurationNs, t.SizeBytes, t.Video, t.Audio, t.Subtitles, false) {
				DuplicateOf = t.DuplicateOf,
			};
			var row = new TitleRow(summary, MinimumLength, unit: "playlist");
			row.IsChecked = !row.IsShort && !row.IsDuplicateOrCompilation;
			Titles.Add(row);
		}
		int shortCount = Titles.Count(t => t.IsShort);
		int repeats = Titles.Count(t => t.IsDuplicateOrCompilation);
		Status = $"Blu-ray: {Titles.Count} playlists (the Title column is the playlist number)" +
				 (shortCount > 0 ? $"; {shortCount} shorter than {MinimumLength.TotalSeconds:0} s (dimmed)" : "") +
				 (repeats > 0 ? $"; {repeats} repeat an earlier playlist" : "") +
				 (Directory.Exists(Path.Combine(root, "AACS")) && KeyDb.Find() is null ? "; AACS-encrypted and no key file found (KEYDB.cfg)" : "") + ".";
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

		if (CheckOutputFolder() is not { } folder) return;
		string name = OutputName();
		var items = chosen.Select(t => _bluray
			? RipJobItem.ForPlaylist(t.Number, Path.Combine(folder, BdScanner.FileName(name, t.Number)), t.Title.SizeBytes / 2048)
			: new RipJobItem(t.Number, Path.Combine(folder, OutputNaming.FileName(name, t.Number)), t.Title.SizeBytes / 2048)).ToList();
		await RunAsync(items, folder);
	}

	/// <summary>G5: rips the cells selected in the cell map, joined in the order selected, into one file "&lt;name&gt; - cells LIST.mkv".</summary>
	public async Task RipSelectedCellsAsync() {
		if (_videoTs is null || IsRipping || CellMap is not { } map) return;
		var cells = SelectedCells.ToList();
		if (CellSelection.Validate(map, cells) is { } error) {
			ShowMessage?.Invoke(error);
			return;
		}
		if (CheckOutputFolder() is not { } folder) return;
		var item = RipJobItem.ForCells(cells, Path.Combine(folder, OutputNaming.CellsFileName(OutputName(), cells)), CellSelection.Sectors(map, cells));
		await RunAsync([item], folder);
	}

	private string OutputName() => OutputNaming.Sanitize(string.IsNullOrWhiteSpace(FileName) ? DiscName : FileName.Trim());

	/// <summary>The output folder, created if needed and checked to be writable; null (after a message) if it can't be used.</summary>
	private string? CheckOutputFolder() {
		string folder = OutputFolder.Trim();
		try {
			Directory.CreateDirectory(folder);
			string probe = Path.Combine(folder, $".lunadr-write-test-{Guid.NewGuid():N}");
			File.WriteAllText(probe, "");
			File.Delete(probe);
			return folder;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			ShowMessage?.Invoke($"Can't write to the output folder \"{folder}\":\n{ex.Message}");
			return null;
		}
	}

	/// <summary>Asks about files that already exist, then runs the job in the background with the progress panel (phase 3).</summary>
	private async Task RunAsync(List<RipJobItem> items, string folder) {
		var existing = items.Where(i => File.Exists(i.OutputPath)).Select(i => Path.GetFileName(i.OutputPath)).ToList();
		if (existing.Count > 0) {
			var choice = AskAboutExistingFiles?.Invoke(existing) ?? ExistingFilesChoice.Cancel;
			if (choice == ExistingFilesChoice.Cancel) return;
			if (choice == ExistingFilesChoice.Skip) items = items.Where(i => !File.Exists(i.OutputPath)).ToList();
		}
		if (items.Count == 0) {
			RipStatus = "Nothing to rip: all the files already exist.";
			return;
		}

		IsRipping = true;
		Results.Clear();
		TitleProgress = OverallProgress = 0;
		TitleProgressText = "Starting…";
		OverallProgressText = $"All titles: 0 of {items.Count} done";
		ShowProgress = true;
		RipStatus = items.Count == 1 ? $"Ripping {items[0].Name.ToLowerInvariant()}…" : $"Ripping {items.Count} titles…";
		_lastOutputFolder = folder;
		_cancel = new CancellationTokenSource();
		// Progress<T> delivers the reports on the UI thread, so the bindings can update.
		IProgress<RipJobProgress> progress = new Progress<RipJobProgress>(OnRipProgress);
		IProgress<RipJobItemResult> finished = new Progress<RipJobItemResult>(r => Results.Add(new ResultRow(r)));
		string videoTs = _videoTs!;   // both callers return early when no disc is scanned
		bool cancelled = false;
		try {
			var options = new RipOptions { TrueHdCore = TrueHdCore };
			var results = await Task.Run(() => new RipJob(videoTs, items, options).Run(progress.Report, _cancel.Token, finished.Report));
			int failed = results.Count(r => r.Error is not null);
			RipStatus = failed == 0
				? $"Done: {results.Count} title(s) ripped to {folder}."
				: $"Done with errors: {results.Count - failed} ripped, {failed} failed (see the list).";
			OverallProgress = 1;
			OverallProgressText = $"All titles: {results.Count - failed} of {items.Count} ripped" + (failed > 0 ? $", {failed} failed" : "");
		} catch (OperationCanceledException) {
			cancelled = true;
			RipStatus = "Cancelled; the unfinished file was removed.";
			TitleProgressText = "Cancelled";
		} finally {
			IsRipping = false;
			_cancel.Dispose();
			_cancel = null;
			// The progress panel lists what was done; the cell map goes back to showing the disc.
			_ripIndex = -1;
			RipCells = null;
			RipTitle = null;
			RipPosition = -1;
		}
		if (!cancelled && PlaySoundWhenDone) NotificationSound.Play();
	}

	/// <summary>Which segment (position in <paramref name="segments"/>, 1-based cell numbers) a fraction of the playlist falls in, and how far into it.</summary>
	internal static (int Position, double Fraction) SegmentPosition(CellMap map, IReadOnlyList<int> segments, double fraction) {
		var lengths = segments.Select(n => (double)map.Cells[n - 1].DurationNs).ToList();
		double target = Math.Clamp(fraction, 0, 1) * lengths.Sum(), done = 0;
		for (int i = 0; i < lengths.Count; i++) {
			if (target < done + lengths[i]) return (i, lengths[i] > 0 ? (target - done) / lengths[i] : 0);
			done += lengths[i];
		}
		return (lengths.Count, 0);
	}

	private void OnRipProgress(RipJobProgress p) {
		string Time(TimeSpan? t) => t is { } r ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00} left" : "estimating time…";
		string state = p.Title.Phase switch {
			RipPhase.Unlocking => "unlocking the drive",
			RipPhase.FindingKey => "finding the title key",
			RipPhase.Finishing => "finishing the file",
			RipPhase.Done => "done",
			_ => $"{p.TitleFraction:P0}   ·   {Time(p.TitleRemaining)}   ·   {p.BytesPerSecond / 1e6:F1} MB/s",
		};
		TitleProgress = p.TitleFraction;
		TitleProgressText = $"{p.Item.Name}: {state}";
		if (p.Index != _ripIndex) {
			// A new item: show its playlist in the cell map (a title's cells from the map, or the chosen cells).
			_ripIndex = p.Index;
			int number = p.Item.Playlist ?? p.Item.TitleNumber;
			RipTitle = p.Item.Cells is null ? number : null;
			RipCells = p.Item.Cells?.ToList()
					   ?? CellMap?.Titles.FirstOrDefault(t => t.TitleNumber == number)?.Cells.Select(c => c + 1).ToList();
		}
		if (CellMap is { Bluray: true } map && RipCells is { Count: > 0 } segments) {
			// The Blu-ray ripper reports progress by play item, not by segment: place the share read so far on the segments by their lengths.
			(RipPosition, RipCellFraction) = SegmentPosition(map, segments, p.TitleFraction);
		} else {
			RipPosition = p.Title.CellPosition;
			RipCellFraction = p.Title.CellFraction;
		}
		OverallProgress = p.OverallFraction;
		OverallProgressText = $"All titles ({p.Index + 1} of {p.Count}): {p.OverallFraction:P0}   ·   {Time(p.OverallRemaining)}";
	}
}
