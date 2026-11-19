using RipDvd.Core.Ripping;

namespace RipDvd.Core.Tests;

/// <summary>
/// A test on a real disc's IFO files, copied to reference/ifo/&lt;disc&gt;/ (git-ignored: they come from commercial discs).
/// Skipped when that folder is missing, e.g. on another computer. To add a disc: copy its VIDEO_TS\*.IFO files there.
/// </summary>
public sealed class IfoFactAttribute : FactAttribute {
	public IfoFactAttribute(string disc) {
		if (IfoDisc.Folder(disc) is null) Skip = $"reference/ifo/{disc} not found (copy the disc's VIDEO_TS\\*.IFO files there to run this test)";
	}
}

internal static class IfoDisc {
	/// <summary>reference/ifo/&lt;disc&gt; in the repository (found by walking up from the test binaries to RipDvd.slnx), or null.</summary>
	public static string? Folder(string disc) {
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) {
			if (!File.Exists(Path.Combine(dir.FullName, "RipDvd.slnx"))) continue;
			string folder = Path.Combine(dir.FullName, "reference", "ifo", disc);
			return File.Exists(Path.Combine(folder, "VIDEO_TS.IFO")) ? folder : null;
		}
		return null;
	}
}

/// <summary>Gypsy (2004) vol. 1: one title with 16-bit LPCM stereo audio and no language, no volume label, no CSS (log 12:34).</summary>
public class GypsyTests {
	private const string Disc = "GYPSY_VOL1";
	private static string Folder => IfoDisc.Folder(Disc)!;

	[IfoFact(Disc)]
	public void OneTitle_WithLpcmAudio() {
		var title = Assert.Single(DiscScanner.Scan(Folder).Titles);
		Assert.Equal(11, title.Chapters);
		Assert.Equal(["LPCM stereo und"], title.Audio);
		var description = new TitleRipper(Folder, 1).Describe();
		// MakeMKV converts LPCM to little-endian PCM and writes no Language when the disc gives none.
		Assert.Contains(description, l => l.StartsWith("audio") && l.Contains("A_PCM/INT/LIT") && l.Contains("(no language)"));
		Assert.Contains("chapters  11", description);
	}
}

/// <summary>Beauty and the Beast (Disney): titles 16-18 have two interleaved angles (log 2026-11-18 22:22).</summary>
public class BeautyAndTheBeastTests {
	private const string Disc = "BEAUTY_AND_THE_BEAST";
	private static string Folder => IfoDisc.Folder(Disc)!;

	[IfoFact(Disc)]
	public void Title16_SizeCountsOneAngle() {
		// Listed as 4.83 GB (title 17: 5.64 GB) while both angles were counted; MakeMKV says 3.69 GB of disc data for angle 1.
		var title16 = DiscScanner.Scan(Folder).Titles.Single(t => t.TitleNumber == 16);
		Assert.InRange(title16.SizeBytes, 3.5e9, 3.9e9);
	}

	[IfoFact(Disc)]
	public void Title16_ChaptersEndWithTheTitle() {
		// Chapters ran to 1:59 in the 1:31 title while the second angle's cells were counted.
		var description = new TitleRipper(Folder, 16).Describe();
		Assert.Contains("chapters  23", description);
		var title16 = DiscScanner.Scan(Folder).Titles.Single(t => t.TitleNumber == 16);
		Assert.InRange(title16.DurationNs, 5_400_000_000_000, 5_480_000_000_000);   // 1:31

		var vmg = Ifo.VmgIfo.Parse(File.ReadAllBytes(Path.Combine(Folder, "VIDEO_TS.IFO")));
		var entry = vmg.Titles[16 - 1];
		var vts = Ifo.VtsIfo.Parse(File.ReadAllBytes(Path.Combine(Folder, $"VTS_{entry.TitleSetNumber:00}_0.IFO")), entry.TitleSetNumber);
		var pgc = vts.Pgcs[vts.Chapters[entry.TitleSetTitleNumber - 1][0].PgcNumber - 1];
		var chapters = TitleRipper.PgcChapters(pgc);
		Assert.Equal(title16.DurationNs, chapters[^1].EndNs);
		// MakeMKV's chapter 2 starts at 167.167 s (B1: later chapters differ from MakeMKV's by up to 0.1 s).
		Assert.Equal(167_167, chapters[1].StartNs / 1_000_000);
	}

	[IfoFact(Disc)]
	public void Title16_Tracks() {
		// As MakeMKV: video, 3 AC3 (eng 5.1, fre 5.1, eng stereo), 2 VobSub, closed captions.
		var description = new TitleRipper(Folder, 16).Describe();
		Assert.Equal(3, description.Count(l => l.StartsWith("audio") && l.Contains("A_AC3")));
		Assert.Equal(2, description.Count(l => l.Contains("S_VOBSUB")));
		Assert.Single(description, l => l.Contains("S_TEXT/UTF8"));
	}

	[IfoFact(Disc)]
	public void CellMap_AndDuplicates() {
		var disc = DiscScanner.Scan(Folder);
		Assert.Equal(158, disc.CellMap.Cells.Count);
		Assert.Equal(17, disc.Titles.Single(t => t.TitleNumber == 18).DuplicateOf);
	}

	[IfoFact(Disc)]
	public void Cells32And45_AreTitle5() {
		// G5 test (log 22:22): ripping cells 32,45 gave a file identical to title 5.
		var map = CellMap.Load(Folder);
		Assert.Equal([31, 44], map.Titles.Single(t => t.TitleNumber == 5).Cells);   // 0-based
		Assert.Equal(TitleRipper.ForCells(Folder, [32, 45]).Describe(), new TitleRipper(Folder, 5).Describe());
	}
}
