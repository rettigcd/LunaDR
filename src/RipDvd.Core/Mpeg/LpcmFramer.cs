namespace RipDvd.Core.Mpeg;

/// <summary>
/// DVD LPCM (uncompressed audio) to little-endian PCM blocks, as MakeMKV writes it (Matroska A_PCM/INT/LIT).
///
/// On DVD, LPCM samples are big-endian and interleaved by channel; there are no frame headers to find in the data.
/// MakeMKV cuts the samples into blocks of exactly 1/30 s (1,600 samples per channel at 48 kHz: 6,400 bytes for 16-bit stereo).
/// Block times are counted in samples from the first PES timestamp, so the 33.33 ms blocks don't drift.
/// Only 16-bit is converted for now; 20/24-bit DVD LPCM packs samples in groups (planned, see the plan).
/// </summary>
public sealed class LpcmFramer : AudioFramer {
	private readonly int _bytesPerSample;
	private readonly int _bytesPerBlock;
	private readonly List<byte> _pcm = [];
	private long _firstPts = -1;
	private long _samplesOut;

	public LpcmFramer(int channels, int sampleRate, int bitsPerSample) {
		if (bitsPerSample != 16) throw new NotSupportedException($"{bitsPerSample}-bit LPCM is not supported yet (16-bit only).");
		Channels = channels;
		SampleRate = sampleRate;
		BitsPerSample = bitsPerSample;
		SamplesPerFrame = sampleRate / 30;
		_bytesPerSample = bitsPerSample / 8;
		_bytesPerBlock = SamplesPerFrame * channels * _bytesPerSample;
	}

	public int BitsPerSample { get; }
	public override int FramesPerBlock => 1;
	protected override int HeaderLength => 1;
	protected override int ReadHeader(List<byte> b, int pos) => -1;   // unused: Feed is overridden

	public override void Feed(ReadOnlySpan<byte> payload, long? ptsNs, int firstAccessUnit) {
		if (_firstPts < 0) {
			if (ptsNs is null) return;   // wait for a timestamp to start from
										 // The PTS belongs to the first audio frame starting at the first-access-unit pointer; skip bytes before it.
										 // The pointer counts from the byte after itself; the payload we get starts 3 bytes later (after the LPCM header), hence -4.
			int skip = Math.Clamp(firstAccessUnit - 4, 0, payload.Length);
			payload = payload[skip..];
			_firstPts = ptsNs.Value;
		}
		// Keep the bytes as they come (a sample may be split across packets) and convert when a block is cut.
		foreach (byte b in payload) _pcm.Add(b);
		while (_pcm.Count >= _bytesPerBlock) Emit(_bytesPerBlock);
	}

	/// <summary>End of stream: write the last, shorter block (whole samples only).</summary>
	public override void Flush() {
		int sampleFrame = Channels * _bytesPerSample;
		int rest = _pcm.Count - _pcm.Count % sampleFrame;
		if (rest > 0 && _firstPts >= 0) Emit(rest);
	}

	private void Emit(int bytes) {
		var block = _pcm.GetRange(0, bytes).ToArray();
		_pcm.RemoveRange(0, bytes);
		// Big-endian to little-endian: swap the two bytes of each 16-bit sample.
		for (int i = 0; i + 1 < block.Length; i += 2) (block[i], block[i + 1]) = (block[i + 1], block[i]);
		long pts = _firstPts + _samplesOut * 1_000_000_000L / SampleRate;
		_ready.Add(new AudioFrame { Data = block, Pts = pts });
		_samplesOut += bytes / (Channels * _bytesPerSample);
	}
}
