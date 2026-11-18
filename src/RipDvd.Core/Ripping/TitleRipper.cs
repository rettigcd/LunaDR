using RipDvd.Core.Css;
using RipDvd.Core.Dvd;
using RipDvd.Core.Ifo;
using RipDvd.Core.Matroska;
using RipDvd.Core.Mpeg;

namespace RipDvd.Core.Ripping;

/// <summary>Result of ripping one title.</summary>
public sealed record RipResult(string OutputPath, long DurationNs, long Sectors, TimeSpan Elapsed, IReadOnlyList<string> Warnings);

/// <summary>
/// Rips one DVD title to an MKV file, the way MakeMKV does:
///   * tracks: the video, then each audio stream the title's PGC marks available;
///   * chapters: one per PGC program, timed from the cell playback times;
///   * streams copied unchanged; timestamps mapped onto one title timeline that starts at 0.
///
/// Timeline: each cell may restart the MPEG clock (STC). For each cell we take the first
/// VOBU's start time from its nav pack, and map stream time t to
///   title time = (time of all earlier cells) + (t − cell's first VOBU start).
/// </summary>
public sealed class TitleRipper {
	public const string AppName = "rip-dvd 0.1";

	private readonly string _videoTs;
	private readonly int _titleNumber;
	private readonly VmgIfo _vmg;
	private readonly TitleEntry _title;
	private readonly VtsIfo _vts;
	private readonly List<Pgc> _pgcs;

	public TitleRipper(string videoTsFolder, int titleNumber) {
		_videoTs = videoTsFolder;
		_titleNumber = titleNumber;
		_vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTsFolder, "VIDEO_TS.IFO")));
		_title = _vmg.Titles[titleNumber - 1];
		_vts = VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTsFolder, $"VTS_{_title.TitleSetNumber:00}_0.IFO")), _title.TitleSetNumber);
		_pgcs = _vts.Chapters[_title.TitleSetTitleNumber - 1].Select(c => c.PgcNumber).Distinct().Select(n => _vts.Pgcs[n - 1]).ToList();
	}

	/// <summary>Rip to <paramref name="outputPath"/>. <paramref name="progress"/> gets (sectors done, total).</summary>
	public RipResult Rip(string outputPath, Action<long, long>? progress = null) {
		var started = DateTime.UtcNow;
		var warnings = new List<string>();
		var pgc = _pgcs[0];
		if (_pgcs.Count > 1) warnings.Add($"Title uses {_pgcs.Count} PGCs; only cells of all PGCs are concatenated (chapters from the first).");

		using var vobs = VobReader.ForTitleSet(_videoTs, _title.TitleSetNumber);
		var key = TitleSectorReader.FindKey(vobs);
		var cells = _pgcs.SelectMany(p => p.Cells).ToList();
		var reader = new TitleSectorReader(vobs, cells, key);

		// ---- Tracks ----
		var video = new MkvTrack {
			Number = 1,
			Type = MkvTrackType.Video,
			CodecId = "V_MPEG2",
			Default = false,
			Lacing = false,
			DefaultDurationNs = VideoFrame.FieldsToNs(2),
			MinCache = 2,
			PixelWidth = _vts.Video.Width,
			PixelHeight = _vts.Video.Height,
			SourceId = "0100E0",
		};
		SetDisplaySize(video, _vts.Video.AspectRatio);
		var tracks = new List<MkvTrack> { video };
		var audioTracks = new Dictionary<int, (MkvTrack Track, Ac3Framer Framer)>();   // key: AC3 substream number
		for (int i = 0; i < _vts.Audio.Count; i++) {
			var ctl = pgc.AudioControl[i];
			if (!ctl.Available) continue;
			var a = _vts.Audio[i];
			if (a.Coding != "AC3") { warnings.Add($"Audio stream {i} is {a.Coding}; not supported yet, skipped."); continue; }
			var track = new MkvTrack {
				Number = tracks.Count + 1,
				Type = MkvTrackType.Audio,
				CodecId = "A_AC3",
				Language = Languages.ToIso6392(a.Language),
				Name = ChannelName(a.Channels),
				Default = audioTracks.Count == 0,
				Lacing = true,
				DefaultDurationNs = Ac3Framer.FrameNs,
				SamplingFrequency = a.SampleRate,
				Channels = a.Channels,
				SourceId = $"01{0x80 + ctl.StreamNumber:X2}BD",
			};
			tracks.Add(track);
			audioTracks[ctl.StreamNumber] = (track, new Ac3Framer());
		}

		// Closed captions (line 21 data in the video) become a text track, as MakeMKV's "CC→Text".
		MkvTrack? captionTrack = null;
		Captions.Eia608Decoder? captionDecoder = null;
		if (_vts.Video.Line21Field1 || _vts.Video.Line21Field2) {
			captionTrack = new MkvTrack {
				Number = tracks.Count + 1,
				Type = MkvTrackType.Subtitle,
				CodecId = "S_TEXT/UTF8",
				Language = "eng",
				Default = true,
				Lacing = false,
				SourceId = "0100E0",
			};
			tracks.Add(captionTrack);
			captionDecoder = new Captions.Eia608Decoder();
		}

		// ---- Chapters: program n starts at its entry cell ----
		var cellStartNs = new long[pgc.Cells.Count + 1];
		var elapsed = default(DvdTime);
		for (int c = 0; c < pgc.Cells.Count; c++) {
			elapsed += pgc.Cells[c].PlaybackTime;   // add frame counts, convert once (exact)
			cellStartNs[c + 1] = elapsed.ToNanoseconds();
		}
		long ifoDurationNs = cellStartNs[^1];
		var chapterStarts = pgc.ProgramEntryCells.Select(cell => cellStartNs[cell - 1]).ToList();
		var chapters = new List<MkvChapter>();
		for (int i = 0; i < chapterStarts.Count; i++) {
			long end = i + 1 < chapterStarts.Count ? chapterStarts[i + 1] : ifoDurationNs;
			chapters.Add(new MkvChapter(chapterStarts[i], end, $"Chapter {i + 1:00}"));
		}

		// ---- Mux ----
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
		using var file = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
		var mkv = new MkvWriter(file, tracks, AppName);
		var framer = new Mpeg2VideoFramer { ExtractCaptions = captionDecoder is not null };
		bool headerWritten = false;
		var pendingVideo = new List<VideoFrame>();
		var audioQueues = audioTracks.Values.Select(v => (v.Track, Queue: new Queue<AudioFrame>())).ToList();

		Cell? currentCell = null;
		long cellOffsetNs = 0;          // title time − stream time for the current cell
		long elapsedBeforeCell = 0;     // title time at the start of the current cell
		uint? cellFirstPtm = null, cellLastPtm = null;
		long sectorsDone = 0, total = reader.TotalSectors;

		void WriteGop(List<VideoFrame> gop, long audioUntilNs) {
			if (!headerWritten) {
				video.CodecPrivate = framer.SequenceHeader;
				mkv.WriteHeader(chapters, null);
				headerWritten = true;
			}
			var keyFrame = gop[0];
			mkv.StartCluster(keyFrame.Pts, addCue: true);
			foreach (var f in gop) mkv.AddVideoFrame(video, f.Pts, f.DurationNs, f.IsKeyFrame, f.Data);
			foreach (var (track, queue) in audioQueues) WriteAudio(mkv, track, queue, audioUntilNs);
			if (captionDecoder is not null) {
				FeedCaptions(captionDecoder, gop);
				foreach (var ev in captionDecoder.TakeEvents()) mkv.AddTextBlock(captionTrack!, ev.StartNs, ev.EndNs - ev.StartNs, ev.Text);
			}
		}

		foreach (var sector in reader.ReadAll()) {
			if (!ReferenceEquals(sector.Cell, currentCell)) {
				if (currentCell is not null && cellFirstPtm is { } f0 && cellLastPtm is { } l0) elapsedBeforeCell += ToNs90k(l0 - f0);
				currentCell = sector.Cell;
				cellFirstPtm = cellLastPtm = null;
			}
			foreach (var p in ProgramStreamParser.Parse(sector.Data)) {
				if (p.Stream.Kind == StreamKind.Navigation) {
					if (p.Stream.Number == 0) {   // PCI: VOBU start/end presentation times
						var nav = NavPack.Parse(sector.Data.Span);
						if (cellFirstPtm is null) {
							cellFirstPtm = nav.StartPtm;
							cellOffsetNs = elapsedBeforeCell - ToNs90k(nav.StartPtm);
						}
						cellLastPtm = nav.EndPtm;
					}
					continue;
				}
				long? pts = p.Pts is { } raw ? ToNs90k(raw) + cellOffsetNs : null;
				if (p.Stream.Kind == StreamKind.Video) {
					framer.Feed(p.Payload.Span, pts);
				} else if (p.Stream.Kind == StreamKind.Ac3 && audioTracks.TryGetValue(p.Stream.Number, out var at)) {
					at.Framer.Feed(p.Payload.Span, pts, p.FirstAccessUnit);
					var q = audioQueues.First(x => x.Track == at.Track).Queue;
					foreach (var af in at.Framer.TakeReady()) q.Enqueue(af);
				}
			}

			// Video frames come out a GOP at a time; write each GOP once the next one has started.
			foreach (var vf in framer.TakeReady()) {
				if (vf.IsKeyFrame && pendingVideo.Count > 0) {
					WriteGop(pendingVideo, vf.Pts);
					pendingVideo = [];
				}
				pendingVideo.Add(vf);
			}

			if (++sectorsDone % 512 == 0) progress?.Invoke(sectorsDone, total);
		}

		framer.Flush();
		pendingVideo.AddRange(framer.TakeReady());
		if (pendingVideo.Count > 0) WriteGop(pendingVideo, long.MaxValue);
		foreach (var (track, queue) in audioQueues) if (queue.Count > 0) WriteAudio(mkv, track, queue, long.MaxValue);
		if (captionDecoder is not null && headerWritten) {
			captionDecoder.Finish(video.LastEndNs);
			foreach (var ev in captionDecoder.TakeEvents()) mkv.AddTextBlock(captionTrack!, ev.StartNs, ev.EndNs - ev.StartNs, ev.Text);
		}
		progress?.Invoke(total, total);

		long durationNs = video.LastEndNs;
		mkv.Finish(durationNs);
		return new RipResult(outputPath, durationNs, total, DateTime.UtcNow - started, warnings);
	}

	/// <summary>
	/// Feed a GOP's caption byte pairs to the decoder.
	/// The pairs carried in the GOP's user data belong to its pictures in display order: pair k is shown with the k-th displayed picture.
	/// Extra pairs (more than pictures) follow at one frame per pair.
	/// </summary>
	private static void FeedCaptions(Captions.Eia608Decoder decoder, List<VideoFrame> gop) {
		var pairs = gop.SelectMany(f => f.Captions).ToList();
		if (pairs.Count == 0) return;
		var times = gop.Select(f => f.Pts).Order().ToList();
		long frame = VideoFrame.FieldsToNs(2);
		for (int k = 0; k < pairs.Count; k++) {
			long t = k < times.Count ? times[k] : times[^1] + (k - times.Count + 1) * frame;
			decoder.Feed(t, pairs[k].Item1, pairs[k].Item2);
		}
	}

	/// <summary>Write queued audio frames that start before <paramref name="untilNs"/>, 3 per laced block like MakeMKV.</summary>
	private static void WriteAudio(MkvWriter mkv, MkvTrack track, Queue<AudioFrame> queue, long untilNs) {
		var group = new List<AudioFrame>(3);
		void Emit() {
			if (group.Count == 0) return;
			mkv.AddLacedFrames(track, group[0].Pts, Ac3Framer.FrameNs, group.Select(f => f.Data).ToList());
			group.Clear();
		}
		while (queue.Count > 0 && queue.Peek().Pts < untilNs) {
			var f = queue.Dequeue();
			// Lace only frames that are contiguous in time and equal in size.
			if (group.Count > 0 && (f.Data.Length != group[0].Data.Length || f.Pts != group[^1].Pts + Ac3Framer.FrameNs)) Emit();
			group.Add(f);
			if (group.Count == 3) Emit();
		}
		Emit();
	}

	private static void SetDisplaySize(MkvTrack v, string aspect) {
		v.DisplayHeight = v.PixelHeight;
		v.DisplayWidth = aspect == "16:9" ? (int)Math.Round(v.PixelHeight * 16 / 9.0) : (int)Math.Round(v.PixelHeight * 4 / 3.0);
	}

	private static string ChannelName(int channels) => channels switch {
		1 => "Mono",
		2 => "Stereo",
		6 => "Surround 5.1",
		7 => "Surround 6.1",
		8 => "Surround 7.1",
		_ => $"{channels} channels",
	};

	private static long ToNs90k(long ticks) => ticks * 100_000 / 9;
}
