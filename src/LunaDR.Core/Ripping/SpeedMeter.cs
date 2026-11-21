namespace LunaDR.Core.Ripping;

/// <summary>
/// Measures how fast a rip reads, for the "time left" estimate: the speed over the last <see cref="Window"/> of reading time
/// (all of it, while less than that has passed).
/// A drive does not read at one speed: it is slow at first and gets faster towards the outer edge of the disc.
/// Dividing by the average of the whole rip then makes the estimate fall faster than the clock (a rising average shrinks it a second time),
/// so the recent speed is used instead.
/// </summary>
internal sealed class SpeedMeter {
	/// <summary>How far back the speed is measured.</summary>
	public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

	/// <summary>Minimum reading time before a speed is reported.</summary>
	public static readonly TimeSpan MinimumMeasureTime = TimeSpan.FromSeconds(3);

	// Samples of (reading time so far, sectors read so far); the first one is the newest at or before the start of the window.
	private readonly Queue<(TimeSpan Time, long Sectors)> _samples = new();

	/// <summary>Records that <paramref name="sectorsRead"/> sectors had been read after <paramref name="readingTime"/> of reading.</summary>
	public void Add(TimeSpan readingTime, long sectorsRead) {
		_samples.Enqueue((readingTime, sectorsRead));
		while (_samples.Count > 1 && _samples.ElementAt(1).Time <= readingTime - Window) _samples.Dequeue();
	}

	/// <summary>Sectors per second over the window ending at the given point; 0 until <see cref="MinimumMeasureTime"/> of reading has been measured.</summary>
	public double SectorsPerSecond(TimeSpan readingTime, long sectorsRead) {
		if (_samples.Count == 0 || readingTime < MinimumMeasureTime) return 0;
		var (start, sectors) = _samples.Peek();
		double seconds = (readingTime - start).TotalSeconds;
		return seconds > 0 && sectorsRead > sectors ? (sectorsRead - sectors) / seconds : 0;
	}
}
