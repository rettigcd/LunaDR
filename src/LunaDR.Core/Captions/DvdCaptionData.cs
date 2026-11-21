namespace LunaDR.Core.Captions;

/// <summary>
/// Closed captions on DVD are carried in MPEG-2 user data (start code 00 00 01 B2), usually once per GOP.
/// Layout after the start code:
///   'C' 'C' 0x01 0xF8,
///   one byte: bit 7 = odd field first, bits 5..1 = block count, bit 0 = one extra field block,
///   then (count * 2 + extra) blocks of 3 bytes: marker (0xFF = odd field = CC1/CC2, 0xFE = even field), byte 1, byte 2.
/// The odd-field pairs are in display order, one per displayed frame of the GOP.
/// </summary>
public static class DvdCaptionData {
	private static ReadOnlySpan<byte> Signature => [0x00, 0x00, 0x01, 0xB2, 0x43, 0x43, 0x01, 0xF8];

	/// <summary>
	/// Finds DVD caption user data in a coded picture, returns its odd-field (CC1/CC2) byte pairs, and removes the user data from the picture.
	/// MakeMKV removes it too when it turns the captions into a text track.
	/// </summary>
	public static List<(byte, byte)> ExtractAndStrip(ref byte[] picture) {
		var pairs = new List<(byte, byte)>();
		var span = picture.AsSpan();
		int start = span.IndexOf(Signature);
		if (start < 0) return pairs;

		// The user data runs to the next start code.
		int end = span[(start + 4)..].IndexOf([(byte)0, (byte)0, (byte)1]);
		end = end < 0 ? span.Length : start + 4 + end;

		int p = start + Signature.Length;
		if (p < end) {
			int count = (span[p] >> 1) & 0x1F;
			int blocks = count * 2 + (span[p] & 1);
			p++;
			for (int i = 0; i < blocks && p + 3 <= end; i++, p += 3) {
				if (span[p] == 0xFF) pairs.Add((span[p + 1], span[p + 2]));
			}
		}

		var stripped = new byte[picture.Length - (end - start)];
		span[..start].CopyTo(stripped);
		span[end..].CopyTo(stripped.AsSpan(start));
		picture = stripped;
		return pairs;
	}
}
