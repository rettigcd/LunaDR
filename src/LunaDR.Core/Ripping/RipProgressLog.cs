using System.Globalization;
using System.Text;

namespace LunaDR.Core.Ripping;

/// <summary>
/// A log of how one rip progressed, to find out why the "time left" is off (and to build a correction for it).
/// One CSV file per ripped title: lines starting with # describe the rip, then one row about every <see cref="Interval"/> of reading,
/// and a last row (row = done, cancelled or failed) with the total time; for "done" the bytes remaining are 0, so every earlier row's
/// true time left is (final reading_s − its reading_s).
/// Columns:
///   row            sample or done/cancelled/failed
///   wall_time      clock time of day
///   elapsed_s      seconds since the title started (including unlocking the drive and finding keys)
///   reading_s      seconds since reading began (what the speeds use)
///   item_pos       the play item being read (0-based), item_fraction how far into it
///   bytes_read, bytes_total, bytes_remaining   of the clip files the playlist reads
///   rate_recent_MBps   speed since the previous row; rate_window_MBps the last minute (what the estimate uses); rate_avg_MBps since reading began
///   raw_remaining_s    the estimate from the last-minute speed, corrected by the usual speed curve of a Blu-ray rip (see <see cref="SpeedCurve"/>);
///                      avg_remaining_s the old estimate from the average speed
///   flat_remaining_s   the estimate from the last-minute speed alone (bytes left ÷ speed), to compare with raw_remaining_s; empty in older logs
///   shown_remaining_s  what the window showed after smoothing
///   read_errors        reads of a block that failed (the drive reported an error; each unit of the block is then read again)
///   unit_retries       6 KB units read again after such a failure; unreadable_units those that still failed (left as zeros); undecryptable_units those that could not be decrypted
///   max_read_ms        the longest single read since the previous row: a drive working through a damaged spot is slow without reporting any error
///   (the four counts are totals so far)
/// Lines starting with # between the rows say where the clip being read lies on the disc (volume size and the file's extents);
/// just before the last row come the rip's warnings (one "# warning: ..." line each, or "# warnings: none"), or "# error: ..." if the rip failed.
/// Failing to write the log never stops a rip.
/// </summary>
internal sealed class RipProgressLog : IDisposable {
	public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

	private readonly StreamWriter? _writer;
	private TimeSpan _lastRowReading = TimeSpan.MinValue;
	private long _lastRowBytes;
	private double _maxReadMs;
	private bool _finished;

	/// <summary>The file written, or null if it could not be created.</summary>
	public string? Path { get; }

	public RipProgressLog(string folder, string title, string description) {
		try {
			Directory.CreateDirectory(folder);
			string safe = string.Concat(title.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
			Path = System.IO.Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss} {safe}.csv");
			_writer = new StreamWriter(Path, false, new UTF8Encoding(false)) { AutoFlush = true };
			_writer.WriteLine("# LunaDR rip progress log");
			_writer.WriteLine($"# started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			foreach (var line in description.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) _writer.WriteLine("# " + line);
			_writer.WriteLine("row,wall_time,elapsed_s,reading_s,item_pos,item_fraction,bytes_read,bytes_total,bytes_remaining," +
							  "rate_recent_MBps,rate_window_MBps,rate_avg_MBps,raw_remaining_s,avg_remaining_s,shown_remaining_s," +
							  "read_errors,unit_retries,unreadable_units,undecryptable_units,max_read_ms,flat_remaining_s");
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			_writer = null;
		}
	}

	/// <summary>Adds a row if <see cref="Interval"/> of reading has passed since the last one (the first reading always gets one).</summary>
	public void Sample(RipProgress p, TimeSpan reading, long sectorsTotal, double windowSectorsPerSecond, TimeSpan? raw, TimeSpan? shown, TimeSpan? flat = null) {
		if (_writer is null || _finished || p.Phase != RipPhase.Ripping) return;
		_maxReadMs = Math.Max(_maxReadMs, p.Health?.LastReadMs ?? 0);   // every read counts, not only the ones that get a row
		if (_lastRowReading != TimeSpan.MinValue && reading - _lastRowReading < Interval) return;
		Row("sample", p, reading, sectorsTotal, windowSectorsPerSecond, raw, shown, flat);
	}

	/// <summary>Adds a line starting with # (between rows), for facts that belong to the rip, such as where the clip lies on the disc.</summary>
	public void Note(string text) {
		if (_writer is null || _finished) return;
		foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) _writer.WriteLine("# " + line);
	}

	/// <summary>Adds the last row. <paramref name="outcome"/> is done, cancelled or failed; for done the bytes remaining are 0.</summary>
	public void Finish(string outcome, RipProgress? last, TimeSpan reading, long sectorsTotal) {
		if (_writer is null || _finished) return;
		var p = last ?? new RipProgress(RipPhase.Done, 0, sectorsTotal, TimeSpan.Zero);
		if (outcome == "done") p = p with { SectorsDone = sectorsTotal };
		Row(outcome, p, reading, sectorsTotal, 0, outcome == "done" ? TimeSpan.Zero : null, outcome == "done" ? TimeSpan.Zero : null);
		_finished = true;
	}

	private void Row(string row, RipProgress p, TimeSpan reading, long total, double windowSps, TimeSpan? raw, TimeSpan? shown, TimeSpan? flat = null) {
		long bytes = p.SectorsDone * 2048, totalBytes = total * 2048;
		double sinceLast = _lastRowReading == TimeSpan.MinValue ? reading.TotalSeconds : (reading - _lastRowReading).TotalSeconds;
		double recent = sinceLast > 0 ? (bytes - _lastRowBytes) / sinceLast / 1e6 : 0;
		double avg = reading.TotalSeconds > 0 ? bytes / reading.TotalSeconds / 1e6 : 0;
		double? avgRemaining = avg > 0 ? (totalBytes - bytes) / (avg * 1e6) : null;
		var c = CultureInfo.InvariantCulture;
		string S(double? v) => v is { } d ? d.ToString("F1", c) : "";
		_writer!.WriteLine(string.Join(",", row, DateTime.Now.ToString("HH:mm:ss", c), S(p.Elapsed.TotalSeconds), S(reading.TotalSeconds),
			p.CellPosition.ToString(c), p.CellFraction.ToString("F3", c), bytes.ToString(c), totalBytes.ToString(c), Math.Max(0, totalBytes - bytes).ToString(c),
			S(recent), S(windowSps * 2048 / 1e6), S(avg), S(raw?.TotalSeconds), S(avgRemaining), S(shown?.TotalSeconds),
			(p.Health?.ReadErrors ?? 0).ToString(c), (p.Health?.UnitRetries ?? 0).ToString(c), (p.Health?.UnreadableUnits ?? 0).ToString(c),
			(p.Health?.UndecryptableUnits ?? 0).ToString(c), _maxReadMs.ToString("F0", c), S(flat?.TotalSeconds)));
		_maxReadMs = 0;
		_lastRowReading = reading;
		_lastRowBytes = bytes;
	}

	public void Dispose() => _writer?.Dispose();
}
