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
	/// <param name="maxCells">For testing: rip only the first cells of the title.</param>
	public RipResult Rip(string outputPath, Action<long, long>? progress = null, int maxCells = int.MaxValue, RipOptions? options = null) {
		options ??= RipOptions.Default;
		var started = DateTime.UtcNow;
		var warnings = new List<string>();
		var pgc = _pgcs[0];
		if (_pgcs.Count > 1) warnings.Add($"Title uses {_pgcs.Count} PGCs; only cells of all PGCs are concatenated (chapters from the first).");

		using var vobs = VobReader.ForTitleSet(_videoTs, _title.TitleSetNumber);
		// A drive only returns scrambled sectors after the CSS handshake (unless something, e.g. MakeMKV, already did it).
		// Unprotected discs make the drive reject the handshake ("Incorrect function"), so a failure only matters if reading then fails.
		string? unlockError = DriveUnlocker.TryUnlock(_videoTs);
		CssKey? key;
		try {
			key = TitleSectorReader.FindKey(vobs);
		} catch (IOException ex) when (unlockError is not null) {
			throw new IOException($"{ex.Message} (drive authentication failed: {unlockError})", ex);
		}
		var cells = _pgcs.SelectMany(p => p.Cells).Take(maxCells).ToList();
		var reader = new TitleSectorReader(vobs, cells, key);

		var (video, tracks, audioTracks, subtitleTracks, captionTrack, captionDecoder, chapters) = BuildPlan(options, warnings);

		// ---- Mux ----
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
		using var file = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
		var mkv = new MkvWriter(file, tracks, AppName);
		var framer = new Mpeg2VideoFramer { ExtractCaptions = captionDecoder is not null };
		bool headerWritten = false;
		var pendingVideo = new List<VideoFrame>();
		var audioQueues = audioTracks.Values.Select(v => (v.Track, v.Framer, Queue: new Queue<AudioFrame>())).ToList();
		var subtitleQueues = subtitleTracks.Values.ToDictionary(v => v.Track, v => new Queue<SubpictureUnit>());

		// Subtitles are written into the cluster of the GOP they start in.
		void WriteSubtitles(long untilNs) {
			foreach (var (track, queue) in subtitleQueues) {
				while (queue.Count > 0 && queue.Peek().Pts < untilNs) {
					var u = queue.Dequeue();
					mkv.AddSubtitleBlock(track, u.Pts, u.DurationNs, u.Data);
				}
			}
		}

		Cell? currentCell = null;
		long cellOffsetNs = 0;          // title time − stream time for the current cell
		long elapsedBeforeCell = 0;     // title time at the start of the current cell
		uint? cellFirstPtm = null, cellLastPtm = null;
		long sectorsDone = 0, total = reader.TotalSectors;

		long? lastAnchor = null, olderAnchor = null;   // presentation times of the last two I/P pictures, in decode order
		long? clusterKeyPts = null;                    // key frame that started the current cluster

		void WriteGop(List<VideoFrame> gop, long audioUntilNs) {
			if (!headerWritten) {
				video.CodecPrivate = framer.SequenceHeader;
				// Channel counts come from the audio stream once seen: the IFO says 5 for 5.1 DTS, where MakeMKV (and the stream) say 6.
				foreach (var (track, af, _) in audioQueues) {
					if (af.Channels > 0) {
						track.Channels = af.Channels;
						track.Name = ChannelName(af.Channels);
					}
				}
				mkv.WriteHeader(chapters, null);
				headerWritten = true;
			}
			// Clusters as MakeMKV makes them: a new cluster at a key frame at least 400 ms after the current cluster's key frame,
			// or where a chapter starts; otherwise the GOP joins the current cluster (short GOPs).
			// The cluster's timestamp is its earliest block (B pictures shown before the key frame); its cue uses the key frame's time.
			var keyFrame = gop[0];
			bool chapterStarts = chapters.Any(c => c.StartNs > (clusterKeyPts ?? long.MinValue) + 1_000_000 && c.StartNs <= keyFrame.Pts + 1_000_000);
			if (clusterKeyPts is null || keyFrame.Pts - clusterKeyPts >= 400_000_000 || chapterStarts) {
				mkv.StartCluster(gop.Min(f => f.Pts), cueTimeNs: keyFrame.Pts);
				clusterKeyPts = keyFrame.Pts;
			}
			foreach (var f in gop) {
				// References, like MakeMKV: a P picture uses the previous anchor (I or P); a B picture the two previous anchors in decode order.
				// References never reach back past a key frame: the B pictures right after it (shown before it, in an open GOP)
				// reference only the key frame (checked against MakeMKV: all 6,119 references of a test title identical).
				IReadOnlyList<long> refs = f.PictureType switch {
					'P' when lastAnchor is { } a => [a],
					'B' when olderAnchor is { } o && lastAnchor is { } a => [o, a],
					'B' when lastAnchor is { } a => [a],
					_ => [],
				};
				mkv.AddVideoFrame(video, f.Pts, f.DurationNs, f.IsKeyFrame, f.Data, refs);
				if (f.IsKeyFrame) {
					olderAnchor = null;
					lastAnchor = f.Pts;
				} else if (f.PictureType is 'I' or 'P') {
					olderAnchor = lastAnchor;
					lastAnchor = f.Pts;
				}
			}
			foreach (var (track, af, queue) in audioQueues) WriteAudio(mkv, track, af, queue, audioUntilNs);
			WriteSubtitles(audioUntilNs);
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
				} else if (audioTracks.TryGetValue(p.Stream, out var at)) {
					at.Framer.Feed(p.Payload.Span, pts, p.FirstAccessUnit);
					var q = audioQueues.First(x => x.Track == at.Track).Queue;
					foreach (var af in at.Framer.TakeReady()) q.Enqueue(af);
				} else if (p.Stream.Kind == StreamKind.Subpicture && subtitleTracks.TryGetValue(p.Stream.Number, out var st)) {
					st.Assembler.Feed(p.Payload.Span, pts);
					foreach (var unit in st.Assembler.TakeReady()) subtitleQueues[st.Track].Enqueue(unit);
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
		foreach (var (track, af, queue) in audioQueues) {
			af.Flush();
			foreach (var f in af.TakeReady()) queue.Enqueue(f);
			if (queue.Count > 0) WriteAudio(mkv, track, af, queue, long.MaxValue);
		}
		if (headerWritten) WriteSubtitles(long.MaxValue);
		if (captionDecoder is not null && headerWritten) {
			captionDecoder.Finish(video.LastEndNs);
			foreach (var ev in captionDecoder.TakeEvents()) mkv.AddTextBlock(captionTrack!, ev.StartNs, ev.EndNs - ev.StartNs, ev.Text);
		}
		progress?.Invoke(total, total);

		// Like MakeMKV: the end of whichever audio/video track runs longest (on the film disc the audio ends 9 ms after the video).
		long durationNs = tracks.Where(t => t.Type != MkvTrackType.Subtitle).Max(t => t.LastEndNs);
		mkv.Finish(durationNs);
		return new RipResult(outputPath, durationNs, total, DateTime.UtcNow - started, warnings);
	}

	/// <summary>The tracks and chapters a rip will write, after applying the options.</summary>
	private sealed record Plan(
		MkvTrack Video, List<MkvTrack> Tracks,
		Dictionary<StreamId, (MkvTrack Track, AudioFramer Framer)> AudioTracks,
		Dictionary<int, (MkvTrack Track, SubpictureAssembler Assembler)> SubtitleTracks,
		MkvTrack? CaptionTrack, Captions.Eia608Decoder? CaptionDecoder, List<MkvChapter> Chapters);

	private Plan BuildPlan(RipOptions options, List<string> warnings) {
		var pgc = _pgcs[0];
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
		var audioTracks = new Dictionary<StreamId, (MkvTrack Track, AudioFramer Framer)>();
		for (int i = 0; i < _vts.Audio.Count; i++) {
			var ctl = pgc.AudioControl[i];
			if (!ctl.Available) continue;
			var a = _vts.Audio[i];
			// AC3 is substream 0x80+n of private stream 1, DTS 0x88+n.
			(StreamKind kind, string codecId, AudioFramer audioFramer, int substreamBase) = a.Coding switch {
				"AC3" => (StreamKind.Ac3, "A_AC3", (AudioFramer)new Ac3Framer(), 0x80),
				"DTS" => (StreamKind.Dts, "A_DTS", new DtsFramer(), 0x88),
				// LPCM is substream 0xA0+n; MakeMKV converts it to little-endian PCM in 1/30 s blocks.
				"LPCM" when a.BitsPerSample == 16 => (StreamKind.Lpcm, "A_PCM/INT/LIT", new LpcmFramer(a.Channels, a.SampleRate, 16), 0xA0),
				_ => (StreamKind.Other, "", null!, 0),
			};
			string? audioLanguage = a.Language is null ? null : Languages.ToIso6392(a.Language);   // MakeMKV writes no Language when the disc gives none
			if (!RipOptions.Keeps(options.AudioLanguages, audioLanguage)) continue;
			if (kind == StreamKind.Other) {
				warnings.Add($"Audio stream {i} is {a.Coding}; not supported yet, skipped.");
				continue;
			}
			var track = new MkvTrack {
				Number = tracks.Count + 1,
				Type = MkvTrackType.Audio,
				CodecId = codecId,
				Language = audioLanguage,
				Name = ChannelName(a.Channels),
				Default = audioTracks.Count == 0,
				DefaultDurationNs = audioFramer.FrameNs > 0 ? audioFramer.FrameNs : 512 * 1_000_000_000L / a.SampleRate,   // DTS: known once the stream is read
				BitDepth = kind == StreamKind.Lpcm ? a.BitsPerSample : 0,
				Lacing = kind != StreamKind.Lpcm,
				SamplingFrequency = a.SampleRate,
				Channels = a.Channels,
				SourceId = $"01{substreamBase + ctl.StreamNumber:X2}BD",
			};
			tracks.Add(track);
			audioTracks[new StreamId(kind, ctl.StreamNumber)] = (track, audioFramer);
		}

		// Subpicture (bitmap subtitle) streams become S_VOBSUB tracks.
		// Each declared stream names a physical substream per display mode; for 16:9 video the "wide" one is used, else the 4:3 one.
		// Streams that point at the same substream are listed once (MakeMKV does the same).
		var subtitleTracks = new Dictionary<int, (MkvTrack Track, SubpictureAssembler Assembler)>();   // key: substream number
		string vobSubHeader = VobSubHeader.Build(_vts.Video.Width, _vts.Video.Height, pgc.Palette, AppName);
		for (int i = 0; i < _vts.Subpictures.Count; i++) {
			var ctl = pgc.SubpictureControl[i];
			if (!ctl.Available) continue;
			int substream = _vts.Video.AspectRatio == "16:9" ? ctl.StreamWide : ctl.Stream4x3;
			if (subtitleTracks.ContainsKey(substream)) continue;
			string subtitleLanguage = Languages.ToIso6392(_vts.Subpictures[i].Language);
			if (!RipOptions.Keeps(options.SubtitleLanguages, subtitleLanguage)) continue;
			var track = new MkvTrack {
				Number = tracks.Count + 1,
				Type = MkvTrackType.Subtitle,
				CodecId = "S_VOBSUB",
				CodecPrivate = System.Text.Encoding.ASCII.GetBytes(vobSubHeader),
				Language = subtitleLanguage,
				Default = subtitleTracks.Count == 0,
				Lacing = false,
				SourceId = $"01{0x20 + substream:X2}BD",
			};
			tracks.Add(track);
			subtitleTracks[substream] = (track, new SubpictureAssembler());
		}

		// Closed captions (line 21 data in the video) become a text track, as MakeMKV's "CC→Text".
		// It is the default subtitle track only when there are no bitmap subtitles.
		MkvTrack? captionTrack = null;
		Captions.Eia608Decoder? captionDecoder = null;
		if (options.ClosedCaptions && (_vts.Video.Line21Field1 || _vts.Video.Line21Field2)) {
			captionTrack = new MkvTrack {
				Number = tracks.Count + 1,
				Type = MkvTrackType.Subtitle,
				CodecId = "S_TEXT/UTF8",
				Language = "eng",
				Default = subtitleTracks.Count == 0,
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
		// A single chapter carries no information; MakeMKV writes no Chapters element then.
		if (chapters.Count == 1 || !options.Chapters) chapters.Clear();

		return new Plan(video, tracks, audioTracks, subtitleTracks, captionTrack, captionDecoder, chapters);
	}

	/// <summary>
	/// What a rip with these options would write, without reading any video: one line per track, then the chapter count.
	/// Used by the CLI's --dry-run; it shares <see cref="BuildPlan"/> with <see cref="Rip"/>, so it shows exactly what a rip produces.
	/// (Tracks that turn out to carry no data are still dropped by a real rip.)
	/// </summary>
	public IReadOnlyList<string> Describe(RipOptions? options = null) {
		var warnings = new List<string>();
		var plan = BuildPlan(options ?? RipOptions.Default, warnings);
		var lines = plan.Tracks.Select(t => t.Type switch {
			MkvTrackType.Video => $"video     {t.CodecId} {t.PixelWidth}x{t.PixelHeight} (display {t.DisplayWidth}x{t.DisplayHeight})",
			MkvTrackType.Audio => $"audio     {t.CodecId} {t.Name} {t.Language ?? "(no language)"}{(t.Default ? " [default]" : "")}",
			_ => $"subtitle  {t.CodecId} {t.Language ?? "(no language)"}{(t.Default ? " [default]" : "")}",
		}).ToList();
		lines.Add($"chapters  {(plan.Chapters.Count == 0 ? "none" : plan.Chapters.Count.ToString())}");
		lines.AddRange(warnings.Select(w => "warning   " + w));
		return lines;
	}

	/// <summary>
	/// Feed a GOP's caption byte pairs to the decoder.
	/// Line-21 captions belong to the broadcast frame rate: one byte pair per 1/29.97 s video frame (two fields), starting when the GOP is first shown.
	/// With 3:2 pulldown (film) a GOP shows fewer pictures than frames, so pairs are timed by frame slot, not by picture.
	/// Timing by picture was 50–67 ms late on a film disc; on video content (no pulldown) both are the same.
	/// </summary>
	private static void FeedCaptions(Captions.Eia608Decoder decoder, List<VideoFrame> gop) {
		var pairs = gop.SelectMany(f => f.Captions).ToList();
		if (pairs.Count == 0) return;
		long start = gop.Min(f => f.Pts);
		for (int k = 0; k < pairs.Count; k++) {
			decoder.Feed(start + VideoFrame.FieldsToNs(2L * k), pairs[k].Item1, pairs[k].Item2);
		}
	}

	/// <summary>
	/// Write queued audio frames that start before <paramref name="untilNs"/>, laced like MakeMKV: 96 ms per block (3 AC3 or 9 DTS frames).
	/// </summary>
	private static void WriteAudio(MkvWriter mkv, MkvTrack track, AudioFramer framer, Queue<AudioFrame> queue, long untilNs) {
		int perBlock = framer.FramesPerBlock;
		long frameNs = framer.FrameNs;
		var group = new List<AudioFrame>(perBlock);
		void Emit() {
			if (group.Count == 0) return;
			mkv.AddLacedFrames(track, group[0].Pts, frameNs, group.Select(f => f.Data).ToList());
			group.Clear();
		}
		while (queue.Count > 0 && queue.Peek().Pts < untilNs) {
			var f = queue.Dequeue();
			// Lace only frames that are contiguous in time (within 1 µs; DTS frames are 10,666,666.67 ns) and equal in size.
			if (group.Count > 0 && (f.Data.Length != group[0].Data.Length || Math.Abs(f.Pts - group[^1].Pts - frameNs) > 1000)) Emit();
			group.Add(f);
			if (group.Count == perBlock) Emit();
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
