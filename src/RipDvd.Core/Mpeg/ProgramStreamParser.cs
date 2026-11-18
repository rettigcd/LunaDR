namespace RipDvd.Core.Mpeg;

/// <summary>
/// Splits one 2048-byte DVD sector (an MPEG-2 program stream "pack") into PES packets.
///
/// Pack layout:  00 00 01 BA, 10 more header bytes, (byte 13 &amp; 7) stuffing bytes,
///               then one or more packets: 00 00 01 &lt;stream id&gt; &lt;16-bit length&gt; &lt;data&gt;.
/// MPEG-2 PES header (video, audio, private stream 1):
///               byte 6 flags, byte 7 flags (bits 7-6 = PTS/DTS present), byte 8 header length,
///               PTS (5 bytes) and DTS (5 bytes) follow, payload starts at 9 + header length.
/// Private stream 1 (0xBD) payload starts with a substream id byte:
///               0x20-0x3F subpicture, 0x80-0x87 AC3, 0x88-0x8F DTS, 0xA0-0xA7 LPCM.
///               AC3/DTS add 3 more bytes (frame count, first access unit pointer);
///               LPCM adds 6 more bytes.
/// </summary>
public static class ProgramStreamParser {
	public const int SectorSize = 2048;

	/// <summary>Parse the PES packets of one descrambled sector. The payloads point into <paramref name="sector"/>.</summary>
	public static List<PesPacket> Parse(ReadOnlyMemory<byte> sector) {
		var s = sector.Span;
		var packets = new List<PesPacket>(2);
		if (!(s[0] == 0 && s[1] == 0 && s[2] == 1 && s[3] == 0xBA))
			throw new InvalidDataException("Sector does not start with an MPEG-2 pack header.");
		if ((s[4] & 0xC0) != 0x40)
			throw new InvalidDataException("MPEG-1 pack header; DVDs use MPEG-2.");

		int pos = 14 + (s[13] & 0x07);
		while (pos + 6 <= SectorSize) {
			if (!(s[pos] == 0 && s[pos + 1] == 0 && s[pos + 2] == 1)) break;
			byte id = s[pos + 3];
			int length = (s[pos + 4] << 8) | s[pos + 5];
			int dataStart = pos + 6;
			int end = Math.Min(dataStart + length, SectorSize);
			pos = end;

			switch (id) {
				case 0xBB: // system header
				case 0xBE: // padding
					continue;
				case 0xBF: // private stream 2: nav pack PCI/DSI (no PES header extension)
					packets.Add(new PesPacket {
						Stream = new StreamId(StreamKind.Navigation, s[dataStart]),
						Payload = sector[dataStart..end],
					});
					continue;
			}

			if (id != 0xBD && id != 0xE0 && (id & 0xF8) != 0xC0) continue;   // not a DVD stream

			int headerLength = s[dataStart + 2];
			int flags = s[dataStart + 1];
			long? pts = (flags & 0x80) != 0 ? ReadTimestamp(s[(dataStart + 3)..]) : null;
			long? dts = (flags & 0xC0) == 0xC0 ? ReadTimestamp(s[(dataStart + 8)..]) : null;
			int payload = dataStart + 3 + headerLength;

			if (id == 0xE0) {
				packets.Add(new PesPacket { Stream = new StreamId(StreamKind.Video, 0), Pts = pts, Dts = dts, Payload = sector[payload..end] });
			} else if ((id & 0xF8) == 0xC0) {
				packets.Add(new PesPacket { Stream = new StreamId(StreamKind.MpegAudio, id & 7), Pts = pts, Payload = sector[payload..end] });
			} else {
				byte sub = s[payload];
				if (sub >= 0x20 && sub <= 0x3F) {
					packets.Add(new PesPacket { Stream = new StreamId(StreamKind.Subpicture, sub & 0x1F), Pts = pts, Payload = sector[(payload + 1)..end] });
				} else if (sub >= 0x80 && sub <= 0x8F) {
					var kind = sub <= 0x87 ? StreamKind.Ac3 : StreamKind.Dts;
					packets.Add(new PesPacket {
						Stream = new StreamId(kind, sub & 7),
						Pts = pts,
						FrameCount = s[payload + 1],
						FirstAccessUnit = (s[payload + 2] << 8) | s[payload + 3],
						Payload = sector[(payload + 4)..end],
					});
				} else if (sub >= 0xA0 && sub <= 0xA7) {
					packets.Add(new PesPacket {
						Stream = new StreamId(StreamKind.Lpcm, sub & 7),
						Pts = pts,
						FrameCount = s[payload + 1],
						FirstAccessUnit = (s[payload + 2] << 8) | s[payload + 3],
						Payload = sector[(payload + 7)..end],
					});
				} else {
					packets.Add(new PesPacket { Stream = new StreamId(StreamKind.Other, sub), Pts = pts, Payload = sector[(payload + 1)..end] });
				}
			}
		}
		return packets;
	}

	/// <summary>33-bit PTS/DTS spread over 5 bytes with marker bits.</summary>
	public static long ReadTimestamp(ReadOnlySpan<byte> b) =>
		((long)((b[0] >> 1) & 0x07) << 30) |
		((long)b[1] << 22) |
		((long)(b[2] >> 1) << 15) |
		((long)b[3] << 7) |
		((long)b[4] >> 1);
}
