namespace LunaDR.Core.Ripping;

/// <summary>
/// The cell map as a text table: one row per cell (its number, title set, VOB id / cell id, sectors, length), one column per title.
/// An entry is the cell's position in that title's playlist; '*' marks a position where a chapter starts; '.' means not used.
/// Used by the CLI's 'info --cells' and the probe's 'cellmap'.
/// </summary>
public static class CellMapTable {
	public static List<string> Lines(CellMap map, int? onlyTitleSet = null) {
		var rows = Enumerable.Range(0, map.Cells.Count).Where(i => onlyTitleSet is null || map.Cells[i].TitleSet == onlyTitleSet).ToList();
		var columns = map.Titles.Where(t => t.Cells.Any(rows.Contains)).ToList();
		var lines = new List<string> {
			$"{rows.Count} cells, {columns.Count} titles. # = cell number (for 'mkv … cells:'); entry = position in the title's playlist, * = chapter starts, . = not used.",
			"",
		};
		var sb = new System.Text.StringBuilder();
		sb.Append($"  {"#",4} {"vts",3} {"cell",-6} {"sectors",-15} {"length",8} ");
		foreach (var c in columns) sb.Append($"|{("t" + c.TitleNumber),4}");
		lines.Add(sb.ToString());
		int lastVts = 0;
		foreach (int row in rows) {
			var cell = map.Cells[row];
			if (cell.TitleSet != lastVts && lastVts != 0) {
				sb.Clear().Append($"  {new string('-', 40)}");
				foreach (var _ in columns) sb.Append("+----");
				lines.Add(sb.ToString());
			}
			lastVts = cell.TitleSet;
			string length = TimeSpan.FromTicks(cell.DurationNs / 100).ToString(@"m\:ss\.f");
			sb.Clear().Append($"  {row + 1,4} {cell.TitleSet,3} {$"{cell.VobId}/{cell.CellId}",-6} {$"{cell.FirstSector}-{cell.LastSector}",-15} {length,8} ");
			foreach (var col in columns) {
				var positions = col.Cells.Select((c, i) => (c, i)).Where(x => x.c == row).Select(x => x.i).ToList();
				string entry = positions.Count == 0 ? "." : string.Join(",", positions.Select(i => $"{i + 1}{(col.ChapterStarts.Contains(i) ? "*" : "")}"));
				sb.Append($"|{entry,4}");
			}
			lines.Add(sb.ToString());
		}
		return lines;
	}
}
