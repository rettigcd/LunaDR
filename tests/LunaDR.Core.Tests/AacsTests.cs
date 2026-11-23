using System.Security.Cryptography;
using LunaDR.Core.Bluray;

namespace LunaDR.Core.Tests;

/// <summary>AACS decryption and the key file, with keys and data made up here (BR3); the real disc is checked with probe `bdkey`.</summary>
public class AacsTests {
	private static readonly byte[] UnitKey = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
	private static readonly byte[] OtherKey = Convert.FromHexString("FFEEDDCCBBAA99887766554433221100");

	/// <summary>An aligned unit of 32 source packets: header bytes with the packet number, TS sync byte 0x47, then a pattern.</summary>
	private static byte[] PlainUnit() {
		var unit = new byte[AacsDecryptor.UnitSize];
		for (int p = 0; p < 32; p++) {
			int o = p * AacsDecryptor.PacketSize;
			unit[o + 3] = (byte)p;
			unit[o + 4] = 0x47;
			for (int i = 5; i < AacsDecryptor.PacketSize; i++) unit[o + i] = (byte)(p * 7 + i);
		}
		return unit;
	}

	[Fact]
	public void EncryptedUnit_DecryptsToTheOriginalPackets() {
		var plain = PlainUnit();
		var unit = (byte[])plain.Clone();
		AacsDecryptor.EncryptUnit(UnitKey, unit);
		Assert.True(AacsDecryptor.IsEncrypted(unit));
		Assert.Equal(1, Enumerable.Range(0, 32).Count(p => unit[p * 192 + 4] == 0x47));   // only the clear first packet's sync is readable

		using var aacs = new AacsDecryptor([UnitKey]);
		Assert.True(aacs.DecryptUnit(unit));
		Assert.Equal(plain, unit);   // copy-permission bits cleared again
	}

	[Fact]
	public void SeveralUnitKeys_TheOneThatGivesValidPacketsIsUsed() {
		var plain = PlainUnit();
		var unit = (byte[])plain.Clone();
		AacsDecryptor.EncryptUnit(UnitKey, unit);
		using var aacs = new AacsDecryptor([OtherKey, UnitKey]);
		Assert.True(aacs.DecryptUnit(unit));
		Assert.Equal(plain, unit);
	}

	[Fact]
	public void WrongKey_IsReported() {
		var unit = PlainUnit();
		AacsDecryptor.EncryptUnit(UnitKey, unit);
		using var aacs = new AacsDecryptor([OtherKey]);
		Assert.False(aacs.DecryptUnit(unit));
	}

	[Fact]
	public void UnencryptedUnit_IsLeftAsItIs() {
		var unit = PlainUnit();
		using var aacs = new AacsDecryptor([UnitKey]);
		Assert.True(aacs.DecryptUnit(unit));
		Assert.Equal(PlainUnit(), unit);
	}

	[Fact]
	public void UnitKeysFromVuk_DecryptTheKeysInUnitKeyFile() {
		// Unit_Key_RO.inf: offset of the key table at 0; at the table: key count, then keys 48 bytes apart starting 48 bytes in.
		var vuk = Convert.FromHexString("0F0E0D0C0B0A09080706050403020100");
		using var aes = Aes.Create();
		aes.Key = vuk;
		var file = new byte[0x100 + 48 + 2 * 48];
		file[3] = 0x00;
		file[2] = 0x01;   // table at 0x100
		file[0x101] = 2;
		aes.EncryptEcb(UnitKey, PaddingMode.None).CopyTo(file, 0x100 + 48);
		aes.EncryptEcb(OtherKey, PaddingMode.None).CopyTo(file, 0x100 + 96);
		var keys = AacsDecryptor.UnitKeysFromVuk(vuk, file);
		Assert.Equal([UnitKey, OtherKey], keys);
	}

	[Fact]
	public void KeyFileLine_IsParsed() {
		string id = "CD9BD1A5F878E3240552220D8CA30871D8A2A8B4";
		var entry = KeyDb.ParseLine($"0x{id} = SOME_DISC (Some Disc) | D | 2008-08-21 | M | 0x{new string('1', 32)} | I | 0x{new string('2', 32)} " +
									$"| V | 0x{Convert.ToHexString(OtherKey)} | U | 1-0x{Convert.ToHexString(UnitKey)} ; MKBv8 comment | with bars");
		Assert.NotNull(entry);
		Assert.Equal(id, entry.DiscId);
		Assert.Equal("SOME_DISC (Some Disc)", entry.Title);
		Assert.Equal(OtherKey, entry.Vuk);
		Assert.Equal([UnitKey], entry.UnitKeys);
		Assert.Null(KeyDb.ParseLine("| DK | DEVICE_KEY 0x00 | DEVICE_NODE 0x0800"));
	}

	[Fact]
	public void NoKeyFile_ClearMessage() {
		string root = Path.Combine(Path.GetTempPath(), "aacs-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(root, "AACS"));
		try {
			File.WriteAllBytes(Path.Combine(root, "AACS", "Unit_Key_RO.inf"), new byte[64]);
			string keyFile = Path.Combine(root, "KEYDB.cfg");
			File.WriteAllText(keyFile, "; empty\n");
			var e = Assert.Throws<KeyNotFoundException>(() => AacsDecryptor.ForDisc(root, keyFile));
			Assert.Contains("no key for this disc", e.Message);
			string discId = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(new byte[64]));
			Assert.Contains($"disc ID {discId}", e.Message);
			Assert.Contains($"0x{discId} =", e.Message);   // the line to add must start with 0x, or the lookup won't find it
			Assert.Null(AacsDecryptor.ForDisc(Path.Combine(root, "AACS"), keyFile));   // no AACS folder: not encrypted
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}
}
