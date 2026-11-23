using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LunaDR.Core.Bluray;

/// <summary>A disc's entry in a key file: title, volume unique key (VUK) and/or unit keys.</summary>
public sealed record KeyDbEntry(string DiscId, string Title, byte[]? Vuk, IReadOnlyList<byte[]> UnitKeys);

/// <summary>
/// The libaacs key file (KEYDB.cfg), supplied by the owner; this program never downloads keys.
/// A disc's line: <c>0x&lt;disc ID, 40 hex&gt; = &lt;title&gt; | D | date | M | 0x… | I | 0x… | V | 0x&lt;VUK&gt; | U | 1-0x&lt;unit key&gt; ; comment</c>.
/// The disc ID is the SHA-1 of AACS/Unit_Key_RO.inf.
/// </summary>
public static partial class KeyDb {
	/// <summary>Where the key file is looked for: next to the program, then in %APPDATA%\aacs and %LOCALAPPDATA%\aacs (libaacs's places), as KEYDB.cfg.</summary>
	public static IEnumerable<string> SearchPaths() {
		yield return Path.Combine(AppContext.BaseDirectory, "KEYDB.cfg");
		foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData }) {
			string dir = Environment.GetFolderPath(folder);
			if (dir.Length > 0) yield return Path.Combine(dir, "aacs", "KEYDB.cfg");
		}
	}

	/// <summary>The first key file that exists (file names are not case-sensitive on Windows), or null.</summary>
	public static string? Find() => SearchPaths().FirstOrDefault(File.Exists);

	/// <summary>Finds the disc's line in the key file and parses it; null if the disc is not in the file.</summary>
	public static KeyDbEntry? Lookup(string keyFile, string discId) {
		string prefix = "0x" + discId;
		foreach (var line in File.ReadLines(keyFile)) {
			if (line.Length > prefix.Length && line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return ParseLine(line);
		}
		return null;
	}

	public static KeyDbEntry? ParseLine(string line) {
		int comment = line.IndexOf(';');
		if (comment >= 0) line = line[..comment];
		int eq = line.IndexOf('=');
		if (eq < 0) return null;
		string id = line[..eq].Trim();
		if (!id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || id.Length != 42) return null;
		var fields = line[(eq + 1)..].Split('|').Select(f => f.Trim()).ToList();
		byte[]? vuk = null;
		var unitKeys = new List<byte[]>();
		for (int i = 1; i < fields.Count; i++) {
			if (fields[i] == "V" && i + 1 < fields.Count && Hex(fields[i + 1]) is { Length: 16 } v) vuk = v;
			if (fields[i] == "U") {
				for (int j = i + 1; j < fields.Count && UnitKeyPattern().Match(fields[j]) is { Success: true } m; j++) {
					int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
					while (unitKeys.Count < n) unitKeys.Add([]);
					unitKeys[n - 1] = Convert.FromHexString(m.Groups[2].Value);
				}
			}
		}
		return new KeyDbEntry(id[2..].ToUpperInvariant(), fields[0], vuk, unitKeys);
	}

	private static byte[]? Hex(string s) =>
		s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && s.Length > 2 && s[2..].All(Uri.IsHexDigit) && s.Length % 2 == 0
			? Convert.FromHexString(s[2..]) : null;

	[GeneratedRegex(@"^(\d+)-0x([0-9A-Fa-f]{32})$")]
	private static partial Regex UnitKeyPattern();
}

/// <summary>
/// AACS (version 1) decryption of a Blu-ray's stream files, as libaacs does it:
/// the unit keys come from the key file, or from AACS/Unit_Key_RO.inf decrypted with the disc's VUK (AES-128 ECB).
/// A stream file is a series of 6,144-byte aligned units (32 source packets of 192 bytes). In an encrypted unit (copy-permission bits set in
/// the first packet's header) the first 16 bytes are clear; the rest is AES-128 CBC with a fixed IV and the key AES-128(unit key, first 16) XOR first 16.
/// A correctly decrypted unit has the TS sync byte 0x47 at byte 4 of every packet.
/// </summary>
public sealed class AacsDecryptor : IDisposable {
	public const int UnitSize = 6144, PacketSize = 192;
	private static readonly byte[] Iv = Convert.FromHexString("0BA0F8DDFEA61FB3D8DF9F566A050F78");

	private readonly IReadOnlyList<byte[]> _unitKeys;
	private readonly Aes _aes = Aes.Create();
	private int _current;   // index of the unit key that worked last

	public AacsDecryptor(IReadOnlyList<byte[]> unitKeys) {
		if (unitKeys.Count == 0 || unitKeys.Any(k => k.Length != 16)) throw new ArgumentException("No usable unit key.");
		_unitKeys = unitKeys;
	}

	public IReadOnlyList<byte[]> UnitKeys => _unitKeys;

	/// <summary>Set when the keys did not come from the disc's own key-file line (see <see cref="ForDisc"/>); to be shown to the user as a warning.</summary>
	public string? KeyNote { get; private init; }

	/// <summary>The encrypted unit keys stored in Unit_Key_RO.inf (libaacs: count at the table offset, keys at offset + 48, 48 bytes apart).</summary>
	public static List<byte[]> EncryptedUnitKeys(byte[] unitKeyFile) {
		int pos = (int)Mpls.Be32(unitKeyFile, 0);
		int count = Mpls.Be16(unitKeyFile, pos);
		var keys = new List<byte[]>();
		for (int i = 0; i < count; i++) keys.Add(unitKeyFile.AsSpan(pos + 48 + i * 48, 16).ToArray());
		return keys;
	}

	/// <summary>Unit keys from the VUK: each encrypted key decrypted with AES-128 ECB.</summary>
	public static List<byte[]> UnitKeysFromVuk(byte[] vuk, byte[] unitKeyFile) {
		using var aes = Aes.Create();
		aes.Key = vuk;
		return EncryptedUnitKeys(unitKeyFile).Select(k => aes.DecryptEcb(k, PaddingMode.None)).ToList();
	}

	/// <summary>
	/// The decryptor for a disc folder (its AACS/Unit_Key_RO.inf), with keys from the key file.
	/// Returns null when the disc has no AACS folder (not encrypted); throws a clear error when the key file or the disc's entry is missing.
	/// </summary>
	public static AacsDecryptor? ForDisc(string root, string? keyFile = null) {
		string ukf = Path.Combine(root, "AACS", "Unit_Key_RO.inf");
		if (!File.Exists(ukf)) return null;
		var ukfBytes = File.ReadAllBytes(ukf);
		string discId = Convert.ToHexString(SHA1.HashData(ukfBytes));
		keyFile ??= KeyDb.Find() ?? throw new FileNotFoundException(
			"This Blu-ray is AACS-encrypted and no key file was found. Put a KEYDB.cfg (libaacs format) in one of: " + string.Join(", ", KeyDb.SearchPaths()));
		var entry = KeyDb.Lookup(keyFile, discId);
		if (entry is null) {
			// Other copies of the same release are listed under other disc IDs but share the unit keys: try those, and use a set only if it decrypts the disc.
			var (found, tried) = SameTitleKeys(root, keyFile, ukfBytes);
			if (found is not null) return found;
			throw new KeyNotFoundException($"The key file {keyFile} has no key for this disc (disc ID {discId}). LunaDR never downloads keys: " +
				$"to rip this disc, add a line to that file in this form: 0x{discId} = <title> | V | 0x<the disc's VUK> " +
				"(KEYDB.cfg format; to search a key list, look for the disc ID, the 40 characters after 0x)." +
				(tried > 0 ? $" The {tried} other unit-key set(s) listed for discs with this volume name did not decrypt this disc." : ""));
		}
		if (entry.UnitKeys.Count > 0 && entry.UnitKeys.All(k => k.Length == 16)) return new AacsDecryptor(entry.UnitKeys);
		if (entry.Vuk is not null) return new AacsDecryptor(UnitKeysFromVuk(entry.Vuk, ukfBytes));
		throw new KeyNotFoundException($"The key file's line for this disc (disc ID {discId}, in {keyFile}) has neither a VUK nor unit keys, so the disc can't be decrypted. " +
		"Replace it with a line that has one.");
	}

	/// <summary>
	/// Fallback when the disc's ID is not in the key file: the unit-key sets (same key count as the disc) of the lines whose title starts with the disc's volume name.
	/// A set is used only if it decrypts sample units of the biggest stream file, so a different pressing's keys are never used by mistake.
	/// Returns the decryptor (null if none worked) and how many different sets were tried.
	/// </summary>
	private static (AacsDecryptor? Found, int Tried) SameTitleKeys(string root, string keyFile, byte[] unitKeyFile) {
		string label = BdScanner.VolumeName(root);
		int count = EncryptedUnitKeys(unitKeyFile).Count;
		if (label.Length == 0 || count == 0) return (null, 0);
		var sets = new Dictionary<string, (List<byte[]> Keys, int Lines)>();
		foreach (var line in File.ReadLines(keyFile)) {
			if (KeyDb.ParseLine(line) is not { } e || e.UnitKeys.Count != count || e.UnitKeys.Any(k => k.Length != 16)) continue;
			if (!e.Title.Split(' ', 2)[0].Equals(label, StringComparison.OrdinalIgnoreCase)) continue;
			string signature = string.Join("|", e.UnitKeys.Select(Convert.ToHexString));
			sets[signature] = sets.TryGetValue(signature, out var s) ? (s.Keys, s.Lines + 1) : (e.UnitKeys.ToList(), 1);
		}
		if (sets.Count == 0) return (null, 0);
		var samples = SampleEncryptedUnits(root);
		if (samples.Count == 0) return (null, sets.Count);   // nothing to check a guess against
		foreach (var (keys, lines) in sets.Values) {
			using var trial = new AacsDecryptor(keys);
			if (!samples.All(s => trial.DecryptUnit(s.ToArray()))) continue;
			return (new AacsDecryptor(keys) {
				KeyNote = $"The key file has no line for this disc's ID; used the unit keys of {lines} other line(s) for \"{label}\", " +
						  $"which decrypted all {samples.Count} sample units of the disc."
			}, sets.Count);
		}
		return (null, sets.Count);
	}

	/// <summary>Up to 50 encrypted units spread over the biggest stream file.</summary>
	private static List<byte[]> SampleEncryptedUnits(string root) {
		var samples = new List<byte[]>();
		string streams = Path.Combine(root, "BDMV", "STREAM");
		if (!Directory.Exists(streams)) return samples;
		string? file = Directory.GetFiles(streams, "*.m2ts").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
		if (file is null) return samples;
		using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
		long units = fs.Length / UnitSize;
		for (long u = 0; u < units && samples.Count < 50; u += Math.Max(1, units / 200)) {
			var unit = new byte[UnitSize];
			fs.Position = u * UnitSize;
			fs.ReadExactly(unit);
			if (IsEncrypted(unit)) samples.Add(unit);
		}
		return samples;
	}

	/// <summary>True if the unit is marked encrypted (copy-permission bits of its first packet).</summary>
	public static bool IsEncrypted(ReadOnlySpan<byte> unit) => (unit[0] & 0xC0) != 0;

	/// <summary>
	/// Decrypts one aligned unit in place (a unit not marked encrypted is left as it is) and clears the copy-permission bits.
	/// Tries the unit key that worked last first, then the others. Returns false if no key gives valid packets.
	/// </summary>
	public bool DecryptUnit(Span<byte> unit) {
		if (unit.Length != UnitSize) throw new ArgumentException("An aligned unit is 6,144 bytes.");
		if (!IsEncrypted(unit)) return true;
		byte[]? original = _unitKeys.Count > 1 ? unit.ToArray() : null;
		for (int attempt = 0; attempt < _unitKeys.Count; attempt++) {
			int k = (_current + attempt) % _unitKeys.Count;
			if (original is not null && attempt > 0) original.CopyTo(unit);
			Decrypt(_unitKeys[k], unit);
			if (IsValid(unit)) {
				_current = k;
				for (int p = 0; p < UnitSize; p += PacketSize) unit[p] &= 0x3F;
				return true;
			}
		}
		return false;
	}

	private void Decrypt(byte[] unitKey, Span<byte> unit) {
		_aes.Key = unitKey;
		Span<byte> key = stackalloc byte[16];
		_aes.EncryptEcb(unit[..16], key, PaddingMode.None);
		for (int i = 0; i < 16; i++) key[i] ^= unit[i];
		_aes.Key = key.ToArray();
		_aes.DecryptCbc(unit[16..], Iv, unit[16..], PaddingMode.None);
	}

	/// <summary>Encrypts a unit the way a disc stores it (for tests): the inverse of <see cref="DecryptUnit"/> with one key.</summary>
	public static void EncryptUnit(byte[] unitKey, Span<byte> unit) {
		// On a disc the copy-permission bits are part of the clear text (and so of the clear first 16 bytes that make the key).
		for (int p = 0; p < UnitSize; p += PacketSize) unit[p] |= 0xC0;
		using var aes = Aes.Create();
		aes.Key = unitKey;
		Span<byte> key = stackalloc byte[16];
		aes.EncryptEcb(unit[..16], key, PaddingMode.None);
		for (int i = 0; i < 16; i++) key[i] ^= unit[i];
		aes.Key = key.ToArray();
		aes.EncryptCbc(unit[16..], Iv, unit[16..], PaddingMode.None);
	}

	private static bool IsValid(ReadOnlySpan<byte> unit) {
		for (int p = 4; p < UnitSize; p += PacketSize) if (unit[p] != 0x47) return false;
		return true;
	}

	public void Dispose() => _aes.Dispose();
}
