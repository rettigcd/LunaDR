using RipDvd.Core.Ifo;

namespace RipDvd.Core.Ripping;

/// <summary>One title as listed to the user (like a row in MakeMKV's title list).</summary>
public sealed record TitleSummary(
	int TitleNumber,          // disc title number (1-based, from VIDEO_TS.IFO)
	int TitleSetNumber,
	int Chapters,
	int Cells,
	long DurationNs,          // from the IFO cell times; equals the stream duration on the test disc
	long SizeBytes,           // sum of cell sectors × 2048 (matches MakeMKV)
	string Video,             // e.g. "720x480 16:9"
	IReadOnlyList<string> Audio,
	bool ClosedCaptions);

/// <summary>Disc-level information and the title list.</summary>
public sealed record DiscSummary(string VideoTsFolder, string VolumeName, IReadOnlyList<TitleSummary> Titles);

/// <summary>
/// Reads the IFO files of a DVD-Video folder and lists its titles.
/// Discs are named like MakeMKV/makemkvcon ("disc:0" = first optical drive) or by drive letter or folder path.
/// </summary>
public static class DiscScanner {
	/// <summary>MakeMKV's default minimum title length.</summary>
	public static readonly TimeSpan DefaultMinimumLength = TimeSpan.FromSeconds(120);

	/// <summary>
	/// Turns "disc:N", "E:", "E:\" or a path (to the disc root or its VIDEO_TS folder) into the VIDEO_TS folder.
	/// Drive letters only exist on Windows; on Linux pass the mount point (e.g. /media/user/LABEL).
	/// </summary>
	public static string ResolveVideoTs(string disc) {
		if (disc.StartsWith("disc:", StringComparison.OrdinalIgnoreCase)) {
			int n = int.Parse(disc[5..]);
			var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).ToList();
			if (n >= drives.Count) throw new ArgumentException($"There is no optical drive {disc} (found {drives.Count}).");
			disc = drives[n].RootDirectory.FullName;
		} else if (OperatingSystem.IsWindows() && disc.Length <= 3 && disc.TrimEnd(':', '\\').Length == 1) {
			disc = disc.TrimEnd(':', '\\') + @":\";
		}
		string videoTs = Path.GetFileName(Path.TrimEndingDirectorySeparator(disc)).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)
			? disc
			: Path.Combine(disc, "VIDEO_TS");
		if (!File.Exists(Path.Combine(videoTs, "VIDEO_TS.IFO")))
			throw new FileNotFoundException($"No DVD-Video found: {Path.Combine(videoTs, "VIDEO_TS.IFO")} does not exist (is a disc inserted?).");
		return videoTs;
	}

	public static DiscSummary Scan(string videoTsFolder) {
		var vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTsFolder, "VIDEO_TS.IFO")));
		var vtsCache = new Dictionary<int, VtsIfo>();
		var titles = new List<TitleSummary>();
		foreach (var t in vmg.Titles) {
			if (!vtsCache.TryGetValue(t.TitleSetNumber, out var vts))
				vtsCache[t.TitleSetNumber] = vts = VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTsFolder, $"VTS_{t.TitleSetNumber:00}_0.IFO")), t.TitleSetNumber);

			var chapters = vts.Chapters[t.TitleSetTitleNumber - 1];
			var pgcs = chapters.Select(c => c.PgcNumber).Distinct().Select(n => vts.Pgcs[n - 1]).ToList();
			var cells = pgcs.SelectMany(p => p.Cells).Where(c => c.BlockType != 1 || c.BlockMode == 1).ToList();
			var time = cells.Aggregate(default(DvdTime), (sum, c) => sum + c.PlaybackTime);

			var audio = new List<string>();
			for (int i = 0; i < vts.Audio.Count; i++) {
				if (!pgcs[0].AudioControl[i].Available) continue;
				var a = vts.Audio[i];
				audio.Add($"{a.Coding} {(a.Channels == 6 ? "5.1" : a.Channels == 2 ? "stereo" : a.Channels + "ch")} {Languages.ToIso6392(a.Language)}");
			}

			titles.Add(new TitleSummary(
				t.TitleNumber, t.TitleSetNumber, chapters.Count, cells.Count, time.ToNanoseconds(),
				cells.Sum(c => c.SectorCount) * 2048,
				$"{vts.Video.Width}x{vts.Video.Height} {vts.Video.AspectRatio}",
				audio,
				vts.Video.Line21Field1 || vts.Video.Line21Field2));
		}

		string volume = vmg.ProviderId;
		try {
			var root = Directory.GetParent(videoTsFolder)?.FullName;
			if (root is not null && OperatingSystem.IsWindows()) volume = new DriveInfo(root).VolumeLabel;
		} catch (IOException) {
			// Not a drive (e.g. a folder on disk): keep the IFO provider id.
		}
		return new DiscSummary(videoTsFolder, volume, titles);
	}
}
