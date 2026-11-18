using RipDvd.Core.Ifo;

namespace DvdProbe;

/// <summary>
/// Probe: a titles-vs-cells table per title set, to see which titles play which cells (duplicates, "play all" titles, shared scenes).
/// Rows are the physical cells of the title set (identified by VOB id / cell id, in disc order); columns are the titles that use the set.
/// An entry is the cell's position in that title's playlist; '*' marks a position where a chapter starts.
/// </summary>
internal static class CellMapProbe {
	public static void Print(string videoTs, int? onlyTitleSet) {
		var vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO")));
		foreach (var set in vmg.Titles.GroupBy(t => t.TitleSetNumber).OrderBy(g => g.Key)) {
			if (onlyTitleSet is { } only && set.Key != only) continue;
			var vts = VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, $"VTS_{set.Key:00}_0.IFO")), set.Key);

			// For each title: its playlist of cells (VOB id, cell id) and which playlist positions start a chapter.
			var columns = new List<(int Title, List<(int Vob, int Cell)> Playlist, HashSet<int> ChapterStarts)>();
			var cellInfo = new Dictionary<(int Vob, int Cell), Cell>();
			foreach (var t in set.OrderBy(t => t.TitleNumber)) {
				var playlist = new List<(int, int)>();
				var chapterStarts = new HashSet<int>();
				var chapters = vts.Chapters[t.TitleSetTitleNumber - 1];
				int offset = 0;
				foreach (int pgcNumber in chapters.Select(c => c.PgcNumber).Distinct()) {
					var pgc = vts.Pgcs[pgcNumber - 1];
					foreach (var c in pgc.Cells) {
						playlist.Add((c.VobId, c.CellId));
						cellInfo.TryAdd((c.VobId, c.CellId), c);
					}
					foreach (var ch in chapters.Where(ch => ch.PgcNumber == pgcNumber))
						chapterStarts.Add(offset + pgc.ProgramEntryCells[ch.ProgramNumber - 1] - 1);
					offset += pgc.Cells.Count;
				}
				columns.Add((t.TitleNumber, playlist, chapterStarts));
			}

			var rows = cellInfo.Keys.OrderBy(k => cellInfo[k].FirstSector).ThenBy(k => k).ToList();
			Console.WriteLine($"Title set {set.Key}: {rows.Count} cells, titles {string.Join(", ", columns.Select(c => c.Title))}");
			Console.Write($"  {"cell (vob/cell)",-16} {"sectors",-17} {"length",8} ");
			foreach (var c in columns) Console.Write($"| t{c.Title,-3}");
			Console.WriteLine();
			foreach (var key in rows) {
				var cell = cellInfo[key];
				var len = cell.PlaybackTime.ToTimeSpan();
				Console.Write($"  {$"{key.Vob}/{key.Cell}",-16} {$"{cell.FirstSector}-{cell.LastSector}",-17} {len.ToString(@"m\:ss\.f"),8} ");
				foreach (var col in columns) {
					var positions = col.Playlist.Select((p, i) => (p, i)).Where(x => x.p == key).Select(x => x.i).ToList();
					string entry = string.Join(",", positions.Select(i => $"{i + 1}{(col.ChapterStarts.Contains(i) ? "*" : "")}"));
					Console.Write($"| {entry,-4}");
				}
				Console.WriteLine();
			}

			// Summary: titles with exactly the same playlist, and titles whose cells are all inside another title's.
			for (int i = 0; i < columns.Count; i++) {
				for (int j = 0; j < columns.Count; j++) {
					if (i == j) continue;
					var a = columns[i];
					var b = columns[j];
					if (i < j && a.Playlist.SequenceEqual(b.Playlist))
						Console.WriteLine($"  title {b.Title} plays the same cells as title {a.Title}" +
										  (a.ChapterStarts.SetEquals(b.ChapterStarts) ? " (same chapters)" : $" ({a.ChapterStarts.Count} vs {b.ChapterStarts.Count} chapters)"));
					else if (a.Playlist.Count < b.Playlist.Count && a.Playlist.All(b.Playlist.Contains))
						Console.WriteLine($"  title {a.Title}'s cells are all part of title {b.Title}");
				}
			}
			Console.WriteLine();
		}
	}
}
