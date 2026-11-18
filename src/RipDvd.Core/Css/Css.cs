// CSS descrambling and title-key recovery.
//
// Ported to C# from libdvdcss src/css.c (dvdcss_unscramble, RecoverTitleKey, AttackPattern):
//   Copyright (C) 1999-2001 VideoLAN; licensed under the GNU GPL v2 or later.
//   The key-recovery attack is Ethan Hawke's DeCSSPlus attack as modified in libdvdcss.
//
// How CSS scrambles a sector:
//   * Each 2048-byte sector is an MPEG-2 pack. If the PES_scrambling_control bits
//     (byte 0x14, mask 0x30) are set, bytes 0x80..0x7FF are scrambled; 0x00..0x7F are clear.
//   * The keystream comes from two LFSRs (17-bit and 25-bit) seeded with
//     (title key XOR the 5 "seed" bytes at 0x54..0x58 of that sector).
//   * Each scrambled byte b becomes Tab1[b] XOR keystream-byte.

using static RipDvd.Core.Css.CssTables;

namespace RipDvd.Core.Css;

/// <summary>A 5-byte CSS key (title key, disc key, ...).</summary>
public readonly record struct CssKey(byte K0, byte K1, byte K2, byte K3, byte K4) {
	public static readonly CssKey Zero = default;
	public bool IsZero => (K0 | K1 | K2 | K3 | K4) == 0;
	public byte this[int i] => i switch { 0 => K0, 1 => K1, 2 => K2, 3 => K3, 4 => K4, _ => throw new IndexOutOfRangeException() };
	public override string ToString() => $"{K0:X2}:{K1:X2}:{K2:X2}:{K3:X2}:{K4:X2}";
}

public static class Css {
	public const int SectorSize = 2048;

	/// <summary>
	/// True if the sector's PES packet is marked scrambled. System header (0xBB),
	/// padding (0xBE) and private stream 2 / navigation (0xBF) packets never are.
	/// </summary>
	public static bool IsScrambled(ReadOnlySpan<byte> sector) {
		if (sector[0] != 0 || sector[1] != 0 || sector[2] != 1 || sector[3] != 0xBA) return false;
		byte streamId = sector[0x11];
		if (streamId is 0xBB or 0xBE or 0xBF) return false;
		return (sector[0x14] & 0x30) != 0;
	}

	/// <summary>Descramble one sector in place with the title key. Clears the scrambling bits.</summary>
	public static void Unscramble(CssKey key, Span<byte> sector) {
		if ((sector[0x14] & 0x30) == 0) return;

		uint t1 = (uint)(key[0] ^ sector[0x54]) | 0x100;
		uint t2 = (uint)(key[1] ^ sector[0x55]);
		uint t3 = (uint)(key[2] | (key[3] << 8) | (key[4] << 16))
				^ (uint)(sector[0x56] | (sector[0x57] << 8) | (sector[0x58] << 16));
		uint t4 = t3 & 7;
		t3 = t3 * 2 + 8 - t4;
		uint t5 = 0;

		for (int i = 0x80; i < SectorSize; i++) {
			// Step LFSR1 (17 bit) one byte.
			t4 = (uint)(Tab2[(int)t2] ^ Tab3[(int)t1]);
			t2 = t1 >> 1;
			t1 = ((t1 & 1) << 8) ^ t4;
			t4 = Tab5[(int)t4];
			// Step LFSR2 (25 bit) one byte.
			uint t6 = (((((((t3 >> 3) ^ t3) >> 1) ^ t3) >> 8) ^ t3) >> 5) & 0xFF;
			t3 = (t3 << 8) | t6;
			t6 = Tab4[(int)t6];
			// Combine the two outputs with carry.
			t5 += t6 + t4;
			sector[i] = (byte)(Tab1[sector[i]] ^ (t5 & 0xFF));
			t5 >>= 8;
		}

		sector[0x14] &= 0xCF;   // mark as no longer scrambled
	}

	/// <summary>
	/// Try to recover the title key from one scrambled sector (libdvdcss "AttackPattern").
	/// MPEG data often ends in a repeating pattern (e.g. stuffing). If the clear bytes just
	/// before 0x80 repeat with some cycle, guess that the first scrambled bytes continue the
	/// pattern; that gives 10 bytes of known plaintext, enough to solve for the key.
	/// </summary>
	public static CssKey? AttackPattern(ReadOnlySpan<byte> sector) {
		int bestLength = 0, bestPeriod = 0;
		for (int period = 2; period < 0x30; period++) {
			for (int j = period + 1; j < 0x80 && sector[0x7F - (j % period)] == sector[0x7F - j]; j++) {
				if (j > bestLength) { bestLength = j; bestPeriod = period; }
			}
		}

		if (bestLength <= 3 || bestLength / bestPeriod < 2) return null;

		int plainStart = 0x80 - (bestLength / bestPeriod) * bestPeriod;
		return RecoverTitleKey(sector.Slice(0x80, 10), sector.Slice(plainStart, 10), sector.Slice(0x54, 5));
	}

	/// <summary>
	/// Given 10 scrambled bytes and their guessed plaintext, search the 2^16 possible LFSR1
	/// states, deduce LFSR2, and check the guess against the remaining bytes.
	/// Returns the title key, or null if no state fits. (libdvdcss RecoverTitleKey)
	/// </summary>
	private static CssKey? RecoverTitleKey(ReadOnlySpan<byte> crypted, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> seed) {
		Span<byte> buffer = stackalloc byte[10];
		for (int i = 0; i < 10; i++) buffer[i] = (byte)(Tab1[crypted[i]] ^ plain[i]);

		CssKey? found = null;
		for (uint attempt = 0; attempt < 0x10000; attempt++) {
			uint t1 = (attempt >> 8) | 0x100;
			uint t2 = attempt & 0xFF;
			uint t3 = 0, t4, t5 = 0, t6;
			int i;

			// Run 4 bytes: with LFSR1 known, the output bytes reveal LFSR2's output bytes.
			for (i = 0; i < 4; i++) {
				t4 = (uint)(Tab2[(int)t2] ^ Tab3[(int)t1]);
				t2 = t1 >> 1;
				t1 = ((t1 & 1) << 8) ^ t4;
				t4 = Tab5[(int)t4];
				t6 = buffer[i];
				if (t5 != 0) t6 = (t6 + 0xFF) & 0xFF;
				if (t6 < t4) t6 += 0x100;
				t6 -= t4;
				t5 += t6 + t4;
				t6 = Tab4[(int)t6];
				t3 = (t3 << 8) | t6;
				t5 >>= 8;
			}

			uint candidate = t3;

			// Run 6 more bytes to validate.
			for (; i < 10; i++) {
				t4 = (uint)(Tab2[(int)t2] ^ Tab3[(int)t1]);
				t2 = t1 >> 1;
				t1 = ((t1 & 1) << 8) ^ t4;
				t4 = Tab5[(int)t4];
				t6 = (((((((t3 >> 3) ^ t3) >> 1) ^ t3) >> 8) ^ t3) >> 5) & 0xFF;
				t3 = (t3 << 8) | t6;
				t6 = Tab4[(int)t6];
				t5 += t6 + t4;
				if ((t5 & 0xFF) != buffer[i]) break;
				t5 >>= 8;
			}

			if (i != 10) continue;

			// Step LFSR2 back 4 bytes to its initial state.
			t3 = candidate;
			for (i = 0; i < 4; i++) {
				uint expected = t3 & 0xFF;
				t3 >>= 8;
				for (uint j = 0; j < 256; j++) {
					t3 = (t3 & 0x1FFFF) | (j << 17);
					t6 = (((((((t3 >> 3) ^ t3) >> 1) ^ t3) >> 8) ^ t3) >> 5) & 0xFF;
					if (t6 == expected) break;
				}
			}

			// Undo the "t3 * 2 + 8 - (t3 & 7)" transform applied when the key is loaded.
			t4 = (t3 >> 1) - 4;
			for (t5 = 0; t5 < 8; t5++) {
				uint k = t4 + t5;
				if (k * 2 + 8 - (k & 7) == t3) {
					found = new CssKey(
						(byte)((attempt >> 8) ^ seed[0]),
						(byte)((attempt & 0xFF) ^ seed[1]),
						(byte)((k & 0xFF) ^ seed[2]),
						(byte)(((k >> 8) & 0xFF) ^ seed[3]),
						(byte)(((k >> 16) & 0xFF) ^ seed[4]));
				}
			}
		}
		return found;
	}
}
