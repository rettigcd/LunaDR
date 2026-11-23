using LunaDR.Core.Ripping;

namespace LunaDR.Core.Tests;

/// <summary>The one-line drive status shown while ripping a Blu-ray (slow reading, retries, skipped units).</summary>
public class DriveStatusTests {
	private static RipProgress Progress(ReadHealth health, long sectors = 11_000_000) => new(RipPhase.Ripping, sectors, 19_000_000, TimeSpan.FromMinutes(5), 0, 0, health);

	private static ReadHealth Healthy(double lastReadMs = 30) => new(0, 0, 0, 0, lastReadMs);

	[Fact]
	public void NormalReading_HasNoStatus() {
		var tracker = new DriveStatusTracker();
		Assert.Null(tracker.Update(Progress(Healthy()), TimeSpan.FromMinutes(5), 17, 15));
	}

	[Fact]
	public void DvdProgress_HasNoStatus() {
		var tracker = new DriveStatusTracker();
		Assert.Null(tracker.Update(new RipProgress(RipPhase.Ripping, 1000, 5000, TimeSpan.FromMinutes(5)), TimeSpan.FromMinutes(5), 0.1, 5));
	}

	[Fact]
	public void SlowReading_IsReportedWithHowLongItHasLasted_AndClearsWhenItRecovers() {
		var tracker = new DriveStatusTracker();
		Assert.Null(tracker.Update(Progress(Healthy()), TimeSpan.FromMinutes(20), 17, 17));
		var first = tracker.Update(Progress(Healthy()), TimeSpan.FromMinutes(21), 0.6, 17);
		Assert.StartsWith("Drive is slow: 0.6 MB/s", first);
		Assert.DoesNotContain(" for ", first);   // just started
		var later = tracker.Update(Progress(Healthy()), TimeSpan.FromMinutes(28), 0.6, 15);
		Assert.Contains("for 7:00", later);
		Assert.Contains("average so far 15.0 MB/s", later);
		Assert.Null(tracker.Update(Progress(Healthy()), TimeSpan.FromMinutes(30), 12, 15));
		// A new slow spell counts from its own start.
		Assert.DoesNotContain(" for ", tracker.Update(Progress(Healthy()), TimeSpan.FromMinutes(31), 0.5, 15));
	}

	[Fact]
	public void SlowSpeedInTheFirstSeconds_IsNotReported() {
		var tracker = new DriveStatusTracker();
		Assert.Null(tracker.Update(Progress(Healthy()), TimeSpan.FromSeconds(10), 0.5, 0.5));
	}

	[Fact]
	public void LongSingleRead_IsReportedEvenAtAnOkAverageSpeed() {
		var tracker = new DriveStatusTracker();
		Assert.Contains("the last read took 12 s", tracker.Update(Progress(Healthy(12_000)), TimeSpan.FromMinutes(5), 9, 10));
	}

	[Fact]
	public void Retry_SaysWhichAttemptOfThree_AndSkippedUnitsAreCounted() {
		var tracker = new DriveStatusTracker();
		var retrying = new ReadHealth(1, 4, 2, 0, 30, RetryPosition: 23_690_000_000, RetryAttempt: 2);
		string text = tracker.Update(Progress(retrying), TimeSpan.FromMinutes(5), 17, 15)!;
		Assert.Contains("attempt 2 of 3", text);
		Assert.Contains("23.69 GB", text);
		Assert.Contains("2 unit(s) of 6 KB skipped so far", text);
		Assert.Equal("Reading normally; 2 unit(s) of 6 KB skipped so far", tracker.Update(Progress(new ReadHealth(1, 4, 2, 0, 30)), TimeSpan.FromMinutes(6), 17, 15));
	}
}
