namespace RipDvd.Core.Ripping;

/// <summary>
/// What to put in the output file. The defaults match MakeMKV's: every audio and subtitle track, closed captions as text, chapters.
/// </summary>
public sealed class RipOptions {
	/// <summary>Audio languages to keep (ISO 639-2, e.g. "eng"; "und" = no language on the disc). Null = all; empty = none.</summary>
	public IReadOnlySet<string>? AudioLanguages { get; init; }

	/// <summary>Bitmap subtitle (VobSub) languages to keep, as for <see cref="AudioLanguages"/>.</summary>
	public IReadOnlySet<string>? SubtitleLanguages { get; init; }

	/// <summary>Convert closed captions to a text subtitle track (and take them out of the video, as MakeMKV does).</summary>
	public bool ClosedCaptions { get; init; } = true;

	/// <summary>
	/// Blu-ray: also write the AC3 version carried inside a Dolby TrueHD stream, as its own track after the TrueHD one.
	/// It lets players without TrueHD play that soundtrack; leaving it out saves about 0.5 GB for a two-hour movie.
	/// </summary>
	public bool TrueHdCore { get; init; } = true;

	/// <summary>Write the disc's chapters.</summary>
	public bool Chapters { get; init; } = true;

	/// <summary>Testing only: sectors (VOB sector numbers) that act as unreadable, to test damaged-disc handling (probe 'rip -bad').</summary>
	public Func<long, bool>? SimulateBadSector { get; init; }

	public static readonly RipOptions Default = new();

	/// <summary>
	/// Parses a language list such as "eng,fre", "en,fr", "all" or "none".
	/// Returns null for "all", an empty set for "none"; two-letter codes are turned into three-letter ones.
	/// </summary>
	public static IReadOnlySet<string>? ParseLanguages(string value) {
		value = value.Trim();
		if (value.Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
		if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return new HashSet<string>();
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			string code = part.Length == 2 ? Languages.ToIso6392(part) : part.ToLowerInvariant();
			if (code.Length != 3) throw new ArgumentException($"'{part}' is not a language code (use e.g. eng, fre, spa, or en, fr, es).");
			set.Add(code);
		}
		return set;
	}

	/// <summary>True if a track with this language (null = none on the disc) is kept by <paramref name="filter"/>.</summary>
	public static bool Keeps(IReadOnlySet<string>? filter, string? language) => filter is null || filter.Contains(language ?? "und");
}
