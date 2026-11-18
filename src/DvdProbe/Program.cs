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

		return (int)cmd.Method.Invoke(null, [args[1..]])!;
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
