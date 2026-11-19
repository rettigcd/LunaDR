using RipDvd.Core.Bluray;
using RipDvd.Core.Mpeg;

namespace RipDvd.Core.Tests;

/// <summary>Blu-ray stream handling with made-up data: transport packets and PES, and the codec helpers (BR4/BR5).</summary>
public class BdStreamTests {
	private static byte[] PtsBytes(int prefix, long pts) => [
		(byte)(prefix << 4 | (int)((pts >> 29) & 0x0E) | 1), (byte)(pts >> 22), (byte)(((pts >> 14) & 0xFE) | 1), (byte)(pts >> 7), (byte)((pts << 1) | 1),
	];

	/// <summary>A PES packet with PTS (and DTS) and optionally a stream ID extension (as Blu-ray TrueHD streams use).</summary>
	private static byte[] Pes(long pts, long? dts, byte[] payload, int extension = 0) {
		var header = new List<byte>();
		header.AddRange(PtsBytes(dts is null ? 2 : 3, pts));
		if (dts is { } d) header.AddRange(PtsBytes(1, d));
		int flags = (dts is null ? 0x80 : 0xC0) | (extension != 0 ? 0x01 : 0);
		if (extension != 0) header.AddRange([0x01, 0x81, (byte)(0x80 | extension)]);   // PES_extension_flag_2, length 1, stream_id_extension
		return [0, 0, 1, (byte)(extension != 0 ? 0xFD : 0xE0), 0, 0, 0x81, (byte)flags, (byte)header.Count, .. header, .. payload];
	}

	/// <summary>Splits a PES packet into 188-byte TS packets of a PID (the last one padded with an adaptation field).</summary>
	private static List<byte[]> TsPackets(int pid, byte[] pes) {
		var packets = new List<byte[]>();
		for (int pos = 0; pos < pes.Length; pos += 184) {
			int n = Math.Min(184, pes.Length - pos);
			var ts = new byte[188];
			ts[0] = 0x47;
			ts[1] = (byte)((pos == 0 ? 0x40 : 0) | pid >> 8);
			ts[2] = (byte)pid;
			if (n == 184) {
				ts[3] = 0x10;
				pes.AsSpan(pos, n).CopyTo(ts.AsSpan(4));
			} else {
				ts[3] = 0x30;
				ts[4] = (byte)(183 - n);
				if (ts[4] > 0) ts[5] = 0;
				for (int i = 6; i < 188 - n; i++) ts[i] = 0xFF;
				pes.AsSpan(pos, n).CopyTo(ts.AsSpan(188 - n));
			}
			packets.Add(ts);
		}
		return packets;
	}

	[Fact]
	public void Demuxer_JoinsPacketsIntoPes_WithTimesAndExtension() {
		var payload = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
		var demux = new TsDemuxer([0x1011, 0x1101]);
		foreach (var ts in TsPackets(0x1011, Pes(1_048_560, 1_044_806, payload))) demux.Feed(ts);
		foreach (var ts in TsPackets(0x1101, Pes(900_000, null, [0x0B, 0x77, 1, 2], extension: 0x76))) demux.Feed(ts);
		foreach (var ts in TsPackets(0x1200, Pes(1, null, [9]))) demux.Feed(ts);   // a PID nobody asked for
		demux.Flush();
		var pes = demux.TakeReady().OrderBy(p => p.Pid).ToList();
		Assert.Equal(2, pes.Count);
		Assert.Equal((0x1011, 1_048_560L, 1_044_806L, 0), (pes[0].Pid, pes[0].Pts!.Value, pes[0].Dts!.Value, pes[0].StreamIdExtension));
		Assert.Equal(payload, pes[0].Payload);
		Assert.Equal((900_000L, (long?)null, 0x76), (pes[1].Pts!.Value, pes[1].Dts, pes[1].StreamIdExtension));
		Assert.Equal([0x0B, 0x77, 1, 2], pes[1].Payload);
	}

	[Fact]
	public void Demuxer_CountsPacketsWithoutSyncByte() {
		var demux = new TsDemuxer([0x1011]);
		demux.Feed(new byte[188]);
		Assert.Equal(1, demux.BadPackets);
	}

	[Fact]
	public void H264_StartCodesBecomeLengths_AudLeftOut_IdrIsKey() {
		byte[] au = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 1, 0x67, 0x64, 0x00, 0x28, 0, 0, 1, 0x68, 0xEE, 0, 0, 1, 0x65, 0x88, 0x84, 0, 0];
		var nals = H264.NalUnits(au);
		Assert.Equal(4, nals.Count);
		Assert.True(H264.IsKeyFrame(nals));
		Assert.Equal([0, 0, 0, 4, 0x67, 0x64, 0x00, 0x28, 0, 0, 0, 2, 0x68, 0xEE, 0, 0, 0, 3, 0x65, 0x88, 0x84], H264.ToLengthPrefixed(nals));
		var avcC = H264.AvcC(nals[1].Span, nals[2].Span);
		Assert.Equal([1, 0x64, 0x00, 0x28, 0xFF, 0xE1, 0, 4], avcC[..8]);
		Assert.False(H264.IsKeyFrame(H264.NalUnits(new byte[] { 0, 0, 1, 0x41, 0x9A })));   // non-IDR slice
	}

	[Fact]
	public void Pgs_DisplaySetFromCompositionToEnd_IsOneBlock() {
		var pgs = new PgsAssembler();
		pgs.Feed([0x14, 0, 1, 0xAA], 5);           // palette before any composition: ignored
		pgs.Feed([0x16, 0, 2, 1, 2], 1_000);       // composition: starts the set, its time is the block's
		pgs.Feed([0x15, 0, 1, 7], 900);
		pgs.Feed([0x80, 0, 0], null);
		var set = Assert.Single(pgs.TakeReady());
		Assert.Equal(1_000, set.Pts);
		Assert.Equal([0x16, 0, 2, 1, 2, 0x15, 0, 1, 7, 0x80, 0, 0], set.Data);
	}

	[Fact]
	public void Pgs_EndSegmentInTheSamePacket_ClosesTheSet() {
		// Regression (The Goonies, log 2026-11-19): one PES packet can hold several segments; the END segment often follows the object segment.
		var pgs = new PgsAssembler();
		pgs.Feed([0x16, 0, 1, 1], 2_000);
		pgs.Feed([0x15, 0, 2, 7, 7, 0x80, 0, 0], 2_000);
		var set = Assert.Single(pgs.TakeReady());
		Assert.Equal([0x16, 0, 1, 1, 0x15, 0, 2, 7, 7, 0x80, 0, 0], set.Data);
	}

	[Fact]
	public void Pgs_SetWithoutEnd_IsClosedTwoSecondsLater_WithAnEndSegment() {
		// Regression (The Goonies): "clear the screen" sets came without an END segment and waited 33 s for the next composition,
		// too late to be placed in the file. They are closed once the video is 2 s past them, with an END segment added.
		var pgs = new PgsAssembler();
		pgs.Feed([0x16, 0, 1, 0, 0x17, 0, 1, 9], 10_000_000_000);
		pgs.Expire(11_000_000_000);
		Assert.Empty(pgs.TakeReady());
		pgs.Expire(12_500_000_000);
		var set = Assert.Single(pgs.TakeReady());
		Assert.Equal(10_000_000_000, set.Pts);
		Assert.Equal([0x16, 0, 1, 0, 0x17, 0, 1, 9, 0x80, 0, 0], set.Data);
	}

	[Fact]
	public void Lpcm_BigEndianToLittleEndian_PaddingChannelDropped() {
		// 3 channels (assignment 4) coded as 4, 16-bit, 48 kHz: one sample frame.
		byte[] pes = [0, 8, 0x41, 0x40, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x99, 0x99];
		Assert.Equal((3, 4, 48_000, 16), BdLpcm.Header(pes));
		Assert.Equal([0x02, 0x01, 0x04, 0x03, 0x06, 0x05], BdLpcm.ToLittleEndian(pes));
	}

	[Fact]
	public void TrueHd_UnitsSplitByLength_ChannelsFromMajorSync() {
		var framer = new TrueHdFramer();
		// Major sync unit (8 words = 16 bytes): F8 72 6F BA, 48 kHz, 8-channel assignment 0x000F = L/R, C, LFE, Ls/Rs → 6 channels.
		byte[] major = [0x00, 0x08, 0, 0, 0xF8, 0x72, 0x6F, 0xBA, 0x00, 0x00, 0x00, 0x0F, 0, 0, 0, 0];
		byte[] minor = [0x00, 0x03, 0, 0, 1, 2];   // 3 words = 6 bytes
		framer.Feed([.. major, .. minor, .. major], 1_000_000, 1);
		var frames = framer.TakeReady();
		Assert.Equal([16, 6], frames.Take(2).Select(f => f.Data.Length));
		Assert.Equal(6, framer.Channels);
		Assert.Equal(1_000_000 + 40 * 1_000_000_000L / 48_000, frames[1].Pts);   // 1/1200 s per unit
	}

	[Fact]
	public void DtsHd_ExtensionSubstreamStaysWithItsCore() {
		var core = DtsCore();
		// Extension substream: sync 64 58 20 25, user byte, then index (2 bits) = 0, header size type = 0, header size (8) and frame size - 1 (16 bits).
		var ext = new byte[40];
		ext[0] = 0x64; ext[1] = 0x58; ext[2] = 0x20; ext[3] = 0x25;
		WriteBits(ext, 43, 8, 15);
		WriteBits(ext, 51, 16, ext.Length - 1);
		var framer = new DtsFramer(keepExtension: true);
		framer.Feed([.. core, .. ext, .. core, .. ext, .. core], 0, 1);
		var frames = framer.TakeReady();
		Assert.Equal(core.Length + ext.Length, frames[0].Data.Length);
		Assert.Equal(2, frames.Count);   // the last core waits for its (possible) extension
	}

	/// <summary>A DTS core frame header: sync, NBLKS = 15 (512 samples), FSIZE = 1005 (1006 bytes), AMODE 9 (5 ch), SFREQ 13 (48 kHz).</summary>
	private static byte[] DtsCore() {
		var f = new byte[1006];
		f[0] = 0x7F; f[1] = 0xFE; f[2] = 0x80; f[3] = 0x01;
		WriteBits(f, 32, 1, 1);
		WriteBits(f, 33, 5, 31);
		WriteBits(f, 39, 7, 15);
		WriteBits(f, 46, 14, 1005);
		WriteBits(f, 60, 6, 9);
		WriteBits(f, 66, 4, 13);
		return f;
	}

	private static void WriteBits(byte[] b, int bit, int count, int value) {
		for (int i = 0; i < count; i++) {
			int pos = bit + i;
			int v = value >> (count - 1 - i) & 1;
			b[pos / 8] = (byte)(b[pos / 8] & ~(0x80 >> pos % 8) | v << (7 - pos % 8));
		}
	}

	[Fact]
	public void Vc1_SizeAndAspectFromSequenceHeader() {
		// Advanced profile header after 00 00 01 0F: max coded 720x480, display_ext with 720x480 and aspect code 5 (40:33, 16:9 NTSC).
		var seq = new byte[20];
		seq[2] = 1; seq[3] = 0x0F;
		WriteBits(seq, 32 + 0, 2, 3);
		WriteBits(seq, 32 + 16, 12, 720 / 2 - 1);
		WriteBits(seq, 32 + 28, 12, 480 / 2 - 1);
		WriteBits(seq, 32 + 46, 1, 1);
		WriteBits(seq, 32 + 47, 14, 719);
		WriteBits(seq, 32 + 61, 14, 479);
		WriteBits(seq, 32 + 75, 1, 1);
		WriteBits(seq, 32 + 76, 4, 5);
		Assert.Equal((720, 480, 873, 480), Vc1.Size(seq));
		var bih = Vc1.CodecPrivate(seq, null, 720, 480);
		Assert.Equal("WVC1", System.Text.Encoding.ASCII.GetString(bih, 16, 4));
		Assert.Equal(bih.Length, BitConverter.ToInt32(bih, 0));
	}

	[Fact]
	public void Eac3_FrameSizeAndChannels() {
		// frmsiz = 0x0FF → 512 bytes; fscod 0 (48 kHz), numblkscod 3 (6 blocks), acmod 7, lfe → 6 channels; bsid 16.
		var frame = new byte[512];
		frame[0] = 0x0B; frame[1] = 0x77; frame[2] = 0x00; frame[3] = 0xFF; frame[4] = 0x3F; frame[5] = 16 << 3;
		var framer = new Eac3Framer();
		framer.Feed([.. frame, .. frame], 0, 1);
		Assert.Equal(2, framer.TakeReady().Count);
		Assert.Equal((6, 1536), (framer.Channels, framer.SamplesPerFrame));
	}
}
