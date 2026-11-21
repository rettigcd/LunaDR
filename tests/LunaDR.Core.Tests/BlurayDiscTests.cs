using LunaDR.Core.Bluray;
using LunaDR.Core.Ripping;

namespace LunaDR.Core.Tests;

/// <summary>
/// A test on a real Blu-ray's metadata (BDMV/PLAYLIST, CLIPINF, index.bdmv; AACS/*.inf), copied to reference/bdmv/&lt;disc&gt;/ (git-ignored: commercial discs).
/// Skipped when that folder is missing.
/// </summary>
public sealed class BdFactAttribute : FactAttribute {
	public BdFactAttribute(string disc) {
		if (BdDisc.Folder(disc) is null) Skip = $"reference/bdmv/{disc} not found (copy the disc's BDMV metadata and AACS files there to run this test)";
	}
}

internal static class BdDisc {
	public static string? Folder(string disc) {
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) {
			if (!File.Exists(Path.Combine(dir.FullName, "LunaDR.slnx"))) continue;
			string folder = Path.Combine(dir.FullName, "reference", "bdmv", disc);
			return BdScanner.IsBluray(folder) ? folder : null;
		}
		return null;
	}
}

/// <summary>The Goonies (Blu-ray, 2008): VC-1 video, AC3 + TrueHD audio, PGS subtitles; six playlists of the movie over two clips (log 2026-11-19).</summary>
public class GooniesBdTests {
	private const string Disc = "THE_GOONIES";
	private static string Folder => BdDisc.Folder(Disc)!;

	[BdFact(Disc)]
	public void TitleList_PlaylistsWithDuplicates() {
		var disc = BdScanner.Scan(Folder);
		Assert.Equal("THE_GOONIES", disc.VolumeName);
		Assert.Equal(24, disc.Titles.Count);
		var movie = disc.Titles.Single(t => t.Playlist == 0);
		Assert.Equal("1:53:55", TimeSpan.FromTicks(movie.DurationNs / 100).ToString(@"h\:mm\:ss"));
		Assert.Equal("VC-1 1080p 23.976", movie.Video);
		Assert.Equal(["AC3 multi-channel eng", "TrueHD multi-channel eng", "AC3 stereo eng", "AC3 stereo fra", "AC3 mono spa", "AC3 stereo eng"], movie.Audio);
		Assert.Equal(["00113"], movie.Clips);
		Assert.Null(movie.DuplicateOf);
		Assert.Equal(0, disc.Titles.Single(t => t.Playlist == 26).DuplicateOf);   // same clip, same in/out times
		Assert.Equal(8, disc.Titles.Single(t => t.Playlist == 16).DuplicateOf);
	}

	[BdFact(Disc)]
	public void Playlist_ItemTimesAndChapters() {
		var mpls = BdScanner.LoadPlaylist(Folder, 0);
		var item = Assert.Single(mpls.Items);
		Assert.Equal((524280u, 308114062u), (item.InTime, item.OutTime));   // 45 kHz ticks
		Assert.Equal(0x1011, item.Video.Single().Pid);
		Assert.Equal([0x1100, 0x1101, 0x1102, 0x1103, 0x1104, 0x1106], item.Audio.Select(a => a.Pid));
		var chapters = mpls.ChapterStartsNs();
		Assert.Equal(0, chapters[0]);
		Assert.True(chapters.Zip(chapters.Skip(1)).All(p => p.First < p.Second));
		Assert.True(chapters[^1] < mpls.DurationNs);
	}

	[BdFact(Disc)]
	public void TrueHdCore_IsATrackUnlessTurnedOff() {
		var ripper = new BdTitleRipper(Folder, 0);
		string[] Audio(RipOptions options) => ripper.Describe(options).Where(l => l.StartsWith("audio")).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]).ToArray();
		// Default: the TrueHD track is followed by its AC3 core.
		Assert.Equal(["A_AC3", "A_TRUEHD", "A_AC3", "A_AC3", "A_AC3", "A_AC3", "A_AC3"], Audio(RipOptions.Default));
		Assert.Equal(["A_AC3", "A_TRUEHD", "A_AC3", "A_AC3", "A_AC3", "A_AC3"], Audio(new RipOptions { TrueHdCore = false }));
	}

	[BdFact(Disc)]
	public void SegmentMap_SamePlaylistsShareSegments() {
		var disc = BdScanner.Scan(Folder);
		var map = disc.Segments!;
		Assert.True(map.Bluray);
		Assert.Equal(disc.Titles.Count, map.Titles.Count);
		var movie = map.Titles.Single(t => t.TitleNumber == 0);
		Assert.Contains(map.Titles[0].TitleNumber, new[] { 0, 26, 31 });               // rows longest first, as in the title list
		Assert.Equal(37, movie.ChapterStarts.Count);                                    // chapters start at segment boundaries
		Assert.All(movie.Cells, c => Assert.Equal("00113", map.Cells[c].Clip));
		Assert.Equal(movie.Cells, map.Titles.Single(t => t.TitleNumber == 26).Cells);   // the duplicate plays the same segments
		long length = movie.Cells.Sum(c => map.Cells[c].DurationNs);
		Assert.InRange(length, disc.Titles.Single(t => t.Playlist == 0).DurationNs - 1_000_000, disc.Titles.Single(t => t.Playlist == 0).DurationNs + 1_000_000);
	}

	[Fact]
	public void SegmentMap_CutsClipsWherePlaylistsAndChaptersStart() {
		BdStream[] none = [];
		BdPlayItem Item(string clip, uint inTime, uint outTime) => new(clip, inTime, outTime, 1, 1, none, none, none);
		// Playlist 1 plays all of clip 00001 (20 s) with a chapter at 10 s; playlist 2 plays its second half, then clip 00002.
		var p1 = new Mpls { Name = "00001", Items = [Item("00001", 0, 900_000)], Marks = [new(1, 0, 0), new(1, 0, 450_000)] };
		var p2 = new Mpls { Name = "00002", Items = [Item("00001", 450_000, 900_000), Item("00002", 0, 90_000)], Marks = [new(1, 0, 450_000), new(1, 1, 0)] };
		// Playlists 3 and 4 play two parts of clip 00003 with a gap between them that nothing plays.
		var p3 = new Mpls { Name = "00003", Items = [Item("00003", 0, 45_000)], Marks = [] };
		var p4 = new Mpls { Name = "00004", Items = [Item("00003", 90_000, 135_000)], Marks = [] };
		var map = BdSegmentMap.Build([(1, p1), (2, p2), (3, p3), (4, p4)]);

		Assert.Equal(["00001/1", "00001/2", "00002/1", "00003/1", "00003/2"], map.Cells.Select(c => c.Label));
		Assert.Equal(10_000_000_000, map.Cells[0].DurationNs);
		Assert.Equal((90_000u, 135_000u), (map.Cells[4].ClipIn, map.Cells[4].ClipOut));
		Assert.Equal([0, 1], map.Titles[0].Cells);
		Assert.Equal([0, 1], map.Titles[0].ChapterStarts.Order());
		Assert.Equal([1, 2], map.Titles[1].Cells);
		Assert.Equal([0, 1], map.Titles[1].ChapterStarts.Order());
		Assert.Equal([3], map.Titles[2].Cells);
		Assert.Equal([4], map.Titles[3].Cells);
		Assert.Equal([1, 2], map.TitlesUsing(1));
	}

	[Fact]
	public void ChapterStarts_MarkOnLaterItem_AddsEarlierItemsLength() {
		BdStream[] none = [];
		var mpls = new Mpls {
			Name = "test",
			Items = [new("00001", 45_000, 135_000, 1, 1, none, none, none), new("00002", 90_000, 540_000, 5, 1, none, none, none)],
			Marks = [new(1, 0, 45_000), new(1, 1, 135_000), new(2, 1, 150_000), new(1, 1, 540_000)],
		};
		// Item 1 lasts 2 s; the mark 1 s into item 2 is at 3 s; the link point (type 2) and the mark at the very end are not chapters.
		Assert.Equal([0L, 3_000_000_000L], mpls.ChapterStartsNs());
		Assert.Equal(12_000_000_000L, mpls.DurationNs);
	}
}
