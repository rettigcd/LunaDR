using RipDvd.Core.Dvd;
using RipDvd.Core.Ifo;

namespace DvdProbe;

/// <summary>Probe: measure title durations from the VOBU timestamps in the nav packs.</summary>
internal static class DurationProbe {
	public static void Measure(string videoTs, int titleNumber, bool perCell) {
		var vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO")));
		var t = vmg.Titles[titleNumber - 1];
		var vts = VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, $"VTS_{t.TitleSetNumber:00}_0.IFO")), t.TitleSetNumber);
		var pgcNumbers = vts.Chapters[t.TitleSetTitleNumber - 1].Select(c => c.PgcNumber).Distinct();
		var cells = pgcNumbers.SelectMany(n => vts.Pgcs[n - 1].Cells).ToList();

		using var reader = VobReader.ForTitleSet(videoTs, t.TitleSetNumber);
		var buf = new byte[VobReader.SectorSize];
		double totalVobuSum = 0, totalSpan = 0, totalIfo = 0;
		int totalVobus = 0;

		foreach (var cell in cells) {
			double vobuSum = 0;
			uint? first = null, last = null;
			int vobus = 0;
			long sector = cell.FirstSector;
			while (sector <= cell.LastSector) {
				reader.Read(sector, 1, buf);
				var nav = NavPack.Parse(buf);
				vobuSum += (nav.EndPtm - nav.StartPtm) / 90000.0;
				first ??= nav.StartPtm;
				last = nav.EndPtm;
				vobus++;
				sector += nav.VobuEndAddress + 1;
			}
			double span = ((last ?? 0) - (first ?? 0)) / 90000.0;
			double ifo = cell.PlaybackTime.ToTimeSpan().TotalSeconds;
			totalVobuSum += vobuSum; totalSpan += span; totalIfo += ifo; totalVobus += vobus;
			if (perCell)
				Console.WriteLine($"  cell {cell.Number,2}: {vobus,5} VOBUs  sum={vobuSum,9:F3}s  span={span,9:F3}s  " +
								  $"ifo={ifo,9:F3}s (nominal {cell.PlaybackTime.Nominal.TotalSeconds:F3})  diff(ifo-sum)={ifo - vobuSum,7:F3}");
		}

		Console.WriteLine($"Title {titleNumber}: {totalVobus} VOBUs  VOBU sum={Fmt(totalVobuSum)}  span sum={Fmt(totalSpan)}  IFO={Fmt(totalIfo)}");
	}

	private static string Fmt(double s) => $"{TimeSpan.FromSeconds(s):h\\:mm\\:ss\\.fff}";
}
