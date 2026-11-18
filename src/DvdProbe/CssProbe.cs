using System.Diagnostics;
using RipDvd.Core.Css;
using RipDvd.Core.Dvd;

namespace DvdProbe;

/// <summary>Probe code for recovering CSS title keys and checking them.</summary>
internal static class CssProbe {
	/// <summary>
	/// Scan a title set's VOBs from the start, run the pattern attack on every scrambled
	/// sector, and tally the candidate keys. A right key shows up many times; a wrong guess
	/// (when the plaintext guess was wrong) gives scattered one-off keys.
	/// </summary>
	public static CssKey? CrackTitleSet(string videoTs, int vts, int maxSectors, int votesNeeded, bool print) {
		using var reader = VobReader.ForTitleSet(videoTs, vts);
		var votes = new Dictionary<CssKey, int>();
		var buf = new byte[VobReader.SectorSize * 64];
		int scrambled = 0, attempts = 0;
		var sw = Stopwatch.StartNew();
		long limit = Math.Min(reader.SectorCount, maxSectors);

		for (long s = 0; s < limit; s += 64) {
			int n = (int)Math.Min(64, limit - s);
			reader.Read(s, n, buf);
			for (int k = 0; k < n; k++) {
				var sector = buf.AsSpan(k * VobReader.SectorSize, VobReader.SectorSize);
				if (!Css.IsScrambled(sector)) continue;
				scrambled++;
				var key = Css.AttackPattern(sector);
				if (key is null) continue;
				attempts++;
				votes[key.Value] = votes.GetValueOrDefault(key.Value) + 1;
				if (votes[key.Value] >= votesNeeded) goto done;
			}
		}
	done:
		var best = votes.OrderByDescending(v => v.Value).FirstOrDefault();
		if (print) {
			Console.WriteLine($"VTS {vts:00}: {scrambled} scrambled sectors seen, {attempts} keys recovered " +
							  $"in {sw.ElapsedMilliseconds} ms");
			foreach (var (k, c) in votes.OrderByDescending(v => v.Value).Take(5))
				Console.WriteLine($"    {k}  x{c}");
		}
		return votes.Count == 0 ? null : best.Key;
	}

	/// <summary>
	/// Check a key by descrambling sectors and counting MPEG start codes (00 00 01) in the
	/// formerly scrambled part. Real MPEG video has several per sector; random bytes almost never.
	/// </summary>
	public static void Verify(string videoTs, int vts, CssKey key, int samples) {
		using var reader = VobReader.ForTitleSet(videoTs, vts);
		var sector = new byte[VobReader.SectorSize];
		int tested = 0, withStartCodeBefore = 0, withStartCodeAfter = 0;
		for (int i = 0; i < samples * 20 && tested < samples; i++) {
			long s = reader.SectorCount * i / (samples * 20);
			reader.Read(s, 1, sector);
			if (!Css.IsScrambled(sector) || sector[0x11] != 0xE0) continue;   // video packets only
			tested++;
			if (HasStartCode(sector)) withStartCodeBefore++;
			Css.Unscramble(key, sector);
			if (HasStartCode(sector)) withStartCodeAfter++;
		}
		Console.WriteLine($"    verify {key}: {tested} scrambled video sectors; with a start code " +
						  $"before={withStartCodeBefore} after={withStartCodeAfter}");
	}

	private static bool HasStartCode(ReadOnlySpan<byte> s) => s[0x80..].IndexOf([(byte)0, (byte)0, (byte)1]) >= 0;
}
