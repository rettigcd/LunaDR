using RipDvd.Core.Dvd;
using RipDvd.Core.Ripping;

namespace RipDvd.Core.Tests;

/// <summary>Disc-free logic: cell selection, naming, languages, duplicate/compilation detection, nav packs.</summary>
public class LogicTests {
	[Theory]
	[InlineData("23", new[] { 23 })]
	[InlineData("23-25,30", new[] { 23, 24, 25, 30 })]
	[InlineData("27-25", new[] { 27, 26, 25 })]
	[InlineData(" 5 , 7 ", new[] { 5, 7 })]
	public void CellSelection_Parse(string text, int[] cells) => Assert.Equal(cells, CellSelection.Parse(text));

	[Theory]
	[InlineData("")]
	[InlineData("x")]
	[InlineData("3-x")]
	public void CellSelection_ParseRejectsBadText(string text) => Assert.Throws<FormatException>(() => CellSelection.Parse(text));

	[Theory]
	[InlineData(new[] { 23, 24, 25, 30 }, "23-25,30")]
	[InlineData(new[] { 32, 45 }, "32,45")]
	[InlineData(new[] { 1, 2 }, "1,2")]   // two in a row stay a list
	[InlineData(new[] { 5, 4, 3 }, "5,4,3")]
	public void CellSelection_Format(int[] cells, string text) => Assert.Equal(text, CellSelection.Format(cells));

	// Title set 1: cells 1-3; title set 2: cells 4-5. Title 1 plays 1,2,3; title 2 plays 4,5.
	private static CellMap SmallMap() => new() {
		Cells = [
			new(1, 1, 1, 0, 99, 60_000_000_000), new(1, 1, 2, 100, 199, 60_000_000_000), new(1, 1, 3, 200, 299, 60_000_000_000),
			new(2, 1, 1, 0, 99, 60_000_000_000), new(2, 1, 2, 100, 199, 60_000_000_000),
		],
		Titles = [new(1, [0, 1, 2], new HashSet<int> { 0 }), new(2, [3, 4], new HashSet<int> { 0 })],
	};

	[Fact]
	public void CellSelection_Validate() {
		var map = SmallMap();
		Assert.Null(CellSelection.Validate(map, [3, 1]));
		Assert.Contains("different title sets", CellSelection.Validate(map, [3, 4]));
		Assert.Contains("No cell 6", CellSelection.Validate(map, [6]));
		Assert.Contains("more than once", CellSelection.Validate(map, [2, 2]));
		Assert.Equal(200, CellSelection.Sectors(map, [1, 2]));
	}

	[Theory]
	[InlineData("DEAD_ZONE_S4_D1", "Dead Zone S4 D1")]
	[InlineData("BUTTERFLY_EFFECT", "Butterfly Effect")]
	[InlineData("A_VERY_LONG_ENGAGEMENT_DISC1", "A Very Long Engagement DISC1")]
	[InlineData("Beauty and the Beast", "Beauty and the Beast")]
	[InlineData("", "DVD")]
	public void DiscName_FromVolumeLabel(string label, string name) => Assert.Equal(name, OutputNaming.DiscName(label));

	[Fact]
	public void FileNames() {
		Assert.Equal("Dead Zone S4 D1 - title 01.mkv", OutputNaming.FileName("Dead Zone S4 D1", 1));
		Assert.Equal("Disc - cells 23-25,30.mkv", OutputNaming.CellsFileName("Disc", [23, 24, 25, 30]));
		Assert.Equal("What_ Why_.mkv", OutputNaming.Sanitize("What? Why*.mkv"));
	}

	[Theory]
	[InlineData("en", "eng")]
	[InlineData("fr", "fre")]
	[InlineData(null, "und")]
	[InlineData("zz", "und")]
	public void Languages_TwoToThreeLetters(string? two, string three) => Assert.Equal(three, Languages.ToIso6392(two));

	private static CellInfo Cell(int n, long seconds = 60) => new(1, 1, n, (uint)(n * 100), (uint)(n * 100 + 99), seconds * 1_000_000_000);

	[Fact]
	public void Overlaps_DuplicateAndCompilation() {
		// Like Dead Zone and Butterfly Effect: titles 1 and 2 play the same cells (2 has other chapters);
		// title 5 is a "play all" of titles 3 and 4; a 0.5 s end cell shared by all is ignored.
		var cells = new List<CellInfo> { Cell(0), Cell(1), Cell(2), Cell(3), Cell(4, seconds: 0) };
		var map = new CellMap {
			Cells = cells,
			Titles = [
				new(1, [0, 1, 4], new HashSet<int> { 0 }),
				new(2, [0, 1, 4], new HashSet<int> { 0, 1 }),
				new(3, [2, 4], new HashSet<int> { 0 }),
				new(4, [3, 4], new HashSet<int> { 0 }),
				new(5, [2, 3, 4], new HashSet<int> { 0, 1 }),
			],
		};
		var overlaps = map.ClassifyOverlaps();
		Assert.Equal((null, null), (overlaps[1].DuplicateOf, overlaps[1].CompilationOf));
		Assert.Equal(1, overlaps[2].DuplicateOf);
		Assert.Null(overlaps[3].CompilationOf);   // an episode inside a "play all" is not a compilation
		Assert.Equal([3, 4], overlaps[5].CompilationOf!);
	}

	[Fact]
	public void NavPack_ParsesPciAndDsi() {
		var sector = TestData.NavPack(865, 145, 2, 1, ilvuCategory: 0x6000, ilvuEnd: 370, nextIlvu: 733, startPtm: 1000, endPtm: 4003);
		Assert.True(NavPack.IsNavPack(sector));
		var nav = NavPack.Parse(sector);
		Assert.Equal((865u, 145u, 2, 1), (nav.Lbn, nav.VobuEndAddress, nav.VobId, nav.CellId));
		Assert.Equal((1000u, 4003u), (nav.StartPtm, nav.EndPtm));
		Assert.True(nav.InIlvu);
		Assert.Equal((370u, 733u), (nav.IlvuEndAddress, nav.NextIlvuStart));
		Assert.False(NavPack.IsNavPack(new byte[TestData.SectorSize]));
	}
}
