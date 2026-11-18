using RipDvd.Core.Ripping;

namespace RipDvd.Gui.ViewModels;

/// <summary>One row of the title list: a <see cref="TitleSummary"/> formatted for display.</summary>
public sealed class TitleRow(TitleSummary title, TimeSpan minimumLength) : ViewModelBase {
	public TitleSummary Title { get; } = title;

	public int Number => Title.TitleNumber;
	public string Length => Format(Title.DurationNs);
	public int Chapters => Title.Chapters;
	public string Size => Title.SizeBytes >= 1e9 ? $"{Title.SizeBytes / 1e9:F2} GB" : $"{Title.SizeBytes / 1e6:F0} MB";
	public string Video => Title.Video;
	public string Audio => Title.Audio.Count == 0 ? "none" : string.Join(", ", Title.Audio);
	public string Subtitles => string.Join(", ", Title.Subtitles);
	public string Captions => Title.ClosedCaptions ? "CC" : "";

	/// <summary>Shorter than the minimum length (120 s): shown dimmed, like MakeMKV hides such titles.</summary>
	public bool IsShort { get; } = title.DurationNs < (long)(minimumLength.TotalSeconds * 1e9);

	/// <summary>"same video as title 1", "compilation of titles 7–11", or empty.</summary>
	public string Note =>
		Title.CompilationOf is { } c ? $"compilation of titles {Ranges(c)}" :
		Title.DuplicateOf is { } d ? $"same video as title {d}" : "";

	public bool IsDuplicateOrCompilation => Title.CompilationOf is not null || Title.DuplicateOf is not null;

	private static string Format(long ns) {
		var t = TimeSpan.FromTicks(ns / 100);
		return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
	}

	/// <summary>7, 8, 9, 10, 11 → "7–11"; 3, 5, 6 → "3, 5–6".</summary>
	private static string Ranges(IReadOnlyList<int> numbers) {
		var parts = new List<string>();
		for (int i = 0; i < numbers.Count; i++) {
			int start = numbers[i];
			while (i + 1 < numbers.Count && numbers[i + 1] == numbers[i] + 1) i++;
			parts.Add(numbers[i] == start ? $"{start}" : $"{start}–{numbers[i]}");
		}
		return string.Join(", ", parts);
	}
}
