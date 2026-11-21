// lunadr: the LunaDR command line, a DVD and Blu-ray ripper, modeled on MakeMKV's makemkvcon.
// Run "lunadr help" for the commands and options; the help text is generated from the tables below,
// so adding an option to Options automatically documents it.

using LunaDR.Core.Bluray;
using LunaDR.Core.Ripping;

namespace LunaDR.Cli;

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
				"--cells adds the cell table: every cell with its number (for 'mkv … cells:') and which titles play it.",
				"Blu-ray: the titles are the disc's playlists, listed longest first by playlist number (00000 = 0); playlists that play the same",
				"video as another are marked. It also says whether the disc is AACS-encrypted and which key file would be used.",
			],
			["lunadr info", "lunadr info E:", "lunadr info E: --all", "lunadr info E: --cells"]),
		new("mkv", "<disc> <titles|all|cells:LIST> <output folder>",
			"Rip titles to MKV files named \"<Disc name> - title NN.mkv\", or chosen cells to one file.",
			[
				"<titles> is one title number or a comma-separated list (e.g. 1 or 1,3,6), as listed by 'info'.",
				"Any title can be ripped by number, even a short one. 'all' rips every title at least --minlength seconds long",
				"(minus compilations and duplicates with --no-compilations / --no-duplicates).",
				"cells:LIST rips the listed cells, joined in the order given, into one file \"<Disc name> - cells LIST.mkv\";",
				"LIST is cell numbers and ranges as shown by 'info --cells' (e.g. cells:23 or cells:23-27,30). The cells must be in one title set.",
				"Each cell becomes a chapter; the audio and subtitle tracks are those of a title that plays the first cell.",
				"By default every audio and subtitle track is kept, closed captions become a text subtitle track, and chapters are written",
				"(MakeMKV's defaults). Existing files are skipped unless --overwrite is given.",
				"Blu-ray: <titles> are playlist numbers as listed by 'info' (e.g. 0 or 0,8); files are named \"<Disc name> - playlist NNNNN.mkv\".",
				"Supported: VC-1, H.264 and MPEG-2 video; AC3, E-AC3, DTS / DTS-HD, TrueHD (written as TrueHD plus its AC3 core; see --truehd-core) and LPCM audio;",
				"PGS subtitles. An AACS-encrypted disc needs a key file in libaacs's KEYDB.cfg format, with the disc's entry, next to the program",
				"or in %APPDATA%\\aacs\\ or %LOCALAPPDATA%\\aacs\\ (this program never downloads keys). UHD (AACS 2) and BD+ discs are not supported.",
			],
			[
				"lunadr mkv E: all C:\\Rips",
				"lunadr mkv E: 1 C:\\Rips --name=\"The Dead Zone S4D1\"",
				"lunadr mkv E: 1,2 C:\\Rips --audio=eng --subtitles=none",
				"lunadr mkv E: all C:\\Rips --no-compilations --no-duplicates",
				"lunadr mkv E: all C:\\Rips --dry-run",
				"lunadr mkv E: cells:23-27 C:\\Rips",
			]),
		new("help", "[command]", "Show this help, or the help for one command.", [], ["lunadr help mkv"]),
	];

	private static readonly Option[] Options = [
		new("minlength", "SECONDS", ["info", "mkv"], "Titles shorter than this are left out of 'info' and of 'mkv … all'.", "120 (MakeMKV's default)"),
		new("all", null, ["info"], "List every title in full, including the short ones."),
		new("cells", null, ["info"], "Also show the cell table: each cell's number, length, and the titles that play it."),
		new("name", "NAME", ["mkv"], "Disc name used in the file names, instead of the one made from the volume label."),
		new("audio", "LANGS", ["mkv"], "Audio tracks to keep, by language: e.g. eng or eng,fre (2-letter codes like en also work); all; none. " +
			"'und' matches tracks with no language.", "all"),
		new("subtitles", "LANGS", ["mkv"], "Bitmap subtitle tracks to keep, by language, as for --audio. (Closed captions are controlled by --captions.)", "all"),
		new("captions", "on|off", ["mkv"], "Convert closed captions to a text subtitle track. With 'off' the caption data stays inside the video.", "on"),
		new("chapters", "on|off", ["mkv"], "Write the disc's chapters.", "on"),
		new("truehd-core", "on|off", ["mkv"], "Blu-ray: also write the AC3 version inside each Dolby TrueHD soundtrack as its own track, " +
			"for players that can't play TrueHD.", "on"),
		new("no-compilations", null, ["mkv"], "With 'all', skip compilations: \"play all\" titles made of parts of other titles (marked in 'info')."),
		new("no-duplicates", null, ["mkv"], "With 'all', skip titles that play the same video as an earlier title, e.g. the same movie with other chapters (marked in 'info')."),
		new("overwrite", null, ["mkv"], "Replace output files that already exist (otherwise they are skipped)."),
		new("dry-run", null, ["mkv"], "Show which files and tracks would be written, without ripping."),
		new("help", null, ["info", "mkv", "help"], "Show help (also -h)."),
	];

	private const string DiscHelp = "<disc> is disc:0 (the first optical drive; disc:1 the second, ...), a drive letter like E:, or a folder containing " +
		"VIDEO_TS (DVD) or BDMV (Blu-ray).";

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
					return Info(positional.ElementAtOrDefault(1) ?? "disc:0", minLength, values.ContainsKey("all"), values.ContainsKey("cells"));
				case "mkv":
					if (positional.Count != 4) return Fail("mkv needs three arguments: <disc> <titles|all|cells:LIST> <output folder>.", command);
					var options = new RipOptions {
						AudioLanguages = values.TryGetValue("audio", out var au) ? RipOptions.ParseLanguages(au!) : null,
						SubtitleLanguages = values.TryGetValue("subtitles", out var su) ? RipOptions.ParseLanguages(su!) : null,
						ClosedCaptions = !values.TryGetValue("captions", out var cc) || OnOff(cc!, "captions"),
						Chapters = !values.TryGetValue("chapters", out var ch) || OnOff(ch!, "chapters"),
						TrueHdCore = !values.TryGetValue("truehd-core", out var core) || OnOff(core!, "truehd-core"),
					};
					return Mkv(positional[1], positional[2], positional[3], minLength, values.GetValueOrDefault("name"), options,
						overwrite: values.ContainsKey("overwrite"), dryRun: values.ContainsKey("dry-run"),
						skipCompilations: values.ContainsKey("no-compilations"), skipDuplicates: values.ContainsKey("no-duplicates"));
			}
			return 0;
		} catch (OptionException ex) {
			return Fail(ex.Message, ex.Command);
		} catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException or FormatException or NotSupportedException or KeyNotFoundException) {
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
		Console.Error.WriteLine(command is null ? "Run 'lunadr help' for usage." : $"Run 'lunadr help {command}' for usage.");
		return 1;
	}

	/// <summary>Prints the general help, or the help for one command, from the Commands and Options tables.</summary>
	private static int Help(string? commandName) {
		var command = Commands.FirstOrDefault(c => c.Name == commandName);
		if (commandName is not null && commandName != "help" && command is null) return Fail($"Unknown command '{commandName}'.");

		if (command is null || command.Name == "help") {
			Console.WriteLine("lunadr (LunaDR): rip DVD and Blu-ray titles to MKV files (modeled on MakeMKV's makemkvcon).");
			Console.WriteLine();
			Console.WriteLine("usage: lunadr <command> [arguments] [options]");
			Console.WriteLine();
			Console.WriteLine("commands:");
			foreach (var c in Commands) Console.WriteLine($"  {c.Name,-6} {c.Summary}");
			Console.WriteLine();
			Console.WriteLine(DiscHelp);
			Console.WriteLine();
			Console.WriteLine("options:");
			PrintOptions(Options.Where(o => o.Name != "help"), showCommands: true);
			Console.WriteLine();
			Console.WriteLine("Run 'lunadr help <command>' for details and examples, e.g. 'lunadr help mkv'.");
			return 0;
		}

		Console.WriteLine($"usage: lunadr {command.Name} {command.Arguments} [options]");
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

	private static int Info(string discArg, TimeSpan minLength, bool all, bool cells) {
		if (BdScanner.ResolveRoot(discArg) is { } bdRoot) return BdInfo(bdRoot, minLength, all);
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
		if (cells) {
			Console.WriteLine();
			foreach (var line in CellMapTable.Lines(disc.CellMap)) Console.WriteLine(line);
		}
		return 0;
	}

	/// <summary>"compilation of titles 7, 8, …" or "same video as title 1", or null.</summary>
	private static string? Relation(TitleSummary t) =>
		t.CompilationOf is { } c ? $"compilation (\"play all\") of titles {string.Join(", ", c)}" :
		t.DuplicateOf is { } d ? $"same video as title {d}" : null;

	private static int Mkv(string discArg, string which, string outputFolder, TimeSpan minLength, string? name, RipOptions options,
		bool overwrite, bool dryRun, bool skipCompilations, bool skipDuplicates) {
		if (BdScanner.ResolveRoot(discArg) is { } bdRoot) return BdMkv(bdRoot, which, outputFolder, minLength, name, options, overwrite, dryRun, skipDuplicates);
		string videoTs = DiscScanner.ResolveVideoTs(discArg);
		var disc = DiscScanner.Scan(videoTs);
		if (which.StartsWith("cells:", StringComparison.OrdinalIgnoreCase))
			return MkvCells(disc, CellSelection.Parse(which[6..]), outputFolder, name, options, overwrite, dryRun);
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
				Console.Error.WriteLine($"No title {string.Join(", ", unknown)} on this disc; titles are 1..{disc.Titles.Count} (see 'lunadr info').");
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
		return RunJob(videoTs, items, options, skippedExisting);
	}

	/// <summary>'info' for a Blu-ray: its playlists (the titles), numbered by the playlist files.</summary>
	private static int BdInfo(string root, TimeSpan minLength, bool all) {
		var disc = BdScanner.Scan(root);
		bool Long(BdTitle t) => t.DurationNs >= (long)(minLength.TotalSeconds * 1e9);
		var listed = disc.Titles.Where(t => all || Long(t)).OrderByDescending(t => t.DurationNs).ToList();
		Console.WriteLine($"Disc: {OutputNaming.DiscName(disc.VolumeName)}   (Blu-ray, volume label \"{disc.VolumeName}\", {disc.Root})");
		Console.WriteLine(all
			? $"{disc.Titles.Count} playlists (titles):"
			: $"{disc.Titles.Count} playlists (titles), {listed.Count} at least {minLength.TotalSeconds:0} s long:");
		Console.WriteLine("Rip a playlist by its number, e.g. 'lunadr mkv <disc> 0 <folder>' for playlist 00000.");
		Console.WriteLine();
		foreach (var t in listed) {
			Console.WriteLine($"  playlist {t.Playlist:00000}  {Format(t.DurationNs)}  {t.Chapters,2} chapters  {t.SizeBytes / 1e9,5:F2} GB  {t.Video}" +
							  (t.Angles > 1 ? $"  ({t.Angles} angles; angle 1 is ripped)" : ""));
			Console.WriteLine($"            audio: {(t.Audio.Count == 0 ? "none" : string.Join(", ", t.Audio))}");
			if (t.Subtitles.Count > 0) Console.WriteLine($"            subtitles: {string.Join(", ", t.Subtitles)}");
			if (t.DuplicateOf is { } d) Console.WriteLine($"            same video as playlist {d:00000}");
		}
		var skipped = disc.Titles.Except(listed).OrderBy(t => t.Playlist).ToList();
		if (skipped.Count > 0)
			Console.WriteLine($"\nShorter than {minLength.TotalSeconds:0} s (not in 'all'; can still be ripped by number; --all lists them): " +
							  $"playlists {string.Join(", ", skipped.Select(t => $"{t.Playlist} ({Format(t.DurationNs)})"))}");
		if (Directory.Exists(Path.Combine(root, "AACS"))) {
			string? keyFile = KeyDb.Find();
			Console.WriteLine($"\nAACS-encrypted. Key file: {keyFile ?? "none found (needed to rip; see 'lunadr help mkv')"}");
		}
		return 0;
	}

	/// <summary>'mkv' for a Blu-ray: playlists by number (or 'all'), each to "&lt;Disc name&gt; - playlist NNNNN.mkv".</summary>
	private static int BdMkv(string root, string which, string outputFolder, TimeSpan minLength, string? name, RipOptions options,
		bool overwrite, bool dryRun, bool skipDuplicates) {
		var disc = BdScanner.Scan(root);
		List<BdTitle> selected;
		if (which == "all") {
			selected = disc.Titles.Where(t => t.DurationNs >= (long)(minLength.TotalSeconds * 1e9)).OrderBy(t => t.Playlist).ToList();
			foreach (var t in selected.Where(t => skipDuplicates && t.DuplicateOf is not null).ToList()) {
				Console.WriteLine($"Playlist {t.Playlist:00000} ({Format(t.DurationNs)}) skipped: same video as playlist {t.DuplicateOf:00000}");
				selected.Remove(t);
			}
		} else if (which.StartsWith("cells:", StringComparison.OrdinalIgnoreCase)) {
			Console.Error.WriteLine("cells: is for DVDs; a Blu-ray is ripped by playlist number (see 'lunadr info').");
			return 1;
		} else {
			var numbers = which.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
			var unknown = numbers.Where(n => disc.Titles.All(t => t.Playlist != n)).ToList();
			if (unknown.Count > 0) {
				Console.Error.WriteLine($"No playlist {string.Join(", ", unknown)} on this disc (see 'lunadr info').");
				return 1;
			}
			selected = numbers.Select(n => disc.Titles.First(t => t.Playlist == n)).ToList();
		}

		string discName = OutputNaming.Sanitize(name ?? OutputNaming.DiscName(disc.VolumeName));
		int skippedExisting = 0;
		var items = new List<RipJobItem>();
		foreach (var t in selected) {
			string path = Path.Combine(outputFolder, BdScanner.FileName(discName, t.Playlist));
			if (dryRun) {
				Console.WriteLine($"Playlist {t.Playlist:00000} ({Format(t.DurationNs)}) -> {path}");
				if (File.Exists(path)) Console.WriteLine(overwrite ? "  (exists; would be replaced)" : "  (exists; would be skipped)");
				foreach (var line in new BdTitleRipper(root, t.Playlist).Describe(options)) Console.WriteLine($"  {line}");
				continue;
			}
			if (File.Exists(path) && !overwrite) {
				Console.WriteLine($"Playlist {t.Playlist:00000} skipped: {path} already exists (use --overwrite to replace it)");
				skippedExisting++;
				continue;
			}
			items.Add(RipJobItem.ForPlaylist(t.Playlist, path, t.SizeBytes / 2048));
		}
		if (dryRun) {
			Console.WriteLine("\nDry run: nothing was written. Tracks that turn out to carry no data are dropped during a real rip.");
			return 0;
		}
		return RunJob(root, items, options, skippedExisting);
	}

	/// <summary>mkv with cells:LIST: the chosen cells, joined in the order given, into one file.</summary>
	private static int MkvCells(DiscSummary disc, List<int> cells, string outputFolder, string? name, RipOptions options, bool overwrite, bool dryRun) {
		if (CellSelection.Validate(disc.CellMap, cells) is { } error) {
			Console.Error.WriteLine($"{error.TrimEnd('.')} (see 'lunadr info --cells').");
			return 1;
		}
		string discName = OutputNaming.Sanitize(name ?? OutputNaming.DiscName(disc.VolumeName));
		string path = Path.Combine(outputFolder, OutputNaming.CellsFileName(discName, cells));
		string what = $"Cells {CellSelection.Format(cells)} ({Format(CellSelection.DurationNs(disc.CellMap, cells))})";
		if (dryRun) {
			Console.WriteLine($"{what} -> {path}");
			if (File.Exists(path)) Console.WriteLine(overwrite ? "  (exists; would be replaced)" : "  (exists; would be skipped)");
			foreach (var line in TitleRipper.ForCells(disc.VideoTsFolder, cells).Describe(options)) Console.WriteLine($"  {line}");
			Console.WriteLine("\nDry run: nothing was written. Tracks that turn out to carry no data are dropped during a real rip.");
			return 0;
		}
		if (File.Exists(path) && !overwrite) {
			Console.WriteLine($"{what} skipped: {path} already exists (use --overwrite to replace it)");
			return 0;
		}
		var item = RipJobItem.ForCells(cells, path, CellSelection.Sectors(disc.CellMap, cells));
		return RunJob(disc.VideoTsFolder, [item], options, 0);
	}

	/// <summary>Runs the rip job with a status line, then prints a line per file.</summary>
	private static int RunJob(string videoTs, List<RipJobItem> items, RipOptions options, int skippedExisting) {
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
					Console.WriteLine($"{p.Item.Name} ({p.Index + 1} of {p.Count}) -> {p.Item.OutputPath}");
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
			if (r.Error is not null) Console.WriteLine($"{r.Item.Name} FAILED: {r.Error.Message}");
			else if (r.Result is { } res) {
				Console.WriteLine($"{r.Item.Name}: {Format(res.DurationNs)} in {res.Elapsed.TotalSeconds:F0} s " +
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
