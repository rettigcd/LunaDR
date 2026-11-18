// rip-dvd: command-line DVD ripper, modeled on MakeMKV's makemkvcon.
//
//   rip-dvd info [disc] [--minlength=120]
//   rip-dvd mkv  <disc> <title|all> <output folder> [--minlength=120]
//
// <disc> is "disc:0" (first optical drive, the default), a drive letter such as "E:", or a folder path.
// Titles are numbered like MakeMKV: 0, 1, 2, ... over the titles that pass the minimum length, in disc order.
// Output files are named "title_t00.mkv", "title_t01.mkv", ... (MakeMKV's usual name; see the plan about its prefixes).

using RipDvd.Core.Ripping;

namespace RipDvd.Cli;

public static class Program {
	public static int Main(string[] args) {
		try {
			var options = args.Where(a => a.StartsWith("--")).ToList();
			var positional = args.Where(a => !a.StartsWith("--")).ToList();
			var minLength = DiscScanner.DefaultMinimumLength;
			foreach (var o in options) {
				if (o.StartsWith("--minlength=")) minLength = TimeSpan.FromSeconds(int.Parse(o["--minlength=".Length..]));
				else return Usage($"Unknown option {o}");
			}

			if (positional.Count == 0) return Usage(null);
			switch (positional[0]) {
				case "info":
					return Info(positional.ElementAtOrDefault(1) ?? "disc:0", minLength);
				case "mkv":
					if (positional.Count != 4) return Usage("mkv needs <disc> <title|all> <output folder>");
					return Mkv(positional[1], positional[2], positional[3], minLength);
				default:
					return Usage($"Unknown command {positional[0]}");
			}
		} catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException) {
			Console.Error.WriteLine($"Error: {ex.Message}");
			return 2;
		}
	}

	private static int Usage(string? error) {
		if (error is not null) Console.Error.WriteLine(error);
		Console.WriteLine("usage:");
		Console.WriteLine("  rip-dvd info [disc] [--minlength=120]");
		Console.WriteLine("  rip-dvd mkv <disc> <title|all> <output folder> [--minlength=120]");
		Console.WriteLine("disc: disc:0 (first optical drive), a drive letter like E:, or a folder path.");
		return error is null ? 0 : 1;
	}

	/// <summary>Titles that pass the minimum length, in disc order; their index in this list is the MakeMKV-style title id.</summary>
	private static List<TitleSummary> KeptTitles(DiscSummary disc, TimeSpan minLength) =>
		disc.Titles.Where(t => t.DurationNs >= (long)(minLength.TotalSeconds * 1e9)).ToList();

	private static int Info(string discArg, TimeSpan minLength) {
		var disc = DiscScanner.Scan(DiscScanner.ResolveVideoTs(discArg));
		var kept = KeptTitles(disc, minLength);
		Console.WriteLine($"Disc: {disc.VolumeName} ({disc.VideoTsFolder})");
		Console.WriteLine($"{disc.Titles.Count} titles, {kept.Count} at least {minLength.TotalSeconds:0} s long:");
		Console.WriteLine();
		for (int i = 0; i < kept.Count; i++) {
			var t = kept[i];
			Console.WriteLine($"  {i,2}: title {t.TitleNumber,2}  {Format(t.DurationNs)}  {t.Chapters,2} chapters  {t.SizeBytes / 1e9,5:F2} GB  {t.Video}");
			Console.WriteLine($"      audio: {string.Join(", ", t.Audio)}{(t.ClosedCaptions ? "; closed captions" : "")}");
		}
		var skipped = disc.Titles.Except(kept).ToList();
		if (skipped.Count > 0)
			Console.WriteLine($"\nSkipped (shorter than {minLength.TotalSeconds:0} s): titles {string.Join(", ", skipped.Select(t => $"{t.TitleNumber} ({Format(t.DurationNs)})"))}");
		return 0;
	}

	private static int Mkv(string discArg, string which, string outputFolder, TimeSpan minLength) {
		string videoTs = DiscScanner.ResolveVideoTs(discArg);
		var kept = KeptTitles(DiscScanner.Scan(videoTs), minLength);
		var ids = which == "all" ? Enumerable.Range(0, kept.Count).ToList() : [int.Parse(which)];
		if (ids.Any(i => i < 0 || i >= kept.Count)) {
			Console.Error.WriteLine($"Title id must be 0..{kept.Count - 1} or 'all' (see 'rip-dvd info').");
			return 1;
		}

		foreach (int id in ids) {
			var t = kept[id];
			string path = Path.Combine(outputFolder, $"title_t{id:00}.mkv");
			Console.WriteLine($"Title {id} (disc title {t.TitleNumber}, {Format(t.DurationNs)}) -> {path}");
			int lastPercent = -1;
			var result = new TitleRipper(videoTs, t.TitleNumber).Rip(path, (done, total) => {
				int pct = (int)(done * 100 / total);
				if (pct != lastPercent) {
					Console.Write($"\r  {pct,3}%");
					lastPercent = pct;
				}
			});
			Console.WriteLine($"\r  done: {Format(result.DurationNs)} in {result.Elapsed.TotalSeconds:F0} s " +
							  $"({result.Sectors * 2048 / 1e6 / Math.Max(1, result.Elapsed.TotalSeconds):F1} MB/s)");
			foreach (var w in result.Warnings) Console.WriteLine($"  warning: {w}");
		}
		return 0;
	}

	private static string Format(long ns) {
		var ts = TimeSpan.FromTicks(ns / 100);
		return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
	}
}
