using System.Globalization;
using System.Text;

namespace LunaDR.Core.Ripping;

/// <summary>
/// Output file names: "&lt;Disc name&gt; - title NN.mkv", e.g. "Dead Zone S4 D1 - title 01.mkv".
/// NN is the disc's own title number (as a DVD player and "lunadr info" show it), so a file can be traced back to the disc.
/// This replaces MakeMKV's "&lt;name&gt;-&lt;comment&gt;_tNN.mkv", whose comment part (C1, B2, ...) and renumbered ids are hard to read (owner's choice).
/// </summary>
public static class OutputNaming {
	/// <summary>
	/// A readable disc name from the volume label: underscores become spaces and words are capitalized ("DEAD_ZONE_S4_D1" → "Dead Zone S4 D1").
	/// Words containing digits are kept exactly as written (S4, D1, DISC1), since they are usually codes.
	/// An empty label gives "DVD".
	/// </summary>
	public static string DiscName(string? volumeLabel) {
		if (string.IsNullOrWhiteSpace(volumeLabel)) return "DVD";
		var words = volumeLabel.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Word);
		return string.Join(' ', words);
	}

	/// <summary>Capitalize an all-caps word; leave words with digits, and mixed-case words, alone.</summary>
	private static string Word(string w) {
		if (w.Any(char.IsDigit) || w.Any(char.IsLower)) return w;
		return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w.ToLowerInvariant());
	}

	public static string FileName(string discName, int titleNumber) => Sanitize($"{discName} - title {titleNumber:00}.mkv");

	/// <summary>File name for chosen cells: "&lt;Disc name&gt; - cells 23-27,30.mkv" (cell numbers as in the cell map).</summary>
	public static string CellsFileName(string discName, IReadOnlyList<int> cellNumbers) =>
		Sanitize($"{discName} - cells {CellSelection.Format(cellNumbers)}.mkv");

	/// <summary>Replaces characters that are not allowed in Windows (or Linux) file names.</summary>
	public static string Sanitize(string name) {
		var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
		var sb = new StringBuilder(name.Length);
		foreach (char c in name) sb.Append(invalid.Contains(c) ? '_' : c);
		return sb.ToString().Trim().TrimEnd('.');
	}
}
