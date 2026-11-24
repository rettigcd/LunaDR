namespace LunaDR.Core.Ripping;

/// <summary>
/// The usual shape of a Blu-ray rip's speed, for the "time left" estimate.
/// A drive reads slowly at the start and faster towards the outer edge of each layer, so the last minute's speed is not what the rest will run at:
/// early on the true time left is 15 to 35 percent below "bytes left ÷ current speed", and in the second half 10 to 35 percent above it.
/// <see cref="Bins"/> is how much time each twentieth of a clip took, relative to the average, over three complete healthy rips
/// (Fellowship, Shawshank, Empire; the peak of the speed lay at 45 to 65 percent of the clip).
/// The estimate takes the current speed, works out how fast the drive is relative to the curve at this spot, and applies that to the rest of the curve.
/// With so few rips the correction is limited (<see cref="MinFactor"/> to <see cref="MaxFactor"/>); a leave-one-out test on those rips
/// cut the average error from about 14 to about 8 percent.
/// </summary>
internal static class SpeedCurve {
	/// <summary>Relative time per twentieth of the clip (the first twentieth is the slowest to read).</summary>
	public static readonly double[] Bins = [1.517, 1.161, 1.088, 1.038, 0.967, 0.914, 0.897, 0.846, 0.825, 0.809, 0.825, 0.789, 0.843, 0.893, 0.894, 1.133, 1.118, 1.086, 1.154, 1.201];

	public const double MinFactor = 0.6;
	public const double MaxFactor = 1.8;

	/// <summary>The time density integrated over the part of the clip from <paramref name="from"/> to <paramref name="to"/> (fractions of the clip).</summary>
	public static double Integral(double from, double to) {
		from = Math.Clamp(from, 0, 1);
		to = Math.Clamp(to, 0, 1);
		double sum = 0;
		for (int i = 0; i < Bins.Length; i++) {
			double lo = Math.Max(from, (double)i / Bins.Length), hi = Math.Min(to, (double)(i + 1) / Bins.Length);
			if (hi > lo) sum += Bins[i] * (hi - lo);
		}
		return sum;
	}

	/// <summary>
	/// The factor to multiply "sectors left ÷ current speed" by: the time the curve says the rest takes, relative to the time it says the last
	/// <paramref name="window"/> took at that same speed. Before a measurable part of the clip has been read it is 1.
	/// </summary>
	public static double Factor(long sectorsDone, long sectorsTotal, double sectorsPerSecond, TimeSpan window) {
		if (sectorsTotal <= 0 || sectorsPerSecond <= 0) return 1;
		double f = Math.Min(1, (double)sectorsDone / sectorsTotal);
		if (f >= 1) return 1;
		double span = Math.Min(f, window.TotalSeconds * sectorsPerSecond / sectorsTotal);   // the part of the clip the speed was measured over
		if (span < 0.005) return 1;
		double windowMean = Integral(f - span, f) / span;
		double restMean = Integral(f, 1) / (1 - f);
		return Math.Clamp(restMean / windowMean, MinFactor, MaxFactor);
	}

	/// <summary>The time left for a Blu-ray clip, or null while the speed is not yet measured.</summary>
	public static TimeSpan? Remaining(long sectorsDone, long sectorsTotal, double sectorsPerSecond, TimeSpan window) =>
		sectorsPerSecond > 0
			? TimeSpan.FromSeconds(Math.Max(0, sectorsTotal - sectorsDone) / sectorsPerSecond * Factor(sectorsDone, sectorsTotal, sectorsPerSecond, window))
			: null;
}
