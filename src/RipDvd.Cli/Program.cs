// rip-dvd: command-line DVD ripper, modeled on MakeMKV's makemkvcon.
// Run "rip-dvd help" for the commands and options; the help text is generated from the tables below,
// so adding an option to Options automatically documents it.

using RipDvd.Core.Ripping;

namespace RipDvd.Cli;

public static class Program {
	/// <summary>A command: its name, argument pattern, one-line summary and longer description.</summary>
	private sealed record Command(string Name, string Arguments, string Summary, string[] Details, string[] Examples);

	/// <summary>An option: --name or --name=value, which commands accept it, and its help text.</summary>
	private sealed record Option(string Name, string? Value, string[] Commands, string Help, string? Default = null);

	private static readonly Command[] Commands = [
		new("info", "[disc]",
			"List the titles on a disc, with their length, chapters, size, audio, subtitles and closed captions.",
			[
				"Titles are listed by the disc's own title numbers; use those numbers with 'mkv'.",
				"Titles shorter than --minlength are only named at the end (use --all to list them in full).",
			],
			["rip-dvd info", "rip-dvd info E:", "rip-dvd info E: --all"]),
		new("mkv", "<disc> <titles|all> <output folder>",
			"Rip titles to MKV files named \"<Disc name> - title NN.mkv\".",
			[
				"<titles> is one title number or a comma-separated list (e.g. 1 or 1,3,6), as listed by 'info'.",
				"Any title can be ripped by number, even a short one. 'all' rips every title at least --minlength seconds long.",
				"By default every audio and subtitle track is kept, closed captions become a text subtitle track, and chapters are written",
				"(MakeMKV's defaults). Existing files are skipped unless --overwrite is given.",
			],
			[
				"rip-dvd mkv E: all C:\\Rips",
				"rip-dvd mkv E: 1 C:\\Rips --name=\"The Dead Zone S4D1\"",
				"rip-dvd mkv E: 1,2 C:\\Rips --audio=eng --subtitles=none",
				"rip-dvd mkv E: all C:\\Rips --dry-run",
			]),
		new("help", "[command]", "Show this help, or the help for one command.", [], ["rip-dvd help mkv"]),
	];

	private static readonly Option[] Options = [
		new("minlength", "SECONDS", ["info", "mkv"], "Titles shorter than this are left out of 'info' and of 'mkv … all'.", "120 (MakeMKV's default)"),
		new("all", null, ["info"], "List every title in full, including the short ones."),
		new("name", "NAME", ["mkv"], "Disc name used in the file names, instead of the one made from the volume label."),
		new("audio", "LANGS", ["mkv"], "Audio tracks to keep, by language: e.g. eng or eng,fre (2-letter codes like en also work); all; none. " +
			"'und' matches tracks with no language.", "all"),
		new("subtitles", "LANGS", ["mkv"], "Bitmap subtitle tracks to keep, by language, as for --audio. (Closed captions are controlled by --captions.)", "all"),
		new("captions", "on|off", ["mkv"], "Convert closed captions to a text subtitle track. With 'off' the caption data stays inside the video.", "on"),
		new("chapters", "on|off", ["mkv"], "Write the disc's chapters.", "on"),
		new("overwrite", null, ["mkv"], "Replace output files that already exist (otherwise they are skipped)."),
		new("dry-run", null, ["mkv"], "Show which files and tracks would be written, without ripping."),
		new("help", null, ["info", "mkv", "help"], "Show help (also -h)."),
	];

	private const string DiscHelp = "<disc> is disc:0 (the first optical drive; disc:1 the second, ...), a drive letter like E:, or a folder containing VIDEO_TS.";

	public static int Main(string[] args) {
		try {
			var positional = args.Where(a => !a.StartsWith('-')).ToList();
			string command = positional.FirstOrDefault() ?? "help";
			bool wantsHelp = args.Any(a => a is "-h" or "--help" or "-?" or "/?");
			if (command == "help" || wantsHelp) return Help(command == "help" ? positional.ElementAtOrDefault(1) : command);

			if (Commands.All(c => c.Name != command)) return Fail($"Unknown command '{command}'.");
			var values = ParseOptions(args.Where(a => a.StartsWith("--")), command);
			var minLength = TimeSpan.FromSeconds(values.TryGetValue("minlength", out var ml) ? int.Parse(ml!) : 120);

			switch (command) {
				case "info":
					if (positional.Count > 2) return Fail("info takes at most one argument: the disc.", command);
					return Info(positional.ElementAtOrDefault(1) ?? "disc:0", minLength, values.ContainsKey("all"));
				case "mkv":
					if (positional.Count != 4) return Fail("mkv needs three arguments: <disc> <titles|all> <output folder>.", command);
					var options = new RipOptions {
						AudioLanguages = values.TryGetValue("audio", out var au) ? RipOptions.ParseLanguages(au!) : null,
						SubtitleLanguages = values.TryGetValue("subtitles", out var su) ? RipOptions.ParseLanguages(su!) : null,
						ClosedCaptions = !values.TryGetValue("captions", out var cc) || OnOff(cc!, "captions"),
						Chapters = !values.TryGetValue("chapters", out var ch) || OnOff(ch!, "chapters"),
					};
					return Mkv(positional[1], positional[2], positional[3], minLength, values.GetValueOrDefault("name"), options,
						overwrite: values.ContainsKey("overwrite"), dryRun: values.ContainsKey("dry-run"));
			}
			return 0;
		} catch (OptionException ex) {
			return Fail(ex.Message, ex.Command);
		} catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException or FormatException or NotSupportedException) {
			Console.Error.WriteLine($"Error: {ex.Message}");
			return 2;
		}
	}

	private sealed class OptionException(string message, string command) : Exception(message) {
		public string Command { get; } = command;
	}

	/// <summary>Parses --name and --name=value against the option table for this command.</summary>
	private static Dictionary<string, string?> ParseOptions(IEnumerable<string> args, string command) {
		var result = new Dictionary<string, string?>();
		foreach (var arg in args) {
			string body = arg[2..];
			int eq = body.IndexOf('=');
			string name = eq < 0 ? body : body[..eq];
			string? value = eq < 0 ? null : body[(eq + 1)..].Trim('"');
			var option = Options.FirstOrDefault(o => o.Name == name);
			if (option is null) throw new OptionException($"Unknown option --{name}.", command);
			if (!option.Commands.Contains(command)) throw new OptionException($"--{name} does not apply to '{command}'.", command);
			if (option.Value is not null && string.IsNullOrEmpty(value)) throw new OptionException($"--{name} needs a value: --{name}={option.Value}.", command);
			if (option.Value is null && value is not null) throw new OptionException($"--{name} takes no value.", command);
			result[name] = value;
		}
		return result;
	}

	private static bool OnOff(string value, string option) => value.ToLowerInvariant() switch {
		"on" or "yes" or "true" => true,
		"off" or "no" or "false" => false,
		_ => throw new OptionException($"--{option} must be on or off.", "mkv"),
	};

	private static int Fail(string message, string? command = null) {
		Console.Error.WriteLine(message);
		Console.Error.WriteLine(command is null ? "Run 'rip-dvd help' for usage." : $"Run 'rip-dvd help {command}' for usage.");
		return 1;
	}

	/// <summary>Prints the general help, or the help for one command, from the Commands and Options tables.</summary>
	private static int Help(string? commandName) {
		var command = Commands.FirstOrDefault(c => c.Name == commandName);
		if (commandName is not null && commandName != "help" && command is null) return Fail($"Unknown command '{commandName}'.");

		if (command is null || command.Name == "help") {
			Console.WriteLine("rip-dvd: rip DVD titles to MKV files (modeled on MakeMKV's makemkvcon).");
			Console.WriteLine();
			Console.WriteLine("usage: rip-dvd <command> [arguments] [options]");
			Console.WriteLine();
			Console.WriteLine("commands:");
			foreach (var c in Commands) Console.WriteLine($"  {c.Name,-6} {c.Summary}");
			Console.WriteLine();
			Console.WriteLine(DiscHelp);
			Console.WriteLine();
			Console.WriteLine("options:");
			PrintOptions(Options.Where(o => o.Name != "help"), showCommands: true);
			Console.WriteLine();
			Console.WriteLine("Run 'rip-dvd help <command>' for details and examples, e.g. 'rip-dvd help mkv'.");
			return 0;
		}

		Console.WriteLine($"usage: rip-dvd {command.Name} {command.Arguments} [options]");
		Console.WriteLine();
		Console.WriteLine(command.Summary);
		foreach (var line in command.Details) Console.WriteLine(line);
		Console.WriteLine();
		Console.WriteLine(DiscHelp);
		Console.WriteLine();
		Console.WriteLine("options:");
		PrintOptions(Options.Where(o => o.Commands.Contains(command.Name)), showCommands: false);
		Console.WriteLine();
		Console.WriteLine("examples:");
		foreach (var e in command.Examples) Console.WriteLine($"  {e}");
		return 0;
	}

	private static void PrintOptions(IEnumerable<Option> options, bool showCommands) {
		foreach (var o in options) {
			string usage = o.Value is null ? $"--{o.Name}" : $"--{o.Name}={o.Value}";
			Console.WriteLine($"  {usage,-22} {o.Help}");
			var notes = new List<string>();
			if (o.Default is not null) notes.Add($"default: {o.Default}");
			if (showCommands) notes.Add($"for: {string.Join(", ", o.Commands.Where(c => c != "help"))}");
			if (notes.Count > 0) Console.WriteLine($"  {"",-22} ({string.Join("; ", notes)})");
		}
	}

	private static bool LongEnough(TitleSummary t, TimeSpan minLength) => t.DurationNs >= (long)(minLength.TotalSeconds * 1e9);

	private static int Info(string discArg, TimeSpan minLength, bool all) {
		var disc = DiscScanner.Scan(DiscScanner.ResolveVideoTs(discArg));
		var listed = disc.Titles.Where(t => all || LongEnough(t, minLength)).ToList();
		Console.WriteLine($"Disc: {OutputNaming.DiscName(disc.VolumeName)}   (volume label \"{disc.VolumeName}\", {disc.VideoTsFolder})");
		Console.WriteLine(all
			? $"{disc.Titles.Count} titles:"
			: $"{disc.Titles.Count} titles, {listed.Count} at least {minLength.TotalSeconds:0} s long:");
		Console.WriteLine();
		foreach (var t in listed) {
			Console.WriteLine($"  title {t.TitleNumber,2}  {Format(t.DurationNs)}  {t.Chapters,2} chapters  {t.SizeBytes / 1e9,5:F2} GB  {t.Video}");
			Console.WriteLine($"            audio: {(t.Audio.Count == 0 ? "none" : string.Join(", ", t.Audio))}");
			if (t.Subtitles.Count > 0) Console.WriteLine($"            subtitles: {string.Join(", ", t.Subtitles)}");
			if (t.ClosedCaptions) Console.WriteLine("            closed captions");
		}
		var skipped = disc.Titles.Except(listed).ToList();
		if (skipped.Count > 0)
			Console.WriteLine($"\nShorter than {minLength.TotalSeconds:0} s (not in 'all'; can still be ripped by number; --all lists them): " +
							  $"titles {string.Join(", ", skipped.Select(t => $"{t.TitleNumber} ({Format(t.DurationNs)})"))}");
		return 0;
	}

	private static int Mkv(string discArg, string which, string outputFolder, TimeSpan minLength, string? name, RipOptions options,
		bool overwrite, bool dryRun) {
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
		int skippedExisting = 0;
		foreach (var t in selected) {
			string path = Path.Combine(outputFolder, OutputNaming.FileName(discName, t.TitleNumber));
			var ripper = new TitleRipper(videoTs, t.TitleNumber);
			Console.WriteLine($"Title {t.TitleNumber} ({Format(t.DurationNs)}) -> {path}");
			if (dryRun) {
				if (File.Exists(path)) Console.WriteLine(overwrite ? "  (exists; would be replaced)" : "  (exists; would be skipped)");
				foreach (var line in ripper.Describe(options)) Console.WriteLine($"  {line}");
				continue;
			}
			if (File.Exists(path) && !overwrite) {
				Console.WriteLine("  skipped: the file already exists (use --overwrite to replace it)");
				skippedExisting++;
				continue;
			}
			int lastPercent = -1;
			var result = ripper.Rip(path, options: options, progress: (done, total) => {
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
		if (dryRun) Console.WriteLine("\nDry run: nothing was written. Tracks that turn out to carry no data are dropped during a real rip.");
		else if (skippedExisting > 0) Console.WriteLine($"\n{skippedExisting} title(s) skipped because the file already exists.");
		return 0;
	}

	private static string Format(long ns) {
		var ts = TimeSpan.FromTicks(ns / 100);
		return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
	}
}
