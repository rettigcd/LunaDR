namespace RipDvd.Core.Css;

/// <summary>
/// Finds a title set's CSS key from its scrambled sectors (no drive authentication).
/// Feed sectors in order with <see cref="Offer"/>; once <see cref="Key"/> is set, stop.
///
/// Each scrambled sector that ends its clear part with a repeating pattern gives a
/// candidate key (<see cref="Css.AttackPattern"/>). A candidate is accepted as soon as it
/// descrambles most of the scrambled video sectors seen so far into data containing MPEG
/// start codes (00 00 01). A wrong key gives random bytes, which almost never contain one.
/// </summary>
public sealed class TitleKeyFinder {
	private const int SamplesKept = 16;
	private const int SamplesNeeded = 4;

	private readonly List<byte[]> _videoSamples = [];
	private readonly HashSet<CssKey> _rejected = [];

	/// <summary>The accepted key, or null while still searching.</summary>
	public CssKey? Key { get; private set; }
	public int ScrambledSectorsSeen { get; private set; }
	public int CandidatesTried { get; private set; }

	/// <summary>Offer one 2048-byte sector (as read from the disc). Returns true once a key is accepted.</summary>
	public bool Offer(ReadOnlySpan<byte> sector) {
		if (Key is not null) return true;
		if (!Css.IsScrambled(sector)) return false;
		ScrambledSectorsSeen++;

		if (sector[0x11] == 0xE0 && _videoSamples.Count < SamplesKept)
			_videoSamples.Add(sector.ToArray());

		var candidate = Css.AttackPattern(sector);
		if (candidate is null || _rejected.Contains(candidate.Value)) return false;
		CandidatesTried++;

		if (Validate(candidate.Value)) {
			Key = candidate;
			return true;
		}
		// Not enough samples yet to judge: keep the candidate for a later retry.
		if (_videoSamples.Count >= SamplesNeeded) _rejected.Add(candidate.Value);
		else _pending.Add(candidate.Value);
		return false;
	}

	private readonly List<CssKey> _pending = [];

	/// <summary>Call when the data has run out: retries candidates that came before enough samples.</summary>
	public CssKey? Finish() {
		if (Key is not null) return Key;
		foreach (var k in _pending)
			if (Validate(k)) return Key = k;
		return null;
	}

	private bool Validate(CssKey key) {
		if (_videoSamples.Count < SamplesNeeded) return false;
		int good = 0;
		var copy = new byte[Css.SectorSize];
		foreach (var sample in _videoSamples) {
			sample.CopyTo(copy, 0);
			Css.Unscramble(key, copy);
			if (copy.AsSpan(0x80).IndexOf([(byte)0, (byte)0, (byte)1]) >= 0) good++;
		}
		return good * 2 > _videoSamples.Count;
	}
}
