using System.Diagnostics;
using RipDvd.Core.Dvd;
using RipDvd.Core.Ifo;
using RipDvd.Core.Mpeg;

namespace DvdProbe;

/// <summary>
/// Probe: demux a title into its elementary streams and sanity-check them.
///   * video: count sequence/GOP/picture start codes and repeat_first_field flags, which
///     gives the exact displayed duration;
///   * AC3: walk the frames (sync word 0B 77 + frame size) and count them;
///   * all streams: packet counts, bytes, first/last PTS.
/// Optionally writes each stream to a file (.m2v / .ac3 / .sub) for inspection.
/// </summary>
internal static class DemuxProbe {
	private sealed class Stats {
		public long Packets, Bytes;
		public long? FirstPts, LastPts;
		public FileStream? Out;
	}

	public static void Run(string videoTs, int titleNumber, string? outDir, int maxCells) {
		var vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO")));
		var t = vmg.Titles[titleNumber - 1];
		var vts = VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, $"VTS_{t.TitleSetNumber:00}_0.IFO")), t.TitleSetNumber);
		var cells = vts.Chapters[t.TitleSetTitleNumber - 1].Select(c => c.PgcNumber).Distinct()
			.SelectMany(n => vts.Pgcs[n - 1].Cells).Take(maxCells).ToList();

		using var vobs = VobReader.ForTitleSet(videoTs, t.TitleSetNumber);
		var sw = Stopwatch.StartNew();
		var key = TitleSectorReader.FindKey(vobs);
		Console.WriteLine($"Title {titleNumber} (VTS {t.TitleSetNumber}): key {key?.ToString() ?? "none"} found in {sw.ElapsedMilliseconds} ms");

		var reader = new TitleSectorReader(vobs, cells, key);
		var stats = new Dictionary<StreamId, Stats>();
		var video = new VideoScanner();
		var ac3 = new Dictionary<int, Ac3Walker>();
		long sectors = 0, stillScrambled = 0;
		sw.Restart();

		foreach (var sector in reader.ReadAll()) {
			sectors++;
			if ((sector.Data.Span[0x14] & 0x30) != 0 && sector.Data.Span[0x11] is not (0xBB or 0xBE or 0xBF)) stillScrambled++;
			foreach (var p in ProgramStreamParser.Parse(sector.Data)) {
				if (!stats.TryGetValue(p.Stream, out var st)) {
					stats[p.Stream] = st = new Stats();
					if (outDir is not null && p.Stream.Kind != StreamKind.Navigation) {
						string ext = p.Stream.Kind switch { StreamKind.Video => "m2v", StreamKind.Ac3 => "ac3", StreamKind.Dts => "dts", StreamKind.Lpcm => "lpcm", StreamKind.Subpicture => "sup", _ => "bin" };
						Directory.CreateDirectory(outDir);
						st.Out = File.Create(Path.Combine(outDir, $"title{titleNumber:00}_{p.Stream.Kind}{p.Stream.Number}.{ext}"));
					}
				}
				st.Packets++;
				st.Bytes += p.Payload.Length;
				if (p.Pts is { } pts) { st.FirstPts ??= pts; st.LastPts = pts; }
				st.Out?.Write(p.Payload.Span);

				if (p.Stream.Kind == StreamKind.Video) video.Feed(p.Payload.Span);
				else if (p.Stream.Kind == StreamKind.Ac3) {
					if (!ac3.TryGetValue(p.Stream.Number, out var w)) ac3[p.Stream.Number] = w = new Ac3Walker();
					w.Feed(p.Payload.Span);
				}
			}
		}

		double secs = sw.Elapsed.TotalSeconds;
		Console.WriteLine($"{sectors:N0} sectors ({sectors * 2048 / 1e6:F0} MB) in {secs:F1} s = {sectors * 2048 / 1e6 / secs:F1} MB/s; " +
						  $"still scrambled after descrambling: {stillScrambled}");
		foreach (var (id, st) in stats.OrderBy(k => k.Key.Kind).ThenBy(k => k.Key.Number)) {
			st.Out?.Dispose();
			string span = st.FirstPts is { } f && st.LastPts is { } l ? $"PTS {f / 90000.0,9:F3} .. {l / 90000.0,9:F3}" : "";
			Console.WriteLine($"  {id,-14} packets {st.Packets,8:N0}  bytes {st.Bytes,14:N0}  {span}");
		}
		video.Report();
		foreach (var (n, w) in ac3) w.Report(n);
	}

	/// <summary>Finds MPEG-2 video start codes across packet boundaries.</summary>
	private sealed class VideoScanner {
		private readonly byte[] _tail = new byte[8];
		private int _tailLength;
		private long _sequences, _gops, _pictures, _repeatFirstField, _userData, _frameRateCode = -1;
		private int _width, _height, _aspect;

		public void Feed(ReadOnlySpan<byte> data) {
			// Scan tail+data; keep the last 8 bytes for the next call so codes split across packets are found.
			var buf = new byte[_tailLength + data.Length];
			_tail.AsSpan(0, _tailLength).CopyTo(buf);
			data.CopyTo(buf.AsSpan(_tailLength));
			int limit = buf.Length - 8;
			for (int i = 0; i < limit; i++) {
				if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 1) continue;
				byte code = buf[i + 3];
				switch (code) {
					case 0x00: _pictures++; break;
					case 0xB8: _gops++; break;
					case 0xB2: _userData++; break;
					case 0xB3:
						_sequences++;
						_width = (buf[i + 4] << 4) | (buf[i + 5] >> 4);
						_height = ((buf[i + 5] & 0x0F) << 8) | buf[i + 6];
						_aspect = buf[i + 7] >> 4;
						_frameRateCode = buf[i + 7] & 0x0F;
						break;
					case 0xB5:
						// Picture coding extension (id 8): byte 3 bit 1 = repeat_first_field.
						if ((buf[i + 4] >> 4) == 8 && (buf[i + 7] & 0x02) != 0) _repeatFirstField++;
						break;
				}
			}
			int keep = Math.Min(8, buf.Length);
			buf.AsSpan(buf.Length - keep).CopyTo(_tail);
			_tailLength = keep;
		}

		public void Report() {
			// Each frame picture shows 2 fields; repeat_first_field adds a third (3:2 pulldown).
			long fields = _pictures * 2 + _repeatFirstField;
			double seconds = fields / 2.0 * 1001.0 / 30000.0;
			Console.WriteLine($"  video: {_width}x{_height} aspect code {_aspect} frame-rate code {_frameRateCode}; " +
							  $"{_sequences:N0} sequence headers, {_gops:N0} GOPs, {_pictures:N0} pictures, " +
							  $"{_repeatFirstField:N0} repeat-first-field, {_userData:N0} user-data (CC) blocks");
			Console.WriteLine($"  video duration from pictures+pulldown (NTSC): {TimeSpan.FromSeconds(seconds):h\\:mm\\:ss\\.fff}");
		}
	}

	/// <summary>Walks AC3 sync frames: 0B 77, CRC (2 bytes), fscod/frmsizecod byte.</summary>
	private sealed class Ac3Walker {
		// Bitrate in kbps by frmsizecod >> 1. At 48 kHz a frame is bitrate*4 bytes (1536 samples).
		private static readonly int[] Bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];
		private readonly List<byte> _pending = [];
		private long _frames, _resyncs, _skippedBytes;
		private int _bitrate, _channelsMode = -1;

		public void Feed(ReadOnlySpan<byte> data) {
			foreach (byte b in data) _pending.Add(b);
			int pos = 0;
			while (pos + 7 <= _pending.Count) {
				if (_pending[pos] != 0x0B || _pending[pos + 1] != 0x77) {
					pos++; _skippedBytes++;
					continue;
				}
				int fscod = _pending[pos + 4] >> 6;
				int frmsizecod = _pending[pos + 4] & 0x3F;
				if (fscod != 0 || frmsizecod >= 38) { pos++; _resyncs++; continue; }
				_bitrate = Bitrates[frmsizecod >> 1];
				int size = _bitrate * 4;
				if (pos + size > _pending.Count) break;
				_channelsMode = _pending[pos + 6] >> 5;   // acmod
				_frames++;
				pos += size;
			}
			_pending.RemoveRange(0, pos);
		}

		public void Report(int number) {
			double seconds = _frames * 1536 / 48000.0;
			Console.WriteLine($"  AC3#{number}: {_frames:N0} frames @ {_bitrate} kbps, acmod {_channelsMode}, " +
							  $"duration {TimeSpan.FromSeconds(seconds):h\\:mm\\:ss\\.fff}; skipped {_skippedBytes} bytes, {_resyncs} bad headers");
		}
	}
}
