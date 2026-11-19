using RipDvd.Core.Dvd;

namespace RipDvd.Core.Tests;

/// <summary>
/// <see cref="TitleSectorReader"/>: which sectors a rip reads.
/// Regression: Beauty and the Beast (log 2026-11-18 22:22) played every piece twice because interleaved angle cells were read straight through.
/// </summary>
public class SectorReaderTests {
	// Two angles interleaved in one range, units (ILVUs) of 6 sectors, each two VOBUs of 3 sectors:
	//   0-5 angle 1 (cell 2/1) | 6-11 angle 2 (cell 3/1) | 12-17 angle 1 | 18-23 angle 2
	private static FakeSectorSource InterleavedDisc(uint lastUnitNext = 0x7FFFFFFF, uint firstUnitNext = 9) {
		var disc = new FakeSectorSource(24);
		// Angle 1: first unit (0-5) → next unit at 12; second unit (12-17) is the last.
		disc.Set(0, TestData.NavPack(0, 2, 2, 1, ilvuCategory: 0x6000, ilvuEnd: 5, nextIlvu: 12));
		disc.Set(3, TestData.NavPack(3, 2, 2, 1, ilvuCategory: 0x5000, ilvuEnd: 2, nextIlvu: firstUnitNext));
		disc.Set(12, TestData.NavPack(12, 2, 2, 1, ilvuCategory: 0x6000, ilvuEnd: 5, nextIlvu: lastUnitNext));
		disc.Set(15, TestData.NavPack(15, 2, 2, 1, ilvuCategory: 0x5000, ilvuEnd: 2, nextIlvu: lastUnitNext));
		// Angle 2's units (never to be read for angle 1).
		disc.Set(6, TestData.NavPack(6, 2, 3, 1, ilvuCategory: 0x6000, ilvuEnd: 5, nextIlvu: 12));
		disc.Set(9, TestData.NavPack(9, 2, 3, 1, ilvuCategory: 0x5000, ilvuEnd: 2, nextIlvu: 9));
		disc.Set(18, TestData.NavPack(18, 2, 3, 1, ilvuCategory: 0x6000, ilvuEnd: 5, nextIlvu: 0x7FFFFFFF));
		disc.Set(21, TestData.NavPack(21, 2, 3, 1, ilvuCategory: 0x5000, ilvuEnd: 2, nextIlvu: 0x7FFFFFFF));
		return disc;
	}

	private static readonly Ifo.Cell Angle1 = TestData.Cell(2, 1, 0, 17, blockType: 1, blockMode: 1, interleaved: true);
	private static readonly Ifo.Cell Angle2 = TestData.Cell(3, 1, 6, 23, blockType: 1, blockMode: 3, interleaved: true);

	private static List<long> SectorNumbers(TitleSectorReader reader) => reader.ReadAll(sectorsPerRead: 4).Select(s => s.SectorNumber).ToList();

	[Fact]
	public void InterleavedAngleCell_ReadsOnlyItsOwnUnits() {
		var reader = new TitleSectorReader(InterleavedDisc(), [Angle1, Angle2], key: null);
		List<long> expected = [0, 1, 2, 3, 4, 5, 12, 13, 14, 15, 16, 17];
		Assert.Equal(expected, SectorNumbers(reader));
	}

	[Fact]
	public void InterleavedAngleCell_JumpIntoAnotherCell_IsAnError() {
		// A wrong "next unit" pointer (to angle 2's unit at 6) must stop the rip, not rip the other angle.
		var reader = new TitleSectorReader(InterleavedDisc(firstUnitNext: 3), [Angle1, Angle2], key: null);
		Assert.Throws<InvalidDataException>(() => SectorNumbers(reader));
	}

	[Fact]
	public void OtherAnglesCells_AreNotPlayed() {
		var played = TitleSectorReader.PlayedCells([Angle1, Angle2]);
		Assert.Equal([Angle1], played);
	}

	[Fact]
	public void SizeOfInterleavedAngleCells_CountsOneAngle() {
		// Title 17 of Beauty and the Beast was listed as 5.64 GB because both angles' units were counted.
		var normal = TestData.Cell(1, 1, 100, 199);   // 100 sectors
		Assert.Equal(100 + 18 / 2, TitleSectorReader.EstimateSectors([normal, Angle1, Angle2]));
	}

	// A normal cell of 30 sectors with a VOBU every 10 sectors (nav packs at 0, 10, 20).
	private static FakeSectorSource PlainDisc() {
		var disc = new FakeSectorSource(30);
		for (uint v = 0; v < 30; v += 10) disc.Set(v, TestData.NavPack(v, 9, 1, 1));
		return disc;
	}

	private static readonly Ifo.Cell Plain = TestData.Cell(1, 1, 0, 29);

	[Fact]
	public void BadSector_IsRetriedThenSkippedToTheNextVobu() {
		var disc = PlainDisc();
		disc.Bad.Add(13);
		var reader = new TitleSectorReader(disc, [Plain], key: null);
		var sectors = reader.ReadAll(sectorsPerRead: 8).Select(s => (s.SectorNumber, s.SkippedBefore)).ToList();

		Assert.Equal([.. Enumerable.Range(0, 13).Select(i => (long)i), .. Enumerable.Range(20, 10).Select(i => (long)i)], sectors.Select(s => s.SectorNumber));
		Assert.Equal(7, sectors.Single(s => s.SectorNumber == 20).SkippedBefore);   // 13..19
		Assert.Equal(1 + TitleSectorReader.ReadRetries, disc.SingleReads[13]);
		var skip = Assert.Single(reader.Skipped);
		Assert.Equal((13L, 7L), (skip.FirstSector, skip.Sectors));
	}

	[Fact]
	public void BadSectorsToTheEndOfTheCell_AreSkippedToTheEnd() {
		var disc = PlainDisc();
		disc.Bad.UnionWith([25, 26, 27, 28, 29]);
		var reader = new TitleSectorReader(disc, [Plain], key: null);
		Assert.Equal(25, reader.ReadAll().Count());
		Assert.Equal((25L, 5L), (reader.Skipped.Single().FirstSector, reader.Skipped.Single().Sectors));
	}

	[Fact]
	public void OtherReadErrors_StillFailTheRip() {
		// E.g. a locked drive ("the sector is encrypted"): skipping would crawl through the whole title, so it must fail instead.
		var disc = PlainDisc();
		disc.FailWith = new IOException("Copy protection error", unchecked((int)0x80070005));
		var reader = new TitleSectorReader(disc, [Plain], key: null);
		Assert.Throws<IOException>(() => reader.ReadAll().ToList());
	}

	[Fact]
	public void KeySearch_DiscSaysUnprotected_ReadsNothing() {
		// Regression (Gypsy, B2): on an unprotected disc the key search read up to 400,000 sectors (800 MB) before giving up,
		// minutes with "finding the title key" and no progress. When the disc's copyright descriptor says "no CSS", nothing may be read.
		var disc = new FakeSectorSource(400_000) { FailWith = new IOException("no read expected") };
		Assert.Null(TitleSectorReader.FindKey(disc, discProtected: false));
	}

	[Fact]
	public void KeySearch_Unknown_ScansAndFindsNoKeyOnUnscrambledSectors() {
		// A folder on a hard disk can't be asked; then the sectors are scanned (fast from a disk) and an unscrambled title gives no key.
		var disc = new FakeSectorSource(2_000);
		Assert.Null(TitleSectorReader.FindKey(disc, discProtected: null));
	}

	[Fact]
	public void BadSectorInAnInterleavedCell_SkipsTheRestOfThatVobuOnly() {
		var disc = InterleavedDisc();
		disc.Bad.Add(4);
		var reader = new TitleSectorReader(disc, [Angle1, Angle2], key: null);
		List<long> expected = [0, 1, 2, 3, 12, 13, 14, 15, 16, 17];
		Assert.Equal(expected, SectorNumbers(reader));
		Assert.Equal((4L, 2L), (reader.Skipped.Single().FirstSector, reader.Skipped.Single().Sectors));
	}
}
