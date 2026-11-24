using LunaDR.Core.Bluray;
using LunaDR.Core.Ripping;

namespace LunaDR.Core.Tests;

/// <summary>The "time left" correction for a Blu-ray rip's usual speed curve, and the disc-layout note of the progress log.</summary>
public class SpeedCurveTests {
	private const long Total = 20_000_000;   // sectors
	private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

	[Fact]
	public void TheCurve_AveragesOne() {
		Assert.Equal(1.0, SpeedCurve.Integral(0, 1), 2);
		Assert.Equal(SpeedCurve.Bins[0] / 20, SpeedCurve.Integral(0, 0.05), 6);
		Assert.Equal(SpeedCurve.Integral(0.2, 0.5), SpeedCurve.Integral(0.2, 0.3) + SpeedCurve.Integral(0.3, 0.5), 9);
	}

	[Fact]
	public void NoCorrection_WhileNothingIsMeasured_OrAtTheEnd() {
		Assert.Equal(1, SpeedCurve.Factor(0, Total, 20_000, Window));
		Assert.Equal(1, SpeedCurve.Factor(1000, Total, 20_000, Window));   // a window covering less than half a percent
		Assert.Equal(1, SpeedCurve.Factor(Total, Total, 20_000, Window));
		Assert.Equal(1, SpeedCurve.Factor(Total / 2, Total, 0, Window));
		Assert.Null(SpeedCurve.Remaining(Total / 2, Total, 0, Window));
	}

	[Fact]
	public void EarlyTheTimeLeftIsShorterThanFlat_InTheSecondHalfLonger() {
		double sps = 20_000;   // a minute is 1.2 million sectors, 6 percent of the clip
		Assert.InRange(SpeedCurve.Factor((long)(Total * 0.10), Total, sps, Window), 0.6, 0.9);
		Assert.InRange(SpeedCurve.Factor((long)(Total * 0.70), Total, sps, Window), 1.05, 1.3);
		var remaining = SpeedCurve.Remaining(Total / 2, Total, sps, Window)!.Value;
		Assert.Equal(Total / 2 / sps * SpeedCurve.Factor(Total / 2, Total, sps, Window), remaining.TotalSeconds, 6);
	}

	[Fact]
	public void ARipThatFollowsTheCurveExactly_IsEstimatedWell() {
		double secondsPerUnit = 2000;   // time density 1 takes 2000 s per whole clip
		foreach (double f in new[] { 0.10, 0.25, 0.40, 0.55, 0.70, 0.85 }) {
			// The window (60 s) ends at f; find how much of the clip it covers.
			double lo = 0, hi = f;
			for (int i = 0; i < 60; i++) {
				double span = (lo + hi) / 2;
				if (secondsPerUnit * SpeedCurve.Integral(f - span, f) > Window.TotalSeconds) hi = span; else lo = span;
			}
			double sps = lo * Total / Window.TotalSeconds;
			double truth = secondsPerUnit * SpeedCurve.Integral(f, 1);
			double estimate = SpeedCurve.Remaining((long)(f * Total), Total, sps, Window)!.Value.TotalSeconds;
			Assert.InRange(estimate / truth, 0.97, 1.03);
		}
	}

	[Fact]
	public void TheCorrection_IsLimited() {
		Assert.InRange(SpeedCurve.Factor((long)(Total * 0.5), Total, 20_000, Window), SpeedCurve.MinFactor, SpeedCurve.MaxFactor);
	}

	[Fact]
	public void DiscLayout_DescribesAFile_EvenWhereExtentsAreUnknown() {
		string path = Path.Combine(Path.GetTempPath(), "layout-" + Guid.NewGuid().ToString("N") + ".m2ts");
		File.WriteAllBytes(path, new byte[1_000_000]);
		try {
			string line = DiscLayout.Describe(Path.GetTempPath(), path);
			Assert.StartsWith("disc layout: volume ", line);
			Assert.Contains(Path.GetFileName(path), line);
			Assert.Contains("extents (file sector:disc sector:length) ", line);
			var extents = DiscLayout.Extents(path);
			if (OperatingSystem.IsWindows() && string.Equals(new DriveInfo(Path.GetPathRoot(path)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) {
				Assert.NotNull(extents);   // a file of 1 MB is stored outside the file table, so it has at least one extent
				Assert.True(extents.Count >= 1);
			}
			if (extents is { Count: > 0 }) Assert.All(extents, e => Assert.True(e.Sectors > 0 && e.DiscSector >= 0));
		} finally {
			File.Delete(path);
		}
	}
}
