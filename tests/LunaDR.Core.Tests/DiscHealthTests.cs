using LunaDR.Core.Bluray;

namespace LunaDR.Core.Tests;

/// <summary>The disc health scan: finding bad stretches, narrowing their edges, and the time estimate (the timing itself needs a real disc: probe `bdscan`).</summary>
public class DiscHealthTests {
	private const long Mb = 1_000_000;
	private static long SampleBytes => DiscHealthScanner.SampleBytes;

	private static HealthSample Good(long offset, double seconds = 1) => new(offset, 3.5, 90, seconds, false);

	private static HealthSample Bad(long offset, double seconds = 4) => new(offset, 0.5, 900, seconds, false);

	private static List<HealthSample> Row(string marks, long step = 100 * Mb) =>
		marks.Select((c, i) => c == '.' ? Good(i * step) : Bad(i * step)).ToList();

	[Fact]
	public void Bisect_FindsTheEdgeWithinOneSample_InTheExpectedNumberOfTests() {
		long edge = 23_470_000_000;
		int tests = 0;
		var (good, bad) = DiscHealthScanner.Bisect(23_300_000_000, 23_500_000_000, SampleBytes, offset => { tests++; return offset >= edge; });
		Assert.True(bad - good <= SampleBytes);
		Assert.InRange(edge, good, bad);
		Assert.True(tests <= DiscHealthScanner.BisectSteps(200 * Mb, SampleBytes));
	}

	[Fact]
	public void Bisect_WorksWhenTheGoodSideIsAfterTheBadSide() {
		long edge = 29_500_000_000;   // bad before it, good after it
		var (good, bad) = DiscHealthScanner.Bisect(29_600_000_000, 29_400_000_000, SampleBytes, offset => offset < edge);
		Assert.True(Math.Abs(bad - good) <= SampleBytes);
		Assert.InRange(edge, bad, good);
	}

	[Fact]
	public void BisectSteps_IsTheNumberOfHalvings() {
		Assert.Equal(0, DiscHealthScanner.BisectSteps(SampleBytes, SampleBytes));
		Assert.Equal(1, DiscHealthScanner.BisectSteps(SampleBytes * 2, SampleBytes));
		Assert.Equal(7, DiscHealthScanner.BisectSteps(100 * Mb, SampleBytes / 2 + 1));   // 100 MB down to a gap of about 0.8 MB
		Assert.Equal(6, DiscHealthScanner.BisectSteps(100 * Mb, SampleBytes));
	}

	[Fact]
	public void FindStretches_ReportsTheGoodSamplesAroundEachRun() {
		var stretches = DiscHealthScanner.FindStretches(Row("...XXX..X."));
		Assert.Equal(2, stretches.Count);
		Assert.Equal(new HealthStretch(200 * Mb, 300 * Mb, 500 * Mb, 600 * Mb), stretches[0]);
		Assert.Equal(new HealthStretch(700 * Mb, 800 * Mb, 800 * Mb, 900 * Mb), stretches[1]);
	}

	[Fact]
	public void FindStretches_RunsAtTheEndsHaveNoGoodSampleOnThatSide() {
		Assert.Equal(new HealthStretch(-1, 0, 100 * Mb, 200 * Mb), Assert.Single(DiscHealthScanner.FindStretches(Row("XX.."))));
		Assert.Equal(new HealthStretch(100 * Mb, 200 * Mb, 300 * Mb, -1), Assert.Single(DiscHealthScanner.FindStretches(Row("..XX"))));
		Assert.Equal(new HealthStretch(-1, 0, 200 * Mb, -1), Assert.Single(DiscHealthScanner.FindStretches(Row("XXX"))));
		Assert.Empty(DiscHealthScanner.FindStretches(Row("....")));
	}

	[Fact]
	public void FindStretches_JoinsRunsSeparatedByFewSlowButNotBadSamples() {
		HealthSample Slow(long offset) => new(offset, 2.0, 200, 2, false);   // slow (under 2.5 MB/s) but not bad (1.5 and over)
		var samples = new List<HealthSample> { Good(0), Bad(100 * Mb), Slow(200 * Mb), Bad(300 * Mb), Slow(400 * Mb), Slow(500 * Mb), Bad(600 * Mb), Good(700 * Mb) };
		var stretch = Assert.Single(DiscHealthScanner.FindStretches(samples));
		Assert.Equal(new HealthStretch(0, 100 * Mb, 600 * Mb, 700 * Mb), stretch);

		// Three slow samples in between are too many; a good sample (2.5 MB/s or more) in between always separates.
		var apart = new List<HealthSample> { Good(0), Bad(100 * Mb), Slow(200 * Mb), Slow(300 * Mb), Slow(400 * Mb), Bad(500 * Mb), Good(600 * Mb) };
		Assert.Equal(2, DiscHealthScanner.FindStretches(apart).Count);
		var separated = new List<HealthSample> { Good(0), Bad(100 * Mb), Good(200 * Mb), Bad(300 * Mb), Good(400 * Mb) };
		Assert.Equal(2, DiscHealthScanner.FindStretches(separated).Count);
	}

	[Fact]
	public void FindStretches_IgnoresABadFirstSample_SpinUp() {
		Assert.Empty(DiscHealthScanner.FindStretches(Row("X...")));
		Assert.Single(DiscHealthScanner.FindStretches(Row("XX..")));   // two in a row is not spin-up
	}

	[Fact]
	public void ReadErrorsAreBad() {
		Assert.True(new HealthSample(0, 0, 0, 1, Error: true).IsBad);
		Assert.True(Bad(0).IsBad);
		Assert.False(Good(0).IsBad);
	}

	[Fact]
	public void Edges_SkipEdgesAlreadyAsNarrowAsOneSample() {
		var stretches = new List<HealthStretch> { new(100 * Mb, 100 * Mb + SampleBytes, 300 * Mb, 400 * Mb) };
		Assert.Equal([(400 * Mb, 300 * Mb)], DiscHealthScanner.Edges(stretches, SampleBytes));
		Assert.Empty(DiscHealthScanner.Edges(stretches, 200 * Mb));   // a coarser resolution needs no refining at all
	}

	[Fact]
	public void EstimateRefine_IsStepsTimesTheMeanSampleTime_ForEachEdge() {
		var samples = Row("..XX..");   // good samples take 1 s, bad ones 4 s
		var stretches = DiscHealthScanner.FindStretches(samples);
		int steps = DiscHealthScanner.BisectSteps(100 * Mb, SampleBytes);
		var estimate = DiscHealthScanner.EstimateRefine(stretches, samples, SampleBytes);
		Assert.Equal(2 * steps * 2.5, estimate.TotalSeconds, 6);   // two edges, each test about (1 + 4) / 2 s
		Assert.Equal(TimeSpan.Zero, DiscHealthScanner.EstimateRefine([], samples, SampleBytes));
	}

	[Fact]
	public void Scan_OfARealFile_SamplesAtTheStepAndMeasuresSomeSpeed() {
		string path = Path.Combine(Path.GetTempPath(), "health-" + Guid.NewGuid().ToString("N") + ".m2ts");
		File.WriteAllBytes(path, new byte[20 * Mb]);
		try {
			using var scanner = new DiscHealthScanner(path);
			var seen = new List<long>();
			var samples = scanner.ScanCoarse(5 * Mb, onSample: s => seen.Add(s.Offset));
			Assert.Equal(samples.Select(s => s.Offset), seen);
			Assert.Equal(4, samples.Count);   // 0, 5, 10, 15 MB (each needs a whole sample before the end of the file)
			Assert.All(samples, s => Assert.False(s.Error));
			Assert.All(samples, s => Assert.True(s.MBps > 0));
			var (good, bad) = scanner.Refine(0, 15 * Mb, SampleBytes);   // nothing is bad: every test is good, so the "bad" end is approached
			Assert.True(bad - good <= SampleBytes);
		} finally {
			File.Delete(path);
		}
	}
}
