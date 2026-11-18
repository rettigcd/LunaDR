using RipDvd.Core.Ifo;
using RipDvd.Core.Ripping;

namespace DvdProbe;

/// <summary>
/// Probe: one titles-vs-cells table for the whole disc, to see which titles play which cells (duplicates, "play all" titles, shared scenes).
/// Rows are every cell of every title set (title set, VOB id / cell id, in disc order); columns are every title.
/// An entry is the cell's position in that title's playlist; '*' marks a position where a chapter starts; '.' means not used.
/// The data comes from RipDvd.Core's <see cref="CellMap"/>, which the GUI uses too.
/// </summary>
internal static class CellMapProbe {
	public static void Print(string videoTs, int? onlyTitleSet) {
		var vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO")));
		var vtsCache = new Dictionary<int, VtsIfo>();
		var map = CellMap.Build(vmg, n => vtsCache.TryGetValue(n, out var v) ? v
			: vtsCache[n] = VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, $"VTS_{n:00}_0.IFO")), n));

		var rows = Enumerable.Range(0, map.Cells.Count).Where(i => onlyTitleSet is null || map.Cells[i].TitleSet == onlyTitleSet).ToList();
		var columns = map.Titles.Where(t => t.Cells.Any(rows.Contains)).ToList();

		Console.WriteLine($"{rows.Count} cells, {columns.Count} titles. Entry = position in the title's playlist, * = chapter starts, . = not used.");
		Console.WriteLine();
		Console.Write($"  {"vts",3} {"cell",-6} {"sectors",-15} {"length",8} ");
		foreach (var c in columns) Console.Write($"|{("t" + c.TitleNumber),4}");
		Console.WriteLine();
		int lastVts = 0;
		foreach (int row in rows) {
			var cell = map.Cells[row];
			if (cell.TitleSet != lastVts && lastVts != 0) {
				Console.Write($"  {new string('-', 35)}");
				foreach (var _ in columns) Console.Write("+----");
				Console.WriteLine();
			}
			lastVts = cell.TitleSet;
			string length = TimeSpan.FromTicks(cell.DurationNs / 100).ToString(@"m\:ss\.f");
			Console.Write($"  {cell.TitleSet,3} {$"{cell.VobId}/{cell.CellId}",-6} {$"{cell.FirstSector}-{cell.LastSector}",-15} {length,8} ");
			foreach (var col in columns) {
				var positions = col.Cells.Select((c, i) => (c, i)).Where(x => x.c == row).Select(x => x.i).ToList();
				string entry = positions.Count == 0 ? "." : string.Join(",", positions.Select(i => $"{i + 1}{(col.ChapterStarts.Contains(i) ? "*" : "")}"));
				Console.Write($"|{entry,4}");
			}
			Console.WriteLine();
		}

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
