namespace LunaDR.Core.Ifo;

/// <summary>
/// A playback time as stored in IFO files: 4 BCD bytes hh mm ss ff.
/// The top two bits of the frame byte give the frame rate (01 = 25 fps, 11 = 30 fps / 29.97).
/// </summary>
public readonly record struct DvdTime(int Hours, int Minutes, int Seconds, int Frames, int FrameRate) {
	public static DvdTime Parse(ReadOnlySpan<byte> b) {
		int rate = (b[3] >> 6) switch { 1 => 25, 3 => 30, _ => 0 };
		return new DvdTime(Bcd(b[0]), Bcd(b[1]), Bcd(b[2]), Bcd((byte)(b[3] & 0x3F)), rate);
	}

	private static int Bcd(byte v) => (v >> 4) * 10 + (v & 0x0F);

	/// <summary>
	/// Duration in real time. NTSC DVDs store times in 30 fps "frame counts", but the video
	/// really runs at 30000/1001 fps, so frame counts are converted with that rate.
	/// </summary>
	public TimeSpan ToTimeSpan() {
		long totalFrames = ((Hours * 60L + Minutes) * 60 + Seconds) * Math.Max(FrameRate, 1) + Frames;
		double seconds = FrameRate switch {
			30 => totalFrames * 1001.0 / 30000.0,
			25 => totalFrames / 25.0,
			_ => (Hours * 3600.0) + Minutes * 60 + Seconds,
		};
		return TimeSpan.FromSeconds(seconds);
	}

	/// <summary>Real duration in nanoseconds, computed exactly from the frame count (30 fps means 30000/1001).</summary>
	public long ToNanoseconds() => FrameRate switch {
		30 => ToFrames(30) * 1_001_000_000_000 / 30_000,
		25 => ToFrames(25) * 40_000_000,
		_ => ((Hours * 60L + Minutes) * 60 + Seconds) * 1_000_000_000,
	};

	/// <summary>Nominal time, exactly as stored on the disc (ignores the 1000/1001 NTSC factor).</summary>
	public TimeSpan Nominal =>
		new TimeSpan(Hours, Minutes, Seconds) + TimeSpan.FromSeconds(FrameRate > 0 ? Frames / (double)FrameRate : 0);

	public static DvdTime operator +(DvdTime a, DvdTime b) {
		int rate = a.FrameRate != 0 ? a.FrameRate : b.FrameRate;
		int r = Math.Max(rate, 1);
		long frames = a.ToFrames(r) + b.ToFrames(r);
		return FromFrames(frames, rate);
	}

	private long ToFrames(int rate) => ((Hours * 60L + Minutes) * 60 + Seconds) * rate + Frames;

	private static DvdTime FromFrames(long frames, int rate) {
		int r = Math.Max(rate, 1);
		long secs = frames / r;
		return new DvdTime((int)(secs / 3600), (int)(secs / 60 % 60), (int)(secs % 60), (int)(frames % r), rate);
	}

	public override string ToString() => $"{Hours}:{Minutes:00}:{Seconds:00}.{Frames:00}@{FrameRate}";
}
