using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LunaDR.Core.Bluray;

/// <summary>
/// One spot of a clip file, read and timed: <see cref="MBps"/> is the speed of the reads after the first (the first includes the drive's seek,
/// <see cref="FirstReadMs"/>), <see cref="Seconds"/> the whole sample.
/// </summary>
public sealed record HealthSample(long Offset, double MBps, double FirstReadMs, double Seconds, bool Error) {
	/// <summary>Under this speed a spot counts as bad (a streaming rip needs far more; healthy spots gave 2.2 to 4 MB/s when sampled like this, damaged ones 0.3 to 1.5).</summary>
	public const double BadMBps = 1.5;

	/// <summary>At or above this speed a spot is good; between <see cref="BadMBps"/> and this it is slow (common on healthy parts too).</summary>
	public const double GoodMBps = 2.5;

	public bool IsBad => Error || MBps < BadMBps;
}

/// <summary>
/// A run of bad samples and the good samples around it, so the true edges lie between <see cref="LastGoodBefore"/> and <see cref="FirstBad"/>
/// and between <see cref="LastBad"/> and <see cref="FirstGoodAfter"/> (offsets in the clip file; -1 where the run touches the end of the scanned range).
/// </summary>
public sealed record HealthStretch(long LastGoodBefore, long FirstBad, long LastBad, long FirstGoodAfter);

/// <summary>
/// Health scan of a Blu-ray clip file: reads a short stretch (<see cref="SampleBytes"/>) at intervals and times it, to find the parts of the disc
/// that read slowly or not at all; then, if wanted, narrows the edges of each bad stretch by halving.
/// Nothing is decrypted or written, so a scan is only reading.
/// </summary>
public sealed class DiscHealthScanner : IDisposable {
	/// <summary>One read: 32 units of 6 KB, as a rip reads.</summary>
	public const int ReadBytes = AacsDecryptor.UnitSize * 32;
	public const int ReadsPerSample = 8;
	public static long SampleBytes => (long)ReadBytes * ReadsPerSample;

	/// <summary>
	/// How narrowly edges are located by default (8 samples, about 12.6 MB).
	/// Finer is pointless: on a real damaged disc the start of the slow part moved by about 0.3 GB between two scans of the same disc.
	/// </summary>
	public static long DefaultResolution => SampleBytes * 8;

	private const FileOptions NoBuffering = (FileOptions)0x20000000;   // FILE_FLAG_NO_BUFFERING: reads go to the disc, not the Windows file cache

	private readonly SafeFileHandle _file;
	private readonly byte[] _raw = GC.AllocateUninitializedArray<byte>(ReadBytes + 4096, pinned: true);   // unbuffered reads need an aligned buffer
	private readonly Memory<byte> _buffer;

	/// <summary>
	/// Opens the clip for reading straight from the disc.
	/// Without this, Windows answers a second scan of the same spots from its file cache, which says "good" however the disc really reads.
	/// </summary>
	public DiscHealthScanner(string clipPath) {
		_file = File.OpenHandle(clipPath, FileMode.Open, FileAccess.Read, FileShare.Read, OperatingSystem.IsWindows() ? NoBuffering : FileOptions.None);
		long address = Marshal.UnsafeAddrOfPinnedArrayElement(_raw, 0);
		_buffer = _raw.AsMemory((int)((4096 - address % 4096) % 4096), ReadBytes);
	}

	public long Length => RandomAccess.GetLength(_file);

	/// <summary>Reads <see cref="ReadsPerSample"/> reads in a row from the offset and times them.</summary>
	public HealthSample Measure(long offset) {
		offset = Math.Clamp(offset, 0, Math.Max(0, Length - SampleBytes)) / ReadBytes * ReadBytes;
		var all = System.Diagnostics.Stopwatch.StartNew();
		double firstMs = 0, seconds = 0;
		int reads = 0;
		bool error = false;
		for (int i = 0; i < ReadsPerSample && !error; i++) {
			var t = System.Diagnostics.Stopwatch.StartNew();
			try {
				ReadFully(offset + (long)i * ReadBytes);
			} catch (IOException) {
				error = true;
			}
			if (i == 0) firstMs = t.Elapsed.TotalMilliseconds;
			else if (!error) {
				seconds += t.Elapsed.TotalSeconds;
				reads++;
			}
		}
		double mbps = reads > 0 && seconds > 0 ? reads * (double)ReadBytes / seconds / 1e6 : 0;
		return new HealthSample(offset, mbps, firstMs, all.Elapsed.TotalSeconds, error);
	}

	private void ReadFully(long position) {
		var span = _buffer.Span;
		int got = 0;
		while (got < ReadBytes) {
			int n = RandomAccess.Read(_file, span[got..], position + got);
			if (n == 0) throw new EndOfStreamException();
			got += n;
		}
	}

	/// <summary>Samples from <paramref name="from"/> to <paramref name="to"/> (bytes of the file) every <paramref name="stepBytes"/>.</summary>
	public List<HealthSample> ScanCoarse(long stepBytes, long from = 0, long to = long.MaxValue, Action<HealthSample>? onSample = null, CancellationToken cancel = default) {
		var samples = new List<HealthSample>();
		long end = Math.Min(to, Length);
		for (long offset = from; offset + SampleBytes <= end; offset += Math.Max(ReadBytes, stepBytes)) {
			cancel.ThrowIfCancellationRequested();
			var s = Measure(offset);
			samples.Add(s);
			onSample?.Invoke(s);
		}
		return samples;
	}

	/// <summary>Narrows an edge between a good spot and a bad one (either order) to within <paramref name="resolution"/>, testing the middle each time.</summary>
	public (long Good, long Bad) Refine(long good, long bad, long resolution, Action<HealthSample>? onTest = null, CancellationToken cancel = default) =>
		Bisect(good, bad, resolution, offset => {
			cancel.ThrowIfCancellationRequested();
			var s = Measure(offset);
			onTest?.Invoke(s);
			return s.IsBad;
		});

	/// <summary>
	/// Halves the gap between a known good and a known bad offset until it is no more than <paramref name="minGap"/>:
	/// the middle (a whole number of reads) is tested and replaces whichever end it agrees with. Returns the final good and bad offsets.
	/// </summary>
	public static (long Good, long Bad) Bisect(long good, long bad, long minGap, Func<long, bool> isBad) {
		while (Math.Abs(bad - good) > minGap) {
			long mid = (good + bad) / 2 / ReadBytes * ReadBytes;
			if (mid == good || mid == bad) break;
			if (isBad(mid)) bad = mid;
			else good = mid;
		}
		return (good, bad);
	}

	/// <summary>How many tests <see cref="Bisect"/> needs for a gap.</summary>
	public static int BisectSteps(long gap, long minGap) => gap <= minGap ? 0 : (int)Math.Ceiling(Math.Log2((double)gap / minGap));

	/// <summary>
	/// The bad stretches of a scan, in order.
	/// The first sample of a scan includes the drive spinning up, so a bad first sample followed by a good one is not counted.
	/// Damaged parts read unevenly, so runs of bad samples separated by one or two samples that are slow but not bad (under
	/// <see cref="GoodMBps"/>) are joined into one stretch.
	/// </summary>
	public static List<HealthStretch> FindStretches(IReadOnlyList<HealthSample> samples) {
		var stretches = new List<HealthStretch>();
		int i = 0;
		if (samples.Count > 1 && samples[0].IsBad && !samples[1].IsBad) i = 1;
		while (i < samples.Count) {
			if (!samples[i].IsBad) { i++; continue; }
			int first = i;
			while (i < samples.Count && samples[i].IsBad) i++;
			stretches.Add(new HealthStretch(
				first > 0 ? samples[first - 1].Offset : -1,
				samples[first].Offset,
				samples[i - 1].Offset,
				i < samples.Count ? samples[i].Offset : -1));
		}
		return Join(stretches, samples);
	}

	private static List<HealthStretch> Join(List<HealthStretch> stretches, IReadOnlyList<HealthSample> samples) {
		var joined = new List<HealthStretch>();
		foreach (var s in stretches) {
			if (joined.Count > 0) {
				var previous = joined[^1];
				var between = samples.Where(x => x.Offset > previous.LastBad && x.Offset < s.FirstBad).ToList();
				if (between.Count <= 2 && between.All(x => x.MBps < HealthSample.GoodMBps)) {
					joined[^1] = new HealthStretch(previous.LastGoodBefore, previous.FirstBad, s.LastBad, s.FirstGoodAfter);
					continue;
				}
			}
			joined.Add(s);
		}
		return joined;
	}

	/// <summary>The edges of the stretches that are wider than <paramref name="resolution"/>: (good offset, bad offset) pairs, start edge first.</summary>
	public static List<(long Good, long Bad)> Edges(IReadOnlyList<HealthStretch> stretches, long resolution) {
		var edges = new List<(long, long)>();
		foreach (var s in stretches) {
			if (s.LastGoodBefore >= 0 && s.FirstBad - s.LastGoodBefore > resolution) edges.Add((s.LastGoodBefore, s.FirstBad));
			if (s.FirstGoodAfter >= 0 && s.FirstGoodAfter - s.LastBad > resolution) edges.Add((s.FirstGoodAfter, s.LastBad));
		}
		return edges;
	}

	/// <summary>
	/// About how long <see cref="Refine"/> of all the edges takes: tests per edge times the time a sample took in the scan
	/// (the mean of the two samples the edge lies between, so a slow area costs what it cost before).
	/// </summary>
	public static TimeSpan EstimateRefine(IReadOnlyList<HealthStretch> stretches, IReadOnlyList<HealthSample> samples, long resolution) {
		var seconds = samples.ToDictionary(s => s.Offset, s => s.Seconds);
		double total = 0;
		foreach (var (good, bad) in Edges(stretches, resolution))
			total += BisectSteps(Math.Abs(bad - good), resolution) * (seconds.GetValueOrDefault(good) + seconds.GetValueOrDefault(bad)) / 2;
		return TimeSpan.FromSeconds(total);
	}

	public void Dispose() => _file.Dispose();
}
