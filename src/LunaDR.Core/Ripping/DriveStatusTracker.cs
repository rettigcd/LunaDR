namespace LunaDR.Core.Ripping;

/// <summary>
/// Turns the read measurements of a rip into one line for the user about what the drive is doing, or null when all is well:
/// retrying a failed unit ("attempt 2 of 3"), reading slowly (and for how long), or having skipped unreadable units.
/// A drive working through a damaged spot does its own retries inside one read, which Windows does not report;
/// from outside that looks like a slow read, so slow reading is what is shown for it.
/// </summary>
internal sealed class DriveStatusTracker {
	/// <summary>Below this speed (about the slowest normal Blu-ray speed, 1x = 4.5 MB/s) reading counts as slow.</summary>
	public const double SlowMBps = 2.0;

	/// <summary>Reading time before a speed is judged (the first seconds are slow while the drive spins up).</summary>
	public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(20);

	private TimeSpan? _slowSince;

	/// <param name="reading">Time spent reading so far.</param>
	/// <param name="windowMBps">Speed over the last minute, in MB/s.</param>
	/// <param name="averageMBps">Average speed since reading began, in MB/s (what "normal" was).</param>
	public string? Update(RipProgress p, TimeSpan reading, double windowMBps, double averageMBps) {
		if (p.Health is not { } h || p.Phase != RipPhase.Ripping) return null;
		string where = $"{p.SectorsDone * 2048 / 1e9:F2} GB ({p.Fraction:P0})";
		string skipped = h.UnreadableUnits > 0 ? $"; {h.UnreadableUnits:N0} unit(s) of 6 KB skipped so far" : "";

		if (h.RetryAttempt > 0) return $"Drive error: reading the unit at {h.RetryPosition / 1e9:F2} GB again (attempt {h.RetryAttempt} of 3){skipped}";

		bool slow = reading >= Warmup && (windowMBps < SlowMBps || h.LastReadMs > 3000);
		if (!slow) {
			_slowSince = null;
			return h.UnreadableUnits > 0 ? $"Reading normally{skipped}" : null;
		}
		_slowSince ??= reading;
		var lasted = reading - _slowSince.Value;
		string waiting = h.LastReadMs > 3000 ? $"; the last read took {h.LastReadMs / 1000:F0} s" : "";
		return $"Drive is slow: {windowMBps:F1} MB/s{(lasted >= TimeSpan.FromSeconds(10) ? $" for {TimeLeftText.Format(lasted)}" : "")} " +
			   $"(average so far {averageMBps:F1} MB/s), at {where}; {h.ReadErrors:N0} read error(s){waiting}{skipped}";
	}
}
