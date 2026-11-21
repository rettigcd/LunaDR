using LunaDR.Core.Ifo;
using LunaDR.Core.Ripping;

namespace DvdProbe;

/// <summary>
/// Probe: one titles-vs-cells table for the whole disc, to see which titles play which cells (duplicates, "play all" titles, shared scenes).
/// Rows are every cell of every title set (title set, VOB id / cell id, in disc order); columns are every title.
/// An entry is the cell's position in that title's playlist; '*' marks a position where a chapter starts; '.' means not used.
/// The data comes from LunaDR.Core's <see cref="CellMap"/>, which the GUI uses too.
/// </summary>
internal static class CellMapProbe {
	public static void Print(string videoTs, int? onlyTitleSet) {
		var map = CellMap.Load(videoTs);
		foreach (var line in CellMapTable.Lines(map, onlyTitleSet)) Console.WriteLine(line);
		var rows = Enumerable.Range(0, map.Cells.Count).Where(i => onlyTitleSet is null || map.Cells[i].TitleSet == onlyTitleSet).ToList();
		var columns = map.Titles.Where(t => t.Cells.Any(rows.Contains)).ToList();

		// Summary: titles with exactly the same playlist, and titles whose cells are all inside another title's.
		var notes = new List<string>();
		for (int i = 0; i < columns.Count; i++) {
			for (int j = 0; j < columns.Count; j++) {
				if (i == j) continue;
				var a = columns[i];
				var b = columns[j];
				if (i < j && a.Cells.SequenceEqual(b.Cells))
					notes.Add($"title {b.TitleNumber} plays the same cells as title {a.TitleNumber}" +
							  (a.ChapterStarts.SetEquals(b.ChapterStarts) ? " (same chapters)" : $" ({a.ChapterStarts.Count} vs {b.ChapterStarts.Count} chapters)"));
				else if (a.Cells.Count < b.Cells.Count && a.Cells.All(b.Cells.Contains))
					notes.Add($"title {a.TitleNumber}'s cells are all part of title {b.TitleNumber}");
			}
		}
		Console.WriteLine();
		Console.WriteLine(notes.Count == 0 ? "No title shares cells with another title." : string.Join(Environment.NewLine, notes));
	}
}
