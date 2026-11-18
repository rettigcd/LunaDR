using static RipDvd.Core.Matroska.MatroskaIds;

namespace RipDvd.Core.Matroska;

public enum MkvTrackType { Video = 1, Audio = 2, Subtitle = 17 }

/// <summary>A track to write. Numbers start at 1; MakeMKV uses the track number as the UID too.</summary>
public sealed class MkvTrack {
	public required int Number { get; init; }
	public required MkvTrackType Type { get; init; }
	public required string CodecId { get; init; }
	public byte[]? CodecPrivate { get; set; }
	public string? Language { get; init; }          // ISO 639-2, e.g. "eng"
	public string? Name { get; init; }
	public bool Default { get; init; }
	public bool Lacing { get; init; }
	public long DefaultDurationNs { get; init; }
	public int MinCache { get; init; }
	// Video
	public int PixelWidth { get; set; }
	public int PixelHeight { get; set; }
	public int DisplayWidth { get; set; }
	public int DisplayHeight { get; set; }
	// Audio
	public double SamplingFrequency { get; init; }
	public int Channels { get; set; }
	/// <summary>SOURCE_ID statistics tag, e.g. "0100E0" (MakeMKV's form).</summary>
	public string? SourceId { get; init; }

	// Statistics, filled while writing.
	internal long FrameCount, ByteCount, FirstPtsNs = long.MaxValue, LastEndNs;
}

public sealed record MkvChapter(long StartNs, long EndNs, string Name);

/// <summary>
/// Writes a Matroska file in the same layout MakeMKV uses:
///   EBML header, Segment { SeekHead, Void, Info, Tracks, Chapters, Clusters..., Cues, Tags }.
/// Blocks are passed in with times in nanoseconds; TimestampScale is 1 ms.
/// Clusters are started by the caller (normally at each video key frame).
/// </summary>
public sealed class MkvWriter : IDisposable {
	private const long NsPerTick = 1_000_000;   // ns per Matroska tick (1 ms)
	private const int SeekHeadReserve = 200;

	private readonly Stream _out;
	private readonly EbmlWriter _w;
	private readonly IReadOnlyList<MkvTrack> _tracks;
	private readonly string _writingApp;
	private long _segmentSizePos, _segmentDataStart, _seekHeadPos, _durationPos;
	private long _infoPos, _tracksPos, _tracksEnd, _chaptersPos;
	private readonly List<(long TimeMs, long ClusterPos)> _cues = [];

	private MemoryStream? _cluster;
	private EbmlWriter? _cw;
	private long _clusterTimeMs;
	private long _clusterPos;

	public MkvWriter(Stream output, IReadOnlyList<MkvTrack> tracks, string writingApp) {
		_out = output;
		_w = new EbmlWriter(output);
		_tracks = tracks;
		_writingApp = writingApp;
	}

	/// <summary>Writes everything up to the first cluster. Chapters must be known now (they come from the IFO).</summary>
	public void WriteHeader(IReadOnlyList<MkvChapter> chapters, string? title) {
		_w.Master(Ebml, e => {
			e.UInt(EbmlVersion, 1);
			e.UInt(EbmlReadVersion, 1);
			e.UInt(EbmlMaxIdLength, 4);
			e.UInt(EbmlMaxSizeLength, 8);
			e.String(DocType, "matroska");
			e.UInt(DocTypeVersion, 2);
			e.UInt(DocTypeReadVersion, 2);
		});

		// Segment with an 8-byte size, patched when finished.
		_w.Id(Segment);
		_segmentSizePos = _w.Position;
		_w.Size(0, 8);
		_segmentDataStart = _w.Position;

		// Room for the SeekHead, written at the end.
		_seekHeadPos = _w.Position;
		_w.Void(SeekHeadReserve);
		_w.Void(1021 + 3);   // MakeMKV leaves a ~1 KB Void here too (room for edits by other tools).

		_infoPos = _w.Position;
		_w.Id(Info);
		var info = new MemoryStream();
		var iw = new EbmlWriter(info);
		iw.UInt(TimestampScale, (ulong)NsPerTick);
		iw.String(MuxingApp, _writingApp);
		iw.String(WritingApp, _writingApp);
		if (title is not null) iw.String(Title, title);
		long durationOffset = info.Position + 3;   // ID (2) + size (1) precede the 8-byte float
		iw.Float(Duration, 0);
		iw.Date(DateUtc, DateTime.UtcNow);
		iw.Binary(SegmentUid, Guid.NewGuid().ToByteArray());
		_w.Size(info.Length);
		_durationPos = _w.Position + durationOffset;
		_w.Stream.Write(info.GetBuffer(), 0, (int)info.Length);

		_tracksPos = _w.Position;
		_w.Master(Tracks, t => {
			foreach (var track in _tracks) t.Master(TrackEntry, e => WriteTrack(e, track));
		});
		_tracksEnd = _w.Position;

		_chaptersPos = -1;
		if (chapters.Count > 0) {
			_chaptersPos = _w.Position;
			_w.Master(Chapters, c => c.Master(EditionEntry, ed => {
				ed.UInt(EditionFlagHidden, 0);
				ed.UInt(EditionFlagDefault, 1);
				ed.UInt(EditionUid, RandomUid());
				foreach (var ch in chapters) {
					ed.Master(ChapterAtom, a => {
						a.UInt(ChapterUid, RandomUid());
						a.UInt(ChapterTimeStart, (ulong)ch.StartNs);
						a.UInt(ChapterFlagHidden, 0);
						a.UInt(ChapterFlagEnabled, 1);
						a.UInt(ChapterTimeEnd, (ulong)ch.EndNs);
						a.Master(ChapterDisplay, d => {
							d.String(ChapString, ch.Name);
							d.String(ChapLanguage, "eng");
						});
					});
				}
			}));
		}
	}

	private static void WriteTrack(EbmlWriter e, MkvTrack t) {
		e.UInt(TrackNumber, (ulong)t.Number);
		e.UInt(TrackUid, (ulong)t.Number);
		e.UInt(TrackType, (ulong)t.Type);
		e.UInt(FlagDefault, t.Default ? 1UL : 0UL);
		e.UInt(FlagLacing, t.Lacing ? 1UL : 0UL);
		if (t.MinCache > 0) e.UInt(MinCache, (ulong)t.MinCache);
		e.String(CodecId, t.CodecId);
		if (t.CodecPrivate is not null) e.Binary(CodecPrivate, t.CodecPrivate);
		if (t.Language is not null) e.String(Language, t.Language);
		if (t.DefaultDurationNs > 0) e.UInt(DefaultDuration, (ulong)t.DefaultDurationNs);
		if (t.Name is not null) e.String(Name, t.Name);
		if (t.Type == MkvTrackType.Video) {
			e.Master(Video, v => {
				v.UInt(PixelWidth, (ulong)t.PixelWidth);
				v.UInt(PixelHeight, (ulong)t.PixelHeight);
				v.UInt(DisplayWidth, (ulong)t.DisplayWidth);
				v.UInt(DisplayHeight, (ulong)t.DisplayHeight);
			});
		} else if (t.Type == MkvTrackType.Audio) {
			e.Master(Audio, a => {
				a.Float(SamplingFrequency, t.SamplingFrequency);
				a.UInt(Channels, (ulong)t.Channels);
			});
		}
	}

	/// <summary>Close the current cluster (if any) and start a new one at <paramref name="timeNs"/>.</summary>
	public void StartCluster(long timeNs, bool addCue) {
		FlushCluster();
		_clusterTimeMs = ToMs(timeNs);
		_cluster = new MemoryStream();
		_cw = new EbmlWriter(_cluster);
		_cw.UInt(Timestamp, (ulong)_clusterTimeMs);
		_clusterPos = _w.Position;
		if (addCue) _cues.Add((_clusterTimeMs, _clusterPos - _segmentDataStart));
	}

	/// <summary>Video frame: a BlockGroup with BlockDuration, and ReferenceBlock for non-key frames.</summary>
	public void AddVideoFrame(MkvTrack track, long ptsNs, long durationNs, bool keyFrame, ReadOnlySpan<byte> data) {
		var body = new MemoryStream();
		var bw = new EbmlWriter(body);
		bw.Id(Block);
		bw.Size(BlockHeaderLength(track) + data.Length);
		WriteBlockHeader(body, track, ptsNs, flags: 0);
		body.Write(data);
		bw.UInt(BlockDuration, (ulong)Math.Max(1, ToMs(durationNs)));
		// ReferenceBlock: relative time of a block this one depends on. Value only signals "not a key frame".
		if (!keyFrame) bw.Int(ReferenceBlock, -1);
		_cw!.Master(BlockGroup, body.GetBuffer().AsSpan(0, (int)body.Length));
		Count(track, ptsNs, durationNs, data.Length, 1);
	}

	/// <summary>
	/// Subtitle text: a BlockGroup with the text as the block data and a BlockDuration.
	/// Captions are only known once they end, so they can start well before the current cluster; the block time is relative and may be negative.
	/// </summary>
	public void AddTextBlock(MkvTrack track, long startNs, long durationNs, string text) =>
		AddSubtitleBlock(track, startNs, durationNs, System.Text.Encoding.UTF8.GetBytes(text));

	/// <summary>A subtitle (text or VobSub bitmap) as a BlockGroup; BlockDuration is written when the duration is known.</summary>
	public void AddSubtitleBlock(MkvTrack track, long startNs, long? durationNs, ReadOnlySpan<byte> data) {
		var body = new MemoryStream();
		var bw = new EbmlWriter(body);
		bw.Id(Block);
		bw.Size(BlockHeaderLength(track) + data.Length);
		WriteBlockHeader(body, track, startNs, flags: 0);
		body.Write(data);
		if (durationNs is { } d) bw.UInt(BlockDuration, (ulong)Math.Max(1, ToMs(d)));
		_cw!.Master(BlockGroup, body.GetBuffer().AsSpan(0, (int)body.Length));
		Count(track, startNs, durationNs ?? 0, data.Length, 1);
	}

	/// <summary>Audio frames of equal size in one SimpleBlock with fixed-size lacing (MakeMKV packs 3 AC3 frames).</summary>
	public void AddLacedFrames(MkvTrack track, long firstPtsNs, long frameNs, IReadOnlyList<byte[]> frames) {
		bool lace = frames.Count > 1;
		int dataLength = frames.Sum(f => f.Length);
		_cw!.Id(SimpleBlock);
		_cw.Size(BlockHeaderLength(track) + (lace ? 1 : 0) + dataLength);
		WriteBlockHeader(_cluster!, track, firstPtsNs, flags: (byte)(0x80 | (lace ? 0x04 : 0)));   // keyframe, fixed-size lacing
		if (lace) _cluster!.WriteByte((byte)(frames.Count - 1));
		foreach (var f in frames) _cluster!.Write(f);
		Count(track, firstPtsNs, frameNs * frames.Count, dataLength, frames.Count);
	}

	private static int BlockHeaderLength(MkvTrack t) => (t.Number < 127 ? 1 : 2) + 3;

	private void WriteBlockHeader(Stream s, MkvTrack track, long ptsNs, byte flags) {
		new EbmlWriter(s).Size(track.Number, track.Number < 127 ? 1 : 2);   // track number as vint
		long rel = ToMs(ptsNs) - _clusterTimeMs;
		if (rel < short.MinValue || rel > short.MaxValue) throw new InvalidOperationException($"Block {rel} ms from cluster start; start clusters more often.");
		s.WriteByte((byte)(rel >> 8));
		s.WriteByte((byte)rel);
		s.WriteByte(flags);
	}

	private static void Count(MkvTrack t, long ptsNs, long durationNs, int bytes, int frames) {
		t.FrameCount += frames;
		t.ByteCount += bytes;
		t.FirstPtsNs = Math.Min(t.FirstPtsNs, ptsNs);
		t.LastEndNs = Math.Max(t.LastEndNs, ptsNs + durationNs);
	}

	private void FlushCluster() {
		if (_cluster is null) return;
		_w.Master(Cluster, _cluster.GetBuffer().AsSpan(0, (int)_cluster.Length));
		_cluster = null;
		_cw = null;
	}

	/// <summary>Writes the last cluster, Cues and Tags, and patches the SeekHead, Duration and Segment size.</summary>
	public void Finish(long durationNs) {
		FlushCluster();

		long cuesPos = _w.Position;
		_w.Master(Cues, c => {
			foreach (var (time, pos) in _cues) {
				c.Master(CuePoint, p => {
					p.UInt(CueTime, (ulong)time);
					p.Master(CueTrackPositions, tp => {
						tp.UInt(CueTrack, 1);
						tp.UInt(CueClusterPosition, (ulong)pos);
					});
				});
			}
		});

		long tagsPos = _w.Position;
		string date = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
		_w.Master(Tags, tags => {
			foreach (var t in _tracks.Where(t => t.FrameCount > 0)) {
				long trackDuration = t.LastEndNs - (t.FirstPtsNs == long.MaxValue ? 0 : t.FirstPtsNs);
				long bps = trackDuration > 0 ? (long)Math.Round(t.ByteCount * 8 / (trackDuration / 1e9)) : 0;
				tags.Master(Tag, tag => {
					tag.Master(Targets, tg => {
						tg.UInt(TagTrackUid, (ulong)t.Number);
						tg.String(TargetType, "MOVIE");
					});
					void Simple(string name, string value) => tag.Master(SimpleTag, s => {
						s.String(TagName, name);
						s.String(TagLanguage, "eng");
						s.String(TagString, value);
					});
					Simple("BPS", bps.ToString());
					Simple("DURATION", FormatDuration(trackDuration));
					Simple("NUMBER_OF_FRAMES", t.FrameCount.ToString());
					Simple("NUMBER_OF_BYTES", t.ByteCount.ToString());
					if (t.SourceId is not null) Simple("SOURCE_ID", t.SourceId);
					Simple("_STATISTICS_WRITING_APP", _writingApp);
					Simple("_STATISTICS_WRITING_DATE_UTC", date);
					Simple("_STATISTICS_TAGS", "BPS DURATION NUMBER_OF_FRAMES NUMBER_OF_BYTES" + (t.SourceId is not null ? " SOURCE_ID" : ""));
				});
			}
		});
		long end = _w.Position;

		RemoveEmptyTracks();

		// SeekHead into the reserved space, padded with a Void.
		_out.Position = _seekHeadPos;
		var seek = new MemoryStream();
		new EbmlWriter(seek).Master(SeekHead, sh => {
			void Entry(uint id, long pos) => sh.Master(Seek, s => {
				var idBytes = new MemoryStream();
				new EbmlWriter(idBytes).Id(id);
				s.Binary(SeekId, idBytes.ToArray());
				s.UInt(SeekPosition, (ulong)(pos - _segmentDataStart));
			});
			Entry(Info, _infoPos);
			Entry(Tracks, _tracksPos);
			if (_chaptersPos >= 0) Entry(Chapters, _chaptersPos);
			Entry(Cues, cuesPos);
			Entry(Tags, tagsPos);
		});
		_out.Write(seek.GetBuffer(), 0, (int)seek.Length);
		_w.Void(SeekHeadReserve - (int)seek.Length);

		// Duration (float, in TimestampScale units).
		_out.Position = _durationPos;
		Span<byte> d = stackalloc byte[8];
		// MakeMKV writes whole milliseconds (e.g. 139772 for 139772.97 ms).
		System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(d, durationNs / NsPerTick);
		_out.Write(d);

		// Segment size.
		_out.Position = _segmentSizePos;
		_w.Size(end - _segmentDataStart, 8);
		_out.Position = end;
		_out.Flush();
	}

	/// <summary>
	/// Tracks that got no blocks are removed from the Tracks element, as MakeMKV does (a stream can be declared but carry no data).
	/// The Tracks element was written before the data, so it is rewritten in place and the freed bytes are filled with a Void.
	/// </summary>
	private void RemoveEmptyTracks() {
		if (_tracks.All(t => t.FrameCount > 0)) return;
		var content = new MemoryStream();
		var cw = new EbmlWriter(content);
		foreach (var track in _tracks.Where(t => t.FrameCount > 0)) cw.Master(TrackEntry, e => WriteTrack(e, track));

		long available = _tracksEnd - _tracksPos;
		var tracks = new MemoryStream();
		var tw = new EbmlWriter(tracks);
		tw.Id(Tracks);
		long idLength = tracks.Length;
		tw.Size(content.Length);
		int sizeLength = (int)(tracks.Length - idLength);
		if (available - (tracks.Length + content.Length) == 1) {
			// A Void needs at least 2 bytes: spend the one spare byte on a longer size field instead.
			tracks.SetLength(idLength);
			tracks.Position = idLength;
			tw.Size(content.Length, sizeLength + 1);
		}
		tracks.Write(content.GetBuffer(), 0, (int)content.Length);

		_out.Position = _tracksPos;
		_out.Write(tracks.GetBuffer(), 0, (int)tracks.Length);
		long gap = available - tracks.Length;
		if (gap > 0) _w.Void((int)gap);
	}

	private static string FormatDuration(long ns) {
		var ts = TimeSpan.FromTicks(ns / 100);
		return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ns % 1_000_000_000:000000000}";
	}

	private static long ToMs(long ns) => (long)Math.Round(ns / (double)NsPerTick, MidpointRounding.AwayFromZero);

	private static ulong RandomUid() => (ulong)Random.Shared.NextInt64(1, long.MaxValue) * 2 + (ulong)Random.Shared.Next(2);

	public void Dispose() => _out.Dispose();
}
