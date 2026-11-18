namespace RipDvd.Core.Mpeg;

/// <summary>One complete DVD subpicture unit (SPU): a bitmap subtitle with its display commands.</summary>
public sealed class SubpictureUnit {
	public required byte[] Data { get; init; }
	/// <summary>Presentation time in nanoseconds on the title timeline.</summary>
	public required long Pts { get; init; }
	/// <summary>How long it is shown (from its "stop display" command), or null if it has none.</summary>
	public long? DurationNs { get; init; }
	/// <summary>Shown even when subtitles are off (the "forced start display" command).</summary>
	public bool Forced { get; init; }
}

/// <summary>
/// Joins the PES payloads of one subpicture stream into complete subpicture units.
///
/// An SPU starts with its total size (2 bytes) and the offset of its control area (2 bytes).
/// It is usually split over several PES packets; only the first one carries a PTS.
/// The control area is a chain of sequences: delay (2 bytes, in units of 1024/90000 s), offset of the next sequence (2 bytes),
/// then commands up to 0xFF: 00 forced start, 01 start display, 02 stop display, 03 palette (2), 04 alpha (2), 05 area (6),
/// 06 pixel data offsets (4).
/// </summary>
public sealed class SubpictureAssembler {
	private readonly List<byte> _unit = [];
	private int _expected;
	private long _pts;
	private readonly List<SubpictureUnit> _ready = [];

	public long DroppedBytes { get; private set; }

	public void Feed(ReadOnlySpan<byte> payload, long? ptsNs) {
		if (_unit.Count == 0) {
			if (ptsNs is null || payload.Length < 2) {
				DroppedBytes += payload.Length;   // continuation without a start: we joined mid-unit
				return;
			}
			_pts = ptsNs.Value;
			_expected = (payload[0] << 8) | payload[1];
		}
		foreach (byte b in payload) _unit.Add(b);
		if (_unit.Count >= _expected) {
			var data = _unit.GetRange(0, _expected).ToArray();
			DroppedBytes += _unit.Count - _expected;
			_unit.Clear();
			var (duration, forced) = ReadControl(data);
			_ready.Add(new SubpictureUnit { Data = data, Pts = _pts, DurationNs = duration, Forced = forced });
		}
	}

	public List<SubpictureUnit> TakeReady() {
		var r = new List<SubpictureUnit>(_ready);
		_ready.Clear();
		return r;
	}

	/// <summary>Walks the control sequences to find the stop-display delay and the forced flag.</summary>
	private static (long? DurationNs, bool Forced) ReadControl(byte[] spu) {
		if (spu.Length < 4) return (null, false);
		int offset = (spu[2] << 8) | spu[3];
		long? stop = null;
		bool forced = false;
		var seen = new HashSet<int>();
		while (offset + 4 <= spu.Length && seen.Add(offset)) {
			int delay = (spu[offset] << 8) | spu[offset + 1];
			int next = (spu[offset + 2] << 8) | spu[offset + 3];
			int p = offset + 4;
			while (p < spu.Length && spu[p] != 0xFF) {
				byte cmd = spu[p++];
				switch (cmd) {
					case 0x00: forced = true; break;
					case 0x01: break;
					case 0x02: stop ??= delay * 1024L * 1_000_000_000 / 90_000; break;
					case 0x03:
					case 0x04: p += 2; break;
					case 0x05: p += 6; break;
					case 0x06: p += 4; break;
					default: p = spu.Length; break;   // unknown command: stop parsing
				}
			}
			if (next == offset) break;   // last sequence points to itself
			offset = next;
		}
		return (stop, forced);
	}
}
