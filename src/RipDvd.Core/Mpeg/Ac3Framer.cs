namespace RipDvd.Core.Mpeg;

/// <summary>One AC3 sync frame (1536 samples = 32 ms at 48 kHz).</summary>
public sealed class AudioFrame {
	public required byte[] Data { get; init; }
	/// <summary>Presentation time in nanoseconds on the title timeline.</summary>
	public required long Pts { get; init; }
}

/// <summary>
/// Splits an AC3 stream into sync frames and timestamps them.
/// Frame header: 0B 77, CRC (2 bytes), then fscod (2 bits) + frmsizecod (6 bits).
/// On DVD the PES header's "first access unit" pointer says which frame the PTS belongs to;
/// frames without their own PTS follow the previous one by 32 ms.
/// </summary>
public sealed class Ac3Framer {
	public const long FrameNs = 32_000_000;

	// Bitrate (kbps) by frmsizecod >> 1. At 48 kHz a frame is bitrate * 4 bytes.
	private static readonly int[] Bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

	private readonly List<byte> _buffer = [];
	private readonly List<(long Offset, long Pts)> _ptsMarks = [];
	private long _bufferBase;
	private long _nextPts = -1;
	private readonly List<AudioFrame> _ready = [];

	public int Bitrate { get; private set; }
	public int Channels { get; private set; }
	public bool HasLfe { get; private set; }
	public long SkippedBytes { get; private set; }

	/// <param name="firstAccessUnit">1-based offset in the payload of the frame the PTS is for (0 = none).</param>
	public void Feed(ReadOnlySpan<byte> payload, long? ptsNs, int firstAccessUnit) {
		if (ptsNs is { } p && firstAccessUnit > 0)
			_ptsMarks.Add((_bufferBase + _buffer.Count + firstAccessUnit - 1, p));
		foreach (byte b in payload) _buffer.Add(b);

		int pos = 0;
		while (pos + 8 <= _buffer.Count) {
			if (_buffer[pos] != 0x0B || _buffer[pos + 1] != 0x77 || (_buffer[pos + 4] >> 6) != 0 || (_buffer[pos + 4] & 0x3F) >= 38) {
				pos++;
				SkippedBytes++;
				continue;
			}
			int size = Bitrates[(_buffer[pos + 4] & 0x3F) >> 1] * 4;
			if (pos + size > _buffer.Count) break;
			ReadInfo(pos);

			long frameOffset = _bufferBase + pos;
			long pts = _nextPts;
			while (_ptsMarks.Count > 0 && _ptsMarks[0].Offset <= frameOffset) {
				if (_ptsMarks[0].Offset == frameOffset || pts < 0) pts = _ptsMarks[0].Pts;
				_ptsMarks.RemoveAt(0);
			}
			if (pts < 0) pts = 0;
			_ready.Add(new AudioFrame { Data = _buffer.GetRange(pos, size).ToArray(), Pts = pts });
			_nextPts = pts + FrameNs;
			pos += size;
		}
		_buffer.RemoveRange(0, pos);
		_bufferBase += pos;
	}

	public List<AudioFrame> TakeReady() {
		var r = new List<AudioFrame>(_ready);
		_ready.Clear();
		return r;
	}

	/// <summary>Bit rate, channel layout (acmod) and LFE flag from the frame header.</summary>
	private void ReadInfo(int pos) {
		Bitrate = Bitrates[(_buffer[pos + 4] & 0x3F) >> 1];
		// BSI starts at byte 5: bsid (5 bits), bsmod (3), acmod (3), then optional fields, then lfeon.
		int acmod = _buffer[pos + 6] >> 5;
		int bit = 3;   // bits consumed in byte 6 after acmod
		long bits = ((long)_buffer[pos + 6] << 8) | _buffer[pos + 7];
		int skip = 0;
		if ((acmod & 1) != 0 && acmod != 1) skip += 2;   // cmixlev
		if ((acmod & 4) != 0) skip += 2;                 // surmixlev
		if (acmod == 2) skip += 2;                       // dsurmod
		int lfeBit = 15 - bit - skip;                    // position in the 16-bit window
		HasLfe = ((bits >> lfeBit) & 1) != 0;
		Channels = acmod switch { 0 => 2, 1 => 1, 2 => 2, 3 => 3, 4 => 3, 5 => 4, 6 => 4, _ => 5 } + (HasLfe ? 1 : 0);
	}
}
