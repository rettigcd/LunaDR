using LunaDR.Core.Matroska;
using LunaDR.Core.Mpeg;
using LunaDR.Core.Ripping;

namespace LunaDR.Core.Bluray;

/// <summary>
/// Rips one Blu-ray playlist to an MKV file (plan steps BR4/BR5):
///   * each play item's clip (BDMV/STREAM/nnnnn.m2ts) is read in 6,144-byte aligned units, decrypted (AACS) and split into PES packets per PID;
///   * timeline: title time = (length of the earlier play items) + (PTS − the item's in time), so the items play as one title starting at 0;
///   * tracks: the video, the audio streams of the first play item's STN table (a TrueHD stream gives two tracks: TrueHD and its AC3 core),
///     the PGS subtitles; chapters from the playlist's entry marks.
/// Clusters start at key frames (the DVD rule: at least 400 ms apart, and at every chapter start); audio and subtitles are written into the
/// cluster of the GOP they play in.
/// </summary>
public sealed class BdTitleRipper {
	private const int UnitsPerRead = 32;   // 196,608 bytes per read

	private readonly string _root;
	private readonly Mpls _mpls;

	public int Playlist { get; }
	public Mpls PlaylistInfo => _mpls;

	public BdTitleRipper(string root, int playlist) : this(root, BdScanner.LoadPlaylist(root, playlist), playlist) { }

	/// <summary>A playlist given directly, e.g. one made up by the probe to rip a test stream (BDMV/STREAM/&lt;clip&gt;.m2ts under <paramref name="root"/>).</summary>
	public BdTitleRipper(string root, Mpls playlist, int number) {
		_root = root;
		Playlist = number;
		_mpls = playlist;
		if (_mpls.Items.Count == 0) throw new ArgumentException($"Playlist {number:00000} has no play items.");
	}

	/// <summary>A video access unit (picture) on the title timeline.</summary>
	private sealed record VFrame(long Pts, bool Key, byte[] Data, IReadOnlyList<long> Refs);

	private sealed class AudioOut {
		public required MkvTrack Track { get; init; }
		public required int Pid { get; init; }
		public AudioFramer? Framer { get; init; }          // null for LPCM
		public bool CoreOfTrueHd { get; init; }            // takes the AC3 PES packets of a TrueHD stream
		public bool TrueHd { get; init; }                  // takes the TrueHD PES packets (not the AC3 core)
		public Queue<AudioFrame> Queue { get; } = new();
	}

	private sealed record Plan(MkvTrack Video, BdStream VideoStream, List<AudioOut> Audio, List<(MkvTrack Track, int Pid)> Subtitles,
		List<MkvChapter> Chapters, List<MkvTrack> Tracks);

	private Plan BuildPlan(RipOptions options, List<string> warnings) {
		var item = _mpls.Items[0];
		var vs = item.Video.FirstOrDefault() ?? throw new InvalidDataException($"Playlist {Playlist:00000} has no video stream.");
		var tracks = new List<MkvTrack>();
		(int w, int h) = vs.Format switch { 1 or 3 => (720, 480), 2 or 7 => (720, 576), 4 or 6 => (1920, 1080), 5 => (1280, 720), 8 => (3840, 2160), _ => (1920, 1080) };
		string codec = vs.CodingType switch {
			0xEA => "V_MS/VFW/FOURCC",
			0x1B => "V_MPEG4/ISO/AVC",
			0x02 => "V_MPEG2",
			_ => throw new NotSupportedException($"{vs.CodecName} video is not supported yet (playlist {Playlist:00000})."),
		};
		var video = new MkvTrack {
			Number = 1,
			Type = MkvTrackType.Video,
			CodecId = codec,
			Lacing = false,
			DefaultDurationNs = vs.FrameRate > 0 ? (long)Math.Round(1e9 / vs.FrameRate) : 0,
			MinCache = 2,
			PixelWidth = w,
			PixelHeight = h,
			DisplayWidth = h >= 720 ? w : h * 4 / 3,
			DisplayHeight = h,
			SourceId = $"{vs.Pid:X4}",
		};
		tracks.Add(video);

		var audio = new List<AudioOut>();
		foreach (var a in item.Audio) {
			string? language = Language(a.Language);
			if (!RipOptions.Keeps(options.AudioLanguages, language)) continue;
			AudioOut Add(string codecId, AudioFramer? framer, int channels, string? name = null, bool core = false, bool trueHd = false) {
				var track = new MkvTrack {
					Number = tracks.Count + 1,
					Type = MkvTrackType.Audio,
					CodecId = codecId,
					Language = language,
					Name = name ?? TitleRipper.ChannelName(channels),
					Default = audio.Count == 0,
					Lacing = framer is not null && framer.FramesPerBlock > 1,
					DefaultDurationNs = framer is { FrameNs: > 0 } ? framer.FrameNs : 0,
					SamplingFrequency = a.SampleRate > 0 ? a.SampleRate : 48_000,
					Channels = channels,
					SourceId = $"{a.Pid:X4}",
				};
				tracks.Add(track);
				var output = new AudioOut { Track = track, Pid = a.Pid, Framer = framer, CoreOfTrueHd = core, TrueHd = trueHd };
				audio.Add(output);
				return output;
			}
			int guess = a.Format switch { 1 => 1, 3 => 2, _ => 6 };
			switch (a.CodingType) {
				case 0x81: Add("A_AC3", new Ac3Framer(), guess); break;
				case 0x84: Add("A_EAC3", new Eac3Framer(), guess); break;
				case 0x82 or 0x85 or 0x86: Add("A_DTS", new DtsFramer(keepExtension: true), guess); break;
				case 0x83:
					Add("A_TRUEHD", new TrueHdFramer(), guess, trueHd: true);
					if (options.TrueHdCore) Add("A_AC3", new Ac3Framer(), guess, core: true);
					break;
				case 0x80: Add("A_PCM/INT/LIT", null, guess); break;
				default: warnings.Add($"Audio stream {a.Pid:X4} ({a.CodecName}, {a.Language}) is not supported yet and was left out."); break;
			}
		}

		var subtitles = new List<(MkvTrack, int)>();
		foreach (var s in item.Subtitles.Where(s => s.CodingType == 0x90).DistinctBy(s => s.Pid)) {
			string? language = Language(s.Language);
			if (!RipOptions.Keeps(options.SubtitleLanguages, language)) continue;
			var track = new MkvTrack {
				Number = tracks.Count + 1,
				Type = MkvTrackType.Subtitle,
				CodecId = "S_HDMV/PGS",
				Language = language,
				Default = false,
				Lacing = false,
				SourceId = $"{s.Pid:X4}",
			};
			tracks.Add(track);
			subtitles.Add((track, s.Pid));
		}

		var chapters = new List<MkvChapter>();
		if (options.Chapters) {
			var starts = _mpls.ChapterStartsNs();
			if (starts.Count > 1) {
				for (int i = 0; i < starts.Count; i++)
					chapters.Add(new MkvChapter(starts[i], i + 1 < starts.Count ? starts[i + 1] : _mpls.DurationNs, $"Chapter {i + 1:00}"));
			}
		}
		return new Plan(video, vs, audio, subtitles, chapters, tracks);
	}

	private static string? Language(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToLowerInvariant();

	/// <summary>What a rip would write: one line per track, then the chapters (for "--dry-run" and tests).</summary>
	public List<string> Describe(RipOptions? options = null) {
		var warnings = new List<string>();
		var plan = BuildPlan(options ?? RipOptions.Default, warnings);
		var lines = plan.Tracks.Select(t => $"{t.Type.ToString().ToLowerInvariant(),-8} {t.Number,2}  {t.CodecId,-16} {t.Language ?? "(no language)"}  {t.Name}".TrimEnd()).ToList();
		lines.Add($"chapters  {(plan.Chapters.Count == 0 ? "none" : plan.Chapters.Count.ToString())}");
		lines.AddRange(warnings.Select(w => "warning: " + w));
		return lines;
	}

	/// <summary>Total bytes of the clip files the playlist reads (progress is counted in 2,048-byte sectors of these).</summary>
	public long TotalBytes => _mpls.Items.Select(i => ClipPath(i.ClipId)).Sum(p => File.Exists(p) ? new FileInfo(p).Length : 0);

	private string ClipPath(string clip) => Path.Combine(_root, "BDMV", "STREAM", clip + ".m2ts");

	public RipResult Rip(string outputPath, Action<RipProgress>? progress = null, RipOptions? options = null, CancellationToken cancel = default,
		long maxBytes = long.MaxValue) {
		options ??= RipOptions.Default;
		var started = DateTime.UtcNow;
		var warnings = new List<string>();
		long total = TotalBytes / 2048, done = 0;
		int itemIndex = -1;
		double itemFraction = 0;
		void Report(RipPhase phase) => progress?.Invoke(new RipProgress(phase, done, total, DateTime.UtcNow - started, itemIndex, itemFraction));

		Report(RipPhase.FindingKey);
		using var aacs = AacsDecryptor.ForDisc(_root);
		var plan = BuildPlan(options, warnings);

		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
		using var file = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
		bool completed = false;
		try {
			var mkv = new MkvWriter(file, plan.Tracks, TitleRipper.AppName);
			var video = plan.Video;
			var clusters = new ClusterRule(plan.Chapters);
			var pending = new List<VFrame>();
			var subtitleQueues = plan.Subtitles.ToDictionary(s => s.Pid, s => (s.Track, Assembler: new PgsAssembler(), Queue: new List<(long Pts, byte[] Data)>()));
			long clusterStartNs = 0, lateSubtitles = 0;
			string? firstLate = null;
			bool headerWritten = false;
			long lastVideoPts = long.MinValue;
			bool seenKey = false;
			var mpeg2 = plan.VideoStream.CodingType == 0x02 ? new Mpeg2VideoFramer() : null;
			var references = new VideoReferences();
			byte[]? sps = null, pps = null;
			long? vc1DelayNs = null;
			long unreadableUnits = 0, undecryptableUnits = 0;

			void WriteGop(List<VFrame> gop, long untilNs) {
				if (!headerWritten) {
					foreach (var a in plan.Audio) {
						if (a.Framer is { Channels: > 0 } f) {
							a.Track.Channels = f.Channels;
							if (!a.CoreOfTrueHd) a.Track.Name = TitleRipper.ChannelName(f.Channels);
						}
						if (a.CoreOfTrueHd) a.Track.Name = $"{TitleRipper.ChannelName(a.Track.Channels)} (AC3 core of the TrueHD track)";
					}
					mkv.WriteHeader(plan.Chapters, null);
					headerWritten = true;
				}
				if (clusters.StartsCluster(gop[0].Pts)) {
					clusterStartNs = gop.Min(f => f.Pts);
					mkv.StartCluster(clusterStartNs, cueTimeNs: gop[0].Pts);
				}
				foreach (var f in gop) mkv.AddVideoFrame(video, f.Pts, video.DefaultDurationNs, f.Key, f.Data, f.Refs);
				foreach (var a in plan.Audio) {
					if (a.Framer is not null) {
						TitleRipper.WriteAudio(mkv, a.Track, a.Framer, a.Queue, untilNs);
					} else {
						while (a.Queue.Count > 0 && a.Queue.Peek().Pts < untilNs) {
							var f = a.Queue.Dequeue();
							mkv.AddLacedFrames(a.Track, f.Pts, 0, [f.Data]);
						}
					}
				}
				// Subtitle display sets in time order (they can complete out of order); a set more than 30 s older than the cluster can't be placed (block times are
				// 16-bit ms relative to the cluster) and is dropped and counted.
				foreach (var (track, _, queue) in subtitleQueues.Values) {
					var due = queue.Where(s => s.Pts < untilNs).OrderBy(s => s.Pts).ToList();
					queue.RemoveAll(s => s.Pts < untilNs);
					foreach (var s in due) {
						if (s.Pts < clusterStartNs - 30_000_000_000) {
							lateSubtitles++;
							firstLate ??= $"first: track {track.Number} at {s.Pts / 1e9:F3} s, in the cluster at {clusterStartNs / 1e9:F3} s";
						} else mkv.AddSubtitleBlock(track, s.Pts, null, s.Data);
					}
				}
			}

			void AddVideo(VFrame f) {
				if (!seenKey && !f.Key) return;   // nothing can be decoded before the first key frame
				seenKey = true;
				if (f.Key && pending.Count > 0) {
					WriteGop(pending, f.Pts);
					pending = [];
				}
				pending.Add(f);
			}

			var starts = _mpls.ItemStartsNs();
			for (itemIndex = 0; itemIndex < _mpls.Items.Count; itemIndex++) {
				var item = _mpls.Items[itemIndex];
				long inNs = (long)item.InTime * 1_000_000_000 / 45_000;
				long offset = starts[itemIndex] - inNs;   // title time = stream time + offset
				long itemEnd = starts[itemIndex] + item.DurationNs;
				long? Title(long? pts90k) => pts90k is { } p ? p * 100_000 / 9 + offset : null;
				bool InItem(long t) => t >= starts[itemIndex] - 1_000_000 && t < itemEnd + 1_000_000;

				var pids = new HashSet<int> { plan.VideoStream.Pid };
				pids.UnionWith(plan.Audio.Select(a => a.Pid));
				pids.UnionWith(plan.Subtitles.Select(s => s.Pid));
				var demux = new TsDemuxer(pids);

				void Handle(TsPes pes) {
					long? t = Title(pes.Pts);
					if (pes.Pid == plan.VideoStream.Pid) {
						if (t is { } now) {
							foreach (var open in subtitleQueues.Values) {
								open.Assembler.Expire(now);
								foreach (var set in open.Assembler.TakeReady())
									if (InItem(set.Pts)) open.Queue.Add(set);
							}
						}
						if (mpeg2 is not null) {
							mpeg2.Feed(pes.Payload, t);
							foreach (var vf in mpeg2.TakeReady()) {
								if (!InItem(vf.Pts)) continue;
								if (video.CodecPrivate is null && mpeg2.SequenceHeader is { } seq) video.CodecPrivate = seq;
								AddVideo(new VFrame(vf.Pts, vf.IsKeyFrame, vf.Data, references.Next(vf.PictureType, vf.IsKeyFrame, vf.Pts)));
							}
							return;
						}
						long pts = t ?? (lastVideoPts == long.MinValue ? starts[itemIndex] : lastVideoPts + video.DefaultDurationNs);
						lastVideoPts = pts;
						if (!InItem(pts)) return;
						if (plan.VideoStream.CodingType == 0xEA) {
							bool key = Vc1.IsKeyFrame(pes.Payload);
							if (key && video.CodecPrivate is null && Vc1.Unit(pes.Payload, 0x0F) is { } seq) {
								var (cw, ch, dw, dh) = Vc1.Size(seq);
								video.PixelWidth = cw;
								video.PixelHeight = ch;
								video.DisplayWidth = dw;
								video.DisplayHeight = dh;
								video.CodecPrivate = Vc1.CodecPrivate(seq, Vc1.Unit(pes.Payload, 0x0E), cw, ch);
							}
							if (video.CodecPrivate is null) return;
							// VC-1 is stored as a "VfW" track, whose block times players (ffmpeg) read as decode times: write the decode time plus the
							// decoder delay (PTS − DTS of the first key frame), so times rise in file order and each key frame keeps its presentation time.
							if (vc1DelayNs is null && key) vc1DelayNs = pes.Pts is { } kp && pes.Dts is { } kd ? (kp - kd) * 100_000 / 9 : 0;
							// A PES packet leaves out the DTS when it equals the PTS.
							long time = (pes.Dts ?? pes.Pts) is { } decode ? decode * 100_000 / 9 + offset + (vc1DelayNs ?? 0) : pts;
							AddVideo(new VFrame(time, key, pes.Payload, []));
						} else {
							var nals = H264.NalUnits(pes.Payload);
							foreach (var n in nals) {
								int type = n.Span[0] & 0x1F;
								if (type == 7) sps ??= n.ToArray();
								if (type == 8) pps ??= n.ToArray();
							}
							if (video.CodecPrivate is null && sps is not null && pps is not null) video.CodecPrivate = H264.AvcC(sps, pps);
							if (video.CodecPrivate is null) return;
							AddVideo(new VFrame(pts, H264.IsKeyFrame(nals), H264.ToLengthPrefixed(nals), []));
						}
						return;
					}
					if (subtitleQueues.TryGetValue(pes.Pid, out var sub)) {
						sub.Assembler.Feed(pes.Payload, t);
						foreach (var set in sub.Assembler.TakeReady())
							if (InItem(set.Pts)) sub.Queue.Add(set);
						return;
					}
					foreach (var a in plan.Audio.Where(a => a.Pid == pes.Pid)) {
						if (a.TrueHd || a.CoreOfTrueHd) {
							bool isAc3 = pes.StreamIdExtension == 0x76 || (pes.StreamIdExtension == 0 && pes.Payload.Length > 1 && pes.Payload[0] == 0x0B && pes.Payload[1] == 0x77);
							if (isAc3 != a.CoreOfTrueHd) continue;
						}
						if (a.Framer is null) {
							if (t is { } lt && InItem(lt) && pes.Payload.Length > 4) {
								var (channels, _, rate, bits) = BdLpcm.Header(pes.Payload);
								if (!headerWritten) {
									// The track header (written at the first GOP) must say what the samples are.
									a.Track.BitDepth = bits;
									a.Track.SamplingFrequency = rate;
									a.Track.Channels = channels;
									a.Track.Name = TitleRipper.ChannelName(channels);
								}
								a.Queue.Enqueue(new AudioFrame { Data = BdLpcm.ToLittleEndian(pes.Payload), Pts = lt });
							}
							continue;
						}
						a.Framer.Feed(pes.Payload, t, firstAccessUnit: t is null ? 0 : 1);
						foreach (var f in a.Framer.TakeReady())
							if (InItem(f.Pts)) a.Queue.Enqueue(f);
					}
				}

				string path = ClipPath(item.ClipId);
				using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
				long clipBytes = Math.Min(fs.Length, maxBytes / AacsDecryptor.UnitSize * AacsDecryptor.UnitSize);
				var buffer = new byte[AacsDecryptor.UnitSize * UnitsPerRead];
				long position = 0;
				Report(RipPhase.Ripping);
				while (position < clipBytes) {
					cancel.ThrowIfCancellationRequested();
					int want = (int)Math.Min(buffer.Length, clipBytes - position);
					int got = ReadAt(fs, position, buffer.AsSpan(0, want), ref unreadableUnits);
					for (int u = 0; u + AacsDecryptor.UnitSize <= got; u += AacsDecryptor.UnitSize) {
						var unit = buffer.AsSpan(u, AacsDecryptor.UnitSize);
						if (unit.IndexOfAnyExcept((byte)0) < 0) continue;   // an unreadable unit (left as zeros)
						if (aacs is not null && !aacs.DecryptUnit(unit)) {
							undecryptableUnits++;
							continue;
						}
						for (int p = 0; p < AacsDecryptor.UnitSize; p += AacsDecryptor.PacketSize) demux.Feed(unit.Slice(p + 4, 188));
					}
					foreach (var pes in demux.TakeReady()) Handle(pes);
					position += want;
					done += want / 2048;
					itemFraction = (double)position / clipBytes;
					Report(RipPhase.Ripping);
				}
				demux.Flush();
				foreach (var pes in demux.TakeReady()) Handle(pes);
				if (demux.BadPackets > 0) warnings.Add($"Clip {item.ClipId}: {demux.BadPackets:N0} damaged transport packets were skipped.");
			}
			itemIndex = _mpls.Items.Count - 1;

			Report(RipPhase.Finishing);
			if (mpeg2 is not null) {
				mpeg2.Flush();
				foreach (var vf in mpeg2.TakeReady()) AddVideo(new VFrame(vf.Pts, vf.IsKeyFrame, vf.Data, references.Next(vf.PictureType, vf.IsKeyFrame, vf.Pts)));
			}
			foreach (var a in plan.Audio) {
				if (a.Framer is null) continue;
				a.Framer.Flush();
				foreach (var f in a.Framer.TakeReady()) a.Queue.Enqueue(f);
			}
			if (pending.Count > 0) WriteGop(pending, long.MaxValue);
			if (!headerWritten) throw new InvalidDataException($"Playlist {Playlist:00000}: no video key frame found; nothing was written.");

			if (unreadableUnits > 0)
				warnings.Add($"Damaged spot on the disc: {unreadableUnits:N0} unit(s) of 6 KB could not be read and were skipped; the picture and sound may jump there.");
			if (lateSubtitles > 0) warnings.Add($"{lateSubtitles:N0} subtitle display set(s) came too late in the stream to be placed and were left out ({firstLate}).");
			if (undecryptableUnits > 0)
				warnings.Add($"{undecryptableUnits:N0} unit(s) of 6 KB could not be decrypted with the disc's key and were left out (wrong key in the key file?).");

			long durationNs = RipTiming.DurationNs(plan.Tracks);
			mkv.Finish(durationNs);
			completed = true;
			itemFraction = 1;
			Report(RipPhase.Done);
			return new RipResult(outputPath, durationNs, total, DateTime.UtcNow - started, warnings);
		} finally {
			if (!completed) {
				file.Dispose();
				try { File.Delete(outputPath); } catch (IOException) { }
			}
		}
	}

	/// <summary>
	/// Reads at a position; on a read error retries each 6 KB unit up to 3 times and leaves units that still fail as zeros (counted), so a damaged
	/// spot costs only those units, as the DVD reader skips to the next VOBU.
	/// </summary>
	private static int ReadAt(FileStream fs, long position, Span<byte> buffer, ref long unreadable) {
		try {
			fs.Position = position;
			return fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
		} catch (IOException) {
		}
		for (int u = 0; u < buffer.Length; u += AacsDecryptor.UnitSize) {
			var unit = buffer.Slice(u, Math.Min(AacsDecryptor.UnitSize, buffer.Length - u));
			bool ok = false;
			for (int attempt = 0; attempt < 3 && !ok; attempt++) {
				try {
					fs.Position = position + u;
					fs.ReadExactly(unit);
					ok = true;
				} catch (IOException) {
				}
			}
			if (!ok) {
				unit.Clear();
				unreadable++;
			}
		}
		return buffer.Length;
	}
}
