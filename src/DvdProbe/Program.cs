// DvdProbe: command-line tool for probing DVDs and testing ideas while building the ripper.
// Commands that are no longer needed are marked [Obsolete] and labeled "old" in the help,
// but are kept so the history of the investigation stays visible.

using System.Reflection;

namespace DvdProbe;

public static class Program {
	public static int Main(string[] args) {
		var commands = typeof(Commands)
			.GetMethods(BindingFlags.Public | BindingFlags.Static)
			.Select(m => (Method: m, Attr: m.GetCustomAttribute<ProbeCommandAttribute>()))
			.Where(x => x.Attr != null)
			.ToDictionary(x => x.Attr!.Name, x => x, StringComparer.OrdinalIgnoreCase);

		if (args.Length == 0 || !commands.TryGetValue(args[0], out var cmd)) {
			Console.WriteLine("usage: DvdProbe <command> [args]\n");
			foreach (var (name, c) in commands.OrderBy(k => k.Key)) {
				bool old = c.Method.GetCustomAttribute<ObsoleteAttribute>() != null;
				Console.WriteLine($"  {name,-12} {(old ? "(old) " : "")}{c.Attr!.Help}");
			}
			return args.Length == 0 ? 0 : 1;
		}

		try {
			return (int)cmd.Method.Invoke(null, [args[1..]])!;
		} catch (TargetInvocationException ex) when (ex.InnerException is IOException or UnauthorizedAccessException) {
			// File and drive problems (no disc, drive not ready, disc unreadable) get a one-line message instead of a stack trace.
			string? drive = args.Skip(1).FirstOrDefault(a => a.Length <= 3 && a.TrimEnd(':', '\\').Length == 1);
			bool noDisc = drive is not null && DriveInfo.GetDrives().Any(d =>
				d.Name.StartsWith(drive.TrimEnd(':', '\\'), StringComparison.OrdinalIgnoreCase) && d.DriveType == DriveType.CDRom && !d.IsReady);
			Console.Error.WriteLine(noDisc
				? $"Error: no disc is ready in drive {drive!.TrimEnd(':', '\\')}: (still spinning up, tray open, or the disc can't be read)."
				: $"Error: {ex.InnerException!.Message}");
			return 2;
		}
	}
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ProbeCommandAttribute(string name, string help) : Attribute {
	public string Name { get; } = name;
	public string Help { get; } = help;
}

public static class Commands {
	[ProbeCommand("drives", "List optical drives and whether a disc is loaded.")]
	public static int Drives(string[] args) {
		foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom)) {
			string label = d.IsReady ? d.VolumeLabel : "(no disc)";
			Console.WriteLine($"{d.Name}  {label}");
		}
		return 0;
	}

	[ProbeCommand("files", "List the VIDEO_TS files on a disc. Args: <drive letter>")]
	public static int Files(string[] args) {
		string root = args.Length > 0 ? args[0].TrimEnd(':', '\\') + ":\\" : "E:\\";
		var dir = new DirectoryInfo(Path.Combine(root, "VIDEO_TS"));
		if (!dir.Exists) { Console.Error.WriteLine($"No VIDEO_TS folder on {root}"); return 1; }
		foreach (var f in dir.GetFiles().OrderBy(f => f.Name))
			Console.WriteLine($"{f.Name,-14} {f.Length,14:N0}");
		return 0;
	}

	[ProbeCommand("titles", "Parse the IFO files and list titles like MakeMKV. Args: <drive or VIDEO_TS path> [-v]")]
	public static int Titles(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args.FirstOrDefault(a => !a.StartsWith('-')) ?? "E:");
		bool verbose = args.Contains("-v");
		IfoProbe.PrintTitles(videoTs, verbose);
		return 0;
	}

	[ProbeCommand("scramble", "Sample sectors of VOB files and count CSS-scrambled ones. Args: <vob path>... [-n samples]")]
	public static int Scramble(string[] args) {
		int n = 200;
		int ni = Array.IndexOf(args, "-n");
		if (ni >= 0) n = int.Parse(args[ni + 1]);
		foreach (var path in args.Where((a, i) => !a.StartsWith('-') && (ni < 0 || i != ni + 1)))
			SectorProbe.Scan(path, n);
		return 0;
	}

	[ProbeCommand("duration", "Measure title durations from nav-pack timestamps. Args: <drive> <title numbers...> [-c per cell]")]
	public static int Duration(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args[0]);
		bool perCell = args.Contains("-c");
		foreach (var t in args.Skip(1).Where(a => !a.StartsWith('-')).Select(int.Parse))
			DurationProbe.Measure(videoTs, t, perCell);
		return 0;
	}

	[ProbeCommand("bench", "Time CSS descrambling and key recovery in memory (no drive I/O). Args: <drive> <vts>")]
	public static int Bench(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args[0]);
		int vts = int.Parse(args[1]);
		using var reader = RipDvd.Core.Dvd.VobReader.ForTitleSet(videoTs, vts);
		// Collect 2000 scrambled sectors into memory first, so only CPU work is timed.
		var sectors = new List<byte[]>();
		var buf = new byte[2048 * 64];
		for (long s = 0; s < reader.SectorCount && sectors.Count < 2000; s += 64) {
			int n = (int)Math.Min(64, reader.SectorCount - s);
			reader.Read(s, n, buf);
			for (int k = 0; k < n && sectors.Count < 2000; k++)
				if (RipDvd.Core.Css.Css.IsScrambled(buf.AsSpan(k * 2048, 2048))) sectors.Add(buf.AsSpan(k * 2048, 2048).ToArray());
		}
		var key = RipDvd.Core.Dvd.TitleSectorReader.FindKey(reader)!.Value;

		var sw = System.Diagnostics.Stopwatch.StartNew();
		int rounds = 0;
		while (sw.ElapsedMilliseconds < 2000) {
			foreach (var s in sectors) { var copy = (byte[])s.Clone(); RipDvd.Core.Css.Css.Unscramble(key, copy); }
			rounds++;
		}
		double mb = rounds * sectors.Count * 2048 / 1e6;
		Console.WriteLine($"Descramble: {mb / sw.Elapsed.TotalSeconds:F0} MB/s on one core ({sectors.Count} sectors x {rounds} rounds)");

		sw.Restart();
		int attacks = 0, hits = 0;
		foreach (var s in sectors) { attacks++; if (RipDvd.Core.Css.Css.AttackPattern(s) is not null) hits++; if (sw.ElapsedMilliseconds > 3000) break; }
		Console.WriteLine($"Key attack: {attacks} sectors in {sw.ElapsedMilliseconds} ms ({hits} gave a key)");
		return 0;
	}

	[ProbeCommand("demux", "Demux a title and check its streams. Args: <drive> <title> [-o outdir] [-cells N]")]
	public static int Demux(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args[0]);
		int title = int.Parse(args[1]);
		int oi = Array.IndexOf(args, "-o");
		int ci = Array.IndexOf(args, "-cells");
		DemuxProbe.Run(videoTs, title, oi >= 0 ? args[oi + 1] : null, ci >= 0 ? int.Parse(args[ci + 1]) : int.MaxValue);
		return 0;
	}

	[ProbeCommand("rip", "Rip a title to MKV with RipDvd.Core TitleRipper. Args: <drive> <title> <output.mkv> [-cells N] [-bad 1000-1010,5000] (simulated unreadable sectors)")]
	public static int Rip(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args[0]);
		var ripper = new RipDvd.Core.Ripping.TitleRipper(videoTs, int.Parse(args[1]));
		int lastPercent = -1;
		int ci = Array.IndexOf(args, "-cells");
		int maxCells = ci >= 0 ? int.Parse(args[ci + 1]) : int.MaxValue;
		int bi = Array.IndexOf(args, "-bad");
		var options = new RipDvd.Core.Ripping.RipOptions();
		if (bi >= 0) {
			var bad = new List<(long From, long To)>();
			foreach (var part in args[bi + 1].Split(',')) {
				var ends = part.Split('-');
				bad.Add((long.Parse(ends[0]), long.Parse(ends[^1])));
			}
			options = new RipDvd.Core.Ripping.RipOptions { SimulateBadSector = s => bad.Any(r => s >= r.From && s <= r.To) };
		}
		var result = ripper.Rip(args[2], maxCells: maxCells, options: options, progress: p => {
			if (p.Phase != RipDvd.Core.Ripping.RipPhase.Ripping) return;
			int pct = (int)(p.Fraction * 100);
			if (pct / 10 != lastPercent / 10) { Console.Write($"{pct}% "); lastPercent = pct; }
		});
		Console.WriteLine();
		Console.WriteLine($"Wrote {result.OutputPath}: duration {TimeSpan.FromTicks(result.DurationNs / 100):h\\:mm\\:ss\\.fff}, " +
						  $"{result.Sectors:N0} sectors in {result.Elapsed.TotalSeconds:F1} s");
		foreach (var w in result.Warnings) Console.WriteLine($"  warning: {w}");
		return 0;
	}

	[ProbeCommand("mkvinfo", "Print a Matroska file's structure and per-track block summary. Args: <file.mkv> [-blocks N] [-dumptrack T] [-saveblock T N file]")]
	public static int MkvInfo(string[] args) {
		int bi = Array.IndexOf(args, "-blocks");
		int di = Array.IndexOf(args, "-dumptrack");
		if (di >= 0) MkvProbe.DumpTrack = long.Parse(args[di + 1]);
		int si = Array.IndexOf(args, "-saveblock");
		if (si >= 0) MkvProbe.SaveBlock = (long.Parse(args[si + 1]), int.Parse(args[si + 2]), args[si + 3]);
		int ci = Array.IndexOf(args, "-clusters");
		if (ci >= 0) MkvProbe.ListClusters = int.Parse(args[ci + 1]);
		MkvProbe.Dump(args[0], bi >= 0 ? int.Parse(args[bi + 1]) : 5);
		return 0;
	}

	[ProbeCommand("chapters", "A title's cells in play order: angle block, flags, IFO time and start on the title timeline (first angle only), '*' = chapter start; -stream adds the start measured from nav packs. Args: <drive> <title> [-stream]")]
	public static int Chapters(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args[0]);
		var vmg = RipDvd.Core.Ifo.VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO")));
		var t = vmg.Titles[int.Parse(args[1]) - 1];
		var vts = RipDvd.Core.Ifo.VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, $"VTS_{t.TitleSetNumber:00}_0.IFO")), t.TitleSetNumber);
		var pgc = vts.Pgcs[vts.Chapters[t.TitleSetTitleNumber - 1][0].PgcNumber - 1];
		// -stream: also the start from the stream, as the ripper's timeline has it (each played cell lasts from its first VOBU's start time to its last VOBU's end time,
		// read from two nav packs per cell).
		RipDvd.Core.Dvd.VobReader? reader = null;
		if (args.Contains("-stream")) {
			RipDvd.Core.Dvd.DriveUnlocker.TryUnlock(videoTs);
			reader = RipDvd.Core.Dvd.VobReader.ForTitleSet(videoTs, t.TitleSetNumber);
		}
		var buf = new byte[RipDvd.Core.Dvd.VobReader.SectorSize];
		var start = default(RipDvd.Core.Ifo.DvdTime);
		long streamNs = 0;
		int chapter = 0;
		foreach (var cell in pgc.Cells) {
			bool entry = pgc.ProgramEntryCells.Contains(cell.Number);
			if (entry) chapter++;
			bool played = cell.BlockType != 1 || cell.BlockMode == 1;
			string block = cell.BlockType == 1 ? $"angle{cell.BlockMode}" : "";
			string flags = (cell.SeamlessPlay ? "S" : "-") + (cell.Interleaved ? "I" : "-") + (cell.StcDiscontinuity ? "D" : "-");
			string stream = reader is null ? "" : $"  stream {streamNs / 1e6,10:F1} ms";
			Console.WriteLine($"{(entry ? $"*{chapter,2}" : "   ")} cell {cell.Number,3} vob {cell.VobId,3}/{cell.CellId,-2} {block,-6} {flags}  " +
							  $"ifo {cell.PlaybackTime.ToTimeSpan().TotalMilliseconds,9:F1} ms  start {start.ToNanoseconds() / 1e6,10:F1} ms{stream}{(played ? "" : "  (other angle)")}");
			if (!played) continue;
			start += cell.PlaybackTime;
			if (reader is not null) {
				reader.Read(cell.FirstSector, 1, buf);
				uint first = RipDvd.Core.Dvd.NavPack.Parse(buf).StartPtm;
				reader.Read(cell.LastVobuStartSector, 1, buf);
				uint last = RipDvd.Core.Dvd.NavPack.Parse(buf).EndPtm;
				streamNs += (long)(last - first) * 100_000 / 9;
			}
		}
		reader?.Dispose();
		return 0;
	}

	[ProbeCommand("bddrive", "Blu-ray (or any optical) drive: model, firmware, current medium, and the feature list (BD read, AACS version, bus encryption). Args: <drive>")]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public static int BdDrive(string[] args) {
		BlurayProbe.Drive(args.Length > 0 ? args[0] : "E:");
		return 0;
	}

	[ProbeCommand("bdfiles", "Blu-ray disc layout: BDMV/AACS folders, MKB version, bus encryption flag, disc ID, and whether the stream files are encrypted. Args: <drive or folder>")]
	public static int BdFiles(string[] args) {
		string root = args.Length > 0 ? args[0] : "E:";
		BlurayProbe.Files(root.Length <= 3 ? root.TrimEnd('\\', '/') + "\\" : root);
		return 0;
	}

	[ProbeCommand("bdtitles", "Blu-ray playlists (titles): length, chapters, size, video, clips; -v adds audio/subtitles, play items with PIDs, chapter times. Args: <drive or folder> [-v]")]
	public static int BdTitles(string[] args) {
		string root = RipDvd.Core.Bluray.BdScanner.ResolveRoot(args.Length > 0 ? args[0] : "E:") ?? throw new IOException("No Blu-ray structure (BDMV/index.bdmv) found.");
		BlurayTitlesProbe.Print(root, args.Contains("-v"));
		return 0;
	}

	[ProbeCommand("bdkey", "Check the AACS key file against a Blu-ray (no keys printed): disc ID, entry, VUK vs unit keys, decrypting sample units of a clip. Args: <drive or folder> [clip]")]
	public static int BdKey(string[] args) {
		string root = RipDvd.Core.Bluray.BdScanner.ResolveRoot(args.Length > 0 ? args[0] : "E:") ?? throw new IOException("No Blu-ray structure (BDMV/index.bdmv) found.");
		BlurayKeyProbe.Check(root, args.Length > 1 ? args[1] : null);
		return 0;
	}

	[ProbeCommand("cellmap", "Table of titles vs cells per title set ('*' = chapter start). Args: <drive> [title set]")]
	public static int CellMap(string[] args) {
		CellMapProbe.Print(IfoProbe.ResolveVideoTs(args.Length > 0 ? args[0] : "E:"), args.Length > 1 ? int.Parse(args[1]) : null);
		return 0;
	}

	[ProbeCommand("canceltest", "Start ripping a title, cancel after N seconds, and check the partial file was removed. Args: <drive> <title> <seconds>")]
	public static int CancelTest(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args[0]);
		string path = Path.Combine(Path.GetTempPath(), "rip-dvd-canceltest.mkv");
		using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(double.Parse(args[2])));
		var lastPhase = (RipDvd.Core.Ripping.RipPhase)(-1);
		try {
			new RipDvd.Core.Ripping.TitleRipper(videoTs, int.Parse(args[1])).Rip(path, cancel: cancel.Token, progress: p => {
				if (p.Phase != lastPhase) Console.WriteLine($"  phase {p.Phase} at {p.Elapsed.TotalSeconds:F1} s ({p.SectorsDone:N0}/{p.SectorsTotal:N0} sectors)");
				lastPhase = p.Phase;
			});
			Console.WriteLine("Finished before the cancel time.");
		} catch (OperationCanceledException) {
			Console.WriteLine($"Cancelled. Partial file still exists: {File.Exists(path)}");
		}
		return 0;
	}

	[ProbeCommand("mkvcompare", "Compare two MKV files track by track (frames, bytes, SHA-256 of payloads, times, chapters). Args: <a.mkv> <b.mkv>")]
	public static int MkvCompareCommand(string[] args) => MkvCompare.Run(args[0], args[1]);

	[ProbeCommand("checkpacks", "Find sectors that don't start with an MPEG-2 pack header (00 00 01 BA, MPEG-2 marker bits) and show their first bytes. Args: <drive> <vts> <from> <to> [max]")]
	public static int CheckPacks(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args[0]);
		int vts = int.Parse(args[1]);
		long from = long.Parse(args[2]), to = long.Parse(args[3]);
		int max = args.Length > 4 ? int.Parse(args[4]) : 20;
		using var reader = RipDvd.Core.Dvd.VobReader.ForTitleSet(videoTs, vts);
		const int chunk = 512;
		var buffer = new byte[chunk * RipDvd.Core.Dvd.VobReader.SectorSize];
		int found = 0;
		var sw = System.Diagnostics.Stopwatch.StartNew();
		for (long s = from; s <= to && found < max; s += chunk) {
			int n = (int)Math.Min(chunk, to - s + 1);
			reader.Read(s, n, buffer);
			for (int i = 0; i < n && found < max; i++) {
				var sector = buffer.AsSpan(i * RipDvd.Core.Dvd.VobReader.SectorSize, RipDvd.Core.Dvd.VobReader.SectorSize);
				if (sector[0] == 0 && sector[1] == 0 && sector[2] == 1 && sector[3] == 0xBA && (sector[4] & 0xC0) == 0x40) continue;
				bool allZero = !sector.ContainsAnyExcept((byte)0);
				Console.WriteLine($"  sector {s + i}: {(allZero ? "all zero" : Convert.ToHexString(sector[..24]))}");
				found++;
			}
		}
		Console.WriteLine($"{found} sector(s) found in {sw.Elapsed.TotalSeconds:F0} s");
		return 0;
	}

	[ProbeCommand("copyright", "Ask the drive whether the disc is copy-protected (copyright descriptor: 0 none, 1 CSS/CPPM, 2 CPRM). Args: <drive>")]
	public static int Copyright(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args.Length > 0 ? args[0] : "E:");
		bool? css = RipDvd.Core.Dvd.DriveUnlocker.IsCssProtected(videoTs);
		Console.WriteLine(css switch { true => "CSS-protected", false => "not protected", null => "unknown (the drive could not be asked)" });
		return 0;
	}

	[ProbeCommand("navwalk", "List the nav packs (VOBUs) from a sector, following VOBU end addresses: ILVU fields for interleaved cells. Args: <drive> <vts> <first sector> [count]")]
	public static int NavWalk(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args[0]);
		int vts = int.Parse(args[1]);
		long s = long.Parse(args[2]);
		int count = args.Length > 3 ? int.Parse(args[3]) : 40;
		RipDvd.Core.Dvd.DriveUnlocker.TryUnlock(videoTs);
		using var reader = RipDvd.Core.Dvd.VobReader.ForTitleSet(videoTs, vts);
		var buffer = new byte[RipDvd.Core.Dvd.VobReader.SectorSize];
		Console.WriteLine("  sector    lbn  vobu_ea  vob/cell  category  ilvu_ea  nxt_ilvu_sa");
		for (int i = 0; i < count; i++) {
			reader.Read(s, 1, buffer);
			if (!RipDvd.Core.Dvd.NavPack.IsNavPack(buffer)) {
				Console.WriteLine($"  {s,7}  not a nav pack");
				return 1;
			}
			var nav = RipDvd.Core.Dvd.NavPack.Parse(buffer);
			Console.WriteLine($"  {s,7} {nav.Lbn,6} {nav.VobuEndAddress,8}  {nav.VobId,3}/{nav.CellId,-3}   {nav.IlvuCategory:X4}  {nav.IlvuEndAddress,7}  {nav.NextIlvuStart,11}");
			s += nav.VobuEndAddress + 1;
		}
		return 0;
	}

	[ProbeCommand("key", "Find CSS title keys with the library's TitleKeyFinder and verify them. Args: <drive> [vts numbers...]")]
	public static int Key(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args.Length > 0 ? args[0] : "E:");
		var titleSets = args.Skip(1).Select(int.Parse).ToList();
		if (titleSets.Count == 0)
			titleSets = Enumerable.Range(1, 99).TakeWhile(n => File.Exists(Path.Combine(videoTs, $"VTS_{n:00}_1.VOB"))).ToList();
		foreach (int vts in titleSets) {
			var sw = System.Diagnostics.Stopwatch.StartNew();
			using var reader = RipDvd.Core.Dvd.VobReader.ForTitleSet(videoTs, vts);
			var key = RipDvd.Core.Dvd.TitleSectorReader.FindKey(reader);
			Console.WriteLine($"VTS {vts:00}: {key?.ToString() ?? "no key"} in {sw.ElapsedMilliseconds} ms");
			if (key is not null) CssProbe.Verify(videoTs, vts, key.Value, samples: 50);
		}
		return 0;
	}

	[Obsolete("Old: replaced by the 'key' command, which uses RipDvd.Core TitleKeyFinder (validates the first candidate instead of waiting for 5 votes).")]
	[ProbeCommand("crack", "Recover CSS title keys by voting (5 agreeing sectors) and verify them. Args: <drive> [vts numbers...]")]
	public static int Crack(string[] args) {
		string videoTs = IfoProbe.ResolveVideoTs(args.Length > 0 ? args[0] : "E:");
		var titleSets = args.Skip(1).Select(int.Parse).ToList();
		if (titleSets.Count == 0)
			titleSets = Enumerable.Range(1, 99).TakeWhile(n => File.Exists(Path.Combine(videoTs, $"VTS_{n:00}_1.VOB"))).ToList();
		foreach (int vts in titleSets) {
			var key = CssProbe.CrackTitleSet(videoTs, vts, maxSectors: 200_000, votesNeeded: 5, print: true);
			if (key is not null) CssProbe.Verify(videoTs, vts, key.Value, samples: 50);
		}
		return 0;
	}
}
