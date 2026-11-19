namespace RipDvd.Core.Ripping;

/// <summary>
/// Cells chosen by the user, to rip alone or joined into one file.
/// Cells are numbered as in the disc's cell map: 1 is the first cell of <see cref="CellMap.Cells"/> (ordered by title set, then position on the disc).
/// The CLI's 'info --cells' and the GUI's cell map show these numbers.
/// Text form: numbers and ranges separated by commas, e.g. "23-27,30"; the order given is the playback order.
/// </summary>
public static class CellSelection {
	/// <summary>Parses "23-27,30" into 23, 24, 25, 26, 27, 30. A range may also run backwards ("27-23").</summary>
	public static List<int> Parse(string text) {
		var numbers = new List<int>();
		foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			int dash = part.IndexOf('-', 1);
			if (dash < 0) {
				numbers.Add(Number(part));
				continue;
			}
			int from = Number(part[..dash]), to = Number(part[(dash + 1)..]);
			int step = to >= from ? 1 : -1;
			for (int n = from; n != to + step; n += step) numbers.Add(n);
		}
		if (numbers.Count == 0) throw new FormatException("No cells given; write cell numbers like 23 or 23-27,30.");
		return numbers;
	}

	private static int Number(string s) =>
		int.TryParse(s.Trim(), out int n) ? n : throw new FormatException($"'{s}' is not a cell number; write cell numbers like 23 or 23-27,30.");

	/// <summary>Why these cells can't be ripped together, or null if they can.</summary>
	public static string? Validate(CellMap map, IReadOnlyList<int> cellNumbers) {
		if (cellNumbers.Count == 0) return "No cells selected.";
		var outside = cellNumbers.Where(n => n < 1 || n > map.Cells.Count).Distinct().ToList();
		if (outside.Count > 0) return $"No cell {string.Join(", ", outside)} on this disc; cells are 1..{map.Cells.Count}.";
		var repeated = cellNumbers.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
		if (repeated.Count > 0) return $"Cell {string.Join(", ", repeated)} is listed more than once.";
		var sets = cellNumbers.Select(n => map.Cells[n - 1].TitleSet).Distinct().ToList();
		if (sets.Count > 1)
			return $"The cells are in different title sets ({string.Join(", ", sets)}); cells can only be joined within one title set, " +
				   "since title sets can differ in video format, audio tracks and copy-protection key.";
		return null;
	}

	/// <summary>The short text form: 23, 24, 25, 30 → "23-25,30". Runs of three or more consecutive cells become a range.</summary>
	public static string Format(IReadOnlyList<int> cellNumbers) {
		var parts = new List<string>();
		for (int i = 0; i < cellNumbers.Count;) {
			int j = i;
			while (j + 1 < cellNumbers.Count && cellNumbers[j + 1] == cellNumbers[j] + 1) j++;
			if (j - i >= 2) parts.Add($"{cellNumbers[i]}-{cellNumbers[j]}");
			else for (int k = i; k <= j; k++) parts.Add(cellNumbers[k].ToString());
			i = j + 1;
		}
		return string.Join(",", parts);
	}

	/// <summary>Size of the chosen cells on the disc, in sectors (weights the progress and time estimates).</summary>
	public static long Sectors(CellMap map, IReadOnlyList<int> cellNumbers) =>
		cellNumbers.Sum(n => (long)map.Cells[n - 1].LastSector - map.Cells[n - 1].FirstSector + 1);

	/// <summary>Playing time of the chosen cells (from the IFO).</summary>
	public static long DurationNs(CellMap map, IReadOnlyList<int> cellNumbers) => cellNumbers.Sum(n => map.Cells[n - 1].DurationNs);
}
