// CSS drive authentication: the challenge/response handshake that makes a DVD drive allow reading scrambled sectors.
//
// ==== LICENSE NOTE: GPL v2 or later (ported from libdvdcss) ====
// CryptKey and the handshake sequence are a C# port of libdvdcss src/css.c (CryptKey, GetBusKey),
// Copyright (C) 1999-2001 VideoLAN, based on css-auth by Derek Fawcus; licensed under the GNU GPL v2 or later.
// Because of this file, the program as a whole must be distributed under the GPL.
// GPL-derived files in this repo (replace all of them to drop the GPL requirement):
//   src/LunaDR.Core/Css/Css.cs
//   src/LunaDR.Core/Css/CssTables.cs
//   src/LunaDR.Core/Css/CssAuthentication.cs  (this file)
// ================================================================
//
// The handshake (MMC "REPORT KEY" / "SEND KEY" commands, wrapped by the OS):
//   1. Get an authentication grant id (AGID) from the drive.
//   2. Send the drive a 10-byte challenge; read back its 5-byte "key 1". The host finds which of 32 variants of CryptKey produced it.
//   3. Read the drive's own challenge; answer with "key 2" = CryptKey(1, variant, challenge).
//   4. Both sides now share a bus key. Reading the disc key afterwards sets the drive's "authentication success flag",
//      after which the drive returns scrambled sectors instead of failing with a copy-protection error.

using static LunaDR.Core.Css.CssTables;

namespace LunaDR.Core.Css;

/// <summary>The drive commands the handshake needs; implemented per operating system.</summary>
public interface ICssDrive : IDisposable {
	/// <summary>Starts an authentication session and returns its AGID.</summary>
	int StartSession();
	void EndSession(int agid);
	void SendChallenge(int agid, ReadOnlySpan<byte> challenge);   // 10 bytes
	byte[] ReadKey1(int agid);                                      // 5 bytes
	byte[] ReadChallenge(int agid);                                 // 10 bytes
	void SendKey2(int agid, ReadOnlySpan<byte> key);                // 5 bytes
	byte[] ReadDiscKey(int agid);                                   // 2048 bytes (encrypted disc key block)
}

public static class CssAuthentication {
	/// <summary>
	/// Runs the handshake and reads the disc key, which unlocks reading scrambled sectors.
	/// Returns the bus key. Throws <see cref="IOException"/> if the drive refuses.
	/// </summary>
	public static byte[] Unlock(ICssDrive drive) {
		int agid = StartSession(drive);
		try {
			byte[] busKey = Authenticate(drive, agid);
			drive.ReadDiscKey(agid);   // the read itself sets the authentication success flag
			return busKey;
		} catch {
			TryEnd(drive, agid);
			throw;
		}
	}

	/// <summary>Gets an AGID; if the drive has none free (a hung earlier session), invalidates AGIDs 0..3 and retries, as libdvdcss does.</summary>
	private static int StartSession(ICssDrive drive) {
		try {
			return drive.StartSession();
		} catch (IOException) {
			for (int i = 0; i < 4; i++) {
				TryEnd(drive, i);
				try {
					return drive.StartSession();
				} catch (IOException) {
					// try the next one
				}
			}
			throw;
		}
	}

	private static void TryEnd(ICssDrive drive, int agid) {
		try {
			drive.EndSession(agid);
		} catch (IOException) {
			// nothing to clean up
		}
	}

	private static byte[] Authenticate(ICssDrive drive, int agid) {
		var challenge = new byte[10];
		var buffer = new byte[10];
		for (int i = 0; i < 10; i++) challenge[i] = (byte)i;   // any values work
		for (int i = 0; i < 10; i++) buffer[9 - i] = challenge[i];   // the drive wants the bytes reversed
		drive.SendChallenge(agid, buffer);

		var reply = drive.ReadKey1(agid);
		var key1 = new byte[5];
		for (int i = 0; i < 5; i++) key1[i] = reply[4 - i];

		int variant = -1;
		var check = new byte[5];
		for (int v = 0; v < 32 && variant < 0; v++) {
			CryptKey(0, v, challenge, check);
			if (check.AsSpan().SequenceEqual(key1)) variant = v;
		}
		if (variant < 0) throw new IOException("The DVD drive's authentication reply matched no CSS variant.");

		var driveChallenge = drive.ReadChallenge(agid);
		for (int i = 0; i < 10; i++) challenge[i] = driveChallenge[9 - i];
		var key2 = new byte[5];
		CryptKey(1, variant, challenge, key2);
		var send = new byte[5];
		for (int i = 0; i < 5; i++) send[4 - i] = key2[i];
		drive.SendKey2(agid, send);

		key1.CopyTo(challenge, 0);
		key2.CopyTo(challenge, 5);
		var busKey = new byte[5];
		CryptKey(2, variant, challenge, busKey);
		return busKey;
	}

	private static readonly byte[][] PermChallenge = [
		[1, 3, 0, 7, 5, 2, 9, 6, 4, 8],
		[6, 1, 9, 3, 8, 5, 7, 4, 0, 2],
		[4, 0, 3, 5, 7, 2, 8, 6, 1, 9],
	];

	private static readonly byte[][] PermVariant = [
		[0x0a, 0x08, 0x0e, 0x0c, 0x0b, 0x09, 0x0f, 0x0d, 0x1a, 0x18, 0x1e, 0x1c, 0x1b, 0x19, 0x1f, 0x1d,
		 0x02, 0x00, 0x06, 0x04, 0x03, 0x01, 0x07, 0x05, 0x12, 0x10, 0x16, 0x14, 0x13, 0x11, 0x17, 0x15],
		[0x12, 0x1a, 0x16, 0x1e, 0x02, 0x0a, 0x06, 0x0e, 0x10, 0x18, 0x14, 0x1c, 0x00, 0x08, 0x04, 0x0c,
		 0x13, 0x1b, 0x17, 0x1f, 0x03, 0x0b, 0x07, 0x0f, 0x11, 0x19, 0x15, 0x1d, 0x01, 0x09, 0x05, 0x0d],
	];

	private static readonly byte[] Variants = [
		0xB7, 0x74, 0x85, 0xD0, 0xCC, 0xDB, 0xCA, 0x73, 0x03, 0xFE, 0x31, 0x03, 0x52, 0xE0, 0xB7, 0x42,
		0x63, 0x16, 0xF2, 0x2A, 0x79, 0x52, 0xFF, 0x1B, 0x7A, 0x11, 0xCA, 0x1A, 0x9B, 0x40, 0xAD, 0x01,
	];

	private static readonly byte[] Secret = [0x55, 0xD6, 0xC4, 0xC5, 0x28];

	/// <summary>
	/// The CSS authentication cipher (libdvdcss CryptKey): turns a 10-byte challenge into a 5-byte key.
	/// keyType 0 = key 1 (drive's answer to our challenge), 1 = key 2 (our answer to the drive), 2 = bus key.
	/// </summary>
	public static void CryptKey(int keyType, int variant, ReadOnlySpan<byte> challenge, Span<byte> key) {
		Span<byte> scratch = stackalloc byte[10];
		Span<byte> bits = stackalloc byte[30];
		Span<byte> tmp1 = stackalloc byte[5];
		Span<byte> tmp2 = stackalloc byte[5];

		for (int i = 9; i >= 0; i--) scratch[i] = challenge[PermChallenge[keyType][i]];
		int cssVariant = keyType == 0 ? variant : PermVariant[keyType - 1][variant];

		// Seed two LFSRs from the second half of the input mixed with the secret.
		for (int i = 4; i >= 0; i--) tmp1[i] = (byte)(scratch[5 + i] ^ Secret[i] ^ CryptTab2[i]);
		uint lfsr0 = (uint)((tmp1[0] << 17) | (tmp1[1] << 9) | ((tmp1[2] & ~7) << 1) | 8 | (tmp1[2] & 7));
		uint lfsr1 = (uint)((tmp1[3] << 9) | 0x100 | tmp1[4]);

		// Generate 30 bytes of key stream: the two LFSR outputs (inverted) added with carry.
		int index = bits.Length;
		int carry = 0;
		do {
			int val = 0;
			for (int bit = 0; bit < 8; bit++) {
				uint o0 = ((lfsr0 >> 24) ^ (lfsr0 >> 21) ^ (lfsr0 >> 20) ^ (lfsr0 >> 12)) & 1;
				lfsr0 = (lfsr0 << 1) | o0;
				uint o1 = ((lfsr1 >> 16) ^ (lfsr1 >> 2)) & 1;
				lfsr1 = (lfsr1 << 1) | o1;
				int combined = (o1 == 0 ? 1 : 0) + carry + (o0 == 0 ? 1 : 0);
				carry = (combined >> 1) & 1;
				val |= (combined & 1) << bit;
			}
			bits[--index] = (byte)val;
		} while (index > 0);

		byte cse = (byte)(Variants[cssVariant] ^ CryptTab2[cssVariant]);

		// Six rounds over 40 bits each, alternating between tmp1 and tmp2.
		Round(bits[25..], scratch, tmp1, cse, finalTable: false);
		Round(bits[20..], tmp1, tmp2, cse, finalTable: false);
		Round(bits[15..], tmp2, tmp1, cse, finalTable: true);
		Round(bits[10..], tmp1, tmp2, cse, finalTable: true);
		Round(bits[5..], tmp2, tmp1, cse, finalTable: false);
		RoundLast(bits, tmp1, key, cse);
	}

	/// <summary>One mixing round (libdvdcss's loops). The middle two rounds add a CryptTab0 lookup; every round but the last folds byte 0 into byte 4.</summary>
	private static void Round(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> input, Span<byte> output, byte cse, bool finalTable) {
		int term = 0;
		for (int i = 4; i >= 0; i--) {
			int idx = bits[i] ^ input[i];
			idx = (byte)(CryptTab1[idx] ^ ~CryptTab2[idx] ^ cse);
			if (finalTable) {
				idx = (byte)(CryptTab2[idx] ^ CryptTab3[idx] ^ term);
				output[i] = (byte)(CryptTab0[idx] ^ CryptTab2[idx]);
			} else {
				output[i] = (byte)(CryptTab2[idx] ^ CryptTab3[idx] ^ term);
			}
			term = input[i];
		}
		output[4] ^= output[0];
	}

	private static void RoundLast(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> input, Span<byte> key, byte cse) {
		int term = 0;
		for (int i = 4; i >= 0; i--) {
			int idx = bits[i] ^ input[i];
			idx = (byte)(CryptTab1[idx] ^ ~CryptTab2[idx] ^ cse);
			key[i] = (byte)(CryptTab2[idx] ^ CryptTab3[idx] ^ term);
			term = input[i];
		}
	}
}
