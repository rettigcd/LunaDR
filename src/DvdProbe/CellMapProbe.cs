using RipDvd.Core.Ifo;

namespace DvdProbe;

/// <summary>
/// Probe: one titles-vs-cells table for the whole disc, to see which titles play which cells (duplicates, "play all" titles, shared scenes).
/// Rows are every cell of every title set (title set, VOB id / cell id, in disc order); columns are every title.
/// An entry is the cell's position in that title's playlist; '*' marks a position where a chapter starts; '.' means not used.
/// </summary>
internal static class CellMapProbe {
	private sealed record Column(int Title, List<(int Vts, int Vob, int Cell)> Playlist, HashSet<int> ChapterStarts);

	public static void Print(string videoTs, int? onlyTitleSet) {
		var vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO")));
		var columns = new List<Column>();
		var cellInfo = new Dictionary<(int Vts, int Vob, int Cell), Cell>();

		foreach (var set in vmg.Titles.GroupBy(t => t.TitleSetNumber).OrderBy(g => g.Key)) {
			if (onlyTitleSet is { } only && set.Key != only) continue;
			var vts = VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, $"VTS_{set.Key:00}_0.IFO")), set.Key);
			foreach (var t in set) {
				var playlist = new List<(int, int, int)>();
				var chapterStarts = new HashSet<int>();
				var chapters = vts.Chapters[t.TitleSetTitleNumber - 1];
				int offset = 0;
				foreach (int pgcNumber in chapters.Select(c => c.PgcNumber).Distinct()) {
					var pgc = vts.Pgcs[pgcNumber - 1];
					foreach (var c in pgc.Cells) {
						playlist.Add((set.Key, c.VobId, c.CellId));
						cellInfo.TryAdd((set.Key, c.VobId, c.CellId), c);
					}
					foreach (var ch in chapters.Where(ch => ch.PgcNumber == pgcNumber))
						chapterStarts.Add(offset + pgc.ProgramEntryCells[ch.ProgramNumber - 1] - 1);
					offset += pgc.Cells.Count;
				}
				columns.Add(new Column(t.TitleNumber, playlist, chapterStarts));
			}
		}
		columns.Sort((a, b) => a.Title.CompareTo(b.Title));

		var rows = cellInfo.Keys.OrderBy(k => k.Vts).ThenBy(k => cellInfo[k].FirstSector).ThenBy(k => k).ToList();
		Console.WriteLine($"{rows.Count} cells, {columns.Count} titles. Entry = position in the title's playlist, * = chapter starts, . = not used.");
		Console.WriteLine();
		Console.Write($"  {"vts",3} {"cell",-6} {"sectors",-15} {"length",8} ");
		foreach (var c in columns) Console.Write($"|{("t" + c.Title),4}");
		Console.WriteLine();
		int lastVts = 0;
		foreach (var key in rows) {
			if (key.Vts != lastVts && lastVts != 0) {
				Console.Write($"  {new string('-', 35)}");
				foreach (var _ in columns) Console.Write("+----");
				Console.WriteLine();
			}
			lastVts = key.Vts;
			var cell = cellInfo[key];
			Console.Write($"  {key.Vts,3} {$"{key.Vob}/{key.Cell}",-6} {$"{cell.FirstSector}-{cell.LastSector}",-15} {cell.PlaybackTime.ToTimeSpan().ToString(@"m\:ss\.f"),8} ");
			foreach (var col in columns) {
				var positions = col.Playlist.Select((p, i) => (p, i)).Where(x => x.p == key).Select(x => x.i).ToList();
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
				if (i < j && a.Playlist.SequenceEqual(b.Playlist))
					notes.Add($"title {b.Title} plays the same cells as title {a.Title}" +
							  (a.ChapterStarts.SetEquals(b.ChapterStarts) ? " (same chapters)" : $" ({a.ChapterStarts.Count} vs {b.ChapterStarts.Count} chapters)"));
				else if (a.Playlist.Count < b.Playlist.Count && a.Playlist.All(b.Playlist.Contains))
					notes.Add($"title {a.Title}'s cells are all part of title {b.Title}");
			}
		}
		Console.WriteLine();
		Console.WriteLine(notes.Count == 0 ? "No title shares cells with another title." : string.Join(Environment.NewLine, notes));
	}
}
