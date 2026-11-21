using System.Security.Cryptography;
using LunaDR.Core.Matroska;

namespace DvdProbe;

/// <summary>
/// Probe: compare two Matroska files track by track (ours vs MakeMKV's).
/// For each track it compares codec, frame count, payload bytes, a SHA-256 over all frame payloads (laced frames split apart,
/// so different lacing doesn't matter), and the first/last block times; also Duration and chapter start times.
/// Tracks are matched by codec and position among tracks of that codec, since track numbers may differ.
/// </summary>
internal static class MkvCompare {
	private sealed class Track {
		public long Number;
		public string Codec = "";
		public string? Language;
		public string? Name;
		public long DefaultDurationNs;
		public long Frames, Bytes;
		public long? First, Last;
		public readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		public string Digest = "";
		public readonly IncrementalHash RefHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		public string RefDigest = "";
		public long References;
		public readonly IncrementalHash TimeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		public string TimeDigest = "";
	}

	private sealed class Info {
		public double Duration;
		public readonly List<ulong> Chapters = [];
		public readonly Dictionary<long, Track> Tracks = [];
	}

	public static int Run(string a, string b) {
		var x = Read(a);
		var y = Read(b);
		bool same = true;
		void Line(string what, string va, string vb) {
			bool eq = va == vb;
			same &= eq;
			Console.WriteLine($"  {(eq ? "same" : "DIFF")}  {what,-28} {va,-30} {(eq ? "" : vb)}");
		}

		Console.WriteLine($"A: {a}\nB: {b}");
		Line("Duration (ms)", x.Duration.ToString("0.###"), y.Duration.ToString("0.###"));
		Line("Chapter starts", string.Join(",", x.Chapters.Select(c => c / 1_000_000)), string.Join(",", y.Chapters.Select(c => c / 1_000_000)));

		var ka = Keyed(x);
		var kb = Keyed(y);
		foreach (var key in ka.Keys.Union(kb.Keys).Order()) {
			ka.TryGetValue(key, out var ta);
			kb.TryGetValue(key, out var tb);
			Console.WriteLine($" {key}  (A track {ta?.Number.ToString() ?? "-"}, B track {tb?.Number.ToString() ?? "-"})");
			if (ta is null || tb is null) {
				same = false;
				Console.WriteLine("  DIFF  track missing on one side");
				continue;
			}
			Line("Language / Name", $"{ta.Language}/{ta.Name}", $"{tb.Language}/{tb.Name}");
			Line("Frames", ta.Frames.ToString(), tb.Frames.ToString());
			Line("Payload bytes", ta.Bytes.ToString(), tb.Bytes.ToString());
			Line("Payload SHA-256", ta.Digest[..16], tb.Digest[..16]);
			Line("ReferenceBlocks (count, SHA)", $"{ta.References} {ta.RefDigest[..16]}", $"{tb.References} {tb.RefDigest[..16]}");
			Line("First / last frame (ms)", $"{ta.First} / {ta.Last}", $"{tb.First} / {tb.Last}");
			Line("Every frame time (SHA)", ta.TimeDigest[..16], tb.TimeDigest[..16]);
		}
		Console.WriteLine(same ? "RESULT: identical in all compared properties" : "RESULT: differences found");
		return same ? 0 : 1;
	}

	/// <summary>"A_AC3#2" = the second A_AC3 track.</summary>
	private static Dictionary<string, Track> Keyed(Info info) {
		var result = new Dictionary<string, Track>();
		foreach (var g in info.Tracks.Values.OrderBy(t => t.Number).GroupBy(t => t.Codec)) {
			int i = 1;
			foreach (var t in g) result[$"{g.Key}#{i++}"] = t;
		}
		return result;
	}

	private static Info Read(string path) {
		var info = new Info();
		using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
		var r = new EbmlReader(fs);
		Walk(r, fs.Length, info, null, 0);
		foreach (var t in info.Tracks.Values) {
			t.Digest = Convert.ToHexString(t.Hash.GetHashAndReset());
			t.RefDigest = Convert.ToHexString(t.RefHash.GetHashAndReset());
			t.TimeDigest = Convert.ToHexString(t.TimeHash.GetHashAndReset());
		}
		return info;
	}

	private static long _clusterTime;
	private static Track? _lastTrack;

	private static void Walk(EbmlReader r, long end, Info info, Track? current, int depth) {
		while (r.Stream.Position < end) {
			var h = r.ReadHeader();
			if (h is null) return;
			var hdr = h.Value;
			long stop = Math.Min(hdr.End, end);
			switch (hdr.Id) {
				case MatroskaIds.Segment:
				case MatroskaIds.Tracks:
				case MatroskaIds.Info:
				case MatroskaIds.Chapters:
				case MatroskaIds.EditionEntry:
				case MatroskaIds.Cluster:
				case MatroskaIds.BlockGroup:
					Walk(r, stop, info, current, depth + 1);
					r.Stream.Position = stop;
					break;
				case MatroskaIds.TrackEntry: {
						var t = new Track();
						Walk(r, stop, info, t, depth + 1);
						info.Tracks[t.Number] = t;
						r.Stream.Position = stop;
						break;
					}
				case MatroskaIds.ChapterAtom:
					Walk(r, stop, info, current, depth + 1);
					r.Stream.Position = stop;
					break;
				case MatroskaIds.TrackNumber: current!.Number = (long)EbmlReader.ToUInt(r.ReadData(hdr)); break;
				case MatroskaIds.CodecId: current!.Codec = EbmlReader.ToText(r.ReadData(hdr)); break;
				case MatroskaIds.Language: current!.Language = EbmlReader.ToText(r.ReadData(hdr)); break;
				case MatroskaIds.Name: current!.Name = EbmlReader.ToText(r.ReadData(hdr)); break;
				case MatroskaIds.DefaultDuration: current!.DefaultDurationNs = (long)EbmlReader.ToUInt(r.ReadData(hdr)); break;
				case MatroskaIds.Duration: info.Duration = EbmlReader.ToFloat(r.ReadData(hdr)); break;
				case MatroskaIds.ChapterTimeStart: info.Chapters.Add(EbmlReader.ToUInt(r.ReadData(hdr))); break;
				case MatroskaIds.Timestamp: _clusterTime = (long)EbmlReader.ToUInt(r.ReadData(hdr)); break;
				case MatroskaIds.ReferenceBlock:
					if (_lastTrack is { } lt) {
						lt.RefHash.AppendData(BitConverter.GetBytes(EbmlReader.ToInt(r.ReadData(hdr))));
						lt.References++;
					} else r.Skip(hdr);
					break;
				case MatroskaIds.SimpleBlock:
				case MatroskaIds.Block:
					AddBlock(info, r.ReadData(hdr));
					break;
				default:
					r.Skip(hdr);
					break;
			}
		}
	}

	private static void AddBlock(Info info, byte[] b) {
		var (number, len) = EbmlReader.ReadVint(b);
		if (!info.Tracks.TryGetValue(number, out var t)) return;
		_lastTrack = t;   // a BlockGroup's ReferenceBlocks follow its Block
		long time = _clusterTime + (short)((b[len] << 8) | b[len + 1]);
		int lacing = (b[len + 2] >> 1) & 3;
		int p = len + 3;
		var frames = new List<(int Offset, int Length)>();
		if (lacing == 0) {
			frames.Add((p, b.Length - p));
		} else if (lacing == 2) {   // fixed-size lacing (the only kind MakeMKV and we write)
			int count = b[p] + 1;
			p++;
			int size = (b.Length - p) / count;
			for (int i = 0; i < count; i++) frames.Add((p + i * size, size));
		} else {
			throw new NotSupportedException($"Lacing type {lacing} not handled by the compare probe.");
		}
		for (int i = 0; i < frames.Count; i++) t.TimeHash.AppendData(BitConverter.GetBytes(time + i * t.DefaultDurationNs / 1_000_000));
		foreach (var (offset, length) in frames) {
			t.Hash.AppendData(b, offset, length);
			t.Bytes += length;
			t.Frames++;
		}
		t.First ??= time;
		// Time of the last frame in the block (laced frames follow each other at DefaultDuration), so lacing differences don't matter.
		t.Last = time + (frames.Count - 1) * t.DefaultDurationNs / 1_000_000;
	}
}
