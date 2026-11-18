// rip-dvd: command-line DVD ripper, modeled on MakeMKV's makemkvcon.
//
//   rip-dvd info [disc] [--minlength=120]
//   rip-dvd mkv  <disc> <titles|all> <output folder> [--minlength=120] [--name="Disc name"]
//
// <disc> is "disc:0" (first optical drive, the default), a drive letter such as "E:", or a folder path.
// Titles are the disc's own title numbers, as "info" lists them (and as a DVD player numbers them), e.g. "1" or "1,3,6".
// "all" means every title at least --minlength seconds long (MakeMKV's default filter, 120 s).
// Output files are named "<Disc name> - title NN.mkv"; the disc name comes from the volume label unless --name is given.

using RipDvd.Core.Ripping;

namespace RipDvd.Cli;

public static class Program {
	public static int Main(string[] args) {
		try {
			var options = args.Where(a => a.StartsWith("--")).ToList();
			var positional = args.Where(a => !a.StartsWith("--")).ToList();
			var minLength = DiscScanner.DefaultMinimumLength;
			string? name = null;
			foreach (var o in options) {
				if (o.StartsWith("--minlength=")) minLength = TimeSpan.FromSeconds(int.Parse(o["--minlength=".Length..]));
				else if (o.StartsWith("--name=")) name = o["--name=".Length..].Trim('"');
				else return Usage($"Unknown option {o}");
			}

			if (positional.Count == 0) return Usage(null);
			switch (positional[0]) {
				case "info":
					return Info(positional.ElementAtOrDefault(1) ?? "disc:0", minLength);
				case "mkv":
					if (positional.Count != 4) return Usage("mkv needs <disc> <titles|all> <output folder>");
					return Mkv(positional[1], positional[2], positional[3], minLength, name);
				default:
					return Usage($"Unknown command {positional[0]}");
			}
		} catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException or FormatException) {
			Console.Error.WriteLine($"Error: {ex.Message}");
			return 2;
		}
	}

	private static int Usage(string? error) {
		if (error is not null) Console.Error.WriteLine(error);
		Console.WriteLine("usage:");
		Console.WriteLine("  rip-dvd info [disc] [--minlength=120]");
		Console.WriteLine("  rip-dvd mkv <disc> <titles|all> <output folder> [--minlength=120] [--name=\"Disc name\"]");
		Console.WriteLine("disc:   disc:0 (first optical drive), a drive letter like E:, or a folder path.");
		Console.WriteLine("titles: disc title numbers as listed by 'info', e.g. 1 or 1,3,6; 'all' = every title of at least --minlength seconds.");
		return error is null ? 0 : 1;
	}

	private static bool LongEnough(TitleSummary t, TimeSpan minLength) => t.DurationNs >= (long)(minLength.TotalSeconds * 1e9);

	private static int Info(string discArg, TimeSpan minLength) {
		var disc = DiscScanner.Scan(DiscScanner.ResolveVideoTs(discArg));
		var kept = disc.Titles.Where(t => LongEnough(t, minLength)).ToList();
		Console.WriteLine($"Disc: {OutputNaming.DiscName(disc.VolumeName)}   (volume label \"{disc.VolumeName}\", {disc.VideoTsFolder})");
		Console.WriteLine($"{disc.Titles.Count} titles, {kept.Count} at least {minLength.TotalSeconds:0} s long:");
		Console.WriteLine();
		foreach (var t in kept) {
			Console.WriteLine($"  title {t.TitleNumber,2}  {Format(t.DurationNs)}  {t.Chapters,2} chapters  {t.SizeBytes / 1e9,5:F2} GB  {t.Video}");
			Console.WriteLine($"            audio: {string.Join(", ", t.Audio)}");
			if (t.Subtitles.Count > 0) Console.WriteLine($"            subtitles: {string.Join(", ", t.Subtitles)}");
			if (t.ClosedCaptions) Console.WriteLine("            closed captions");
		}
		var skipped = disc.Titles.Except(kept).ToList();
		if (skipped.Count > 0)
			Console.WriteLine($"\nShorter than {minLength.TotalSeconds:0} s (not in 'all', can still be ripped by number): titles {string.Join(", ", skipped.Select(t => $"{t.TitleNumber} ({Format(t.DurationNs)})"))}");
		return 0;
	}

	private static int Mkv(string discArg, string which, string outputFolder, TimeSpan minLength, string? name) {
		string videoTs = DiscScanner.ResolveVideoTs(discArg);
		var disc = DiscScanner.Scan(videoTs);
		List<TitleSummary> selected;
		if (which == "all") {
			selected = disc.Titles.Where(t => LongEnough(t, minLength)).ToList();
		} else {
			var numbers = which.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
			var unknown = numbers.Where(n => disc.Titles.All(t => t.TitleNumber != n)).ToList();
			if (unknown.Count > 0) {
				Console.Error.WriteLine($"No title {string.Join(", ", unknown)} on this disc; titles are 1..{disc.Titles.Count} (see 'rip-dvd info').");
				return 1;
			}
			selected = numbers.Select(n => disc.Titles.First(t => t.TitleNumber == n)).ToList();
		}

		string discName = OutputNaming.Sanitize(name ?? OutputNaming.DiscName(disc.VolumeName));
		foreach (var t in selected) {
			string path = Path.Combine(outputFolder, OutputNaming.FileName(discName, t.TitleNumber));
			Console.WriteLine($"Title {t.TitleNumber} ({Format(t.DurationNs)}) -> {path}");
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
