using System.Runtime.Versioning;
using RipDvd.Core.Bluray;

namespace DvdProbe;

/// <summary>Probe: what a Blu-ray drive reports, and how a Blu-ray disc is laid out and protected (plan step BR1).</summary>
internal static class BlurayProbe {
	private static readonly Dictionary<int, string> Profiles = new() {
		[0x08] = "CD-ROM",
		[0x09] = "CD-R",
		[0x0A] = "CD-RW",
		[0x10] = "DVD-ROM",
		[0x11] = "DVD-R",
		[0x12] = "DVD-RAM",
		[0x13] = "DVD-RW",
		[0x1A] = "DVD+RW",
		[0x1B] = "DVD+R",
		[0x2B] = "DVD+R DL",
		[0x40] = "BD-ROM",
		[0x41] = "BD-R SRM",
		[0x42] = "BD-R RRM",
		[0x43] = "BD-RE",
	};

	private static readonly Dictionary<int, string> FeatureNames = new() {
		[0x0000] = "Profile list",
		[0x0001] = "Core",
		[0x0002] = "Morphing",
		[0x0003] = "Removable medium",
		[0x001E] = "CD read",
		[0x001F] = "DVD read",
		[0x0040] = "BD read",
		[0x0041] = "BD write",
		[0x0106] = "DVD CSS",
		[0x0107] = "Real-time streaming",
		[0x010B] = "DVD CPRM",
		[0x010D] = "AACS",
		[0x010E] = "DVD CSS managed recording",
		[0x0110] = "VCPS",
		[0x0113] = "SecurDisc",
		[0x0142] = "OSSC",
	};

	[SupportedOSPlatform("windows")]
	public static void Drive(string drive) {
		using var scsi = new WindowsScsiDrive(drive);
		var (vendor, product, revision) = scsi.Inquiry();
		Console.WriteLine($"Drive {drive}: {vendor} {product}, firmware {revision}");
		var (profile, features) = scsi.GetConfiguration();
		Console.WriteLine($"Current medium: {Profiles.GetValueOrDefault(profile, "unknown")} (profile 0x{profile:X4})");
		foreach (var (code, current, data) in features) {
			string name = FeatureNames.GetValueOrDefault(code, "");
			string detail = code switch {
				0x0000 => string.Join(" ", Enumerable.Range(0, data.Length / 4)
					.Select(i => (data[i * 4] << 8 | data[i * 4 + 1], (data[i * 4 + 2] & 1) != 0))
					.Select(x => Profiles.GetValueOrDefault(x.Item1, $"0x{x.Item1:X4}") + (x.Item2 ? "*" : ""))),
				0x010D when data.Length >= 4 =>
					$"AACS version {data[3]}, AGIDs {data[2] & 0x0F}, binding nonce {((data[0] & 1) != 0 ? "yes" : "no")} (blocks {data[1]}), " +
					$"bus encryption capable {((data[0] & 2) != 0 ? "yes" : "no")}, write bus encryption {((data[0] & 4) != 0 ? "yes" : "no")}, " +
					$"read media key block of CPRM {((data[0] & 8) != 0 ? "yes" : "no")}, read data key {((data[0] & 16) != 0 ? "yes" : "no")}",
				_ => Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 16))),
			};
			Console.WriteLine($"  feature 0x{code:X4}{(current ? " (current)" : ""),-10} {name,-26} {detail}");
		}
	}

	/// <summary>Disc layout, AACS files, and whether the stream files are encrypted (copy-permission bits of the first aligned units).</summary>
	public static void Files(string root) {
		string bdmv = Path.Combine(root, "BDMV"), aacs = Path.Combine(root, "AACS");
		foreach (var dir in new[] { bdmv, aacs }) {
			if (!Directory.Exists(dir)) { Console.WriteLine($"{dir}: missing"); continue; }
			Console.WriteLine($"{dir}:");
			foreach (var sub in Directory.GetDirectories(dir).Prepend(dir)) {
				var files = Directory.GetFiles(sub);
				long bytes = files.Sum(f => new FileInfo(f).Length);
				Console.WriteLine($"  {Path.GetRelativePath(dir, sub),-12} {files.Length,4} files {bytes / 1e9,8:F2} GB" +
								  (files.Length <= 8 ? "  " + string.Join(", ", files.Select(Path.GetFileName)) : ""));
			}
		}

		string mkb = Path.Combine(aacs, "MKB_RO.inf");
		if (File.Exists(mkb)) {
			var b = File.ReadAllBytes(mkb);
			// First record: type 0x10 (type and version), 3-byte length, MKB type (4), version (4).
			Console.WriteLine($"MKB_RO.inf: first record 0x{b[0]:X2}, MKB type 0x{ReadBe32(b, 4):X8}, version {ReadBe32(b, 8)}");
		}
		foreach (var cer in Directory.Exists(aacs) ? Directory.GetFiles(aacs, "Content*.cer") : []) {
			var b = File.ReadAllBytes(cer);
			// Content certificate: byte 0 = type (0 = AACS 1), byte 1 bit 7 = bus encryption enabled.
			Console.WriteLine($"{Path.GetFileName(cer)}: type {b[0]}, bus encryption {((b[1] & 0x80) != 0 ? "ENABLED" : "not enabled")}");
		}
		string ukf = Path.Combine(aacs, "Unit_Key_RO.inf");
		if (File.Exists(ukf)) {
			var b = File.ReadAllBytes(ukf);
			Console.WriteLine($"Unit_Key_RO.inf: {b.Length} bytes, disc ID (SHA-1) {Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b))}, " +
							  $"unit keys {(b[ReadBe32(b, 0)] << 8 | b[ReadBe32(b, 0) + 1])}");
		}

		string stream = Path.Combine(bdmv, "STREAM");
		if (!Directory.Exists(stream)) return;
		Console.WriteLine("Stream files (first 4 aligned units of 6,144 bytes = 32 packets each):");
		foreach (var file in Directory.GetFiles(stream, "*.m2ts").OrderByDescending(f => new FileInfo(f).Length).Take(6)) {
			using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
			var buf = new byte[6144 * 4];
			int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
			var units = new List<string>();
			for (int u = 0; u + 6144 <= n; u += 6144) {
				int cpi = buf[u] >> 6;
				int syncs = Enumerable.Range(0, 32).Count(i => buf[u + i * 192 + 4] == 0x47);
				units.Add($"cpi {cpi} sync {syncs}/32");
			}
			Console.WriteLine($"  {Path.GetFileName(file),-12} {new FileInfo(file).Length / 1e9,6:F2} GB  {string.Join(" | ", units)}");
		}
	}

	private static int ReadBe32(byte[] b, int p) => b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3];
}
