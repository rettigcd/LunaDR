using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using RipDvd.Core.Ripping;

namespace RipDvd.Gui.ViewModels;

/// <summary>An optical drive in the drive picker.</summary>
public sealed record DriveItem(string Letter, string Label, bool HasDisc) {
	public string Display => HasDisc ? $"{Letter}  {Label}" : $"{Letter}  (no disc)";
}

/// <summary>
/// The main window's state (phase 1: scan and present).
/// Scans run on a background thread; the window updates through bindings when they finish.
/// </summary>
public sealed class MainViewModel : ViewModelBase {
	private DriveItem? _selectedDrive;
	private bool _isScanning;
	private string _status = "Insert a DVD.";
	private string _discName = "";
	private CellMap? _cellMap;
	private TitleRow? _selectedTitle;
	private int? _selectedTitleNumber;
	private string _hoverInfo = "";

	public MainViewModel() {
		ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsScanning && SelectedDrive is { HasDisc: true });
		RefreshDrives();
	}

	public ObservableCollection<DriveItem> Drives { get; } = [];
	public ObservableCollection<TitleRow> Titles { get; } = [];
	public ICommand ScanCommand { get; }

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

	public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
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
		SelectedTitle = null;
		Status = status;
	}

	/// <summary>Reads the disc's IFO files on a background thread and fills the title list and cell map.</summary>
	public async Task ScanAsync() {
		if (SelectedDrive is not { HasDisc: true } drive || IsScanning) return;
		IsScanning = true;
		ClearDisc($"Scanning {drive.Letter} …");
		try {
			var disc = await Task.Run(() => DiscScanner.Scan(DiscScanner.ResolveVideoTs(drive.Letter)));
			if (SelectedDrive != drive) return;   // the user switched drives meanwhile
			DiscName = OutputNaming.DiscName(disc.VolumeName);
			foreach (var t in disc.Titles) Titles.Add(new TitleRow(t, MinimumLength));
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
}
