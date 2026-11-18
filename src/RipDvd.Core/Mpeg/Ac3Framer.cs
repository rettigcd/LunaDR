namespace RipDvd.Core.Mpeg;

/// <summary>One compressed audio frame (AC3 sync frame, DTS frame, ...).</summary>
public sealed class AudioFrame {
	public required byte[] Data { get; init; }
	/// <summary>Presentation time in nanoseconds on the title timeline.</summary>
	public required long Pts { get; init; }
}

/// <summary>
/// Splits a DVD audio stream into frames and timestamps them.
/// On DVD the PES header's "first access unit" pointer says which frame the PTS belongs to.
/// Frames without their own PTS follow the last timestamped frame; their time is counted in whole samples from it,
/// so frames that last a fractional number of nanoseconds (DTS: 512/48000 s) don't drift.
/// Subclasses read their codec's frame header.
/// </summary>
public abstract class AudioFramer {
	private readonly List<byte> _buffer = [];
	private readonly List<(long Offset, long Pts)> _ptsMarks = [];
	private long _bufferBase;
	private long _anchorPts = -1;
	private long _samplesSinceAnchor;
	protected readonly List<AudioFrame> _ready = [];

	public int Channels { get; protected set; }
	public int SampleRate { get; protected set; } = 48000;
	public long SkippedBytes { get; private set; }

	/// <summary>Samples in one frame (AC3 1536, DTS usually 512).</summary>
	public int SamplesPerFrame { get; protected set; }

	/// <summary>Nominal frame duration in ns (used for Matroska DefaultDuration and lacing checks).</summary>
	public long FrameNs => SamplesPerFrame * 1_000_000_000L / SampleRate;

	/// <summary>Frames per laced SimpleBlock, as MakeMKV writes them (96 ms per block for AC3 and DTS).</summary>
	public abstract int FramesPerBlock { get; }

	/// <summary>Minimum bytes needed to read a frame header.</summary>
	protected abstract int HeaderLength { get; }

	/// <summary>If a valid frame header starts at <paramref name="pos"/>, read its info and return the frame size; else -1.</summary>
	protected abstract int ReadHeader(List<byte> b, int pos);

	/// <param name="firstAccessUnit">1-based offset in the payload of the frame the PTS is for (0 = none).</param>
	public virtual void Feed(ReadOnlySpan<byte> payload, long? ptsNs, int firstAccessUnit) {
		if (ptsNs is { } p && firstAccessUnit > 0)
			_ptsMarks.Add((_bufferBase + _buffer.Count + firstAccessUnit - 1, p));
		foreach (byte b in payload) _buffer.Add(b);

		int pos = 0;
		while (pos + HeaderLength <= _buffer.Count) {
			int size = ReadHeader(_buffer, pos);
			if (size <= 0) {
				pos++;
				SkippedBytes++;
				continue;
			}
			if (pos + size > _buffer.Count) break;

			long frameOffset = _bufferBase + pos;
			while (_ptsMarks.Count > 0 && _ptsMarks[0].Offset <= frameOffset) {
				if (_ptsMarks[0].Offset == frameOffset || _anchorPts < 0) {
					_anchorPts = _ptsMarks[0].Pts;
					_samplesSinceAnchor = 0;
				}
				_ptsMarks.RemoveAt(0);
			}
			if (_anchorPts < 0) _anchorPts = 0;
			long pts = _anchorPts + _samplesSinceAnchor * 1_000_000_000L / SampleRate;
			_ready.Add(new AudioFrame { Data = _buffer.GetRange(pos, size).ToArray(), Pts = pts });
			_samplesSinceAnchor += SamplesPerFrame;
			pos += size;
		}
		_buffer.RemoveRange(0, pos);
		_bufferBase += pos;
	}

	/// <summary>End of stream: emit anything still buffered (only formats without frame headers need this).</summary>
	public virtual void Flush() { }

	public List<AudioFrame> TakeReady() {
		var r = new List<AudioFrame>(_ready);
		_ready.Clear();
		return r;
	}

	/// <summary>Reads <paramref name="count"/> bits starting at bit <paramref name="bitOffset"/> of the frame (MSB first).</summary>
	protected static int Bits(List<byte> b, int pos, int bitOffset, int count) {
		int value = 0;
		for (int i = 0; i < count; i++) {
			int bit = bitOffset + i;
			value = (value << 1) | ((b[pos + bit / 8] >> (7 - bit % 8)) & 1);
		}
		return value;
	}
}

/// <summary>
/// AC3 (Dolby Digital): sync frames of 1536 samples (32 ms at 48 kHz).
/// Header: 0B 77, CRC (2 bytes), fscod (2 bits) + frmsizecod (6 bits), then bsid, bsmod, acmod, ... lfeon.
/// </summary>
public sealed class Ac3Framer : AudioFramer {
	public const long FrameNs32 = 32_000_000;

	// Bitrate (kbps) by frmsizecod >> 1. At 48 kHz a frame is bitrate * 4 bytes.
	private static readonly int[] Bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

	public Ac3Framer() => SamplesPerFrame = 1536;

	public int Bitrate { get; private set; }
	public bool HasLfe { get; private set; }
	public override int FramesPerBlock => 3;
	protected override int HeaderLength => 8;

	protected override int ReadHeader(List<byte> b, int pos) {
		if (b[pos] != 0x0B || b[pos + 1] != 0x77 || (b[pos + 4] >> 6) != 0 || (b[pos + 4] & 0x3F) >= 38) return -1;
		Bitrate = Bitrates[(b[pos + 4] & 0x3F) >> 1];
		// BSI starts at byte 5: bsid (5 bits), bsmod (3), acmod (3 bits at the top of byte 6), optional mix levels, then lfeon.
		int acmod = b[pos + 6] >> 5;
		int bit = 6 * 8 + 3;
		if ((acmod & 1) != 0 && acmod != 1) bit += 2;   // cmixlev
		if ((acmod & 4) != 0) bit += 2;                 // surmixlev
		if (acmod == 2) bit += 2;                       // dsurmod
		HasLfe = Bits(b, pos, bit, 1) == 1;
		Channels = acmod switch { 0 => 2, 1 => 1, 2 => 2, 3 => 3, 4 => 3, 5 => 4, 6 => 4, _ => 5 } + (HasLfe ? 1 : 0);
		return Bitrate * 4;
	}
}

/// <summary>
/// DTS: frames of (NBLKS+1)*32 samples, usually 512 (10.67 ms at 48 kHz). On DVD the 16-bit big-endian form is used.
/// Header bits after the 32-bit sync 7F FE 80 01: FTYPE(1) SHORT(5) CPF(1) NBLKS(7) FSIZE(14) AMODE(6) SFREQ(4) RATE(5)
/// FIXEDBIT(1) DYNF(1) TIMEF(1) AUXF(1) HDCD(1) EXT_AUDIO_ID(3) EXT_AUDIO(1) ASPF(1) LFF(2) ...
/// The frame is FSIZE+1 bytes.
/// </summary>
public sealed class DtsFramer : AudioFramer {
	private static readonly int[] SampleRates = [0, 8000, 16000, 32000, 0, 0, 11025, 22050, 44100, 0, 0, 12000, 24000, 48000, 0, 0];

	public bool HasLfe { get; private set; }
	public override int FramesPerBlock => 9;
	protected override int HeaderLength => 12;

	protected override int ReadHeader(List<byte> b, int pos) {
		if (b[pos] != 0x7F || b[pos + 1] != 0xFE || b[pos + 2] != 0x80 || b[pos + 3] != 0x01) return -1;
		int nblks = Bits(b, pos, 39, 7);
		int fsize = Bits(b, pos, 46, 14);
		int amode = Bits(b, pos, 60, 6);
		int sfreq = Bits(b, pos, 66, 4);
		int lff = Bits(b, pos, 85, 2);
		if (fsize < 95 || nblks < 5 || SampleRates[sfreq] == 0) return -1;
		SamplesPerFrame = (nblks + 1) * 32;
		SampleRate = SampleRates[sfreq];
		HasLfe = lff is 1 or 2;
		Channels = amode switch { 0 => 1, 1 or 2 or 3 or 4 => 2, 5 or 6 => 3, 7 or 8 => 4, 9 => 5, 10 or 11 or 12 => 6, 13 => 7, _ => 8 } + (HasLfe ? 1 : 0);
		return fsize + 1;
	}
}
