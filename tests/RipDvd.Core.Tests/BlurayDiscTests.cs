using RipDvd.Core.Bluray;
using RipDvd.Core.Ripping;

namespace RipDvd.Core.Tests;

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
			if (!File.Exists(Path.Combine(dir.FullName, "RipDvd.slnx"))) continue;
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
