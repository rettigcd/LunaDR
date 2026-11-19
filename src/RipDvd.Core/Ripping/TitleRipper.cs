using RipDvd.Core.Css;
using RipDvd.Core.Dvd;
using RipDvd.Core.Ifo;
using RipDvd.Core.Matroska;
using RipDvd.Core.Mpeg;

namespace RipDvd.Core.Ripping;

/// <summary>What a rip is doing.</summary>
public enum RipPhase { Unlocking, FindingKey, Ripping, Finishing, Done }

/// <summary>
/// Progress of one title's rip: the phase, sectors read so far out of the total, and time since the rip started.
/// While ripping, <see cref="CellPosition"/> is the cell being read (0-based position in the title's playlist, or in the chosen cells)
/// and <see cref="CellFraction"/> how far into it; -1 and 0 before the first cell.
/// </summary>
public sealed record RipProgress(RipPhase Phase, long SectorsDone, long SectorsTotal, TimeSpan Elapsed, int CellPosition = -1, double CellFraction = 0) {
	public double Fraction => SectorsTotal == 0 ? 0 : (double)SectorsDone / SectorsTotal;
}

/// <summary>Result of ripping one title.</summary>
public sealed record RipResult(string OutputPath, long DurationNs, long Sectors, TimeSpan Elapsed, IReadOnlyList<string> Warnings);

/// <summary>
/// Rips one DVD title, or a list of cells chosen by the user, to an MKV file, the way MakeMKV does:
///   * tracks: the video, then each audio stream the title's PGC marks available;
///   * chapters: one per PGC program, timed from the cell playback times (for chosen cells: one per cell);
///   * streams copied unchanged; timestamps mapped onto one title timeline that starts at 0.
///
/// Timeline: each cell may restart the MPEG clock (STC). For each cell we take the first
/// VOBU's start time from its nav pack, and map stream time t to
///   title time = (time of all earlier cells) + (t − cell's first VOBU start).
/// The same mapping joins any cells of one title set, so chosen cells play continuously in the order given.
/// </summary>
public sealed class TitleRipper {
	public const string AppName = "rip-dvd 0.1";

	private readonly string _videoTs;
	private readonly int _titleSet;
	private readonly VtsIfo _vts;
	private readonly Pgc _pgc;                  // where the audio/subtitle settings and the subtitle palette come from
	private readonly List<Cell> _cells;         // in playback order
	private readonly List<MkvChapter> _chapters;
	private readonly string? _setupWarning;

	private TitleRipper(string videoTs, int titleSet, VtsIfo vts, Pgc pgc, List<Cell> cells, List<MkvChapter> chapters, string? setupWarning) {
		_videoTs = videoTs;
		_titleSet = titleSet;
		_vts = vts;
		_pgc = pgc;
		_cells = cells;
		_chapters = chapters;
		_setupWarning = setupWarning;
	}

	/// <summary>Ripper for a title, by the disc's own title number.</summary>
	public TitleRipper(string videoTsFolder, int titleNumber) : this(ForTitle(videoTsFolder, titleNumber)) { }

	private TitleRipper(TitleRipper other)
		: this(other._videoTs, other._titleSet, other._vts, other._pgc, other._cells, other._chapters, other._setupWarning) { }

	private static TitleRipper ForTitle(string videoTs, int titleNumber) {
		var vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO")));
		var title = vmg.Titles[titleNumber - 1];
		var vts = LoadVts(videoTs, title.TitleSetNumber);
		var pgcs = TitlePgcs(vts, title);
		string? warning = pgcs.Count > 1 ? $"Title uses {pgcs.Count} PGCs; only cells of all PGCs are concatenated (chapters from the first)." : null;
		return new TitleRipper(videoTs, title.TitleSetNumber, vts, pgcs[0], pgcs.SelectMany(p => p.Cells).ToList(), PgcChapters(pgcs[0]), warning);
	}

	/// <summary>
	/// Ripper for cells chosen by the user, joined in the order given into one file.
	/// <paramref name="cellNumbers"/> are cell numbers as in the disc's cell map (1 = first cell of <see cref="CellMap.Cells"/>); see <see cref="CellSelection"/>.
	/// All cells must be in one title set (same video format, audio layout and CSS key).
	/// The audio/subtitle settings come from a title that plays the first cell; each cell becomes a chapter.
	/// </summary>
	public static TitleRipper ForCells(string videoTsFolder, IReadOnlyList<int> cellNumbers) {
		var vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTsFolder, "VIDEO_TS.IFO")));
		var vtsCache = new Dictionary<int, VtsIfo>();
		VtsIfo Vts(int n) => vtsCache.TryGetValue(n, out var v) ? v : vtsCache[n] = LoadVts(videoTsFolder, n);
		var map = CellMap.Build(vmg, Vts);
		if (CellSelection.Validate(map, cellNumbers) is { } error) throw new ArgumentException(error);

		var chosen = cellNumbers.Select(n => map.Cells[n - 1]).ToList();
		var vts = Vts(chosen[0].TitleSet);
		var title = vmg.Titles[map.TitlesUsing(cellNumbers[0] - 1).First() - 1];
		var pgcs = TitlePgcs(vts, title);
		var pgc = pgcs.FirstOrDefault(p => p.Cells.Any(c => c.VobId == chosen[0].VobId && c.CellId == chosen[0].CellId)) ?? pgcs[0];
		var allCells = vts.Pgcs.SelectMany(p => p.Cells).ToList();
		var cells = chosen.Select(ci => allCells.First(c => c.VobId == ci.VobId && c.CellId == ci.CellId)).ToList();

		var chapters = new List<MkvChapter>();
		var elapsed = default(DvdTime);
		for (int i = 0; i < cells.Count; i++) {
			long start = elapsed.ToNanoseconds();
			elapsed += cells[i].PlaybackTime;   // add frame counts, convert once (exact)
			chapters.Add(new MkvChapter(start, elapsed.ToNanoseconds(), $"Cell {cellNumbers[i]}"));
		}
		return new TitleRipper(videoTsFolder, chosen[0].TitleSet, vts, pgc, cells, chapters, null);
	}

	private static VtsIfo LoadVts(string videoTs, int titleSet) =>
		VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, $"VTS_{titleSet:00}_0.IFO")), titleSet);

	private static List<Pgc> TitlePgcs(VtsIfo vts, TitleEntry title) =>
		vts.Chapters[title.TitleSetTitleNumber - 1].Select(c => c.PgcNumber).Distinct().Select(n => vts.Pgcs[n - 1]).ToList();

	/// <summary>
	/// Chapters of a title: program n starts at its entry cell, timed from the IFO cell playback times.
	/// Cells of angles other than the first are not played, so their time is not counted (in an angle block every angle has the same length).
	/// </summary>
	internal static List<MkvChapter> PgcChapters(Pgc pgc) {
		var cellStartNs = new long[pgc.Cells.Count + 1];
		var elapsed = default(DvdTime);
		for (int c = 0; c < pgc.Cells.Count; c++) {
			var cell = pgc.Cells[c];
			if (cell.BlockType != 1 || cell.BlockMode == 1) elapsed += cell.PlaybackTime;   // add frame counts, convert once (exact)
			cellStartNs[c + 1] = elapsed.ToNanoseconds();
		}
		long ifoDurationNs = cellStartNs[^1];
		var chapterStarts = pgc.ProgramEntryCells.Select(cell => cellStartNs[cell - 1]).ToList();
		var chapters = new List<MkvChapter>();
		for (int i = 0; i < chapterStarts.Count; i++) {
			long end = i + 1 < chapterStarts.Count ? chapterStarts[i + 1] : ifoDurationNs;
			chapters.Add(new MkvChapter(chapterStarts[i], end, $"Chapter {i + 1:00}"));
		}
		return chapters;
	}

	/// <summary>
	/// Rip to <paramref name="outputPath"/>. <paramref name="progress"/> is called at each phase and every 512 sectors while ripping.
	/// Cancelling <paramref name="cancel"/> stops the rip with an <see cref="OperationCanceledException"/>; as with any failure, the partial file is deleted.
	/// </summary>
	/// <param name="maxCells">For testing: rip only the first cells of the title.</param>
	public RipResult Rip(string outputPath, Action<RipProgress>? progress = null, int maxCells = int.MaxValue, RipOptions? options = null,
		CancellationToken cancel = default) {
		options ??= RipOptions.Default;
		var started = DateTime.UtcNow;
		long sectorsDone = 0, total = 0;
		int cellPosition = -1;
		long cellSectorsDone = 0;
		IReadOnlyList<long> cellSectors = [];
		void Report(RipPhase phase) {
			double cellFraction = phase == RipPhase.Done ? 1
				: cellPosition < 0 || cellSectors[cellPosition] == 0 ? 0
				: Math.Min(1, (double)cellSectorsDone / cellSectors[cellPosition]);
			progress?.Invoke(new RipProgress(phase, sectorsDone, total, DateTime.UtcNow - started, cellPosition, cellFraction));
		}
		var warnings = new List<string>();
		if (_setupWarning is not null) warnings.Add(_setupWarning);

		// First report before touching the drive: opening the VOBs can take seconds while the disc spins up.
		Report(RipPhase.Unlocking);
		using var vobs = VobReader.ForTitleSet(_videoTs, _titleSet);
		// Ask the disc whether it is CSS-protected (its copyright descriptor, as libdvdcss does). If it says no, there is nothing to unlock
		// and no key to search for: the search would otherwise read up to 800 MB first (Gypsy: minutes with no progress shown).
		bool? discProtected = DriveUnlocker.IsCssProtected(_videoTs);
		// A drive only returns scrambled sectors after the CSS handshake (unless something, e.g. MakeMKV, already did it).
		// Unprotected discs make the drive reject the handshake ("Incorrect function"), so a failure only matters if reading then fails.
		string? unlockError = discProtected == false ? null : DriveUnlocker.TryUnlock(_videoTs);
		Report(RipPhase.FindingKey);
		CssKey? key;
		try {
			key = TitleSectorReader.FindKey(vobs, discProtected, cancel: cancel);
		} catch (IOException ex) when (unlockError is not null) {
			throw new IOException($"{ex.Message} (drive authentication failed: {unlockError})", ex);
		}
		ISectorSource source = options.SimulateBadSector is { } bad ? new BadSectorSource(vobs, bad) : vobs;
		var reader = new TitleSectorReader(source, _cells.Take(maxCells).ToList(), key);
		total = reader.TotalSectors;
		cellSectors = reader.CellSectors;

		var (video, tracks, audioTracks, subtitleTracks, captionTrack, captionDecoder, chapters) = BuildPlan(options, warnings);

		// ---- Mux ----
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
		using var file = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
		bool completed = false;
		try {
			Report(RipPhase.Ripping);
			var mkv = new MkvWriter(file, tracks, AppName);
			var framer = new Mpeg2VideoFramer { ExtractCaptions = captionDecoder is not null };
			bool headerWritten = false;
			var pendingVideo = new List<VideoFrame>();
			var audioQueues = audioTracks.Values.Select(v => (v.Track, v.Framer, Queue: new Queue<AudioFrame>())).ToList();
			var audioContinuity = audioTracks.Values.ToDictionary(v => v.Track, v => new AudioContinuity(v.Framer));
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

			var references = new VideoReferences();
			var clusters = new ClusterRule(chapters);

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
				// Clusters and references as MakeMKV makes them (rules in VideoRules.cs).
				// The cluster's timestamp is its earliest block (B pictures shown before the key frame); its cue uses the key frame's time.
				var keyFrame = gop[0];
				if (clusters.StartsCluster(keyFrame.Pts)) mkv.StartCluster(gop.Min(f => f.Pts), cueTimeNs: keyFrame.Pts);
				foreach (var f in gop)
					mkv.AddVideoFrame(video, f.Pts, f.DurationNs, f.IsKeyFrame, f.Data, references.Next(f.PictureType, f.IsKeyFrame, f.Pts));
				foreach (var (track, af, queue) in audioQueues) WriteAudio(mkv, track, af, queue, audioUntilNs);
				WriteSubtitles(audioUntilNs);
				if (captionDecoder is not null) {
					FeedCaptions(captionDecoder, gop);
					foreach (var ev in captionDecoder.TakeEvents()) mkv.AddTextBlock(captionTrack!, ev.StartNs, ev.EndNs - ev.StartNs, ev.Text);
				}
			}

			// Damaged discs: stretches the reader had to skip become warnings, with the title time where they are and their approximate length.
			long lastTitleNs = 0;
			long reportedSkipped = 0;
			long playNs = TitleSectorReader.PlayedCells(_cells.Take(maxCells).ToList()).Aggregate(default(DvdTime), (sum, c) => sum + c.PlaybackTime).ToNanoseconds();
			void WarnSkipped(long sectors, string where) {
				reportedSkipped += sectors;
				double seconds = total == 0 ? 0 : sectors * (playNs / 1e9) / total;
				warnings.Add($"Damaged or empty spot on the disc: {sectors:N0} sector(s) that could not be read or held no video data skipped {where}, " +
							 $"about {Math.Max(seconds, 0.1):F1} s; the picture and sound may jump there.");
			}

			foreach (var sector in reader.ReadAll()) {
				if (sector.SkippedBefore > 0) {
					var t = TimeSpan.FromTicks(lastTitleNs / 100);
					WarnSkipped(sector.SkippedBefore, $"at {(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}");
					sectorsDone += sector.SkippedBefore;
				}
				if (!ReferenceEquals(sector.Cell, currentCell)) {
					if (currentCell is not null && cellFirstPtm is { } f0 && cellLastPtm is { } l0) elapsedBeforeCell += ToNs90k(l0 - f0);
					currentCell = sector.Cell;
					cellFirstPtm = cellLastPtm = null;
					cellPosition++;
					cellSectorsDone = 0;
					Report(RipPhase.Ripping);
				}
				cellSectorsDone++;
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
					if (pts is { } shown && shown > lastTitleNs) lastTitleNs = shown;
					if (p.Stream.Kind == StreamKind.Video) {
						framer.Feed(p.Payload.Span, pts);
					} else if (audioTracks.TryGetValue(p.Stream, out var at)) {
						at.Framer.Feed(p.Payload.Span, pts, p.FirstAccessUnit);
						var q = audioQueues.First(x => x.Track == at.Track).Queue;
						foreach (var af in at.Framer.TakeReady()) q.Enqueue(audioContinuity[at.Track].Place(af));
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

				cancel.ThrowIfCancellationRequested();
				if (++sectorsDone % 512 == 0) Report(RipPhase.Ripping);
			}

			// Sectors skipped at the very end are not followed by a sector that could carry the mark.
			long trailing = reader.Skipped.Sum(r => r.Sectors) - reportedSkipped;
			if (trailing > 0) WarnSkipped(trailing, "at the end");

			Report(RipPhase.Finishing);
			framer.Flush();
			pendingVideo.AddRange(framer.TakeReady());
			if (pendingVideo.Count > 0) WriteGop(pendingVideo, long.MaxValue);
			foreach (var (track, af, queue) in audioQueues) {
				af.Flush();
				foreach (var f in af.TakeReady()) queue.Enqueue(audioContinuity[track].Place(f));
				if (queue.Count > 0) WriteAudio(mkv, track, af, queue, long.MaxValue);
			}
			if (headerWritten) WriteSubtitles(long.MaxValue);
			if (captionDecoder is not null && headerWritten) {
				captionDecoder.Finish(video.LastEndNs);
				foreach (var ev in captionDecoder.TakeEvents()) mkv.AddTextBlock(captionTrack!, ev.StartNs, ev.EndNs - ev.StartNs, ev.Text);
			}

			// Like MakeMKV: the end of whichever audio/video track runs longest (on the film disc the audio ends 9 ms after the video).
			long durationNs = RipTiming.DurationNs(tracks);
			mkv.Finish(durationNs);
			completed = true;
			Report(RipPhase.Done);
			return new RipResult(outputPath, durationNs, total, DateTime.UtcNow - started, warnings);
		} finally {
			if (!completed) {
				// Cancelled or failed: an incomplete MKV is useless, so remove it.
				file.Dispose();
				try { File.Delete(outputPath); } catch (IOException) { }
			}
		}
	}

	/// <summary>The tracks and chapters a rip will write, after applying the options.</summary>
	private sealed record Plan(
		MkvTrack Video, List<MkvTrack> Tracks,
		Dictionary<StreamId, (MkvTrack Track, AudioFramer Framer)> AudioTracks,
		Dictionary<int, (MkvTrack Track, SubpictureAssembler Assembler)> SubtitleTracks,
		MkvTrack? CaptionTrack, Captions.Eia608Decoder? CaptionDecoder, List<MkvChapter> Chapters);

	private Plan BuildPlan(RipOptions options, List<string> warnings) {
		var pgc = _pgc;
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

		// A single chapter carries no information; MakeMKV writes no Chapters element then.
		var chapters = new List<MkvChapter>(_chapters);
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
	internal static void FeedCaptions(Captions.Eia608Decoder decoder, List<VideoFrame> gop) {
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

	/// <summary>
	/// Closes small audio gaps, as MakeMKV does.
	/// At some seamless cell joins the disc leaves a gap shorter than one audio frame (Beauty and the Beast: 17 ms at 50:41).
	/// MakeMKV plays the next frame right after the previous one and keeps that shift for the rest of the title (checked: its AC3 frame times);
	/// we do the same, so the audio stays contiguous. Gaps of a frame or longer are real silence and are kept.
	/// </summary>
	internal sealed class AudioContinuity(AudioFramer framer) {
		private long _shift;
		private long? _nextPts;

		public AudioFrame Place(AudioFrame f) {
			long pts = f.Pts + _shift;
			long frameNs = framer.FrameNs;
			if (_nextPts is { } expected && frameNs > 0) {
				long gap = pts - expected;
				if (gap > 1000 && gap < frameNs) {   // more than rounding (1 µs), less than a frame
					_shift -= gap;
					pts -= gap;
				}
			}
			_nextPts = pts + frameNs;
			return pts == f.Pts ? f : new AudioFrame { Data = f.Data, Pts = pts };
		}
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
