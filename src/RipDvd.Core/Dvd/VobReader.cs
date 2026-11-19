namespace RipDvd.Core.Dvd;

/// <summary>
/// Reads 2048-byte sectors from a title set's title VOBs (VTS_xx_1.VOB, VTS_xx_2.VOB, ...),
/// treating them as one continuous run of sectors. Cell sector numbers in the IFO are
/// relative to the start of this run.
/// </summary>
public sealed class VobReader : IDisposable {
	public const int SectorSize = 2048;

	private readonly List<(FileStream Stream, long FirstSector, long SectorCount)> _parts = [];

	public long SectorCount { get; }

	public VobReader(IEnumerable<string> vobFiles) {
		long next = 0;
		foreach (var path in vobFiles) {
			var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
			long count = fs.Length / SectorSize;
			_parts.Add((fs, next, count));
			next += count;
		}
		SectorCount = next;
	}

	/// <summary>Opens VTS_nn_1.VOB, VTS_nn_2.VOB, ... in the given VIDEO_TS folder.</summary>
	public static VobReader ForTitleSet(string videoTsFolder, int titleSetNumber) {
		var files = new List<string>();
		for (int i = 1; i <= 9; i++) {
			string p = Path.Combine(videoTsFolder, $"VTS_{titleSetNumber:00}_{i}.VOB");
			if (!File.Exists(p)) break;
			files.Add(p);
		}
		return new VobReader(files);
	}

	/// <summary>
	/// For testing damaged-disc handling: sectors for which this returns true fail to read with the same error Windows gives for a bad sector
	/// ("Data error (cyclic redundancy check)", ERROR_CRC).
	/// </summary>
	public Func<long, bool>? SimulateBadSector { get; set; }

	/// <summary>Windows error codes that mean the disc itself could not be read there (scratch, dirt, defect), as opposed to e.g. a locked drive.</summary>
	public static bool IsMediaError(IOException ex) => (ex.HResult & 0xFFFF) is
		23 or     // ERROR_CRC: data error (cyclic redundancy check)
		25 or     // ERROR_SEEK
		27 or     // ERROR_SECTOR_NOT_FOUND
		30 or     // ERROR_READ_FAULT
		31 or     // ERROR_GEN_FAILURE
		1117;     // ERROR_IO_DEVICE

	/// <summary>Read <paramref name="count"/> sectors starting at <paramref name="sector"/> into the buffer.</summary>
	public void Read(long sector, int count, Span<byte> buffer) {
		if (SimulateBadSector is { } bad)
			for (long s = sector; s < sector + count; s++)
				if (bad(s)) throw new IOException($"Data error (cyclic redundancy check) at sector {s} (simulated).", unchecked((int)0x80070017));
		int done = 0;
		while (done < count) {
			long s = sector + done;
			var part = _parts.First(p => s >= p.FirstSector && s < p.FirstSector + p.SectorCount);
			int n = (int)Math.Min(count - done, part.FirstSector + part.SectorCount - s);
			part.Stream.Position = (s - part.FirstSector) * SectorSize;
			part.Stream.ReadExactly(buffer.Slice(done * SectorSize, n * SectorSize));
			done += n;
		}
	}

	public void Dispose() {
		foreach (var p in _parts) p.Stream.Dispose();
	}
}
