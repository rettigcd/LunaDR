namespace RipDvd.Core.Bluray;

/// <summary>
/// A PES packet of one PID: presentation/decode time (90 kHz ticks, as in the stream), the stream ID extension (stream ID 0xFD: on Blu-ray
/// 0x72 = TrueHD, 0x76 = the AC3 core of a TrueHD stream), and the payload.
/// </summary>
public sealed record TsPes(int Pid, long? Pts, long? Dts, int StreamIdExtension, byte[] Payload);

/// <summary>
/// Joins MPEG transport stream packets (188 bytes) of the wanted PIDs into PES packets (plan step BR4).
/// A PES packet starts in a TS packet with payload_unit_start_indicator set and runs until the next one of its PID.
/// </summary>
public sealed class TsDemuxer(IEnumerable<int> pids) {
	private readonly HashSet<int> _pids = [.. pids];
	private readonly Dictionary<int, MemoryStream> _open = [];
	private readonly List<TsPes> _ready = [];

	/// <summary>TS packets without the 0x47 sync byte (damaged or wrongly decrypted data).</summary>
	public long BadPackets { get; private set; }

	/// <summary>Takes one 188-byte TS packet.</summary>
	public void Feed(ReadOnlySpan<byte> ts) {
		if (ts[0] != 0x47) {
			BadPackets++;
			return;
		}
		int pid = (ts[1] & 0x1F) << 8 | ts[2];
		if (!_pids.Contains(pid)) return;
		bool start = (ts[1] & 0x40) != 0;
		int adaptation = (ts[3] >> 4) & 3;
		if (adaptation is 0 or 2) return;   // no payload
		int p = adaptation == 3 ? 5 + ts[4] : 4;
		if (p >= 188) return;
		if (start) {
			Complete(pid);
			_open[pid] = new MemoryStream();
		}
		if (_open.TryGetValue(pid, out var pes)) pes.Write(ts[p..]);
	}

	/// <summary>End of input: completes the PES packets still open.</summary>
	public void Flush() {
		foreach (int pid in _open.Keys.ToList()) Complete(pid);
	}

	public List<TsPes> TakeReady() {
		var r = new List<TsPes>(_ready);
		_ready.Clear();
		return r;
	}

	private void Complete(int pid) {
		if (!_open.Remove(pid, out var ms)) return;
		if (Parse(pid, ms.GetBuffer().AsSpan(0, (int)ms.Length)) is { } pes) _ready.Add(pes);
	}

	/// <summary>Parses a PES packet (00 00 01, stream ID, length, flags, header data with PTS/DTS and the extension); null if it isn't one.</summary>
	public static TsPes? Parse(int pid, ReadOnlySpan<byte> b) {
		if (b.Length < 9 || b[0] != 0 || b[1] != 0 || b[2] != 1) return null;
		int streamId = b[3];
		if (streamId is 0xBC or 0xBE or 0xBF) return new TsPes(pid, null, null, 0, b[6..].ToArray());
		int flags = b[7];
		int headerEnd = 9 + b[8];
		if (headerEnd > b.Length) return null;
		long? pts = null, dts = null;
		int q = 9;
		if ((flags & 0x80) != 0) {
			pts = Timestamp(b[q..]);
			q += 5;
		}
		if ((flags & 0x40) != 0) {
			dts = Timestamp(b[q..]);
			q += 5;
		}
		int extension = 0;
		if ((flags & 0x01) != 0) {
			if ((flags & 0x20) != 0) q += 6;   // ESCR
			if ((flags & 0x10) != 0) q += 3;   // ES rate
			if ((flags & 0x08) != 0) q += 1;   // DSM trick mode
			if ((flags & 0x04) != 0) q += 1;   // additional copy info
			if ((flags & 0x02) != 0) q += 2;   // CRC
			if (q < headerEnd) {
				int ext = b[q++];
				if ((ext & 0x80) != 0) q += 16;                       // private data
				if ((ext & 0x40) != 0 && q < headerEnd) q += 1 + b[q];   // pack header
				if ((ext & 0x20) != 0) q += 2;                        // sequence counter
				if ((ext & 0x10) != 0) q += 2;                        // P-STD buffer
				if ((ext & 0x01) != 0 && q + 1 < headerEnd) extension = b[q + 1] & 0x7F;
			}
		}
		return new TsPes(pid, pts, dts, extension, b[headerEnd..].ToArray());
	}

	private static long Timestamp(ReadOnlySpan<byte> t) =>
		((long)(t[0] >> 1) & 7) << 30 | (long)t[1] << 22 | (long)(t[2] >> 1) << 15 | (long)t[3] << 7 | (long)(t[4] >> 1);
}
