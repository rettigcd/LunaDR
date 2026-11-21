using LunaDR.Core.Ripping;

namespace LunaDR.Core.Bluray;

/// <summary>
/// The Blu-ray version of the DVD cell map (<see cref="CellMap"/>): which playlists play which parts of which clips.
/// A Blu-ray has no cells; a play item names a clip and an in and out time on it, and two playlists may use different parts of one clip.
/// So each clip is cut at every time where some playlist's play item starts or ends, or a chapter starts, and each piece (a "segment") is a cell of the map.
/// Two playlists that share a segment play the same video there, as two DVD titles that share a cell do.
/// Segments are ordered by clip, then by time; parts of a clip no playlist plays are left out.
/// Multi-angle play items count with their first angle's clip only (as when ripping).
/// </summary>
public static class BdSegmentMap {
	/// <summary>Builds the map; the playlists become the map's rows in the order given.</summary>
	public static CellMap Build(IEnumerable<(int Number, Mpls Playlist)> playlists) {
		var list = playlists.ToList();
		// Chapter starts of each playlist, per play item, as clip times.
		var chapters = list.Select(p => p.Playlist.EntryMarks().ToLookup(m => m.Item, m => m.ClipTime)).ToList();

		// Cut points of each clip, and the parts that some play item covers.
		var cuts = new Dictionary<string, SortedSet<uint>>();
		var covered = new Dictionary<string, List<(uint In, uint Out)>>();
		for (int k = 0; k < list.Count; k++) {
			var items = list[k].Playlist.Items;
			for (int i = 0; i < items.Count; i++) {
				var item = items[i];
				if (item.OutTime <= item.InTime) continue;
				if (!cuts.TryGetValue(item.ClipId, out var set)) {
					cuts[item.ClipId] = set = [];
					covered[item.ClipId] = [];
				}
				set.Add(item.InTime);
				set.Add(item.OutTime);
				covered[item.ClipId].Add((item.InTime, item.OutTime));
				foreach (uint t in chapters[k][i])
					if (t > item.InTime && t < item.OutTime) set.Add(t);
			}
		}

		var cells = new List<CellInfo>();
		var firstOfClip = new Dictionary<string, int>();
		foreach (string clip in cuts.Keys.Order(StringComparer.Ordinal)) {
			var times = cuts[clip].ToList();
			int group = int.TryParse(clip, out int n) ? n : 0;
			firstOfClip[clip] = cells.Count;
			int number = 0;
			for (int j = 0; j + 1 < times.Count; j++) {
				uint a = times[j], b = times[j + 1];
				if (!covered[clip].Any(c => c.In <= a && b <= c.Out)) continue;
				cells.Add(new CellInfo(group, 0, ++number, 0, 0, (long)(b - a) * 1_000_000_000 / 45_000) { Clip = clip, ClipIn = a, ClipOut = b });
			}
		}

		var titles = new List<TitlePlaylist>();
		for (int k = 0; k < list.Count; k++) {
			var items = list[k].Playlist.Items;
			var sequence = new List<int>();
			var chapterStarts = new HashSet<int>();
			for (int i = 0; i < items.Count; i++) {
				var item = items[i];
				if (item.OutTime <= item.InTime) continue;
				var marks = chapters[k][i].ToHashSet();
				for (int c = firstOfClip[item.ClipId]; c < cells.Count && cells[c].Clip == item.ClipId; c++) {
					var cell = cells[c];
					if (cell.ClipIn < item.InTime || cell.ClipOut > item.OutTime) continue;
					if (sequence.Count == 0 || marks.Contains(cell.ClipIn)) chapterStarts.Add(sequence.Count);
					sequence.Add(c);
				}
			}
			titles.Add(new TitlePlaylist(list[k].Number, sequence, chapterStarts));
		}
		return new CellMap { Cells = cells, Titles = titles, Bluray = true };
	}
}
