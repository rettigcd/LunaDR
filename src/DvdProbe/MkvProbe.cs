using RipDvd.Core.Matroska;

namespace DvdProbe;

/// <summary>
/// Probe: print a Matroska file's structure like mkvinfo. Clusters are summarized per track
/// (block counts, keyframes, bytes, first/last timestamps, lacing) instead of listed.
/// </summary>
internal static class MkvProbe {
	private sealed class TrackStats {
		public long Blocks, KeyFrames, Bytes, Laced, Frames;
		public long? First, Last;
		public long MinGap = long.MaxValue, MaxGap;
		public long? PrevTime;
		public List<string> FirstBlocks = [];
	}

	/// <summary>Track whose block payloads are printed in full as UTF-8 text (for subtitle tracks), or 0.</summary>
	public static long DumpTrack;

	/// <summary>
	/// Option -saveblock T N file: writes the payload of block N (0-based) of track T to a file, to compare single frames byte by byte.
	/// N = -1 writes all unlaced blocks of the track one after another (e.g. the whole video stream).
	/// </summary>
	public static (long Track, int Index, string Path)? SaveBlock;
	private static readonly Dictionary<long, int> BlockIndex = [];

	/// <summary>How many clusters to list with their time and size (option -clusters N).</summary>
	public static int ListClusters;
	private static readonly List<(long Start, long Size)> ClusterList = [];

	public static void Dump(string path, int showBlocks) {
		using var fs = File.OpenRead(path);
		var r = new EbmlReader(fs);
		var tracks = new Dictionary<long, TrackStats>();
		long clusters = 0;
		Walk(r, fs.Length, 0, tracks, ref clusters, showBlocks);

		Console.WriteLine($"Clusters: {clusters:N0}");
		foreach (var (start, size) in ClusterList) {
			fs.Position = start;
			var th = r.ReadHeader()!.Value;
			Console.WriteLine($"  cluster at {start,12:N0}: time {EbmlReader.ToUInt(r.ReadData(th)),8} ms, size {size,9:N0}");
		}
		foreach (var (n, t) in tracks.OrderBy(k => k.Key)) {
			Console.WriteLine($"  track {n}: {t.Blocks:N0} blocks ({t.KeyFrames:N0} key, {t.Laced:N0} laced, {t.Frames:N0} frames), " +
							  $"{t.Bytes:N0} bytes, time {t.First / 1000.0:F3} .. {t.Last / 1000.0:F3} s, gap {t.MinGap}..{t.MaxGap} ms");
			foreach (var b in t.FirstBlocks) Console.WriteLine($"      {b}");
		}
	}

	private static long _clusterTime;

	private static void Walk(EbmlReader r, long end, int depth, Dictionary<long, TrackStats> tracks, ref long clusters, int showBlocks) {
		while (r.Stream.Position < end) {
			var h = r.ReadHeader();
			if (h is null) break;
			var hdr = h.Value;
			var (name, type) = MatroskaIds.Describe(hdr.Id);
			string indent = new(' ', depth * 2);

			if (hdr.Id == MatroskaIds.Cluster) {
				clusters++;
				if (ClusterList.Count < ListClusters) ClusterList.Add((hdr.DataStart, hdr.Size));
				ReadCluster(r, hdr, tracks, showBlocks);
				continue;
			}

			if (type == EbmlType.Master) {
				Console.WriteLine($"{indent}{name} (size {(hdr.Size == EbmlReader.UnknownSize ? "unknown" : hdr.Size.ToString("N0"))}, at {hdr.DataStart - hdr.HeaderLength:N0})");
				Walk(r, Math.Min(hdr.End, end), depth + 1, tracks, ref clusters, showBlocks);
				r.Stream.Position = Math.Min(hdr.End, end);
				continue;
			}

			var d = r.ReadData(hdr);
			string value = type switch {
				EbmlType.UInt => EbmlReader.ToUInt(d).ToString(),
				EbmlType.Int => EbmlReader.ToInt(d).ToString(),
				EbmlType.Float => EbmlReader.ToFloat(d).ToString("R"),
				EbmlType.String or EbmlType.Utf8 => $"\"{EbmlReader.ToText(d)}\"",
				EbmlType.Date => DateTime.UnixEpoch.AddYears(31).AddTicks(EbmlReader.ToInt(d) / 100).ToString("u"),
				_ => d.Length <= 64 ? Convert.ToHexString(d) : $"{Convert.ToHexString(d, 0, 64)}... ({d.Length} bytes)",
			};
			if (hdr.Id == MatroskaIds.Void) value = $"({d.Length} bytes)";
			Console.WriteLine($"{indent}{name}: {value}");
		}
	}

	private static void ReadCluster(EbmlReader r, EbmlReader.Header cluster, Dictionary<long, TrackStats> tracks, int showBlocks) {
		while (r.Stream.Position < cluster.End) {
			var h = r.ReadHeader();
			if (h is null) break;
			var hdr = h.Value;
			if (hdr.Id == MatroskaIds.Timestamp) {
				_clusterTime = (long)EbmlReader.ToUInt(r.ReadData(hdr));
			} else if (hdr.Id == MatroskaIds.SimpleBlock) {
				AddBlock(r.ReadData(hdr), simple: true, tracks, showBlocks, null);
			} else if (hdr.Id == MatroskaIds.BlockGroup) {
				long? duration = null;
				byte[]? block = null;
				bool hasReference = false;
				var refs = new List<long>();
				while (r.Stream.Position < hdr.End) {
					var c = r.ReadHeader()!.Value;
					if (c.Id == MatroskaIds.Block) block = r.ReadData(c);
					else if (c.Id == MatroskaIds.BlockDuration) duration = (long)EbmlReader.ToUInt(r.ReadData(c));
					else if (c.Id == MatroskaIds.ReferenceBlock) { hasReference = true; refs.Add(EbmlReader.ToInt(r.ReadData(c))); } else r.Skip(c);
				}
				_lastRefs = refs.Count == 0 ? "" : " refs=" + string.Join(",", refs);
				if (block is not null) AddBlock(block, simple: false, tracks, showBlocks, duration, hasReference);
			} else if (hdr.Size == EbmlReader.UnknownSize) {
				// An unknown-size cluster ends where the next top-level element starts.
				r.Stream.Position = hdr.DataStart - hdr.HeaderLength;
				return;
			} else {
				r.Skip(hdr);
			}
		}
	}

	private static string _lastRefs = "";

	private static void AddBlock(byte[] b, bool simple, Dictionary<long, TrackStats> tracks, int showBlocks, long? duration, bool hasReference = false) {
		var (track, len) = EbmlReader.ReadVint(b);
		short rel = (short)((b[len] << 8) | b[len + 1]);
		byte flags = b[len + 2];
		bool key = simple ? (flags & 0x80) != 0 : !hasReference;
		int lacing = (flags >> 1) & 3;
		int frames = lacing == 0 ? 1 : b[len + 3] + 1;
		long time = _clusterTime + rel;

		if (!tracks.TryGetValue(track, out var t)) tracks[track] = t = new TrackStats();
		t.Blocks++;
		if (key) t.KeyFrames++;
		if (lacing != 0) t.Laced++;
		t.Frames += frames;
		t.Bytes += b.Length - len - 3;
		t.First ??= time;
		t.Last = time;
		if (t.PrevTime is { } p) {
			long gap = time - p;
			t.MinGap = Math.Min(t.MinGap, gap);
			t.MaxGap = Math.Max(t.MaxGap, gap);
		}
		t.PrevTime = time;
		int index = BlockIndex[track] = BlockIndex.GetValueOrDefault(track, -1) + 1;
		if (SaveBlock is { } save && save.Track == track) {
			if (save.Index == index) File.WriteAllBytes(save.Path, b[(len + 3)..]);
			else if (save.Index < 0 && lacing == 0) {
				using var all = new FileStream(save.Path, index == 0 ? FileMode.Create : FileMode.Append);
				all.Write(b, len + 3, b.Length - len - 3);
			}
		}
		if (track == DumpTrack && t.FirstBlocks.Count < showBlocks) {
			string text = System.Text.Encoding.UTF8.GetString(b, len + 3, b.Length - len - 3).Replace("\r", "\\r").Replace("\n", "\\n");
			t.FirstBlocks.Add($"t={time} dur={duration} \"{text}\"");
		} else if (t.FirstBlocks.Count < showBlocks) {
			int payload = len + 3;
			t.FirstBlocks.Add($"{(simple ? "SimpleBlock" : "BlockGroup")} t={time} ms key={key} lacing={lacing} frames={frames} dur={duration}{(simple ? "" : _lastRefs)} " +
							  $"size={b.Length - payload} data={Convert.ToHexString(b, payload, Math.Min(24, b.Length - payload))}");
		}
	}
}
