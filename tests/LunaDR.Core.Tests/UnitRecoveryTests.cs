using LunaDR.Core.Bluray;

namespace LunaDR.Core.Tests;

/// <summary>Re-reading units that came back as corrupted data (a damaged disc that reports no error).</summary>
public class UnitRecoveryTests {
	private static readonly byte[] UnitKey = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");

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

	private static byte[] Encrypted() {
		var unit = PlainUnit();
		AacsDecryptor.EncryptUnit(UnitKey, unit);
		return unit;
	}

	private static byte[] Corrupt() {
		var unit = Encrypted();
		unit[8] ^= 0xFF;   // the first 16 bytes make the unit's key, so damage there turns the whole unit to garbage (damage later in the unit is not detected)
		return unit;
	}

	[Fact]
	public void CorruptFirstRead_IsRecoveredByAGoodReread() {
		using var aacs = new AacsDecryptor([UnitKey]);
		var health = new ReadStats();
		var unit = Corrupt();
		int rereads = 0, reports = 0;
		bool ok = UnitRecovery.Decrypt(aacs, unit, 12288, health, (pos, dest) => { rereads++; Assert.Equal(12288, pos); Encrypted().CopyTo(dest); return true; }, () => reports++);
		Assert.True(ok);
		Assert.Equal(PlainUnit(), unit);
		Assert.Equal(1, rereads);
		Assert.Equal(1, reports);
		Assert.Equal(1, health.Recovered);
		Assert.Equal(0, health.Undecryptable);
		Assert.Equal(0, health.RetryAttempt);
	}

	[Fact]
	public void GoodUnit_IsNotReread() {
		using var aacs = new AacsDecryptor([UnitKey]);
		var health = new ReadStats();
		var unit = Encrypted();
		Assert.True(UnitRecovery.Decrypt(aacs, unit, 0, health, (_, _) => throw new InvalidOperationException(), () => { }));
		Assert.Equal(PlainUnit(), unit);
	}

	[Fact]
	public void StillCorruptAfterThreeRereads_IsCountedAndDropped() {
		using var aacs = new AacsDecryptor([UnitKey]);
		var health = new ReadStats();
		var unit = Corrupt();
		int rereads = 0;
		var attempts = new List<int>();
		Assert.False(UnitRecovery.Decrypt(aacs, unit, 0, health, (_, dest) => { rereads++; Corrupt().CopyTo(dest); return true; }, () => attempts.Add(health.RetryAttempt)));
		Assert.Equal(3, rereads);
		Assert.Equal([1, 2, 3], attempts);
		Assert.Equal(1, health.Undecryptable);
		Assert.Equal(0, health.Recovered);
	}

	[Fact]
	public void SecondRereadCanSucceed_AndAFailedReadCountsAsAnAttempt() {
		using var aacs = new AacsDecryptor([UnitKey]);
		var health = new ReadStats();
		var unit = Corrupt();
		int rereads = 0;
		bool ok = UnitRecovery.Decrypt(aacs, unit, 0, health, (_, dest) => {
			rereads++;
			if (rereads == 1) return false;   // a read error
			Encrypted().CopyTo(dest);
			return true;
		}, () => { });
		Assert.True(ok);
		Assert.Equal(2, rereads);
		Assert.Equal(PlainUnit(), unit);
	}

	[Fact]
	public void AllZeroReread_IsNotAccepted() {
		using var aacs = new AacsDecryptor([UnitKey]);
		var health = new ReadStats();
		var unit = Corrupt();
		Assert.False(UnitRecovery.Decrypt(aacs, unit, 0, health, (_, dest) => { dest.Clear(); return true; }, () => { }));
	}

	[Fact]
	public void GivesUpRereading_AfterManyUnitsInARowFail_ButARecoveryResetsTheCount() {
		using var aacs = new AacsDecryptor([UnitKey]);
		var health = new ReadStats();
		int rereads = 0;
		bool Bad(byte[] unit) => UnitRecovery.Decrypt(aacs, unit, 0, health, (_, dest) => { rereads++; Corrupt().CopyTo(dest); return true; }, () => { });

		for (int i = 0; i < UnitRecovery.GiveUpAfter - 1; i++) Assert.False(Bad(Corrupt()));
		Assert.False(health.GaveUpRereading);
		// One that recovers resets the streak.
		Assert.True(UnitRecovery.Decrypt(aacs, Corrupt(), 0, health, (_, dest) => { Encrypted().CopyTo(dest); return true; }, () => { }));
		Assert.Equal(0, health.FailedInARow);

		for (int i = 0; i < UnitRecovery.GiveUpAfter; i++) Assert.False(Bad(Corrupt()));
		Assert.True(health.GaveUpRereading);

		rereads = 0;
		Assert.False(Bad(Corrupt()));   // given up: dropped at once, without a re-read
		Assert.Equal(0, rereads);
	}

	[Fact]
	public void UnbufferedFile_ReadsAUnitAtAnAlignedPosition() {
		string path = Path.Combine(Path.GetTempPath(), "unit-" + Guid.NewGuid().ToString("N") + ".m2ts");
		var data = new byte[AacsDecryptor.UnitSize * 3];
		new Random(1).NextBytes(data);
		File.WriteAllBytes(path, data);
		try {
			using var file = new UnbufferedFile(path);
			var unit = new byte[AacsDecryptor.UnitSize];
			Assert.True(file.TryRead(AacsDecryptor.UnitSize, unit));
			Assert.Equal(data.AsSpan(AacsDecryptor.UnitSize, AacsDecryptor.UnitSize).ToArray(), unit);
			Assert.False(file.TryRead(AacsDecryptor.UnitSize * 3, unit));   // past the end
		} finally {
			File.Delete(path);
		}
	}
}
