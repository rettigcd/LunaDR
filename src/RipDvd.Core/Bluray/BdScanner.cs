using RipDvd.Core.Ripping;

namespace RipDvd.Core.Bluray;

/// <summary>One Blu-ray title (a playlist) as listed to the user, like a row in MakeMKV's title list.</summary>
public sealed record BdTitle(
	int Playlist,                      // playlist number: 800 for 00800.mpls (also the number used to rip it)
	long DurationNs,
	int Chapters,
	long SizeBytes,                    // estimated: each play item's share of its clip file
	string Video,                      // e.g. "H.264 1080p 23.976"
	IReadOnlyList<string> Audio,       // e.g. "TrueHD multi-channel eng"
	IReadOnlyList<string> Subtitles,   // languages of the PGS tracks
	IReadOnlyList<string> Clips,       // clip names in play order, e.g. "00113"
	int Angles) {
	/// <summary>Set when this playlist plays the same clips with the same in/out times as an earlier one.</summary>
	public int? DuplicateOf { get; init; }
}

public sealed record BdDiscSummary(string Root, string VolumeName, IReadOnlyList<BdTitle> Titles);

/// <summary>Reads a Blu-ray disc's playlists into a title list (plan step BR2).</summary>
public static class BdScanner {
	/// <summary>True if <paramref name="root"/> (a drive root or a folder) holds a Blu-ray disc structure (BDMV/index.bdmv).</summary>
	public static bool IsBluray(string root) => File.Exists(Path.Combine(root, "BDMV", "index.bdmv"));

	/// <summary>Turns "disc:N", "E:" or a path (the disc root, or its BDMV folder) into the disc root, or null if it holds no Blu-ray structure.</summary>
	public static string? ResolveRoot(string disc) {
		if (disc.StartsWith("disc:", StringComparison.OrdinalIgnoreCase)) {
			int n = int.Parse(disc[5..]);
			var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).ToList();
			if (n >= drives.Count) return null;
			disc = drives[n].RootDirectory.FullName;
		} else if (OperatingSystem.IsWindows() && disc.Length <= 3 && disc.TrimEnd(':', '\\').Length == 1) {
			disc = disc.TrimEnd(':', '\\') + @":\";
		}
		if (Path.GetFileName(Path.TrimEndingDirectorySeparator(disc)).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
			disc = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(disc))!;
		return IsBluray(disc) ? disc : null;
	}

	/// <summary>The disc's volume label (drive root) or folder name (a copy on disk).</summary>
	public static string VolumeName(string root) {
		try {
			var info = new DriveInfo(root);
			if (info.IsReady && Path.TrimEndingDirectorySeparator(info.RootDirectory.FullName) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
				return info.VolumeLabel;
		} catch (ArgumentException) {
		}
		return Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
	}

	public static Mpls LoadPlaylist(string root, int number) {
		string name = $"{number:00000}";
		return Mpls.Parse(File.ReadAllBytes(Path.Combine(root, "BDMV", "PLAYLIST", name + ".mpls")), name);
	}

	public static BdDiscSummary Scan(string root) {
		var clips = new Dictionary<string, (long Bytes, long Ticks)>();
		(long, long) Clip(string id) {
			if (!clips.TryGetValue(id, out var c)) {
				string clpi = Path.Combine(root, "BDMV", "CLIPINF", id + ".clpi");
				string m2ts = Path.Combine(root, "BDMV", "STREAM", id + ".m2ts");
				long bytes = File.Exists(m2ts) ? new FileInfo(m2ts).Length : 0;
				long ticks = File.Exists(clpi) ? Clpi.Parse(File.ReadAllBytes(clpi)).DurationTicks : 0;
				clips[id] = c = (bytes, ticks);
			}
			return c;
		}

		var titles = new List<BdTitle>();
		var seen = new Dictionary<string, int>();
		foreach (var file in Directory.GetFiles(Path.Combine(root, "BDMV", "PLAYLIST"), "*.mpls").Order(StringComparer.OrdinalIgnoreCase)) {
			string name = Path.GetFileNameWithoutExtension(file);
			if (!int.TryParse(name, out int number)) continue;
			Mpls mpls;
			try {
				mpls = Mpls.Parse(File.ReadAllBytes(file), name);
			} catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) {
				continue;   // damaged or deliberately malformed playlist (some discs carry decoys)
			}
			if (mpls.Items.Count == 0) continue;
			var title = Describe(number, mpls, Clip);
			string key = string.Join(";", mpls.Items.Select(i => $"{i.ClipId}:{i.InTime}-{i.OutTime}"));
			if (seen.TryGetValue(key, out int first)) title = title with { DuplicateOf = first };
			else seen[key] = number;
			titles.Add(title);
		}
		return new BdDiscSummary(root, VolumeName(root), titles);
	}

	private static BdTitle Describe(int number, Mpls mpls, Func<string, (long Bytes, long Ticks)> clip) {
		long size = 0;
		foreach (var item in mpls.Items) {
			var (bytes, ticks) = clip(item.ClipId);
			size += ticks <= 0 ? bytes : (long)(bytes * Math.Min(1.0, (double)(item.OutTime - item.InTime) / ticks));
		}
		var first = mpls.Items[0];
		var v = first.Video.FirstOrDefault();
		string video = v is null ? "" : $"{v.CodecName} {v.VideoFormat} {v.FrameRate:0.###}";
		var audio = first.Audio.Select(a => $"{a.CodecName} {a.ChannelLayout} {a.Language}".Replace("  ", " ")).ToList();
		var subtitles = first.Subtitles.Where(s => s.CodingType == 0x90).Select(s => s.Language ?? "und").ToList();
		return new BdTitle(number, mpls.DurationNs, mpls.ChapterStartsNs().Count, size, video, audio, subtitles,
			mpls.Items.Select(i => i.ClipId).ToList(), mpls.Items.Max(i => i.Angles));
	}

	/// <summary>Output file name: "&lt;Disc name&gt; - playlist 00800.mkv" (the number traces the file back to the disc's playlist).</summary>
	public static string FileName(string discName, int playlist) => OutputNaming.Sanitize($"{discName} - playlist {playlist:00000}.mkv");
}
