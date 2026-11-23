using LunaDR.Core.Ripping;

namespace LunaDR.Core.Tests;

/// <summary>The time left shown to the user: smoothed, to the minute, and a log of the rip's progress to analyze it.</summary>
public class TimeLeftTests {
	[Fact]
	public void Smoother_CountsDownWithTheClockWhenTheEstimateAgrees() {
		var smoother = new RemainingSmoother();
		TimeSpan? shown = null;
		for (int s = 0; s <= 120; s += 2) shown = smoother.Next(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(1000 - s));
		Assert.Equal(880, shown!.Value.TotalSeconds, 1);
	}

	[Fact]
	public void Smoother_DoesNotJumpWithASingleWildEstimate() {
		var smoother = new RemainingSmoother();
		for (int s = 0; s < 60; s += 2) smoother.Next(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(1000 - s));
		var shown = smoother.Next(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(500));   // the estimate halves for one reading
		Assert.InRange(shown!.Value.TotalSeconds, 900, 950);                              // the shown value moves only a little
	}

	[Fact]
	public void Smoother_FollowsALastingChangeOfTheEstimate() {
		var smoother = new RemainingSmoother();
		TimeSpan? shown = null;
		for (int s = 0; s <= 300; s += 2) shown = smoother.Next(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(2000 - s * 2));   // estimate falls twice as fast as the clock
		Assert.InRange(shown!.Value.TotalSeconds, 1400, 1480);   // raw 1400, the shown value lags a little behind
	}

	[Fact]
	public void Smoother_NullInNullOut_AndZeroAtOnce() {
		var smoother = new RemainingSmoother();
		Assert.Null(smoother.Next(TimeSpan.FromSeconds(1), null));
		smoother.Next(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(600));
		Assert.Equal(TimeSpan.Zero, smoother.Next(TimeSpan.FromSeconds(12), TimeSpan.Zero));
	}

	[Theory]
	[InlineData(null, "estimating")]
	[InlineData(0, "0:00")]
	[InlineData(45, "0:45")]
	[InlineData(605, "10:05")]
	[InlineData(3599, "59:59")]
	[InlineData(3600, "1:00:00")]
	[InlineData(3 * 3600 + 21 * 60 + 20, "3:21:20")]
	public void TimeLeftText_IsInWholeSeconds(int? seconds, string expected) {
		Assert.Equal(expected, TimeLeftText.Format(seconds is { } s ? TimeSpan.FromSeconds(s) : null));
	}

	[Fact]
	public void TimeLeftText_RoundsFractionsOfASecond() {
		Assert.Equal("0:02", TimeLeftText.Format(TimeSpan.FromSeconds(1.6)));
		Assert.Equal("0:01", TimeLeftText.Format(TimeSpan.FromSeconds(1.4)));
	}

	[Fact]
	public void ProgressLog_WritesARowEveryFiveSecondsAndAFinalRowWithNothingRemaining() {
		string folder = Path.Combine(Path.GetTempPath(), "lunadr-log-test-" + Guid.NewGuid().ToString("N"));
		try {
			string? file;
			using (var log = new RipProgressLog(folder, "Test disc - playlist 00001", "output x.mkv")) {
				file = log.Path;
				long total = 100_000;
				for (int s = 0; s <= 20; s++) {
					var p = new RipProgress(RipPhase.Ripping, s * 1_000L, total, TimeSpan.FromSeconds(s + 3), 0, s / 20.0);
					log.Sample(p, TimeSpan.FromSeconds(s), total, 1_000, TimeSpan.FromSeconds(total / 1_000 - s), TimeSpan.FromSeconds(total / 1_000 - s));
				}
				log.Finish("done", null, TimeSpan.FromSeconds(100), total);
			}
			var lines = File.ReadAllLines(file!);
			var rows = lines.Where(l => !l.StartsWith('#') && !l.StartsWith("row,")).ToList();
			Assert.Equal(5 + 1, rows.Count);   // readings at 0, 5, 10, 15, 20 s, then the final row
			var last = rows[^1].Split(',');
			Assert.Equal("done", last[0]);
			Assert.Equal("100.0", last[3]);   // reading_s
			Assert.Equal("0", last[8]);       // bytes_remaining
			Assert.Equal(last[7], last[6]);   // bytes_read = bytes_total
		} finally {
			if (Directory.Exists(folder)) Directory.Delete(folder, true);
		}
	}
}
