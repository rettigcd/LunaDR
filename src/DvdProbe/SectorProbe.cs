namespace DvdProbe;

/// <summary>Probe code for reading raw 2048-byte sectors from VOB files and inspecting them.</summary>
internal static class SectorProbe {
	public const int SectorSize = 2048;

	/// <summary>
	/// Each VOB sector is one MPEG-2 "pack": a 14+ byte pack header (00 00 01 BA ...) followed
	/// by a PES packet. The PES header's flags byte holds the 2-bit PES_scrambling_control;
	/// non-zero means the payload after the PES header is CSS-scrambled.
	/// Returns null if the sector doesn't look like a pack with a PES packet.
	/// </summary>
	public static bool? IsScrambled(ReadOnlySpan<byte> s) {
		if (s[0] != 0 || s[1] != 0 || s[2] != 1 || s[3] != 0xBA) return null;
		int stuffing = s[13] & 0x07;
		int pes = 14 + stuffing;
		if (s[pes] != 0 || s[pes + 1] != 0 || s[pes + 2] != 1) return null;
		byte streamId = s[pes + 3];
		// System header (BB), padding (BE) and navigation (BF) packets are never scrambled.
		if (streamId is 0xBB or 0xBE or 0xBF) return false;
		return (s[pes + 6] & 0x30) != 0;
	}

	public static void Scan(string vobPath, int samples) {
		using var fs = new FileStream(vobPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
		long totalSectors = fs.Length / SectorSize;
		Console.WriteLine($"{Path.GetFileName(vobPath)}: {totalSectors:N0} sectors");

		var buf = new byte[SectorSize];
		int scrambled = 0, clear = 0, other = 0, errors = 0;
		for (int i = 0; i < samples; i++) {
			long sector = totalSectors * i / samples;
			try {
				fs.Position = sector * SectorSize;
				fs.ReadExactly(buf);
				switch (IsScrambled(buf)) {
					case true: scrambled++; break;
					case false: clear++; break;
					default: other++; break;
				}
				if (i < 3) Console.WriteLine($"  sector {sector}: {Convert.ToHexString(buf, 0, 32)}");
			} catch (IOException ex) {
				errors++;
				if (errors <= 3) Console.WriteLine($"  sector {sector}: read error: {ex.Message}");
			}
		}
		Console.WriteLine($"  sampled {samples}: scrambled={scrambled} clear={clear} not-a-pack={other} read-errors={errors}");
	}
}
