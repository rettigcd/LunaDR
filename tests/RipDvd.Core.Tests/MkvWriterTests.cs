using RipDvd.Core.Matroska;
using static RipDvd.Core.Matroska.MatroskaIds;

namespace RipDvd.Core.Tests;

/// <summary><see cref="MkvWriter"/>: a small file written in memory and read back with <see cref="EbmlReader"/>.</summary>
public class MkvWriterTests {
	private const long Ms = 1_000_000;

	/// <summary>An element of the file, with its children if it is a master element.</summary>
	private sealed record Element(uint Id, byte[] Data, List<Element> Children) {
		public IEnumerable<Element> All(uint id) => Children.Where(c => c.Id == id);
		public IEnumerable<Element> Descendants(uint id) => Children.SelectMany(c => (c.Id == id ? [c] : Enumerable.Empty<Element>()).Concat(c.Descendants(id)));
		public ulong UInt => EbmlReader.ToUInt(Data);
	}

	private static List<Element> Parse(EbmlReader r, long end) {
		var list = new List<Element>();
		while (r.Stream.Position < end && r.ReadHeader() is { } h) {
			if (Describe(h.Id).Type == EbmlType.Master) {
				var children = Parse(r, h.End);
				r.Stream.Position = h.End;
				list.Add(new Element(h.Id, [], children));
			} else {
				list.Add(new Element(h.Id, r.ReadData(h), []));
			}
		}
		return list;
	}

	private static Element WriteSample(Action<MkvWriter, MkvTrack, MkvTrack> body, long durationNs) {
		var video = new MkvTrack { Number = 1, Type = MkvTrackType.Video, CodecId = "V_MPEG2", PixelWidth = 720, PixelHeight = 480, DisplayWidth = 853, DisplayHeight = 480 };
		var audio = new MkvTrack { Number = 2, Type = MkvTrackType.Audio, CodecId = "A_AC3", SamplingFrequency = 48000, Channels = 6, Lacing = true };
		var unused = new MkvTrack { Number = 3, Type = MkvTrackType.Subtitle, CodecId = "S_TEXT/UTF8" };
		var file = new MemoryStream();
		var mkv = new MkvWriter(file, [video, audio, unused], "test");
		mkv.WriteHeader([], null);
		body(mkv, video, audio);
		mkv.Finish(durationNs);
		file.Position = 0;
		var top = Parse(new EbmlReader(file), file.Length);
		return top.Single(e => e.Id == Segment);
	}

	[Fact]
	public void EmptyTracks_AreDroppedFromTracksAndTags() {
		// MakeMKV drops tracks that end up empty (e.g. a declared subtitle stream with no data); the Tracks element is rewritten in place.
		var segment = WriteSample((mkv, video, audio) => {
			mkv.StartCluster(0, 0);
			mkv.AddVideoFrame(video, 0, 33 * Ms, true, [1, 2, 3], []);
			mkv.AddLacedFrames(audio, 0, 32 * Ms, [[9, 9], [9, 9]]);
		}, 64 * Ms);
		var tracks = segment.All(Tracks).Single().All(TrackEntry).Select(t => t.All(TrackNumber).Single().UInt).ToList();
		Assert.Equal([1UL, 2UL], tracks);
		Assert.Equal(2, segment.Descendants(Tag).Count());
		Assert.NotEmpty(segment.Descendants(MatroskaIds.Void));   // the freed space
	}

	[Fact]
	public void ClusterTimestamp_IsTruncated_DurationIsWholeMilliseconds() {
		// MakeMKV truncates the cluster timestamp (500.5 ms → 500) and writes Duration in whole ms.
		var segment = WriteSample((mkv, video, audio) => {
			mkv.StartCluster(500 * Ms + Ms / 2, 500 * Ms + Ms / 2);
			mkv.AddVideoFrame(video, 500 * Ms + Ms / 2, 33 * Ms, true, [1], []);
			mkv.AddLacedFrames(audio, 500 * Ms + Ms / 2, 32 * Ms, [[9]]);
		}, 533 * Ms + 900_000);
		var cluster = segment.All(Cluster).Single();
		Assert.Equal(500UL, cluster.All(Timestamp).Single().UInt);
		double duration = EbmlReader.ToFloat(segment.All(Info).Single().All(MatroskaIds.Duration).Single().Data);
		Assert.Equal(533.0, duration);
	}
}
