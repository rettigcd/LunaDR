namespace LunaDR.Core.Dvd;

/// <summary>
/// A run of 2048-byte sectors, as a title set's VOBs are (<see cref="VobReader"/>).
/// Tests serve made-up sectors through it; <see cref="BadSectorSource"/> makes chosen sectors unreadable.
/// </summary>
public interface ISectorSource {
	long SectorCount { get; }

	/// <summary>Read <paramref name="count"/> sectors starting at <paramref name="sector"/> into the buffer; a bad sector throws <see cref="IOException"/>.</summary>
	void Read(long sector, int count, Span<byte> buffer);
}

/// <summary>
/// For testing damaged-disc handling (probe 'rip -bad', unit tests): sectors for which <paramref name="isBad"/> returns true fail to read
/// with the error Windows gives for a bad sector ("Data error (cyclic redundancy check)", ERROR_CRC); the rest come from <paramref name="inner"/>.
/// </summary>
public sealed class BadSectorSource(ISectorSource inner, Func<long, bool> isBad) : ISectorSource {
	/// <summary>HRESULT of Windows' ERROR_CRC (23), as an <see cref="IOException"/> from reading a bad sector carries it.</summary>
	public const int CrcErrorHResult = unchecked((int)0x80070017);

	public long SectorCount => inner.SectorCount;

	public void Read(long sector, int count, Span<byte> buffer) {
		for (long s = sector; s < sector + count; s++)
			if (isBad(s)) throw new IOException($"Data error (cyclic redundancy check) at sector {s} (simulated).", CrcErrorHResult);
		inner.Read(sector, count, buffer);
	}
}
