using RipDvd.Core.Mpeg;

namespace RipDvd.Core.Bluray;

/// <summary>
/// Dolby TrueHD access units (MLP family). Each unit starts with a 4-byte header: check nibble (4 bits), unit length in 16-bit words (12 bits), timing (16 bits).
/// Units that start a "major sync" carry F8 72 6F BA at byte 4, then the format info: sampling rate (4 bits), ..., the 6- and 8-channel speaker assignments.
/// A unit lasts 1/1200 s (40 samples at 48 kHz). On Blu-ray the AC3 core travels in the same PID in its own PES packets (handled apart).
/// </summary>
public sealed class TrueHdFramer : AudioFramer {
	// Speaker pairs per bit of the channel assignment (ffmpeg's thd_chancount): L/R, C, LFE, Ls/Rs, Tfl/Tfr, Lsc/Rsc, Lb/Rb, Cs, Ts, Lsd/Rsd, Lw/Rw, Tfc, LFE2.
	private static readonly int[] ChannelCount = [2, 1, 1, 2, 2, 2, 2, 1, 1, 2, 2, 1, 1];
	private bool _synced;

	public TrueHdFramer() => SamplesPerFrame = 40;

	/// <summary>MakeMKV and mkvmerge write one TrueHD unit per block.</summary>
	public override int FramesPerBlock => 1;
	protected override int HeaderLength => 4;

	protected override int ReadHeader(List<byte> b, int pos) {
		int size = ((b[pos] & 0x0F) << 8 | b[pos + 1]) * 2;
		if (size < 4) return -1;
		bool major = pos + 12 <= b.Count && b[pos + 4] == 0xF8 && b[pos + 5] == 0x72 && b[pos + 6] == 0x6F && b[pos + 7] == 0xBA;
		if (!_synced && !major) return -1;   // start at the first major sync
		if (major) {
			_synced = true;
			int rateCode = b[pos + 8] >> 4;
			SampleRate = rateCode switch { 0 => 48_000, 1 => 96_000, 2 => 192_000, 8 => 44_100, 9 => 88_200, 10 => 176_400, _ => SampleRate };
			SamplesPerFrame = 40 * SampleRate / (rateCode >= 8 ? 44_100 : 48_000);
			int ch6 = Bits(b, pos + 8, 12, 5);
			int ch8 = Bits(b, pos + 8, 19, 13);
			int mask = ch8 != 0 ? ch8 : ch6;
			Channels = Enumerable.Range(0, 13).Where(i => (mask >> i & 1) != 0).Sum(i => ChannelCount[i]);
		}
		return size;
	}
}

/// <summary>
/// E-AC3 (Dolby Digital Plus): sync 0B 77, strmtyp (2) substreamid (3) frmsiz (11, words - 1), fscod (2), numblkscod (2), acmod (3), lfeon (1).
/// Dependent substreams (strmtyp 1) are not merged into the independent frame yet (no test disc has them).
/// </summary>
public sealed class Eac3Framer : AudioFramer {
	public override int FramesPerBlock => 1;
	protected override int HeaderLength => 6;

	protected override int ReadHeader(List<byte> b, int pos) {
		if (b[pos] != 0x0B || b[pos + 1] != 0x77 || (b[pos + 5] >> 3) <= 10) return -1;   // bsid 11..16 = E-AC3
		int size = (((b[pos + 2] & 0x07) << 8) | b[pos + 3]) * 2 + 2;
		int fscod = b[pos + 4] >> 6;
		int numblkscod = (b[pos + 4] >> 4) & 3;
		SampleRate = fscod switch { 0 => 48_000, 1 => 44_100, 2 => 32_000, _ => 24_000 };
		SamplesPerFrame = fscod == 3 ? 1536 : (numblkscod switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 }) * 256;
		int acmod = (b[pos + 4] >> 1) & 7;
		bool lfe = (b[pos + 4] & 1) != 0;
		Channels = acmod switch { 0 => 2, 1 => 1, 2 => 2, 3 => 3, 4 => 3, 5 => 4, 6 => 4, _ => 5 } + (lfe ? 1 : 0);
		return size;
	}
}

/// <summary>H.264 in Matroska (V_MPEG4/ISO/AVC): NAL units with 4-byte lengths instead of start codes, and an avcC record as CodecPrivate.</summary>
public static class H264 {
	/// <summary>The NAL units of an Annex B access unit (start codes 00 00 01 / 00 00 00 01 removed, trailing zero bytes dropped).</summary>
	public static List<ReadOnlyMemory<byte>> NalUnits(ReadOnlyMemory<byte> au) {
		var units = new List<ReadOnlyMemory<byte>>();
		var s = au.Span;
		int start = -1;
		for (int i = 0; i + 2 < s.Length; i++) {
			if (s[i] == 0 && s[i + 1] == 0 && s[i + 2] == 1) {
				if (start >= 0) units.Add(Trim(au[start..i]));
				start = i + 3;
				i += 2;
			}
		}
		if (start >= 0 && start < s.Length) units.Add(Trim(au[start..]));
		return units.Where(u => u.Length > 0).ToList();
	}

	private static ReadOnlyMemory<byte> Trim(ReadOnlyMemory<byte> nal) {
		int end = nal.Length;
		while (end > 0 && nal.Span[end - 1] == 0) end--;
		return nal[..end];
	}

	/// <summary>Length-prefixed NAL units for a Matroska block; access unit delimiters (type 9) are left out.</summary>
	public static byte[] ToLengthPrefixed(IEnumerable<ReadOnlyMemory<byte>> nals) {
		var ms = new MemoryStream();
		foreach (var n in nals) {
			if ((n.Span[0] & 0x1F) == 9) continue;
			int len = n.Length;
			ms.Write([(byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len]);
			ms.Write(n.Span);
		}
		return ms.ToArray();
	}

	/// <summary>Key frame: an IDR picture (type 5) or a picture with a recovery point SEI (type 6, payload 6), as Blu-ray uses for open GOPs.</summary>
	public static bool IsKeyFrame(IEnumerable<ReadOnlyMemory<byte>> nals) =>
		nals.Any(n => (n.Span[0] & 0x1F) == 5 || ((n.Span[0] & 0x1F) == 6 && n.Length > 1 && n.Span[1] == 6));

	/// <summary>avcC (AVCDecoderConfigurationRecord) from one SPS and one PPS.</summary>
	public static byte[] AvcC(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps) {
		var ms = new MemoryStream();
		ms.Write([1, sps[1], sps[2], sps[3], 0xFF, 0xE1, (byte)(sps.Length >> 8), (byte)sps.Length]);
		ms.Write(sps);
		ms.Write([1, (byte)(pps.Length >> 8), (byte)pps.Length]);
		ms.Write(pps);
		return ms.ToArray();
	}
}

/// <summary>
/// VC-1 (advanced profile, as on Blu-ray) in Matroska: V_MS/VFW/FOURCC with a BITMAPINFOHEADER (compression "WVC1") followed by the
/// sequence header and entry point header (with their start codes), as mkvmerge writes it. Frames are stored as they are in the stream.
/// Start codes: 00 00 01 0F sequence header, 0E entry point, 0D frame, 0C field, 0B slice.
/// </summary>
public static class Vc1 {
	/// <summary>The bytes from a start code (00 00 01 <paramref name="code"/>) to the next start code, or null.</summary>
	public static byte[]? Unit(ReadOnlySpan<byte> s, byte code) {
		for (int i = 0; i + 3 < s.Length; i++) {
			if (s[i] != 0 || s[i + 1] != 0 || s[i + 2] != 1 || s[i + 3] != code) continue;
			int end = i + 4;
			while (end + 2 < s.Length && !(s[end] == 0 && s[end + 1] == 0 && s[end + 2] == 1)) end++;
			if (end + 2 >= s.Length) end = s.Length;
			return s[i..end].ToArray();
		}
		return null;
	}

	/// <summary>A key frame starts a GOP: the access unit carries an entry point header.</summary>
	public static bool IsKeyFrame(ReadOnlySpan<byte> au) => Unit(au, 0x0E) is not null;

	/// <summary>
	/// Coded and display size from an advanced-profile sequence header (after 00 00 01 0F): profile (2), level (3), colordiff (2), frmrtq (3), bitrtq (5),
	/// postproc (1), max coded width (12, (w/2)-1), max coded height (12), pulldown, interlace, tfcntr, finterp, reserved, psf (1 each), display_ext (1),
	/// then display width (14, -1) and height (14, -1).
	/// </summary>
	public static (int Width, int Height, int DisplayWidth, int DisplayHeight) Size(byte[] sequenceHeader) {
		int Bits(int from, int count) {
			int v = 0;
			for (int i = 0; i < count; i++) {
				int bit = 32 + from + i;
				v = v << 1 | (bit / 8 < sequenceHeader.Length ? sequenceHeader[bit / 8] >> (7 - bit % 8) & 1 : 0);
			}
			return v;
		}
		int width = (Bits(16, 12) + 1) * 2, height = (Bits(28, 12) + 1) * 2;
		if (Bits(46, 1) == 0) return (width, height, width, height);
		int dw = Bits(47, 14) + 1, dh = Bits(61, 14) + 1;
		// Then aspect_ratio_flag (1) and the sample aspect ratio code (4; 15 = explicit 8-bit width and height), e.g. 40:33 for 16:9 NTSC.
		if (Bits(75, 1) == 1) {
			int code = Bits(76, 4);
			(int n, int d) = code == 15 ? (Bits(80, 8), Bits(88, 8)) : code switch {
				2 => (12, 11),
				3 => (10, 11),
				4 => (16, 11),
				5 => (40, 33),
				6 => (24, 11),
				7 => (20, 11),
				8 => (32, 11),
				9 => (80, 33),
				10 => (18, 11),
				11 => (15, 11),
				12 => (64, 33),
				13 => (160, 99),
				_ => (1, 1),
			};
			if (n > 0 && d > 0) dw = (int)Math.Round((double)dw * n / d);
		}
		return (width, height, dw, dh);
	}

	public static byte[] CodecPrivate(byte[] sequenceHeader, byte[]? entryPoint, int width, int height) {
		var extra = new MemoryStream();
		extra.WriteByte(0);
		extra.Write(sequenceHeader);
		if (entryPoint is not null) extra.Write(entryPoint);
		var bih = new byte[40 + extra.Length];
		BitConverter.TryWriteBytes(bih.AsSpan(0), bih.Length);   // biSize
		BitConverter.TryWriteBytes(bih.AsSpan(4), width);
		BitConverter.TryWriteBytes(bih.AsSpan(8), height);
		BitConverter.TryWriteBytes(bih.AsSpan(12), (short)1);    // planes
		BitConverter.TryWriteBytes(bih.AsSpan(14), (short)24);   // bit count
		"WVC1"u8.CopyTo(bih.AsSpan(16));                         // compression
		BitConverter.TryWriteBytes(bih.AsSpan(20), width * height * 3);
		extra.ToArray().CopyTo(bih, 40);
		return bih;
	}
}

/// <summary>
/// Blu-ray PGS subtitles (S_HDMV/PGS): each PES packet holds one segment (type, 16-bit length, data).
/// A display set runs from a presentation composition segment (0x16) to the end segment (0x80) and becomes one block at the composition's time.
/// </summary>
public sealed class PgsAssembler {
	private MemoryStream? _set;
	private long _start;
	private readonly List<(long Pts, byte[] Data)> _ready = [];

	/// <summary>Takes one PES payload: one or more segments (type, 16-bit length, data); an END segment often follows another in the same packet.</summary>
	public void Feed(ReadOnlySpan<byte> payload, long? ptsNs) {
		for (int p = 0; p + 3 <= payload.Length;) {
			int length = payload[p + 1] << 8 | payload[p + 2];
			var segment = payload.Slice(p, Math.Min(3 + length, payload.Length - p));
			p += 3 + length;
			if (segment[0] == 0x16) {
				// A new composition: a set left without its END segment is closed here, so it is not lost.
				CloseOpenSet();
				_set = new MemoryStream();
				_start = ptsNs ?? _start;
			}
			if (_set is null) continue;
			_set.Write(segment);
			if (segment[0] == 0x80) {
				_ready.Add((_start, _set.ToArray()));
				_set = null;
			}
		}
	}

	/// <summary>
	/// Closes a set still open more than 2 s after it started (called as the video moves on). Some discs leave out the END segment of a
	/// "clear the screen" set (The Goonies: composition and window segments only), which would otherwise wait for the next composition, maybe 30 s later.
	/// </summary>
	public void Expire(long nowNs) {
		if (_set is not null && nowNs - _start > 2_000_000_000) CloseOpenSet();
	}

	/// <summary>Ends the open set with an END segment of our own, so players show (or clear) it.</summary>
	private void CloseOpenSet() {
		if (_set is null) return;
		_set.Write([0x80, 0, 0]);
		_ready.Add((_start, _set.ToArray()));
		_set = null;
	}

	public List<(long Pts, byte[] Data)> TakeReady() {
		var r = new List<(long, byte[])>(_ready);
		_ready.Clear();
		return r;
	}
}

/// <summary>
/// Blu-ray LPCM: each PES payload starts with a 4-byte header: data size (16 bits), channel assignment (4), sampling frequency (4), bits per sample (2), ...
/// Samples are big-endian; channel counts are padded to an even number. Converted to little-endian A_PCM/INT/LIT without the padding channel.
/// </summary>
public static class BdLpcm {
	public static (int Channels, int CodedChannels, int SampleRate, int Bits) Header(ReadOnlySpan<byte> h) {
		int assignment = h[2] >> 4;
		int channels = assignment switch { 1 => 1, 3 => 2, 4 => 3, 5 => 3, 6 => 4, 7 => 4, 8 => 5, 9 => 6, 10 => 7, 11 => 8, _ => 2 };
		int rate = (h[2] & 0x0F) switch { 1 => 48_000, 4 => 96_000, 5 => 192_000, _ => 48_000 };
		int bits = (h[3] >> 6) switch { 1 => 16, 2 => 20, 3 => 24, _ => 16 };
		return (channels, (channels + 1) & ~1, rate, bits);
	}

	/// <summary>The PES payload's samples as little-endian, without padding channels (20-bit samples stay in 3 bytes).</summary>
	public static byte[] ToLittleEndian(ReadOnlySpan<byte> payload) {
		var (channels, coded, _, bits) = Header(payload);
		int bytes = bits == 16 ? 2 : 3;
		var data = payload[4..];
		int frames = data.Length / (coded * bytes);
		var output = new byte[frames * channels * bytes];
		int o = 0;
		for (int f = 0; f < frames; f++) {
			for (int c = 0; c < channels; c++) {
				int i = (f * coded + c) * bytes;
				for (int k = bytes - 1; k >= 0; k--) output[o++] = data[i + k];
			}
		}
		return output;
	}
}
