using RipDvd.Core.Ifo;

namespace RipDvd.Core.Ripping;

/// <summary>
/// One physical cell of a title set: a run of sectors in that set's VOBs.
/// On a Blu-ray (<see cref="Bluray.BdSegmentMap"/>) it is a segment of a clip instead: <see cref="Clip"/> from <see cref="ClipIn"/> to
/// <see cref="ClipOut"/>; TitleSet is then the clip number, CellId the segment's number within the clip, and the sectors are 0.
/// </summary>
public sealed record CellInfo(int TitleSet, int VobId, int CellId, uint FirstSector, uint LastSector, long DurationNs) {
	/// <summary>Blu-ray: the clip (e.g. "00113") this segment is part of; null on a DVD.</summary>
	public string? Clip { get; init; }

	/// <summary>Blu-ray: start and end of the segment on the clip's timeline (45 kHz ticks).</summary>
	public uint ClipIn { get; init; }
	public uint ClipOut { get; init; }

	public string Label => Clip is null ? $"{TitleSet}:{VobId}/{CellId}" : $"{Clip}/{CellId}";

	/// <summary>The group the cell is drawn in: its title set (DVD) or clip (Blu-ray).</summary>
	public string Group => Clip ?? TitleSet.ToString();
}

/// <summary>A title's playlist: indexes into <see cref="CellMap.Cells"/> in playback order, and which playlist positions start a chapter.</summary>
public sealed record TitlePlaylist(int TitleNumber, IReadOnlyList<int> Cells, IReadOnlySet<int> ChapterStarts);

/// <summary>
/// Which titles play which cells, for the whole disc: the data behind the probe's "cellmap" table and the GUI's cell map.
/// Cells are ordered by title set, then by position on the disc.
/// </summary>
public sealed class CellMap {
	public required IReadOnlyList<CellInfo> Cells { get; init; }
	public required IReadOnlyList<TitlePlaylist> Titles { get; init; }

	/// <summary>A Blu-ray's segment map: titles are playlists and cells are clip segments (see <see cref="Bluray.BdSegmentMap"/>).</summary>
	public bool Bluray { get; init; }

	/// <summary>Cells shorter than this (e.g. the 0.5 s end cells many titles share) are ignored when comparing titles.</summary>
	public const long SignificantCellNs = 2_000_000_000;

	/// <summary>The titles that play the cell at <paramref name="cellIndex"/>.</summary>
	public IEnumerable<int> TitlesUsing(int cellIndex) => Titles.Where(t => t.Cells.Contains(cellIndex)).Select(t => t.TitleNumber);

	/// <summary>Reads the IFO files of a VIDEO_TS folder and builds the map.</summary>
	public static CellMap Load(string videoTsFolder) {
		var vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTsFolder, "VIDEO_TS.IFO")));
		var cache = new Dictionary<int, VtsIfo>();
		return Build(vmg, n => cache.TryGetValue(n, out var v) ? v
			: cache[n] = VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTsFolder, $"VTS_{n:00}_0.IFO")), n));
	}

	/// <summary>Builds the map from parsed IFO files. Angle blocks: only the first angle's cells are part of a title (as when ripping).</summary>
	public static CellMap Build(VmgIfo vmg, Func<int, VtsIfo> titleSet) {
		var cellIndex = new Dictionary<(int Vts, int Vob, int Cell), int>();
		var cells = new List<CellInfo>();
		var raw = new List<(int Title, List<(int, int, int)> Playlist, HashSet<int> ChapterStarts)>();

		foreach (var t in vmg.Titles) {
			var vts = titleSet(t.TitleSetNumber);
			var chapters = vts.Chapters[t.TitleSetTitleNumber - 1];
			var playlist = new List<(int, int, int)>();
			var chapterStarts = new HashSet<int>();
			foreach (int pgcNumber in chapters.Select(c => c.PgcNumber).Distinct()) {
				var pgc = vts.Pgcs[pgcNumber - 1];
				// Position in the playlist of each PGC cell (cells of other angles are left out).
				var positionOf = new Dictionary<int, int>();
				for (int c = 0; c < pgc.Cells.Count; c++) {
					var cell = pgc.Cells[c];
					if (cell.BlockType == 1 && cell.BlockMode != 1) continue;
					positionOf[c + 1] = playlist.Count;
					var key = (t.TitleSetNumber, cell.VobId, cell.CellId);
					playlist.Add(key);
					if (!cellIndex.ContainsKey(key)) {
						cellIndex[key] = -1;
						cells.Add(new CellInfo(t.TitleSetNumber, cell.VobId, cell.CellId, cell.FirstSector, cell.LastSector, cell.PlaybackTime.ToNanoseconds()));
					}
				}
				foreach (var ch in chapters.Where(ch => ch.PgcNumber == pgcNumber)) {
					int entryCell = pgc.ProgramEntryCells[ch.ProgramNumber - 1];
					if (positionOf.TryGetValue(entryCell, out int position)) chapterStarts.Add(position);
				}
			}
			raw.Add((t.TitleNumber, playlist, chapterStarts));
		}

		// Order the cells by title set, then disc position, and index them.
		var ordered = cells.OrderBy(c => c.TitleSet).ThenBy(c => c.FirstSector).ThenBy(c => c.VobId).ThenBy(c => c.CellId).ToList();
		for (int i = 0; i < ordered.Count; i++) cellIndex[(ordered[i].TitleSet, ordered[i].VobId, ordered[i].CellId)] = i;

		var titles = raw.OrderBy(r => r.Title)
			.Select(r => new TitlePlaylist(r.Title, r.Playlist.Select(k => cellIndex[k]).ToList(), r.ChapterStarts))
			.ToList();
		return new CellMap { Cells = ordered, Titles = titles };
	}

	/// <summary>The cells of a title that count when comparing titles (at least <see cref="SignificantCellNs"/> long), in playlist order.</summary>
	public List<int> SignificantCells(TitlePlaylist title) => title.Cells.Where(c => Cells[c].DurationNs >= SignificantCellNs).ToList();

	/// <summary>
	/// For each title: the earlier title it duplicates, or the titles it is a compilation of (see <see cref="DiscScanner"/> for the rules).
	///   * Duplicate: exactly the same significant cells in the same order as an earlier title (the chapters may differ).
	///   * Compilation: every significant cell also belongs to other titles, taken from at least two of them, and no single other title holds them all
	///     (a "play all" of episodes or extras; an episode inside a "play all" title is not a compilation, since it uses one other title).
	/// </summary>
	public Dictionary<int, (int? DuplicateOf, IReadOnlyList<int>? CompilationOf)> ClassifyOverlaps() {
		var significant = Titles.Select(SignificantCells).ToList();
		var result = new Dictionary<int, (int?, IReadOnlyList<int>?)>();
		for (int i = 0; i < Titles.Count; i++) {
			var mine = significant[i];
			int? duplicateOf = null;
			IReadOnlyList<int>? compilationOf = null;
			for (int j = 0; j < i && duplicateOf is null; j++)
				if (mine.Count > 0 && mine.SequenceEqual(significant[j])) duplicateOf = Titles[j].TitleNumber;

			if (duplicateOf is null && mine.Count > 0) {
				var set = mine.ToHashSet();
				var sources = Enumerable.Range(0, Titles.Count)
					.Where(j => j != i && significant[j].Any(set.Contains) && !significant[j].SequenceEqual(mine))
					.ToList();
				bool allCovered = set.All(c => sources.Any(j => significant[j].Contains(c)));
				bool insideOne = sources.Any(j => set.IsSubsetOf(significant[j]));
				if (allCovered && !insideOne && sources.Count >= 2) compilationOf = sources.Select(j => Titles[j].TitleNumber).ToList();
			}
			result[Titles[i].TitleNumber] = (duplicateOf, compilationOf);
		}
		return result;
	}
}
