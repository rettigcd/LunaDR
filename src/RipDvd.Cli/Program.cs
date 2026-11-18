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
				"Compilations (\"play all\" titles made of other titles) and duplicates (same video as another title) are marked.",
			],
			["rip-dvd info", "rip-dvd info E:", "rip-dvd info E: --all"]),
		new("mkv", "<disc> <titles|all> <output folder>",
			"Rip titles to MKV files named \"<Disc name> - title NN.mkv\".",
			[
				"<titles> is one title number or a comma-separated list (e.g. 1 or 1,3,6), as listed by 'info'.",
				"Any title can be ripped by number, even a short one. 'all' rips every title at least --minlength seconds long",
				"(minus compilations and duplicates with --no-compilations / --no-duplicates).",
				"By default every audio and subtitle track is kept, closed captions become a text subtitle track, and chapters are written",
				"(MakeMKV's defaults). Existing files are skipped unless --overwrite is given.",
			],
			[
				"rip-dvd mkv E: all C:\\Rips",
				"rip-dvd mkv E: 1 C:\\Rips --name=\"The Dead Zone S4D1\"",
				"rip-dvd mkv E: 1,2 C:\\Rips --audio=eng --subtitles=none",
				"rip-dvd mkv E: all C:\\Rips --no-compilations --no-duplicates",
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
		new("no-compilations", null, ["mkv"], "With 'all', skip compilations: \"play all\" titles made of parts of other titles (marked in 'info')."),
		new("no-duplicates", null, ["mkv"], "With 'all', skip titles that play the same video as an earlier title, e.g. the same movie with other chapters (marked in 'info')."),
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
						overwrite: values.ContainsKey("overwrite"), dryRun: values.ContainsKey("dry-run"),
						skipCompilations: values.ContainsKey("no-compilations"), skipDuplicates: values.ContainsKey("no-duplicates"));
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
			if (Relation(t) is { } rel) Console.WriteLine($"            {rel}");
		}
		var skipped = disc.Titles.Except(listed).ToList();
		if (skipped.Count > 0)
			Console.WriteLine($"\nShorter than {minLength.TotalSeconds:0} s (not in 'all'; can still be ripped by number; --all lists them): " +
							  $"titles {string.Join(", ", skipped.Select(t => $"{t.TitleNumber} ({Format(t.DurationNs)})"))}");
		return 0;
	}

	/// <summary>"compilation of titles 7, 8, …" or "same video as title 1", or null.</summary>
	private static string? Relation(TitleSummary t) =>
		t.CompilationOf is { } c ? $"compilation (\"play all\") of titles {string.Join(", ", c)}" :
		t.DuplicateOf is { } d ? $"same video as title {d}" : null;

	private static int Mkv(string discArg, string which, string outputFolder, TimeSpan minLength, string? name, RipOptions options,
		bool overwrite, bool dryRun, bool skipCompilations, bool skipDuplicates) {
		string videoTs = DiscScanner.ResolveVideoTs(discArg);
		var disc = DiscScanner.Scan(videoTs);
		List<TitleSummary> selected;
		if (which == "all") {
			selected = disc.Titles.Where(t => LongEnough(t, minLength)).ToList();
			foreach (var t in selected.ToList()) {
				bool skip = (skipCompilations && t.CompilationOf is not null) || (skipDuplicates && t.DuplicateOf is not null);
				if (!skip) continue;
				Console.WriteLine($"Title {t.TitleNumber} ({Format(t.DurationNs)}) skipped: {Relation(t)}");
				selected.Remove(t);
			}
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
		var items = new List<RipJobItem>();
		foreach (var t in selected) {
			string path = Path.Combine(outputFolder, OutputNaming.FileName(discName, t.TitleNumber));
			if (dryRun) {
				Console.WriteLine($"Title {t.TitleNumber} ({Format(t.DurationNs)}) -> {path}");
				if (File.Exists(path)) Console.WriteLine(overwrite ? "  (exists; would be replaced)" : "  (exists; would be skipped)");
				foreach (var line in new TitleRipper(videoTs, t.TitleNumber).Describe(options)) Console.WriteLine($"  {line}");
				continue;
			}
			if (File.Exists(path) && !overwrite) {
				Console.WriteLine($"Title {t.TitleNumber} skipped: {path} already exists (use --overwrite to replace it)");
				skippedExisting++;
				continue;
			}
			items.Add(new RipJobItem(t.TitleNumber, path, t.SizeBytes / 2048));
		}
		if (dryRun) {
			Console.WriteLine("\nDry run: nothing was written. Tracks that turn out to carry no data are dropped during a real rip.");
			return 0;
		}

		// Ctrl+C cancels cleanly: the title being ripped stops and its partial file is removed.
		using var cancel = new CancellationTokenSource();
		Console.CancelKeyPress += (_, e) => {
			e.Cancel = true;
			cancel.Cancel();
		};

		int lastIndex = -1;
		var lastPrint = DateTime.MinValue;
		var job = new RipJob(videoTs, items, options);
		List<RipJobItemResult> results;
		try {
			results = job.Run(p => {
				if (p.Index != lastIndex) {
					if (lastIndex >= 0) Console.WriteLine();
					Console.WriteLine($"Title {p.Item.TitleNumber} ({p.Index + 1} of {p.Count}) -> {p.Item.OutputPath}");
					lastIndex = p.Index;
				}
				// Rewrite the status line at most twice a second.
				if (p.Title.Phase != RipPhase.Done && (DateTime.UtcNow - lastPrint).TotalMilliseconds < 500) return;
				lastPrint = DateTime.UtcNow;
				string status = p.Title.Phase switch {
					RipPhase.Unlocking => "unlocking the drive",
					RipPhase.FindingKey => "finding the title key",
					RipPhase.Finishing => "finishing the file",
					RipPhase.Done => "done",
					_ => $"{p.TitleFraction,4:P0} ({Remaining(p.TitleRemaining)} left)  all: {p.OverallFraction,4:P0} ({Remaining(p.OverallRemaining)} left)  {p.BytesPerSecond / 1e6:F1} MB/s",
				};
				Console.Write($"\r  {status}".PadRight(100));
			}, cancel.Token);
		} catch (OperationCanceledException) {
			Console.WriteLine("\nCancelled; the unfinished file was removed.");
			return 3;
		}
		Console.WriteLine();

		foreach (var r in results) {
			if (r.Error is not null) Console.WriteLine($"Title {r.Item.TitleNumber} FAILED: {r.Error.Message}");
			else if (r.Result is { } res) {
				Console.WriteLine($"Title {r.Item.TitleNumber}: {Format(res.DurationNs)} in {res.Elapsed.TotalSeconds:F0} s " +
								  $"({res.Sectors * 2048 / 1e6 / Math.Max(1, res.Elapsed.TotalSeconds):F1} MB/s) -> {Path.GetFileName(res.OutputPath)}");
				foreach (var w in res.Warnings) Console.WriteLine($"  warning: {w}");
			}
		}
		if (skippedExisting > 0) Console.WriteLine($"\n{skippedExisting} title(s) skipped because the file already exists.");
		return results.Any(r => r.Error is not null) ? 2 : 0;
	}

	private static string Remaining(TimeSpan? t) => t is { } r ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : "-:--:--";

	private static string Format(long ns) {
		var ts = TimeSpan.FromTicks(ns / 100);
		return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
	}
}
