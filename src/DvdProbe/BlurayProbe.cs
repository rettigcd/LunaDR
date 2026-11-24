using System.Runtime.Versioning;
using LunaDR.Core.Bluray;

namespace DvdProbe;

/// <summary>Probe: what a Blu-ray drive reports, and how a Blu-ray disc is laid out and protected (plan step BR1).</summary>
internal static class BlurayProbe {
	private static readonly Dictionary<int, string> Profiles = new() {
		[0x08] = "CD-ROM",
		[0x09] = "CD-R",
		[0x0A] = "CD-RW",
		[0x10] = "DVD-ROM",
		[0x11] = "DVD-R",
		[0x12] = "DVD-RAM",
		[0x13] = "DVD-RW",
		[0x1A] = "DVD+RW",
		[0x1B] = "DVD+R",
		[0x2B] = "DVD+R DL",
		[0x40] = "BD-ROM",
		[0x41] = "BD-R SRM",
		[0x42] = "BD-R RRM",
		[0x43] = "BD-RE",
	};

	private static readonly Dictionary<int, string> FeatureNames = new() {
		[0x0000] = "Profile list",
		[0x0001] = "Core",
		[0x0002] = "Morphing",
		[0x0003] = "Removable medium",
		[0x001E] = "CD read",
		[0x001F] = "DVD read",
		[0x0040] = "BD read",
		[0x0041] = "BD write",
		[0x0106] = "DVD CSS",
		[0x0107] = "Real-time streaming",
		[0x010B] = "DVD CPRM",
		[0x010D] = "AACS",
		[0x010E] = "DVD CSS managed recording",
		[0x0110] = "VCPS",
		[0x0113] = "SecurDisc",
		[0x0142] = "OSSC",
	};

	[SupportedOSPlatform("windows")]
	public static void Drive(string drive) {
		using var scsi = new WindowsScsiDrive(drive);
		var (vendor, product, revision) = scsi.Inquiry();
		Console.WriteLine($"Drive {drive}: {vendor} {product}, firmware {revision}");
		var (profile, features) = scsi.GetConfiguration();
		Console.WriteLine($"Current medium: {Profiles.GetValueOrDefault(profile, "unknown")} (profile 0x{profile:X4})");
		foreach (var (code, current, data) in features) {
			string name = FeatureNames.GetValueOrDefault(code, "");
			string detail = code switch {
				0x0000 => string.Join(" ", Enumerable.Range(0, data.Length / 4)
					.Select(i => (data[i * 4] << 8 | data[i * 4 + 1], (data[i * 4 + 2] & 1) != 0))
					.Select(x => Profiles.GetValueOrDefault(x.Item1, $"0x{x.Item1:X4}") + (x.Item2 ? "*" : ""))),
				0x010D when data.Length >= 4 =>
					$"AACS version {data[3]}, AGIDs {data[2] & 0x0F}, binding nonce {((data[0] & 1) != 0 ? "yes" : "no")} (blocks {data[1]}), " +
					$"bus encryption capable {((data[0] & 2) != 0 ? "yes" : "no")}, write bus encryption {((data[0] & 4) != 0 ? "yes" : "no")}, " +
					$"read media key block of CPRM {((data[0] & 8) != 0 ? "yes" : "no")}, read data key {((data[0] & 16) != 0 ? "yes" : "no")}",
				_ => Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 16))),
			};
			Console.WriteLine($"  feature 0x{code:X4}{(current ? " (current)" : ""),-10} {name,-26} {detail}");
		}
	}

	/// <summary>Disc layout, AACS files, and whether the stream files are encrypted (copy-permission bits of the first aligned units).</summary>
	public static void Files(string root) {
		string bdmv = Path.Combine(root, "BDMV"), aacs = Path.Combine(root, "AACS");
		foreach (var dir in new[] { bdmv, aacs }) {
			if (!Directory.Exists(dir)) { Console.WriteLine($"{dir}: missing"); continue; }
			Console.WriteLine($"{dir}:");
			foreach (var sub in Directory.GetDirectories(dir).Prepend(dir)) {
				var files = Directory.GetFiles(sub);
				long bytes = files.Sum(f => new FileInfo(f).Length);
				Console.WriteLine($"  {Path.GetRelativePath(dir, sub),-12} {files.Length,4} files {bytes / 1e9,8:F2} GB" +
								  (files.Length <= 8 ? "  " + string.Join(", ", files.Select(Path.GetFileName)) : ""));
			}
		}

		string mkb = Path.Combine(aacs, "MKB_RO.inf");
		if (File.Exists(mkb)) {
			var b = File.ReadAllBytes(mkb);
			// First record: type 0x10 (type and version), 3-byte length, MKB type (4), version (4).
			Console.WriteLine($"MKB_RO.inf: first record 0x{b[0]:X2}, MKB type 0x{ReadBe32(b, 4):X8}, version {ReadBe32(b, 8)}");
		}
		foreach (var cer in Directory.Exists(aacs) ? Directory.GetFiles(aacs, "Content*.cer") : []) {
			var b = File.ReadAllBytes(cer);
			// Content certificate: byte 0 = type (0 = AACS 1), byte 1 bit 7 = bus encryption enabled.
			Console.WriteLine($"{Path.GetFileName(cer)}: type {b[0]}, bus encryption {((b[1] & 0x80) != 0 ? "ENABLED" : "not enabled")}");
		}
		string ukf = Path.Combine(aacs, "Unit_Key_RO.inf");
		if (File.Exists(ukf)) {
			var b = File.ReadAllBytes(ukf);
			Console.WriteLine($"Unit_Key_RO.inf: {b.Length} bytes, disc ID (SHA-1) {Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b))}, " +
							  $"unit keys {(b[ReadBe32(b, 0)] << 8 | b[ReadBe32(b, 0) + 1])}");
		}

		string stream = Path.Combine(bdmv, "STREAM");
		if (!Directory.Exists(stream)) return;
		Console.WriteLine("Stream files (first 4 aligned units of 6,144 bytes = 32 packets each):");
		foreach (var file in Directory.GetFiles(stream, "*.m2ts").OrderByDescending(f => new FileInfo(f).Length).Take(6)) {
			using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
			var buf = new byte[6144 * 4];
			int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
			var units = new List<string>();
			for (int u = 0; u + 6144 <= n; u += 6144) {
				int cpi = buf[u] >> 6;
				int syncs = Enumerable.Range(0, 32).Count(i => buf[u + i * 192 + 4] == 0x47);
				units.Add($"cpi {cpi} sync {syncs}/32");
			}
			Console.WriteLine($"  {Path.GetFileName(file),-12} {new FileInfo(file).Length / 1e9,6:F2} GB  {string.Join(" | ", units)}");
		}
	}

	private static int ReadBe32(byte[] b, int p) => b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3];
}

internal static class BlurayTitlesProbe {
	/// <summary>All playlists with their play items, streams and chapter marks (probe `bdtitles`).</summary>
	public static void Print(string root, bool verbose) {
		var disc = BdScanner.Scan(root);
		Console.WriteLine($"Disc: {disc.VolumeName} ({disc.Root}), {disc.Titles.Count} playlists");
		foreach (var t in disc.Titles.OrderByDescending(t => t.DurationNs)) {
			var d = TimeSpan.FromTicks(t.DurationNs / 100);
			Console.WriteLine($"  {t.Playlist:00000}  {(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}  {t.Chapters,3} ch  {t.SizeBytes / 1e9,6:F2} GB  {t.Video}" +
							  $"  clips {string.Join(",", t.Clips)}{(t.Angles > 1 ? $"  {t.Angles} angles" : "")}{(t.DuplicateOf is { } o ? $"  (same as {o:00000})" : "")}");
			if (!verbose) continue;
			Console.WriteLine($"      audio: {string.Join(", ", t.Audio)}");
			Console.WriteLine($"      subtitles: {string.Join(", ", t.Subtitles)}");
			var mpls = BdScanner.LoadPlaylist(root, t.Playlist);
			foreach (var i in mpls.Items)
				Console.WriteLine($"      item {i.ClipId} in {i.InTime} out {i.OutTime} cc {i.ConnectionCondition} pids " +
								  string.Join(" ", i.Video.Concat(i.Audio).Concat(i.Subtitles).Select(s => $"{s.Pid:X4}:{s.CodecName}")));
			Console.WriteLine($"      chapters (s): {string.Join(" ", mpls.ChapterStartsNs().Select(c => (c / 1e9).ToString("0.###")))}");
		}
	}
}

internal static class BlurayKeyProbe {
	/// <summary>Checks the key file against the disc without printing keys: VUK-derived vs listed unit keys, and decrypting sample units of a clip.</summary>
	public static void Check(string root, string? clip) {
		string? keyFile = KeyDb.Find();
		Console.WriteLine($"Key file: {keyFile ?? "none found"} (searched: {string.Join(", ", KeyDb.SearchPaths())})");
		var ukf = File.ReadAllBytes(Path.Combine(root, "AACS", "Unit_Key_RO.inf"));
		string id = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(ukf));
		var entry = keyFile is null ? null : KeyDb.Lookup(keyFile, id);
		Console.WriteLine($"Disc ID {id}: {(entry is null ? "not in the key file" : $"\"{entry.Title}\", VUK {(entry.Vuk is null ? "no" : "yes")}, unit keys {entry.UnitKeys.Count}")}");
		if (entry is null) return;
		if (entry.Vuk is not null && entry.UnitKeys.Count > 0) {
			var derived = AacsDecryptor.UnitKeysFromVuk(entry.Vuk, ukf);
			bool same = derived.Count == entry.UnitKeys.Count && derived.Zip(entry.UnitKeys).All(p => p.First.AsSpan().SequenceEqual(p.Second));
			Console.WriteLine($"Unit keys from the VUK and Unit_Key_RO.inf {(same ? "match" : "DIFFER from")} the listed unit keys ({derived.Count} derived).");
		}
		using var aacs = AacsDecryptor.ForDisc(root, keyFile)!;
		string file = clip is not null ? Path.Combine(root, "BDMV", "STREAM", clip + ".m2ts")
			: Directory.GetFiles(Path.Combine(root, "BDMV", "STREAM"), "*.m2ts").OrderByDescending(f => new FileInfo(f).Length).First();
		using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
		var unit = new byte[AacsDecryptor.UnitSize];
		int ok = 0, encrypted = 0, samples = 0;
		long units = fs.Length / AacsDecryptor.UnitSize;
		for (long u = 0; u < units; u += Math.Max(1, units / 200)) {
			fs.Position = u * AacsDecryptor.UnitSize;
			fs.ReadExactly(unit);
			samples++;
			if (AacsDecryptor.IsEncrypted(unit)) encrypted++;
			if (aacs.DecryptUnit(unit)) ok++;
		}
		Console.WriteLine($"{Path.GetFileName(file)}: {samples} sample units, {encrypted} encrypted, {ok} decrypted to valid packets.");
	}
}

internal static class BlurayKeyGuessProbe {
	/// <summary>
	/// Experiment: the disc's own ID is not in the key file, but lines for the same title (other disc IDs) may carry the right unit keys.
	/// Tries each distinct unit-key set of the lines whose title contains <paramref name="titleText"/> on sample units of a clip.
	/// Prints only counts, never keys.
	/// </summary>
	public static void Check(string root, string titleText, string? clip) {
		string keyFile = KeyDb.Find() ?? throw new FileNotFoundException("No key file found.");
		var ukf = File.ReadAllBytes(Path.Combine(root, "AACS", "Unit_Key_RO.inf"));
		Console.WriteLine($"Disc has {AacsDecryptor.EncryptedUnitKeys(ukf).Count} unit keys; disc ID {Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(ukf))}.");

		var sets = new Dictionary<string, (List<byte[]> Keys, int Lines)>();
		int lines = 0;
		foreach (var line in File.ReadLines(keyFile)) {
			if (KeyDb.ParseLine(line) is not { } e || !e.Title.Contains(titleText, StringComparison.OrdinalIgnoreCase)) continue;
			lines++;
			if (e.UnitKeys.Count == 0 || e.UnitKeys.Any(k => k.Length != 16)) continue;
			string signature = string.Join("|", e.UnitKeys.Select(Convert.ToHexString));
			sets[signature] = sets.TryGetValue(signature, out var s) ? (s.Keys, s.Lines + 1) : (e.UnitKeys.ToList(), 1);
		}
		Console.WriteLine($"{lines} key-file lines have \"{titleText}\" in the title; {sets.Count} distinct usable unit-key set(s).");

		string file = clip is not null ? Path.Combine(root, "BDMV", "STREAM", clip + ".m2ts")
			: Directory.GetFiles(Path.Combine(root, "BDMV", "STREAM"), "*.m2ts").OrderByDescending(f => new FileInfo(f).Length).First();
		using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
		long units = fs.Length / AacsDecryptor.UnitSize;
		var samples = new List<byte[]>();
		for (long u = 0; u < units && samples.Count < 200; u += Math.Max(1, units / 400)) {
			var unit = new byte[AacsDecryptor.UnitSize];
			fs.Position = u * AacsDecryptor.UnitSize;
			fs.ReadExactly(unit);
			if (AacsDecryptor.IsEncrypted(unit)) samples.Add(unit);
		}
		Console.WriteLine($"{Path.GetFileName(file)}: {samples.Count} encrypted sample units.");

		int n = 0;
		foreach (var (keys, count) in sets.Values) {
			n++;
			using var aacs = new AacsDecryptor(keys);
			int ok = samples.Count(s => aacs.DecryptUnit(s.ToArray()));
			Console.WriteLine($"  set {n} ({keys.Count} keys, on {count} line(s)): {ok} of {samples.Count} units decrypted to valid packets.");
		}
	}
}

internal static class BlurayRipProbe {
	/// <summary>Rips a playlist with BdTitleRipper (optionally only the first MB of each clip) and prints progress and warnings.</summary>
	public static void Rip(string root, int playlist, string output, long maxMb) {
		var ripper = new BdTitleRipper(root, playlist);
		foreach (var line in ripper.Describe()) Console.WriteLine("  " + line);
		int lastPercent = -1;
		var result = ripper.Rip(output, p => {
			int percent = (int)(p.Fraction * 100);
			if (percent != lastPercent && percent % 5 == 0) {
				lastPercent = percent;
				Console.WriteLine($"  {p.Phase} {percent}% item {p.CellPosition} {p.Elapsed:g}");
			}
		}, maxBytes: maxMb > 0 ? maxMb * 1_000_000 : long.MaxValue);
		Console.WriteLine($"Done: {result.OutputPath}, {TimeSpan.FromTicks(result.DurationNs / 100)}, {result.Elapsed:g}");
		foreach (var w in result.Warnings) Console.WriteLine("  warning: " + w);
	}
}

internal static class BlurayDecryptProbe {
	/// <summary>
	/// Writes a stretch of a clip, AACS-decrypted, to a loose .m2ts (units that cannot be decrypted are skipped and counted),
	/// so another program (ffmpeg) can read exactly what LunaDR reads from the disc.
	/// </summary>
	public static void Run(string root, string clip, string output, long fromMb, long maxMb) {
		using var aacs = AacsDecryptor.ForDisc(root);
		using var fs = new FileStream(Path.Combine(root, "BDMV", "STREAM", clip + ".m2ts"), FileMode.Open, FileAccess.Read, FileShare.Read, 1);
		using var outFile = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
		fs.Position = fromMb * 1_000_000 / AacsDecryptor.UnitSize * AacsDecryptor.UnitSize;
		long end = maxMb > 0 ? Math.Min(fs.Length, fs.Position + maxMb * 1_000_000) : fs.Length;
		var unit = new byte[AacsDecryptor.UnitSize];
		long written = 0, failed = 0;
		while (fs.Position + unit.Length <= end) {
			fs.ReadExactly(unit);
			if (aacs is not null && !aacs.DecryptUnit(unit)) {
				failed++;
				continue;
			}
			outFile.Write(unit);
			written++;
		}
		Console.WriteLine($"Wrote {written:N0} units ({written * unit.Length / 1e6:F1} MB) to {output}; {failed:N0} could not be decrypted.");
	}
}

internal static class BlurayPesProbe {
	/// <summary>Lists the PES packets of one PID in a clip (decrypted): PTS/DTS in seconds, the first payload bytes, and the newest video PTS seen when it arrived.</summary>
	public static void Print(string root, string clip, int pid, int videoPid, long maxMb, int max) {
		using var aacs = AacsDecryptor.ForDisc(root);
		using var fs = new FileStream(Path.Combine(root, "BDMV", "STREAM", clip + ".m2ts"), FileMode.Open, FileAccess.Read, FileShare.Read, 1);
		var demux = new TsDemuxer([pid, videoPid]);
		var unit = new byte[AacsDecryptor.UnitSize];
		long limit = Math.Min(fs.Length, maxMb * 1_000_000);
		double lastVideo = 0;
		int shown = 0;
		while (fs.Position + unit.Length <= limit && shown < max) {
			fs.ReadExactly(unit);
			if (aacs is not null && !aacs.DecryptUnit(unit)) continue;
			for (int p = 0; p < unit.Length; p += 192) demux.Feed(unit.AsSpan(p + 4, 188));
			foreach (var pes in demux.TakeReady()) {
				if (pes.Pid == videoPid) {
					if (pes.Pts is { } v) lastVideo = Math.Max(lastVideo, v / 90000.0);
					continue;
				}
				shown++;
				Console.WriteLine($"  pts {pes.Pts / 90000.0,10:F3}  dts {pes.Dts / 90000.0,10:F3}  video {lastVideo,10:F3}  len {pes.Payload.Length,6}  " +
								  Convert.ToHexString(pes.Payload.AsSpan(0, Math.Min(8, pes.Payload.Length))));
			}
		}
	}
}

internal static class BlurayClipProbe {
	/// <summary>
	/// Rips a loose .m2ts file (e.g. made by ffmpeg with -mpegts_m2ts_mode 1) through BdTitleRipper, with a one-item playlist made up here:
	/// the streams are given as PID=coding type (hex), e.g. 1011=1B 1100=84. Tests formats no test disc has (H.264, E-AC3, DTS, LPCM).
	/// </summary>
	public static void Rip(string m2ts, string output, IEnumerable<string> streams) {
		string root = Path.Combine(Path.GetTempPath(), "bdclip-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(root, "BDMV", "STREAM"));
		try {
			File.Copy(m2ts, Path.Combine(root, "BDMV", "STREAM", "00001.m2ts"));
			var list = streams.Select(s => s.Split('=')).Select(p => new BdStream(Convert.ToInt32(p[0], 16), Convert.ToInt32(p[1], 16), 6, 1, "eng")).ToList();
			var item = new BdPlayItem("00001", 0, 45_000 * 3600, 1, 1,
				list.Where(s => s.Kind == BdStreamKind.Video).ToList(), list.Where(s => s.Kind == BdStreamKind.Audio).ToList(),
				list.Where(s => s.Kind == BdStreamKind.Subtitle).ToList());
			var mpls = new Mpls { Name = "00001", Items = [item], Marks = [] };
			var ripper = new BdTitleRipper(root, mpls, 1);
			foreach (var line in ripper.Describe()) Console.WriteLine("  " + line);
			var result = ripper.Rip(output);
			Console.WriteLine($"Done: {result.OutputPath}, {TimeSpan.FromTicks(result.DurationNs / 100)}");
			foreach (var w in result.Warnings) Console.WriteLine("  warning: " + w);
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}
}

internal static class BlurayScanProbe {
	/// <summary>
	/// Health scan of a clip with <see cref="DiscHealthScanner"/>: samples every <paramref name="stepMb"/> MB, prints a map and the bad stretches,
	/// then offers to narrow their edges by halving (<paramref name="refine"/>: true = do it, false = don't, null = ask, with the time it takes).
	/// </summary>
	public static void Scan(string root, string? clip, long stepMb, double fromGb, double toGb, bool? refine) {
		string file = clip is not null ? Path.Combine(root, "BDMV", "STREAM", clip + ".m2ts")
			: Directory.GetFiles(Path.Combine(root, "BDMV", "STREAM"), "*.m2ts").OrderByDescending(f => new FileInfo(f).Length).First();
		using var scanner = new DiscHealthScanner(file);
		long step = Math.Max(1, stepMb) * 1_000_000;
		Console.WriteLine($"{Path.GetFileName(file)}: {scanner.Length / 1e9:F2} GB; a {DiscHealthScanner.SampleBytes / 1e6:F1} MB sample every {step / 1e6:F0} MB. " +
						  $"Listed while scanning: read errors and speeds under {HealthSample.BadMBps} MB/s.");
		var total = System.Diagnostics.Stopwatch.StartNew();
		var samples = scanner.ScanCoarse(step, (long)(fromGb * 1e9), (long)Math.Min(toGb * 1e9, long.MaxValue), s => {
			if (s.IsBad || s.FirstReadMs > 1500)
				Console.WriteLine($"  {s.Offset / 1e9,6:F2} GB ({(double)s.Offset / scanner.Length,4:P0})  {(s.Error ? "READ ERROR" : $"{s.MBps:F1} MB/s")}  first read {s.FirstReadMs:F0} ms");
		});

		// Short reads after a seek run slower than a streaming rip does (about 3.5 MB/s here on a healthy disc, against 15 to 18 MB/s in a rip).
		char Mark(HealthSample s) => s.Error ? '!' : s.MBps >= HealthSample.GoodMBps ? '.' : s.IsBad ? 'X' : 's';
		Console.WriteLine();
		Console.WriteLine("Marks: . good (2.5 MB/s or more), s slow (1.5 to 2.5), X bad (under 1.5), ! read error. Map (each mark is one sample; row = 100 samples):");
		for (int i = 0; i < samples.Count; i += 100)
			Console.WriteLine($"  {samples[i].Offset / 1e9,6:F2} GB  {new string(samples.Skip(i).Take(100).Select(Mark).ToArray())}");
		Console.WriteLine($"  {samples.Count(s => Mark(s) == '.')} good, {samples.Count(s => Mark(s) == 's')} slow, {samples.Count(s => Mark(s) == 'X')} bad, " +
						  $"{samples.Count(s => Mark(s) == '!')} read errors, of {samples.Count} samples; the scan took {total.Elapsed:m\\:ss}.");

		var stretches = DiscHealthScanner.FindStretches(samples);
		if (stretches.Count == 0) {
			Console.WriteLine("No bad stretches.");
			return;
		}
		Console.WriteLine($"{stretches.Count} bad stretch(es); the edges are only known to within one sample step:");
		foreach (var s in stretches) Console.WriteLine("  " + Describe(s, scanner.Length));

		long resolution = DiscHealthScanner.DefaultResolution;
		var edges = DiscHealthScanner.Edges(stretches, resolution);
		if (edges.Count == 0) return;
		var estimate = DiscHealthScanner.EstimateRefine(stretches, samples, resolution);
		bool go = refine ?? Ask($"Narrow the {edges.Count} edge(s) by halving? It takes about {Format(estimate)}. [y/N] ");
		if (!go) return;
		if (refine == true) Console.WriteLine($"Narrowing the {edges.Count} edge(s) by halving; about {Format(estimate)}.");

		total.Restart();
		var refined = new List<string>();
		foreach (var s in stretches) {
			double start = s.LastGoodBefore >= 0 ? Midpoint(scanner.Refine(s.LastGoodBefore, s.FirstBad, resolution)) : s.FirstBad;
			double end = s.FirstGoodAfter >= 0 ? Midpoint(scanner.Refine(s.FirstGoodAfter, s.LastBad, resolution)) : s.LastBad + DiscHealthScanner.SampleBytes;
			refined.Add($"bad from about {start / 1e9:F3} GB ({start / scanner.Length:P1}) to about {end / 1e9:F3} GB ({end / scanner.Length:P1}), " +
						$"{(end - start) / 1e9:F2} GB long");
		}
		Console.WriteLine($"Narrowed to within {resolution / 1e6:F1} MB in {total.Elapsed:m\\:ss} (estimated {Format(estimate)}):");
		foreach (var line in refined) Console.WriteLine("  " + line);
	}

	private static double Midpoint((long Good, long Bad) edge) => (edge.Good + edge.Bad) / 2.0;

	private static string Describe(HealthStretch s, long length) =>
		$"starts between {(s.LastGoodBefore < 0 ? "the beginning" : $"{s.LastGoodBefore / 1e9:F2}")} and {s.FirstBad / 1e9:F2} GB, " +
		$"ends between {s.LastBad / 1e9:F2} and {(s.FirstGoodAfter < 0 ? "the end" : $"{s.FirstGoodAfter / 1e9:F2}")} GB " +
		$"({(double)s.FirstBad / length:P0} to {(double)(s.FirstGoodAfter < 0 ? length : s.FirstGoodAfter) / length:P0} of the file)";

	/// <summary>Rounded up to 10 seconds: "40 s", "2 min 10 s".</summary>
	private static string Format(TimeSpan t) {
		int s = Math.Max(10, (int)(Math.Ceiling(t.TotalSeconds / 10) * 10));
		return s >= 60 ? $"{s / 60} min {s % 60:00} s" : $"{s} s";
	}

	private static bool Ask(string question) {
		if (Console.IsInputRedirected) {
			Console.WriteLine(question + "(no answer: input is redirected; pass -refine to do it)");
			return false;
		}
		Console.Write(question);
		return Console.ReadLine()?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true;
	}
}
