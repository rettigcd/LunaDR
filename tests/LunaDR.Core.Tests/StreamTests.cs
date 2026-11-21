using LunaDR.Core.Mpeg;
using LunaDR.Core.Ripping;

namespace LunaDR.Core.Tests;

/// <summary>The stream framers (audio and video) and the VobSub palette.</summary>
public class StreamTests {
	private const long Ms = 1_000_000;

	[Fact]
	public void Lpcm_FirstAccessUnitPointerSkipsPointerMinus4() {
		// Regression (log 12:34): the samples came out one byte off. The pointer counts from the byte after itself,
		// and the payload we get starts 3 bytes later (after the LPCM header), so the skip is pointer − 4.
		var framer = new LpcmFramer(channels: 2, sampleRate: 48000, bitsPerSample: 16);
		var samples = Enumerable.Range(0, 6400).Select(i => (byte)(i * 7)).ToArray();   // one 1/30 s block of 16-bit stereo
		byte[] payload = [0xEE, 0xEE, .. samples];
		framer.Feed(payload, ptsNs: 5 * Ms, firstAccessUnit: 6);
		var block = Assert.Single(framer.TakeReady());
		Assert.Equal(5 * Ms, block.Pts);
		Assert.Equal(samples[1], block.Data[0]);   // big-endian sample bytes swapped to little-endian
		Assert.Equal(samples[0], block.Data[1]);
		Assert.Equal(6400, block.Data.Length);
	}

	/// <summary>Writes bits most significant first, for making frame headers.</summary>
	private sealed class Bits(int bytes) {
		public readonly byte[] Data = new byte[bytes];
		private int _pos;

		public Bits Put(int value, int count) {
			for (int i = count - 1; i >= 0; i--, _pos++)
				if (((value >> i) & 1) != 0) Data[_pos / 8] |= (byte)(0x80 >> (_pos % 8));
			return this;
		}
	}

	[Fact]
	public void Dts_ChannelCountComesFromTheStream() {
		// Regression: the IFO says 5 channels for 5.1 DTS; MakeMKV (and the stream) say 6.
		const int frameBytes = 1006;
		var header = new Bits(frameBytes)
			.Put(0x7FFE8001, 32)
			.Put(1, 1).Put(31, 5).Put(0, 1)   // FTYPE, SHORT, CPF
			.Put(15, 7)                       // NBLKS: 16 blocks of 32 = 512 samples
			.Put(frameBytes - 1, 14)          // FSIZE
			.Put(9, 6)                        // AMODE 9: 5 channels
			.Put(13, 4)                       // SFREQ: 48 kHz
			.Put(0, 5).Put(0, 1).Put(0, 1).Put(0, 1).Put(0, 1).Put(0, 1).Put(0, 3).Put(0, 1).Put(0, 1)
			.Put(1, 2);                       // LFF: LFE present
		var framer = new DtsFramer();
		framer.Feed(header.Data, 0, firstAccessUnit: 1);
		Assert.Single(framer.TakeReady());
		Assert.Equal(6, framer.Channels);
		Assert.Equal(512 * 1_000_000_000L / 48000, framer.FrameNs);
	}

	[Theory]
	[InlineData(0x00108080u, "000000")]
	[InlineData(0x00808080u, "828282")]   // regression: full-range conversion gave 808080
	[InlineData(0x00B08080u, "bababa")]
	[InlineData(0x00EB8080u, "ffffff")]
	public void VobSubPalette_UsesBt601StudioRange(uint ycrcb, string rgb) =>
		Assert.Equal(rgb, VobSubHeader.YCrCbToRgbHex(ycrcb));

	/// <summary>A tiny MPEG-2 elementary stream: sequence header, GOP header, and pictures with a picture coding extension and a fake slice.</summary>
	private static byte[] Gop(bool withSequenceHeader, params (char Type, int TemporalReference, bool RepeatFirstField)[] pictures) {
		var es = new List<byte>();
		if (withSequenceHeader) es.AddRange([0, 0, 1, 0xB3, 0x2D, 0x01, 0xE0, 0x34, 0xFF, 0xFF, 0xE0, 0x18]);   // 720x480, 16:9
		es.AddRange([0, 0, 1, 0xB8, 0x00, 0x08, 0x00, 0x00]);
		foreach (var (type, tr, rff) in pictures) {
			int code = type switch { 'I' => 1, 'P' => 2, _ => 3 };
			es.AddRange([0, 0, 1, 0x00, (byte)(tr >> 2), (byte)(((tr & 3) << 6) | (code << 3)), 0xFF, 0xF8]);
			es.AddRange([0, 0, 1, 0xB5, 0x8F, 0xFF, 0xF3, (byte)(rff ? 0x82 : 0x80), 0x80]);   // picture coding extension
			es.AddRange([0, 0, 1, 0x01, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]);               // a slice
		}
		return [.. es];
	}

	[Fact]
	public void Video_PulldownTimesComeFromFieldCounts() {
		// Film with 3:2 pulldown: pictures alternate 3 and 2 fields (repeat_first_field). Times are counted in fields
		// (1001/60000 s each) and converted once, so they don't drift (log 2026-11-18: this made chapter ends exact).
		// Decode order I P B B, display order I B B P.
		var framer = new Mpeg2VideoFramer();
		byte[] stream = [
			.. Gop(true, ('I', 0, true), ('P', 3, false), ('B', 1, true), ('B', 2, false)),
			.. Gop(false, ('I', 0, true), ('P', 1, false)),
		];
		// All in one Feed: regression for the framer's scan limit, which must be re-read after each picture is taken out of the buffer.
		framer.Feed(stream, 1000 * Ms);
		framer.Flush();
		var frames = framer.TakeReady();
		Assert.Equal(6, frames.Count);
		long Fields(int n) => 1000 * Ms + VideoFrame.FieldOffsetNs(n);
		Assert.Equal([Fields(0), Fields(3 + 3 + 2), Fields(3), Fields(3 + 3)], frames.Take(4).Select(f => f.Pts));   // I P B B
		Assert.Equal(1000 * Ms + VideoFrame.FieldsToNs(10), frames[4].Pts);   // next GOP starts after 3+3+2+2 fields
		Assert.True(frames[0].IsKeyFrame);
		Assert.Equal(3, frames[0].Fields);
	}

	[Fact]
	public void Video_PictureBeforeTheAnchor_IsPlacedInWhole90kHzTicksLikeMakeMkv() {
		// Regression (A Very Long Engagement title 5, log 2026-11-19): in an open GOP (display order B B I) the I picture has the PTS and the B pictures
		// are placed before it. 5 fields before the I is 7507.5 ticks; MakeMKV uses 7507, so the B lands half a tick later.
		// With exact field times our B was 500.4999 ms where MakeMKV's was 500.5056 ms: 500 vs 501 after rounding, in one GOP out of two.
		var framer = new Mpeg2VideoFramer();
		framer.Feed(Gop(true, ('I', 2, false), ('B', 0, false), ('B', 1, true)), 1000 * Ms);
		framer.Flush();
		var frames = framer.TakeReady();
		Assert.Equal(1000 * Ms, frames[0].Pts);
		Assert.Equal(1000 * Ms - 7507 * 100_000 / 9, frames[1].Pts);   // 2 + 3 fields before the I
	}
}
