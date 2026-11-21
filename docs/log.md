# Log

Append-only. Newest entries at the bottom. See [CLAUDE.md](../CLAUDE.md) for the rules.

## 2026-11-18 09:16
- **Started project.** Owner's request: a .NET app that rips movies and TV episodes from DVD,
	working roughly like MakeMKV; prefer MakeMKV's behavior for design decisions; work
	autonomously; start with a probe command-line app; keep obsolete probe methods (mark them
	old, don't delete); drive the work with `docs/current-plan.md` and this log; persist the
	instructions (done in `CLAUDE.md`).
- **Environment found:** .NET SDKs up to 10.0.301; git 2.32; DVD drive E: (HL-DT-ST
	DVDRAM SP80NB80 USB) with disc `DEAD_ZONE_S4_D1`; MakeMKV installed (includes
	`makemkvcon64.exe`, usable as a reference).
- **Decision:** target .NET 10 (newest SDK installed).
- **Decision:** output MKV, keep all tracks and chapters, minimum title length 120 s, MakeMKV
	file naming. Reason: these are MakeMKV's defaults.
- **Decision:** no external tools (ffmpeg etc.) at run time. Reason: MakeMKV is self-contained,
	and doing it in C# keeps all the logic readable in this repo.
- **Step:** git repo initialized; probe app `src/DvdProbe` scaffolded.

## 2026-11-18 09:24
- **Question answered:** GUI like MakeMKV's, or command line only? **Answer:** a MakeMKV-style
	GUI is wanted eventually but is secondary; start with the command-line app.
- **Decision:** put the ripping logic in a library so a later GUI can reuse it.
- **Question answered:** is Blu-ray out of scope? **Answer:** yes, DVD only.

## 2026-11-18 09:29
- **Owner confirmed** the test disc is The Dead Zone, season 4, disc 1; title sets 1–4 are
	episodes and the small title sets are extras/menus.
- **Step:** ran MakeMKV v2.0.0 `makemkvcon64 -r info disc:0` (default and `--minlength=0`);
	saved the output in `reference/makemkv/`. Results: 14 titles; 8 kept (titles 1–4 episodes
	of 42:27, 42:48, 42:45, 42:53 with 9/9/10/9 cells; titles 6–9 extras of 2:19–3:35 with 2
	cells), 6 skipped for being under 120 s (titles 5, 10–14). MakeMKV uses "direct disc
	access mode".
- **Finding:** MakeMKV's file names are `C1_t00.mkv` … `C1_t03.mkv`, `B2_t04.mkv`,
	`B4_t05.mkv`, `B6_t06.mkv`, `B8_t07.mkv`, not `<volume>_tNN.mkv` as assumed. `tNN` is the
	position among kept titles. The prefix rule is unknown; added to next steps.
- **Decision (corrected):** file naming is `<prefix>_t<NN>.mkv`, with the prefix rule to be
	worked out from MakeMKV's behavior.
- **Owner request:** added to the plan's new "Future" section: look up episode titles on
	thetvdb.com and match them to the ripped titles. The MakeMKV-style GUI moved to the same
	section.

## 2026-11-18 09:53
- **Step:** created library `src/RipDvd.Core` with an IFO parser (`Ifo/`: `VmgIfo`, `VtsIfo`,
	`Pgc`, `Cell`, stream attributes, `DvdTime`). Added probe command `titles [-v]`.
- **Result on test disc:** matches MakeMKV for the title list, cells (9/9/10/9 episodes, 2
	per extra), chapters, sizes to the byte, titles under 120 s, audio track counts
	(2/1/1/1 episodes, 2 per extra), aspect ratio (16:9 episodes, 4:3 extras) and
	closed-caption presence (episodes only).
- **Finding:** the disc's VTS attribute tables declare 8 audio and 32 subtitle streams; only
	the streams marked available in the program chain are real. MakeMKV follows the PGC.
- **Decision:** trust PGC stream availability, not the declared stream counts. Reason: that
	is what MakeMKV shows.
- **Finding:** for extras, MakeMKV's duration equals the IFO's stored (nominal 30 fps) time,
	truncated to whole seconds (e.g. 3:35.86 → 3:35). For the episodes MakeMKV is 3–5 s
	shorter than the IFO time (title 1: 42:30.46 vs 0:42:27), so it probably measures the
	actual stream. Deferred until we can read the video (plan step 4).
- **Finding:** MakeMKV's file name prefix (`C1`, `B2`, …) is its "comment" attribute (TINFO
	49). Source unknown; moved to the end of the next steps.
- **Finding:** MakeMKV adds a "CC→Text" subtitle track (codec `S_CC608/DVD`) for titles whose
	VTS video attributes have the line-21 closed-caption flag. Added as a design decision.

## 2026-11-18 10:01
- **Step:** probe `scramble` samples VOB sectors. The test disc is CSS-protected: ~15% of
	sectors have PES_scrambling_control set. Windows reads every sector without error
	(MakeMKV had already run in this session, so the drive may have been authenticated).
- **Step:** downloaded libdvdcss `css.c`/`csstables.h` (to the session scratchpad, not the
	repo) as a reference. Ported the CSS tables, the descrambler and the title-key attack to
	C# in `src/RipDvd.Core/Css` with attribution. Added `Dvd/VobReader` and probe `crack`.
- **Result:** title keys recovered for all 14 title sets; every key was agreed on by 4–5
	independent sectors. Check: in 50 scrambled video sectors per VTS, MPEG start codes
	appear in 0 before descrambling and 39–50 after. Recovery took 0.6–49 s per VTS.
- **Decision:** recover title keys from the data (libdvdcss "title" method) instead of
	drive authentication + player keys. Reason: needs no authentication or key lists and
	works on this drive; authentication is added later only if needed.
- **Decision:** accept a key only when several sectors agree on it (5 votes). Reason: a
	wrong plaintext guess yields a wrong key; voting filters those out.
- **Open questions added:** GPL license of the ported libdvdcss code; legal note on CSS.

## 2026-11-18 10:08
- **Step:** added `Dvd/NavPack` (parses each VOBU's nav pack) and probe `duration`, which
	walks nav packs from VOBU to VOBU (reads ~5000 sectors, not the whole title).
- **Finding:** title 1's VOBU timestamps sum to 42:33.017, matching the IFO cell times
	(converted at 30000/1001 fps) cell for cell. MakeMKV shows 0:42:27, so its figure is not
	the stream length either; the difference (~6 s on episodes, <1 s on extras) is not a
	constant ratio. Next: rip with MakeMKV and inspect the file.
- **Owner question:** would parts written in C be more efficient? **Answer:** no. Measured
	(probe `bench`, Release build): descrambling 430 MB/s on one core; key recovery ~6 ms of
	CPU per 2000 sectors. A DVD drive gives at most ~22 MB/s, so the drive is the bottleneck.
	C would add native build/interop work per platform and make the code harder to read.
- **Decision:** all C#, no native code; optimize in C# if a hot spot ever appears.
- **Owner request:** the app must run on Windows 11; identify what would allow or prevent
	Linux. **Answer:** added a "Platform" section to the plan. Everything so far is portable
	C# except disc location (drive letters) and, later, drive authentication/raw reads
	(different OS calls) and the GUI toolkit.
- **Decision:** OS-specific code goes behind interfaces in `RipDvd.Core` with one
	implementation per OS; if Linux matters, the GUI uses Avalonia.

## 2026-11-18 10:09
- **Owner request (formatting):** indent all files with tabs (= 4 spaces); open braces at
	the end of the control line, not on their own line.
- **Step:** added `.editorconfig` with these rules (CRLF, tabs, `csharp_new_line_before_open_brace
	= none`, no new line before else/catch/finally). Reformatted all C# with
	`dotnet format whitespace`, and converted indentation in `.csproj`, `.slnx` and `.md` files
	to tabs. Collection-expression tables now open with `=> [` on the same line. Build passes,
	`titles` output unchanged, `dotnet format --verify-no-changes` is clean. Rule recorded in
	CLAUDE.md.

## 2026-11-18 10:13
- **Question answered:** the CSS code is ported from libdvdcss (GPL v2+); is distributing
	this program under the GPL OK? **Answer:** yes, GPL redistribution is fine.
- **Decision:** the program is GPL v2+. Every file derived from GPL code starts with a
	"LICENSE NOTE" block that names the source and lists all GPL-derived files, so they can
	be found and replaced later. Currently: `src/RipDvd.Core/Css/Css.cs` and
	`src/RipDvd.Core/Css/CssTables.cs`. The same list is in the plan's design decisions.
- **Step:** added the LICENSE NOTE blocks to both files. Also corrected a comment in
	CssTables.cs that described Tab4/Tab5 more precisely than I had verified.

## 2026-11-18 10:17
- **Owner question:** why port CSS from libdvdcss instead of using libdvdcss directly?
	**Answer:** (1) the owner wants readable .NET code; a native DLL would be a black box;
	(2) consistent with the self-contained decision (MakeMKV does its own CSS too);
	(3) libdvdcss has no official Windows build, so it would need building or extracting
	from VLC, per-architecture shipping and a P/Invoke layer; (4) only ~200 lines were needed
	(the data-based key attack). Cost of the choice: libdvdcss is far more battle-tested and
	also does drive authentication, player keys and disc-key cracking. Licensing is not a
	difference: libdvdcss is GPL (not LGPL), so linking it would also make the program GPL.
	This choice was made without asking the owner at the time; now recorded.
- **Decision (owner agreed):** keep the C# port; if a disc or drive defeats it, add
	libdvdcss as an optional fallback behind the same interface.
- **Plan tidy-up:** two design-decision bullets had ended up below the Platform table;
	moved back. Status updated (probe commands, duration gap).

## 2026-11-18 10:22
- **Step:** `RipDvd.Core/Css/TitleKeyFinder`: accepts the first candidate key that
	descrambles most sampled video sectors into data with MPEG start codes. Probe `key` uses
	it: same 14 keys as before, now 0.1–0.4 s per title set (was up to 49 s). Plan step
	"speed up key recovery" done. The old voting method (`CssProbe.CrackTitleSet`, probe
	`crack`) is marked `[Obsolete]` / "(old)".
- **Step:** demuxer: `Mpeg/ProgramStreamParser` (pack → PES packets: video, AC3, DTS, LPCM,
	MPEG audio, subpictures, nav), `Dvd/TitleSectorReader` (cells in PGC order, descrambled,
	angle-1 cells only). Probe `demux` checks the streams.
- **Result (title 6):** key in 57 ms; nothing left scrambled; video 4,189 pictures, no
	pulldown, duration 2:19.772; both AC3 tracks 4,367 clean frames, 2:19.744.
- **Step:** `Matroska/EbmlReader` + `MatroskaIds` and probe `mkvinfo` to inspect MKV files.
- **Step:** MakeMKV rip of title 6 (its `B2_t04.mkv`) into git-ignored `reference/rips/`;
	structure dump saved as `reference/makemkv/B2_t04-mkvinfo.txt`.
- **Finding (durations resolved for files):** MakeMKV's MKV Duration is 139,772 ms =
	2:19.772, exactly our stream measurement. The shorter times in MakeMKV's title list are
	a display value only. Decision: our MKV duration = measured stream duration.
- **Finding (MakeMKV MKV layout, to copy):**
	- EBML DocType "matroska" v2. Segment order: SeekHead, Void, Info, Tracks, Chapters,
		Clusters, Cues, Tags. TimestampScale 1 ms. Info has MuxingApp/WritingApp, Duration,
		DateUTC, SegmentUID.
	- Video: `V_MPEG2`, CodecPrivate = sequence header + extensions (152 bytes),
		DefaultDuration 33,366,666 ns, MinCache 2, FlagDefault 0, no lacing, Pixel 720×480,
		Display 640×480 for 4:3. One frame per BlockGroup (BlockDuration 33, ReferenceBlock
		on non-key frames), in decode order, timestamps = PTS shifted so the first is 0.
		Bytes are the elementary stream unchanged (75,134,800 = our demuxer's count).
	- AC3: `A_AC3`, Language "eng", Name "Stereo" (or e.g. "Surround 5.1"), first audio
		FlagDefault 1, others 0; DefaultDuration 32 ms; SimpleBlocks with fixed-size lacing,
		3 frames per block. Payload unchanged (3,353,856 bytes = ours).
	- Chapters: one default edition; ChapterAtoms "Chapter 01".., ChapLanguage "eng", with
		ChapterTimeStart/End in ns.
	- Tags per track (TargetType "MOVIE", TagTrackUID): BPS, DURATION, NUMBER_OF_FRAMES,
		NUMBER_OF_BYTES, SOURCE_ID ("0100E0" video, "0180BD"/"0181BD" AC3), plus
		_STATISTICS_WRITING_APP/_DATE_UTC/_TAGS.
	- 270 clusters (≈ one per GOP), one Cue per cluster on the video track.

## 2026-11-18 10:29
- **Step:** first working MKV writer. New in `RipDvd.Core`: `Matroska/EbmlWriter`,
	`Matroska/MkvWriter` (MakeMKV's layout), `Mpeg/Mpeg2VideoFramer` (pictures + display
	times from temporal_reference and field counts, anchored on PES PTS per GOP),
	`Mpeg/Ac3Framer`, `Ripping/TitleRipper` (tracks, chapters, cell-by-cell timeline from
	nav-pack VOBU start times), `Ripping/Languages` (ISO 639-1 → 639-2). Probe `rip`.
- **Result (title 6 vs MakeMKV's B2_t04.mkv):** identical video blocks (4,189, 292 key,
	75,134,800 bytes, same timestamps incl. B-frame order), identical audio frames/bytes,
	same Duration (139772), same chapter times to the nanosecond, same tags except BPS
	(4300391 vs 4300420). Small differences left: 291 vs 270 clusters, audio lacing breaks
	in slightly different places (1,564 vs 1,563 blocks). Rip time 1.3 s (data cached).
- **Fixes on the way:** framer scan limit must be re-read after a frame is removed from the
	buffer; times are now computed from whole field/frame counts (1001/60000 s per field)
	and converted to ns once, which made chapter ends exact.
- **Decision:** MKV Duration is written in whole milliseconds, truncated, as MakeMKV does.
- **Blocked:** downloaded an ffmpeg build to the session scratchpad to decode-test our
	output; running it was denied by the permission system (running a freshly downloaded
	binary). Asking the owner. Until then the check is structural (mkvinfo vs MakeMKV).

## 2026-11-18 10:46
- **Question answered (closed):** legal note on bypassing CSS. **Answer:** the user of the
	program is responsible for determining legality. Removed from open questions.
- **Owner rule (formatting):** comments and `<summary>` docs may wrap at up to ~140 chars
	(first said "twice", corrected to "up to 50% longer"); start each new sentence on a new
	line. Recorded in CLAUDE.md. Applies to new and edited comments.
- **Result (episode, title 1 vs MakeMKV's C1_t00.mkv):** same Duration (2553017 ms), same 9
	chapter times, same track names/flags ("Surround 5.1", "Stereo"), display 853×480, same
	CodecPrivate, identical audio frames and bytes, same 76,514 video frames and 5,429 key
	frames. Video bytes differ by 507,945 (~94 per GOP): MakeMKV removes the closed-caption
	user data from the video when it converts CC to its text track (track 4, `S_TEXT/UTF8`,
	FlagDefault 1, 839 blocks, e.g. "I HAD THE PERFEC..." with leading spaces). We don't do
	CC yet.
- **Finding (speed):** raw drive read 6.7 MB/s (dual-layer disc, outer area); demux alone
	6.1 MB/s; first rip of title 1 3.4 MB/s (488 s). Added a read-ahead thread in
	`TitleSectorReader.ReadAll` (8 × 1 MB chunks) and `FileOptions.SequentialScan` in
	`VobReader`: title 2 ripped at 4.6 MB/s (341 s). Timing MakeMKV on the same title for
	comparison.

## 2026-11-18 10:55
- **Result (speed):** MakeMKV ripped title 2 in 434 s (includes its disc scan); ours took 341 s. We are at least as fast as MakeMKV on this drive/disc.
- **Step:** closed captions → text track.
	New `Captions/DvdCaptionData` (takes the "CC" user data out of each GOP's first picture and returns the CC1/CC2 byte pairs) and `Captions/Eia608Decoder` (two 15×32 screens; pop-on, roll-up, paint-on; PACs, tabs, mid-row italics, special/extended characters).
	`TitleRipper` adds an `S_TEXT/UTF8` track (language eng, default, after the audio) when the VTS video attributes have the line-21 flag; pair k of a GOP is timed at the GOP's k-th displayed picture.
	Probe `mkvinfo -dumptrack N` prints a track's blocks as text.
- **Result (title 1 vs MakeMKV's C1_t00.mkv):** video track now byte-identical (1,407,923,592 bytes; the CC user data is stripped as MakeMKV does). Caption track: all 839 events have identical text; start times within ±1 ms; MakeMKV's durations are 1–4 ms shorter (mostly 2). Left as is: not visible.
- **Findings while matching captions:**
	MakeMKV starts a new event whenever a pop-on caption is swapped in (EOC), even with identical text (e.g. repeated "[RING]"); we now do the same.
	MakeMKV writes the 608 extended character ’ as ASCII `'`; we now map ’ and ‘ to `'` and “ ” to `"` (only ’ was seen on this disc).
- **Note:** the 5.5 s rip time of title 1 here is from the Windows file cache (data read minutes earlier), not the drive.

## 2026-11-18 10:57
- **Question answered:** how to check that our MKVs really play (ffmpeg was blocked)? **Answer (owner):** the files in `reference/rips/ours/` play; permission given to run the downloaded ffmpeg to test files.
- **Result (ffmpeg decode test):** `ffmpeg -v error -i <file> -map 0:v -map 0:a -f null -` gives 0 errors for our title01.mkv and title06.mkv, same as for MakeMKV's files.
	ffprobe sees the same streams in ours and MakeMKV's: mpeg2video 720×480 (DAR 853:480 / 4:3), AC3 6ch/2ch eng, subrip (our S_TEXT/UTF8 CC track) eng; durations 2553.017 / 139.772 s.
- **Step:** main CLI `src/RipDvd.Cli` (assembly `rip-dvd`) with `info` and `mkv`, plus `Ripping/DiscScanner` in the library (title list, disc argument parsing `disc:N` / drive letter / folder).
	`info E:` lists the same 8 kept titles as MakeMKV, with the same ids (e.g. id 4 = disc title 6). `mkv E: 4 <folder>` wrote `title_t04.mkv`.
- **Decision:** output name prefix `title` until MakeMKV's `C1`/`B2` rule is understood. Reason: MakeMKV's usual name is `title_tNN.mkv`.
- **Note for future sessions:** ffmpeg was downloaded to the session scratchpad only (BtbN build, GitHub); a new session must download it again. Owner allows running it for testing.

## 2026-11-18 11:08
- **Owner question:** how do I know if a disc has subtitles? **Answer:** the case lists "Subtitles: …" (the CC logo means closed captions, which is different); MakeMKV shows "Subtitles" tracks with codec VobSub; probe `titles -v` prints `subp` lines.
- **Second test disc:** owner inserted `A_VERY_LONG_ENGAGEMENT_DISC1` (film). 7 titles, all but one in VTS 1.
	Main title: 2:13:19 (IFO), 39 chapters, 40 cells, 6.42 GB (dual layer). Audio: AC3 5.1 French, AC3 stereo French (code extension 3 = director's comments). Subpictures: en, fr, es, en, each with a widescreen stream (0..3) and a letterbox stream (4..7). Closed captions too.
- **MakeMKV reference** (`reference/makemkv/A_VERY_LONG_ENGAGEMENT_DISC1-info.txt`): keeps title 1 (40 cells, shown as 2:12:54) and title 5 (trailer, 2:05).
	Title 1 tracks: video; AC3 5.1 fre (default); AC3 stereo fre; S_VOBSUB eng (default), fre, spa, eng; CC→Text eng (not default here, because a VobSub track is default).
	Title 5: the IFO marks all 4 subpicture streams available but they all point to substream 0; MakeMKV lists one VobSub track only (duplicates merged).
- **Finding (naming):** MakeMKV's title name here is "A VERY LONG ENGAGEMENT DISC 1" (volume label, `_` → space, "DISC1" → "DISC 1"); files are `<name>-<comment>_tNN.mkv`, e.g. `A VERY LONG ENGAGEMENT DISC 1-B1_t00.mkv`. On the Dead Zone disc the name was empty, giving `C1_t00.mkv`. Why one label gives a name and the other doesn't is still open.
- **Finding:** MakeMKV's displayed duration (2:12:54) is 25 s less than the IFO time; to be checked against its file, as on the first disc.
- **Step:** MakeMKV ripping the trailer and the main title into `reference/rips/makemkv/avle/` (background) as references for VobSub.

## 2026-11-18 11:33
- **Step:** VobSub support written: `Mpeg/SubpictureAssembler` (joins an SPU from its PES packets by its 2-byte size; duration from the stop-display command; forced flag), `Ripping/VobSubHeader` (CodecPrivate "size:" + 16-color palette, YCrCb → RGB with BT.601), S_VOBSUB tracks in `TitleRipper` (16:9 video uses each stream's "wide" substream, 4:3 the 4:3 one; duplicates merged; first is default; the CC text track is default only when there are no VobSub tracks). `rip-dvd info` lists subtitle languages; on this disc it shows exactly MakeMKV's track set.
- **Finding (trailer, title 5):** MakeMKV's file has no subtitle and no CC track although its title list shows them: those streams carry no data in this title. MakeMKV drops tracks that end up empty.
	**Decision:** do the same. `MkvWriter` rewrites the Tracks element in place at the end without empty tracks (freed space becomes a Void) and writes no tags for them. Track numbers of later tracks are not changed (gaps are allowed).
- **Finding:** MakeMKV writes no Chapters element for a single-chapter title. **Decision:** same.
- **Result (trailer vs MakeMKV):** identical video (3,000 frames incl. pulldown, 69,249,336 bytes) and audio (3,910 frames); tags equal except BPS (±1); Duration 125124 vs MakeMKV 125125 (1 ms, rounding; on the first disc MakeMKV truncated: open detail).
- **Finding:** `ffmpeg -f null` reports "non monotonically increasing dts" once per GOP on pulldown content, for MakeMKV's file and ours alike. It's a warning from ffmpeg's null muxer, not a decode error.

## 2026-11-18 12:10
- **Result (movie, title 1 of A_VERY_LONG_ENGAGEMENT_DISC1, vs MakeMKV's `...-B1_t00.mkv`):**
	Video identical (191,808 frames incl. pulldown, 16,016 key, 5,682,746,996 bytes, same timestamps). Both AC3 tracks: same 250,000 frames and bytes.
	All 4 VobSub tracks identical: 1,344 / 1,353 / 1,339 / 2,012 subpictures, same bytes, start times and durations.
	Speed: 1,583 s for 6.4 GB vs MakeMKV 1,836 s (MakeMKV's run overlapped with some of our reads).
	ffmpeg decodes every stream (video, audio, 4 dvd_subtitle, subrip) with no errors; only the per-GOP pulldown dts warning, as for MakeMKV's files. (To decode subtitles with `-f null`, name encoders: `-c:s:N dvdsub` / `srt`.)
- **Finding (VobSub format):** MakeMKV's CodecPrivate is a full .idx header (508 bytes): "# VobSub index file, v7 …", comment lines naming MakeMKV, then size, org, alpha, smooth, fadein/out, align, time offset, forced subs, langidx 0, palette, "# end". Blocks are raw SPUs (no compression), one per BlockGroup with BlockDuration.
	**Decision:** write the same header text (our name in the "generated by" line).
- **Finding (palette):** MakeMKV converts the PGC's YCrCb palette with BT.601 *studio range* (Y 16..235): Y 16/128/176/235 → 00/82/ba/ff. Our first version (full range) gave 80 for 128. Fixed.
- **Finding (duration):** MakeMKV's Duration is the end of the longest audio/video track: 8,000,000 ms here, set by the audio (250,000 × 32 ms); our video-only end gave 7,999,991. Fixed.
- **Finding (CC timing with pulldown):** line-21 caption pairs come one per 1/29.97 s frame, not one per coded picture. Timing pair k at the k-th picture was 50–67 ms late on film (pulldown) content. Now timed at GOP start + k frames; on the movie's opening, starts match MakeMKV to 1 ms (durations 1–3 ms longer, as on the first disc).
- **Step:** `TitleRipper.Rip(…, maxCells)` and probe `rip … -cells N` rip only a title's first cells (quick tests on long titles). Probe `titles -v` prints the PGC palette.

## 2026-11-18 12:34
- **Owner request:** list the movies on the Plex drive (D:) and find which are DTS. **Answer:** no web lookup needed: ffprobe on the 470 files under D:\Movies, D:\Theater, D:\Video shows 424 untouched DVD rips (720×480 MPEG-2); 32 have DTS (5 of them DTS-ES 6.1), 3 have LPCM (Celtic Women: A New Journey also has DTS and AC3). The list stays in the session scratchpad, not the repo (owner's library).
- **Third test disc:** `LOST_IN_TRANSLATION`: 23 titles; main title 16 (1:41:45) with AC3 5.1 eng, DTS eng, AC3 5.1 fre, VobSub eng/spa/fre. The IFO gives the DTS stream 5 channels; the stream itself (and MakeMKV) say 6.
	Reference: the owner's Plex copy `D:\Movies\Lost In Translation (2003).mkv` is a MakeMKV 1.14.7 rip of this title, so no new MakeMKV rip was needed.
- **Finding (drive authentication needed):** on this freshly inserted disc Windows refused reads with "Copy Protection Error - The read failed because the sector is encrypted". On the first two discs MakeMKV had already authenticated the drive. Plan step "drive authentication" is now required.
- **Step:** CSS drive authentication. `Css/CssAuthentication` (port of libdvdcss CryptKey + GetBusKey; GPL, license note added and listed in all GPL files), crypt tables `CryptTab0..3` added to `Css/CssTables`, `Dvd/WindowsCssDrive` (DeviceIoControl with IOCTL_DVD_START_SESSION / READ_KEY / SEND_KEY / END_SESSION on \\.\E:, own code), `DriveUnlocker.TryUnlock` called by `TitleRipper` before reading. After the handshake the disc key is read, which sets the drive's authentication success flag; scrambled sectors then read normally.
- **Step:** DTS audio. `Mpeg/Ac3Framer.cs` now holds a shared `AudioFramer` base (frame splitting, PTS from the first-access-unit pointer, times counted in samples from the last PTS so fractional frame lengths don't drift) with `Ac3Framer` and `DtsFramer` (sync 7FFE8001; NBLKS, FSIZE, AMODE, SFREQ, LFF). Audio tracks are keyed by stream kind + number; channel counts and names are taken from the stream once seen.
- **Result (first 3 cells, 8.5 min, of title 16 vs the Plex MakeMKV copy):** DTS track `A_DTS`, 6 channels, "Surround 5.1", DefaultDuration 10,666,666, 9 frames per laced block (96 ms, like 3 AC3 frames); first blocks byte-identical to MakeMKV's. AC3 blocks and subtitle start times identical. ffmpeg decodes it all (dts 5.1(side)) with no errors.
- **Decision:** DTS: lace 9 frames per block and use the stream's channel count, as MakeMKV does.

## 2026-11-18 12:58
- **Owner question:** Celtic Women can't be found; which other DVDs would work? **Answer:** for LPCM only Gypsy (2004); also scanned D:\TV Shows (2,518 files, 1,726 DVD rips): no LPCM, MPEG or DTS audio there. No disc in the library has MPEG audio. DTS-ES candidates: Se7en, Dirty Dancing, The Mask, X-Men: The Last Stand, Butterfly Effect DC.
- **Fourth test disc:** Gypsy (2004) volume 1: no volume label, one title (1:13:20, 11 chapters), one LPCM 16-bit stereo 48 kHz stream with no language; not CSS-protected. Reference: the owner's Plex copy `D:\Theater\Gypsy (2004)\Gypsy (2004) - Act 1.mkv` (MakeMKV 1.15.3).
- **Finding (MakeMKV LPCM):** converted to `A_PCM/INT/LIT` (little-endian), BitDepth 16, one block per 1/30 s (1,600 samples, 6,400 bytes), DefaultDuration 33,333,333, no lacing, name "Stereo", no Language element when the disc gives none.
- **Step:** `Mpeg/LpcmFramer` (16-bit: byte swap per sample, 1/30 s blocks timed by sample count from the first PTS, flush of the last partial block); `AudioAttributes.BitsPerSample` from the IFO; `MkvTrack.BitDepth`; audio framers get a `Flush()`.
	20/24-bit LPCM (grouped sample packing) is reported as unsupported for now.
- **Bug found:** the LPCM samples first came out one byte off. The first-access-unit pointer counts from the byte after itself; for LPCM the payload we receive starts 3 bytes later (LPCM header), so the skip is pointer − 4 (for AC3/DTS the payload starts right after the pointer).
- **Result (first cell vs MakeMKV):** first blocks byte-identical, same 1/30 s timing; ffmpeg decodes it (pcm_s16le, 2 ch, 48 kHz) with no errors.
- **Finding:** on this unprotected disc the drive rejects the CSS handshake ("Incorrect function"). **Decision:** run the handshake anyway, but report its failure only if reading then fails.
- **Decision:** omit the Language element when the disc gives no language (MakeMKV does).

## 2026-11-18 13:52
- **Fifth test disc:** `BUTTERFLY_EFFECT` (director's cut): 23 titles; titles 1 and 2 are the movie (1:59:38; title 2 has 56 chapters and 3 subtitle streams). Audio: AC3 5.1, DTS-ES, 2 × AC3 stereo (commentaries). Reference: the owner's Plex copy `D:\Movies\Butterfly Effect Directors Cut (2004).mkv` (MakeMKV 1.14.7).
- **Result (first 3 cells of title 1):** DTS-ES needs nothing extra: the 6.1 extension (XCh) is inside each DTS frame and is copied with it. Our blocks are byte-identical to MakeMKV's; both label the track "Surround 5.1", 6 channels (the core), and ffprobe reads both files as DTS-ES 7 channels. AC3 tracks identical; ffmpeg decodes everything with no errors.
- **Owner question:** would audio discs solve the MPEG-audio item? **Answer:** no. Audio CDs are not DVD-Video (plain PCM, no VIDEO_TS); DVD-Audio discs use MLP/LPCM in AUDIO_TS, and their video zone normally uses AC3/DTS. MPEG audio mostly appears on some PAL (European) DVDs and on home-made DVDs (camcorder / home authoring software).

## 2026-11-18 13:57
- **Owner question:** is current-plan.md being kept up to date; were steps 1–4 finished before suggesting step 5?
	**Answer:** no. Steps 1–4 were not finished (Matroska polish not started, full-disc CLI test not done, audio only partly done, Linux authentication not started), and the Status section had become a history with stale lines ("no drive authentication needed", the first test disc as "available"), against the CLAUDE.md rule.
	**Step:** rewrote current-plan.md as a present-state document: compact status with a table of the five test discs, updated design decisions, next steps re-ordered with what each still needs and which need the owner.
	**Decision:** when proposing what to do next, follow the plan's order or say why not.

## 2026-11-18 14:00
- **Decision (owner):** move 20/24-bit LPCM + MPEG audio, and Linux drive authentication, from Next steps to Future. Reason: the owner has no disc with those audio formats and no Linux machine with a DVD drive to test on.

## 2026-11-18 14:34
- **Step:** probe `mkvcompare <a> <b>`: compares two MKVs track by track (tracks matched by codec + order): language/name, frame count, payload bytes, SHA-256 over all frame payloads (laced frames split, so lacing differences don't count), first/last frame time; plus Duration and chapter starts.
- **Step (plan step 1 done):** full-disc test through the CLI: `rip-dvd mkv E: all` on DEAD_ZONE_S4_D1 (owner re-inserted the disc). 8 titles in 1,816 s (episodes 3.3–4.8 MB/s, extras 2.1–2.9 MB/s; small titles read slower).
- **Result:** title_t00 (episode 1) and title_t04 (extra) identical to MakeMKV's files in every compared property, including the SHA-256 of all video, audio and caption payloads. title_t01 (episode 2) identical except the first and last caption times, 1 ms off (3,403 vs 3,404 ms). The other 5 titles have no MakeMKV rip to compare against.
	All 8 files decode with ffmpeg with no problems (video, all audio, caption text).
- **Correction:** I had told the owner MakeMKV references existed for every title of this disc; only 3 titles had full MakeMKV rips (the rest have only MakeMKV's title listing). Corrected before the run.

## 2026-11-18 14:46
- **Owner decision:** MakeMKV's file naming doesn't matter; use something clearer. Plan step "MakeMKV file naming" dropped (the C1/B2 comment rule stays unexplained).
- **Decision:** files are named `<Disc name> - title NN.mkv` (`Ripping/OutputNaming`): disc name from the volume label (`_` → space, all-caps words capitalized, words with digits kept as written, `DVD` when there is no label), overridable with `--name=`; NN is the disc's own title number.
	Reason: readable, and the number traces the file back to the disc (the title a DVD player would play).
- **Decision:** the CLI uses the disc's title numbers everywhere: `info` lists `title N`; `mkv` takes `1` or `1,3,6` (any title, even short ones) or `all` (titles at least --minlength long). MakeMKV's renumbered ids (0, 1, 2 over kept titles) are gone. Reason: one numbering, so what `info` shows is what `mkv` takes.
- **Result:** `info E:` shows "Dead Zone S4 D1"; `mkv E: 6 <folder>` wrote `Dead Zone S4 D1 - title 06.mkv`; `mkv E: 99 x` reports "No title 99 on this disc; titles are 1..14" and exits with 1.

## 2026-11-18 14:58
- **Step (plan: Matroska details):** real ReferenceBlock values instead of the placeholder -1.
	MakeMKV (seen with probe `mkvinfo`, which now prints refs): each reference is (referenced block time - this block time) in ms, from the rounded block times; P pictures reference the previous anchor (I/P); B pictures the two previous anchors in decode order (backward first); a key frame starts a new cluster and nothing references back past it, so the B pictures right after a key frame reference only that key frame.
	Implemented in `TitleRipper` (anchor tracking) and `MkvWriter.AddVideoFrame` (list of reference times).
- **Step:** probe `mkvcompare` also compares ReferenceBlocks (count and SHA-256 of all values per track).
- **Result:** on the Dead Zone extra (title 6) our file is identical to MakeMKV's in every compared property, now including all 6,119 ReferenceBlocks. (A first version also gave the leading B pictures of a GOP a reference into the previous GOP: 6,651 vs 6,119; fixed.)

## 2026-11-18 15:05
- **Step (plan: Matroska details):** cluster layout like MakeMKV's. Found by listing cluster times/sizes (probe `mkvinfo -clusters N`) and simulating rules against MakeMKV's file:
	- a new cluster starts at a key frame only if it is at least 400 ms after the key frame that started the current cluster, or where a chapter starts; short GOPs join the current cluster;
	- the cluster timestamp is the earliest block time in it (the B pictures shown before the key frame), truncated to whole ms (block times are rounded);
	- the cue still points at the key frame's time.
- **Result (Dead Zone extra, title 6):** 270 clusters like MakeMKV (was 291), all 270 cluster timestamps identical, and the file is identical to MakeMKV's in every property `mkvcompare` checks. Each cluster is 4–10 bytes smaller than MakeMKV's (element size encoding); left as is.

## 2026-11-18 14:52
- **Owner request:** do the CLI options, including a help option that explains them.
- **Step:** `Ripping/RipOptions` (audio/subtitle languages: list, all or none, 2-letter codes accepted, `und` = no language; captions on/off; chapters on/off). `TitleRipper`: track and chapter setup moved into `BuildPlan(options)`, used by `Rip` and by the new `Describe` (for dry runs), so both always agree.
- **Step:** CLI rewritten around two tables (commands, options) from which the parser and the help are generated: `rip-dvd help`, `help <command>`, `-h`/`--help`. New: `info --all`; `mkv --audio`, `--subtitles`, `--captions`, `--chapters`, `--overwrite`, `--dry-run`. Unknown options, missing values, options for the wrong command and bad values give a message plus a pointer to the help (exit 1).
- **Decision:** existing output files are skipped unless `--overwrite` (safe default; MakeMKV asks in its GUI).
- **Result:** default rip of the Dead Zone extra still identical to MakeMKV's (`mkvcompare`); dry runs list the expected tracks (e.g. episode 1: video, AC3 5.1 + stereo, CC text, 9 chapters; with `--audio=en --captions=off --chapters=off`: no CC track, no chapters); a real rip with `--audio=none --chapters=off` has only the video track and no chapters and decodes cleanly; all error cases behave.
- **Plan:** the planned steps are done; only cosmetic differences from MakeMKV remain (optional). Future lists the next possible projects.

## 2026-11-18 15:20
- **Owner request:** analyse whether ripped titles can be matched to TheTVDB episodes. **Answer:** yes, with the user confirming series/season (when the volume label doesn't give it) and, from disc 2 on, possibly the first episode number. Written up in `docs/episode-matching-analysis.md`.
- **Findings:**
	- Our rip of DEAD_ZONE_S4_D1 title 1 is byte-identical (audio and caption payloads, duration) to the owner's `The Dead Zone (2002)\Season 4\s04e01 - Broken Circle.mkv`: disc order = episode order; the owner's library names episodes `sNNeMM - Name.mkv`.
	- TheTVDB API v4: project key + per-user subscriber PIN (about $12/year, to be verified); episode records have name, number, seasonNumber, aired, runtime (whole minutes, nullable); season types include `dvd`.
	- Free alternatives: TVmaze (no key, aired order only), TMDB (free key, DVD order only where episode groups exist); terms not yet verified. TheDiscDB maps specific discs' titles to episodes (identification method/API not verified).
	- Runtime cannot tell episodes apart (seconds apart on disc, whole minutes in TheTVDB); order is the main signal.
- **Open questions added:** data source (TheTVDB paid vs free) and episode file naming.

## 2026-11-18 15:45
- **Owner questions:** why does MakeMKV list duplicate titles; are chapters different from cells; a titles-vs-cells table. **Answers:** DVDs define titles as playlists of cells, and several titles can play the same cells (play-all titles, the same movie with other chapters/subtitles, alternate paths, decoy titles); MakeMKV drops only exact duplicates. Chapters point at programs (groups of cells); not every cell starts a chapter. Probe `cellmap` prints one whole-disc table (a row per cell, a column per title); maps saved in `reference/cellmaps/`.
- **Finding (BUTTERFLY_EFFECT, 143 cells, 23 titles):** titles 1 and 2 play the same 57 cells (22 vs 56 chapters); title 14 = titles 15–23 in a row; title 13 = parts of titles 7–11 (without their first/last cells and some chapter continuations), in a different order; many titles share a 0.5 s end cell.
- **Note:** the drive could not read the disc on one insert (Windows: no disc; drive noises); re-inserting fixed it. Probe now prints a one-line "no disc is ready" error instead of a stack trace.
- **Owner request:** a flag to skip compilations ("I never want to watch all episodes together"). **Step:** `DiscScanner` classifies titles (duplicate / compilation, rules in the plan); CLI `--no-compilations` and `--no-duplicates` for `mkv … all`; `info` marks such titles. On Butterfly Effect: title 2 = duplicate of 1; titles 13 and 14 = compilations; titles 15–23 (each inside 14) correctly not flagged. Dry run skips 2, 13, 14 with reasons.
- **Decision:** default stays MakeMKV's (keep them); the flags opt in. Explicitly numbered titles are always ripped.

## 2026-11-18 15:47
- **Question answered:** make skipping compilations a saved or built-in default? **Answer (owner):** no, leave it as is: compilations and duplicates are kept by default; `--no-compilations` / `--no-duplicates` opt in.

## 2026-11-18 15:52
- **Owner decision:** the remaining small differences from MakeMKV's files (audio lacing breaks, caption durations 1–4 ms longer, Duration ±1 ms, clusters 4–10 bytes smaller) are not important. Removed from Next steps; recorded in the plan's design decisions as accepted differences. Next steps is now empty; possible next projects are under Future.

## 2026-11-18 16:00
- **Owner request:** start the GUI, Windows only, in three phases: (1) scan the disc and present it, with cell information in a compact graphical table so duplicates are visible; (2) the user selects titles and clicks Start; (3) ripping with % done and remaining time for the current title and all titles, then an optional sound. Asked for a plan with steps and any questions.
- **Step:** plan written (current-plan.md Next steps G0–G4): library groundwork (cell-map model in Core, progress + cancellation in TitleRipper, batch runner with ETA), then WPF phases 1–3, then testing. The GUI moved from Future to Next steps.
- **Decision:** the GUI is Windows-only (owner), so the Platform table no longer plans Avalonia for it. Proposed WPF, no third-party packages.
- **Open questions added:** cell-map block widths, auto-scan on disc insert, track options in the first version, which completion sound, WPF vs WinForms.

## 2026-11-18 16:05
- **Owner question:** does Windows have a more modern UI than WPF? **Answer:** WinUI 3 (Windows App SDK) is the newest; but its first-party table control (TableView) is still experimental as of late 2026, and apps usually need the Windows App SDK runtime / MSIX packaging. WPF has had a built-in Fluent (Windows 11) theme since .NET 9 (`ThemeMode`), giving a modern look with WPF's mature DataGrid and a single .exe. Recommended WPF + Fluent; open question updated.

## 2026-11-18 16:07
- **Question answered:** GUI framework. **Answer (owner):** WPF is fine. **Decision:** WPF on .NET 10 with the built-in Fluent theme, no third-party packages. Removed from open questions.
- **Next:** start G0 (library groundwork), which doesn't depend on the remaining GUI questions (cell-map widths, auto-scan, track options, sound).

## 2026-11-18 16:20
- **Step (G0.1, done but not committed by request):** `Ripping/CellMap` in the library (cells, per-title playlists and chapter starts, `ClassifyOverlaps`); `DiscScanner` returns it in `DiscSummary.CellMap` and uses it for duplicates/compilations; probe `cellmap` renders it. Checked: probe output identical to the saved BUTTERFLY_EFFECT table; `info` flags unchanged. The owner then said "stop"; the code changes are left uncommitted until the owner decides.
- **Owner request:** split the design decisions into core and UI. **Step:** current-plan.md now has "Design decisions → Core" (library and CLI) and "→ UI" (WPF + Fluent, Windows only, plain MVVM, no ripping logic in the GUI); the framework sentence moved out of the Next steps header. Also fixed a control character (from a PowerShell backtick escape) that had turned `all` into "ll" in the plan.

## 2026-11-18 16:30
- **Questions answered (owner):**
	- GUI cell map block widths: **same width** for every cell (no proportional mode).
	- GUI disc insert: **scan automatically** when a disc goes in.
	- GUI selection: **show all titles**, mark duplicates and compilations, the user checks/unchecks which to rip. Track options are not in the first version (moved to Future); all tracks kept.
	- GUI completion sound: the **Windows default notification sound** for now.
	- Episode naming (data source, file names): **deferred**; kept in open questions, marked deferred.
- **Decision (mine, owner may change):** initially checked titles = at least 120 s and neither duplicate nor compilation.
- **Step:** current-plan.md updated: UI design decisions, G1–G3 steps (no more (Q) marks), open questions reduced to the two deferred ones.

## 2026-11-18 16:33
- **Question answered:** commit the G0.1 code left uncommitted after "stop"? **Answer (owner):** commit it. Committed (`Ripping/CellMap`, `DiscScanner` using it, probe `cellmap` rendering it).

## 2026-11-18 16:50
- **Step (G0.2):** `TitleRipper.Rip` reports `RipProgress` (phase Unlocking / FindingKey / Ripping / Finishing / Done, sectors done/total, elapsed) and takes a `CancellationToken` (checked every sector and during the key search). A cancelled or failed rip deletes its partial file. The first report is sent before the VOBs are opened (spin-up took 4 s on one test).
	Probe `canceltest`: cancelled after 8 s, partial file removed.
- **Step (G0.3):** `Ripping/RipJob` rips a list of titles; reports per-title and overall % and remaining time from the measured read speed (reading time only, over the whole job, after 3 s); a failing title is recorded and the job continues; cancellation stops it. The CLI `mkv` now uses it (status line with % and time left per title and overall, MB/s; Ctrl+C cancels cleanly, exit code 3; exit code 2 if a title failed).
- **Result:** Butterfly Effect titles 3 and 6 through the CLI: estimates appear after 3 s and settle (overall estimate 0:47 at 7%, actual ~42 s), carry over to the second title; both files written.

## 2026-11-18 17:20
- **Step (G1 done):** WPF app `src/RipDvd.Gui` (assembly `rip-dvd-gui`), .NET 10, Fluent theme (`ThemeMode="System"`; warning WPF0001 suppressed since the theme is still marked experimental), plain MVVM (`ViewModelBase`, `RelayCommand`, `MainViewModel`, `TitleRow`).
	- Drive picker; scanning on a background thread; automatic scan when Windows reports a disc inserted (`WM_DEVICECHANGE` / `DBT_DEVICEARRIVAL` with `DBTF_MEDIA`), cleared on removal.
	- Title list (DataGrid): all titles, short ones dimmed, Note column "same video as title N" / "compilation of titles 7–11".
	- `Controls/CellMapView`: custom-drawn map, rows = titles, columns = cells (same width, grouped by title set); blue = played by one title, orange = shared; notch = chapter start; selected row and rows sharing its cells highlighted; hover text with the cell's details; clicking a row selects the title (kept in step with the list).
	- Test mode `--screenshot file.png [--drive E]`: scans, renders the window to PNG, exits. Screenshot of Butterfly Effect saved as `docs/gui-phase1-butterfly-effect.png`.
- **Not yet verified:** automatic scan on disc insert needs a physical eject/insert; to be tried by the owner (not a blocker).

## 2026-11-18 17:45
- **Step (G2 done):** a "Rip" checkbox column (initially checked: at least 120 s, not duplicate/compilation); Select all / None; output folder with Browse (`OpenFolderDialog`) and an editable file-name start (from the disc name), the folder remembered in `%APPDATA%\rip-dvd\gui-settings.json` (`GuiSettings`); Start validates (something checked, folder creatable and writable, existing files → Yes replace / No skip / Cancel) and runs `RipJob` in the background with a one-line status (phase, % and time left for the title and overall, MB/s) and Cancel. Dialogs are passed into the view model by the window, so the view model has no window code.
- **Step:** test mode `--rip 6 --to <folder>` (with `--screenshot`) checks those titles, starts the rip, and saves a picture when done; it doesn't change the saved folder.
- **Result:** the GUI's rip of Butterfly Effect title 6 is identical to the CLI's (`mkvcompare`).
- **Bug found and fixed:** after a rip the buttons stayed disabled (WPF re-evaluates commands only after user input); now `CommandManager.InvalidateRequerySuggested` runs whenever scanning/ripping starts or stops.
- **Owner question (during G2):** is current-plan.md updated as I go? **Answer:** at each milestone (G1 was recorded when committed; G2 is recorded with this commit).

## 2026-11-18 17:50
- **Owner question:** are steps logged as they are completed and removed from current-plan.md? **Answer:** logged, yes (one dated entry per milestone); removed, no: G0–G2 had been left in Next steps marked "(done)", against the plan's present-state rule.
- **Step:** removed G0–G2 from Next steps (their history is in the log entries 16:20–17:45); Status now lists the GUI (phases 1–2) and the new library pieces (cell map, progress/cancellation, `RipJob`). The remaining Cancel work is now "ask for confirmation" (Cancel itself works); the owner's insert/eject test was moved into G4.
- **Process:** CLAUDE.md rule made explicit: a completed step is recorded in the log and removed from Next steps, not marked done.

## 2026-11-18 16:54
- **Step (G3 done):** phase 3 of the GUI. A progress panel appears when a rip starts: a bar and text for the current title ("Title 6: 42% · 0:01:23 left · 4.5 MB/s", or the phase while unlocking/finding the key) and for all titles ("All titles (1 of 2): …"); a list of finished titles, each with its file, length and rip time, any warnings, or the error if that title failed (the job goes on with the next title); "Open folder" opens Explorer on the output folder.
	Cancel asks "Stop ripping?" first. "Play a sound when done" (checkbox, remembered in gui-settings.json) plays the Windows notification sound set in Sound settings (registry `AppEvents\Schemes\Apps\.Default\Notification.Default`), falling back to the standard file, then the Asterisk sound; not played after a cancel.
	Library: `RipJob.Run` takes an `itemFinished` callback so the list fills in as each title finishes.
- **Result:** GUI test-mode rip of Butterfly Effect titles 3 and 6 identical to the CLI rips (`mkvcompare`, all properties incl. payload SHA-256 and ReferenceBlocks). Screenshot `docs/gui-phase3-butterfly-effect.png`.
- **Correction:** the timestamps of the three entries before this one (17:20, 17:45, 17:50) were estimated instead of read from the clock and are too late; the commits show they were written at 16:40, 16:45 and 16:47 (the 16:50 entry likewise belongs at 16:34). From now on the time is read from the system clock.

## 2026-11-18 16:56
- **Step (G4, part):** Butterfly Effect (the disc in the drive) tested in the GUI: scan, cell map with its shared cells and "play all" titles, rip of titles 3 and 6 identical to the CLI (see 16:54). The other four test discs need the owner to insert them; left in Next steps.
- **Step (G4.2):** `README.md` at the repo root: what the program does, requirements, build, CLI commands and options, a walk-through of the Windows app with the phase-3 screenshot, project layout, license (GPL v2+ because of the libdvdcss port; legality is the user's to determine).
- **Not done:** adding the GPL v2 text as `LICENSE`; downloading it from gnu.org failed (connection reset). Added to Next steps.

## 2026-11-18 19:10
- **Step (G4.1, owner):** the owner ejected and inserted a disc with the GUI open; it scanned the disc automatically. Automatic scan on insert is verified; removed from Next steps.

## 2026-11-18 20:00
- **Owner questions answered:**
	- Can individual cells be ripped? Yes: a rip is already a list of cell sector ranges; the track info is borrowed from a title that plays the cell. Not offered yet.
	- Can several cells be joined into one file? Yes, within one title set (same video format, audio layout and CSS key); across title sets no.
	- Can the cell map show progress? Yes, during ripping (the scan only reads the IFO files and takes about a second).
	- What does a scratch wipe out? Usually one or more 32 KB error-correction blocks (16 sectors, about 30–50 ms of video); playback loses about half a second, up to the next key frame. Today any unreadable sector fails the whole title (no retry or skip); MakeMKV retries and skips.
- **Decision (owner):** add all four to the plan, priority to cell ripping and cell-map progress. Added as G5 (rip chosen cells, alone or joined), G6 (progress in the cell map), G7 (retry and skip read errors). "Next steps" is no longer GUI-only; G4 waits on the owner, so G5 can start meanwhile.

## 2026-11-18 20:14
- **Owner question:** could we build a Blu-ray probe and work out how to rip Blu-ray discs (the owner's Blu-ray drive never worked with MakeMKV; no example rips)? **Answer:** the probe, disc structure, transport-stream demuxing and MKV writing are all doable; AACS decryption needs a per-disc key that the owner supplies (key-file approach as in libaacs); testing would rely on ffmpeg and the disc's own playlists instead of reference rips.
- **Decision (owner):** add Blu-ray probing and ripping as a future feature, after the current Next steps. The design decision "DVD only; Blu-ray is out of scope" became "DVD only for now". Owner's note recorded in the plan: https://fvonline-db.bplaced.net/ contains keys we can use.

## 2026-11-18 22:22
- **Owner report:** two titles ripped from Beauty and the Beast (16 and 17) had broken audio: little clips repeating, as if each segment played twice.
- **Cause:** titles 16/17 have 2 angles (Disney: alternate on-screen text), stored as interleaved cells: the angle cells share one sector range cut into interleaved units (ILVUs) that alternate angle 1, angle 2, ... We kept the first angle's cells (correct) but read each from its first to its last sector, so the other angle's nearly identical units were ripped too.
	Confirmed with the new probe command `navwalk` (DSI SML_PBI fields: ILVU end and next-ILVU start, both relative to the nav pack) and by MakeMKV, which lists the angle-1 title with exactly those cells.
- **Fix:** `TitleSectorReader` reads interleaved cells VOBU by VOBU and jumps to the cell's next ILVU at the end of each unit; a jump that lands in another cell stops the rip with an error instead of ripping wrong data. Title sizes and progress now count only the first angle's share (title 17 had shown 5.64 GB).
- **Two more bugs found by comparing with MakeMKV's rip of title 16 (makemkvcon, `reference/rips/makemkv/bb/L3_t00.mkv`):**
	- Chapter times counted the other angle's cells too (chapters ran to 1:59 in a 1:31 title); now only played cells count. This affected every multi-angle title.
	- At a seamless cell join (50:41) the disc has a 17 ms audio gap; MakeMKV closes it and keeps the shift, we kept the gap. Now audio gaps shorter than one frame are closed the same way (longer gaps stay as silence). It applies to all discs; the five earlier discs matched MakeMKV exactly, so they probably have no such gaps, but they were not re-ripped (G4.1 re-checks them).
- **Result:** title 16 ripped again (through the GUI test mode): video, all 3 AC3 tracks and both VobSub tracks identical to MakeMKV (payload SHA-256 and first/last times), duration identical.
	Remaining differences: 10 of 23 chapter starts 17–100 ms off (MakeMKV probably times chapters from the stream; new step B1); 2,654 video frames 1 ms earlier after the first angle join (sub-ms offset, accepted as cosmetic); captions: ours has 38 more lines than MakeMKV's, which drops lines (e.g. ours "Oh, look out!", MakeMKV " )").
	ffmpeg prints "non monotonically increasing dts" warnings for this disc's video in both our and the owner's earlier rip, from the first seconds (before any angle cell); the video data is identical to MakeMKV's, so it is a property of the stream, not of our files.
	The owner's two broken files (`Videos\rip-dvd\Beauty and the Beast - title 16/17.mkv`) should be ripped again.
- **Step (G5 done): rip chosen cells.** `TitleRipper.ForCells` (cells of one title set, in the order given, a chapter per cell named "Cell N", tracks from a title playing the first cell); `CellSelection` (parse "23-27,30", validate, format); `CellMap.Load`; `CellMapTable` (the probe's table, now with a cell-number column, shared with the CLI); `RipJobItem.ForCells`.
	CLI: `info --cells`, `mkv <disc> cells:LIST <folder>` → `<Disc name> - cells LIST.mkv` (decided: cells numbered 1..N in cell-map order rather than the planned "title set:cell" form, which is shorter to type and matches the table). GUI: click/Ctrl+click/Shift+click cells in the map, "Rip selected cells", Clear; test mode `--cells 32,45 --to folder`.
	Tests: cells 32,45 (= title 5) through the GUI identical to the CLI's title 5 in every compared property incl. chapters; cells 32,34,37 joined: ffmpeg decodes cleanly, 3 chapters at the joins, audio continuous except a 40 ms gap at the first join (longer than a frame, so kept). Invalid selections (two title sets, unknown or repeated cells, bad text) give clear messages.
- **Step (G6 done): rip progress in the cell map.** `RipProgress` reports the cell position and how far into it; the cell map draws a strip above the cells (finished cells green, the current one partly) and turns the ripped title's blocks green. Test mode `--midshot file.png` saves a picture at 50%; checked on title 16.
- **Step (G7 done, simulated): damaged discs.** Media read errors (Windows CRC, seek, sector not found, read fault, general failure, I/O device) are retried 3 times, then reading continues at the next readable VOBU of the cell (in interleaved cells, of the same angle); each skip becomes a warning with title time and approximate length, shown in the CLI and the GUI results. Other read errors still fail the title, so a locked drive is not slowly skipped through.
	Probe `rip -bad 10000-10050,20000-20003` simulates bad sectors (`RipOptions.SimulateBadSector`). Tests: title 5 with two bad stretches → 2 warnings, gaps of 0.4 s and 0.2 s exactly there, all tracks end as in the clean rip; title 16 start with bad sectors in angle 1, in angle 2 (correctly ignored) and on an angle-1 nav pack → 2 warnings, right length. A real test needs a scratched disc (G7.4).
- **Process note:** one GUI test ran an old build: the GUI build had failed (stale `obj`, "App.baml cannot be found") and the error was hidden by filtering the build output. Fixed by a clean build; builds are now checked by exit code.

## 2026-11-18 22:39
- **Owner question:** no test project for the core yet; shouldn't there be one? **Answer:** yes. The MakeMKV comparisons need a disc and minutes per run; nothing re-checked disc-free logic quickly, and two of today's bugs (angle chapter times; a stale build that ran old code) would have been caught by unit tests run before each commit.
- **Decision (owner):** add a test project as the next step (T1, before B1), and go through this log for bugs that tests should have caught; those tests are listed in T1.3 and are written now.

## 2026-11-18 22:47
- **Step (T1 done): test project.** `tests/RipDvd.Core.Tests` (xUnit 2.9, in the solution): 55 tests, under a second, no disc needed. `dotnet test RipDvd.slnx` passes.
	- To make the rip rules testable they moved out of `TitleRipper.Rip` into small internal classes: `VideoReferences`, `ClusterRule`, `RipTiming.DurationNs` (new `Ripping/VideoRules.cs`); `AudioContinuity`, `PgcChapters`, `FeedCaptions` became internal; `InternalsVisibleTo` the test project.
	- `TitleSectorReader` and `FindKey` read through a new `ISectorSource` (implemented by `VobReader`); the bad-sector hook is now `BadSectorSource` (still used by probe `rip -bad`).
	- Regression tests for the bugs found in this log: LPCM pointer − 4; BT.601 studio-range palette; duration from the longest audio/video track; caption timing per frame slot with pulldown; field-count video timing and the framer's scan limit; ReferenceBlocks never past a key frame; cluster rule and truncated cluster time; empty tracks dropped; DTS channel count from the stream; interleaved angle cells (only own units; a jump into another cell is an error); angle chapter times; closing sub-frame audio gaps; read-error retry/skip and non-media errors failing; one-angle sizes.
	- Logic tests: cell selection, disc/file names, languages, duplicate/compilation detection, nav pack parsing.
	- Real-disc tests: IFO files of Beauty and the Beast copied to `reference/ifo/BEAUTY_AND_THE_BEAST/` (git-ignored, commercial disc; 432 KB): title 16 size, chapters (end = title end; chapter 2 at 167.167 s as MakeMKV), tracks, cell map, title 18 = duplicate of 17, cells 32,45 = title 5. They are skipped (not failed) when the folder is missing; checked.
	- Checked that the tests catch the bugs: with the interleaved-cell fix undone 3 tests fail; with the angle-chapter fix undone 1 fails (the real-disc chapter test did not, so it now also checks chapter times).
- **Process:** CLAUDE.md: run `dotnet test RipDvd.slnx` before each commit and commit only when it passes; a bug fix comes with a test that fails without it; check the build's exit code.
- **Owner question:** how to launch the GUI? **Answer:** `dotnet run --project src/RipDvd.Gui` from the repo folder, or run `src\RipDvd.Gui\bin\Debug\net10.0-windows\rip-dvd-gui.exe`; close it before building (it locks its files).

## 2026-11-18 22:54
- **Step (G4.2 done):** the owner supplied the GPL v2 text; saved as `LICENSE` at the repo root (CRLF; the title lines centered as in the official text, which the paste had lost). README and the plan's license decision point to it.
- **Owner questions answered (between T1 and this step):**
	- Why do DVDs hold cells that look the same (Beauty and the Beast cells 117 and 130)? They are the same scene in two cuts of the film, at the same playlist position (54): 117 (45.5 s) in title 16, 130 (24.5 s) in titles 17/18. With seamless branching a cell is the smallest unit a title can choose, so a scene that differs anywhere is stored once per cut; other reasons are angles, title sets (a title can only play its own set's cells) and authoring convenience.
	- How did I know title 16 is the Special Edition and 17 the theatrical cut? I did not read it from the disc (DVDs carry no title names); it was an inference from the running times (91 vs 84 min, the known lengths of the two cuts) and the branching structure, and I had stated it as fact. To confirm: title 16 should contain the song "Human Again", titles 17/18 not.
	- What next? G4.1 (owner: four test discs), B1 (chapter times, needs Beauty and the Beast in the drive), G7.4 (owner: a damaged disc).

## 2026-11-18 23:01
- **G4.1 (part), Gypsy vol. 1:** the owner inserted it. IFO files copied to `reference/ifo/GYPSY_VOL1/` (git-ignored) and a test added (one title, 11 chapters, LPCM → `A_PCM/INT/LIT` with no language).
- **Owner report:** during the GUI test rip the window looked frozen. **Cause:** Gypsy is not CSS-protected, so the key search read up to 400,000 sectors (800 MB) looking for a scrambled sector before giving up, minutes with "finding the title key" and no progress (made slower by a second copy of the GUI using the drive). Both windows were then closed, so that rip was not finished.
- **Step (B2 done):** before the handshake and key search the ripper asks the disc's copyright descriptor (`IOCTL_DVD_READ_STRUCTURE`, `DvdCopyrightDescriptor`; libdvdcss reads the same byte) whether it uses CSS; if not, both are skipped. `WindowsCssDrive.ReadCopyrightProtectionType`, `DriveUnlocker.IsCssProtected` (null when it can't be asked, e.g. a folder on a hard disk: the search runs as before), `FindKey(…, discProtected)`. Probe `copyright <drive>` prints it: Gypsy "not protected".
	Test (owner: "make sure you add a test so we don't regress"): `KeySearch_DiscSaysUnprotected_ReadsNothing` (a fake disc that fails on any read) fails without the fix; plus a test that an unknown, unscrambled source is scanned and gives no key. Real check: Gypsy's first cell (290 MB) ripped in 66 s, reading from the start.

## 2026-11-18 23:13
- **Owner remark:** I had said each test disc takes a few minutes, then started a 20-minute full-title rip without saying so. **Answer:** the quick check (scan, a short piece, compare) is a few minutes; I switched to the full title as the stronger check after the audio-gap change without asking. The owner chose to let it finish; the other three discs get the quick check, and I ask before longer rips.
- **Bug found (G4.1, Gypsy):** the full CLI rip of title 1 failed after 8 minutes: "Sector does not start with an MPEG-2 pack header". New probe `checkpacks <drive> <vts> <from> <to>`: cell 7 holds two runs of all-zero sectors (1,127,972-1,127,983 and 1,129,889-1,129,903, 27 sectors), read without an error. Only the opening had been ripped before. MakeMKV rips the title.
- **Fix:** `TitleSectorReader` drops sectors that hold no MPEG-2 pack (adjacent ones joined into one skipped stretch) and continues with the next sector; they count as skipped and the rip warns "damaged or empty spot on the disc" with the time, like an unreadable stretch.
	Test `SectorsWithoutMpegData_AreDroppedAndCounted` (fails without the fix). The test sector source now serves pack headers by default.

## 2026-11-18 23:27
- **Finding (Gypsy vol. 1, G4.1):** the second full rip failed in its first minute with "MPEG-1 pack header" on a sector the first run had read fine, and a re-scan of the opening (0-300,000) was clean. Re-scanning cell 7: the bad sectors move between reads (zero runs at other places, garbled headers such as 00 A2 01 BA); 6,206 of the 20,000 sectors 1,120,000-1,140,000 were bad. The drive returns corrupted data there without reporting an error.
	**Conclusion:** the disc (or the drive) can no longer be read reliably in that area; not a ripper bug. The owner's MakeMKV copy is from 2020, so the disc has probably picked up dirt or a scratch since. Asked the owner to inspect and clean it; if it still fails, Gypsy is set aside (the owner has a good copy) and G4.1 continues with the other discs.
	Probe `checkpacks` now also checks the MPEG-2 marker bits, as the parser does. Open: sectors that are corrupted but still look like valid packs cannot be detected by the reader; they become glitches (MakeMKV has the same limit when a drive reports no error).

## 2026-11-19 05:58
- **Decision (owner):** Gypsy vol. 1 is set aside; the owner believes the disc is degraded (cell 7 reads back different corrupted data on each pass, see 2026-11-18 23:27). Its opening rip, the B2 fix and the dropped-sector fix stay tested; the owner keeps the 2020 MakeMKV copy. G4.1 continues with the other three discs.

## 2026-11-19 06:08
- **G4.1 (part), A Very Long Engagement disc 1:** IFO files copied to `reference/ifo/A_VERY_LONG_ENGAGEMENT_DISC1/` (git-ignored); test `AVeryLongEngagementTests` (7 titles; movie: 39 chapters, audio, 4 subtitle tracks, CC, 8,000 s; titles 1 and 5 at least 120 s). Scan in the CLI and the GUI as before. Trailer (title 5, 2:05) ripped in test mode by the GUI (34 s) and the CLI (32 s): identical files. Screenshot checked.
- **Finding:** compared with MakeMKV's file, the trailer's video payload was identical but the ReferenceBlock values differed: in every other GOP one B picture was 1 ms earlier (500 vs 501 ms; 125 of 3,000 pictures). Yesterday's rip had the same, so it is not a regression. The earlier "same timestamps" for the movie (12:10) was too strong: `mkvcompare` only compared the first and last frame time.
	**Cause:** in an open GOP the B pictures are placed before the I picture that carries the PTS. 5 fields (3:2 pulldown) are 7,507.5 ticks of 90 kHz; MakeMKV uses whole ticks (7,507), so its B lands half a tick later, which here decides the rounding to whole ms.
	**Fix:** picture offsets within a GOP are whole 90 kHz ticks, truncated (`VideoFrame.FieldOffsetNs`); only odd field counts change, so content without pulldown (Dead Zone) is unaffected. Result: all 3,000 video times and ReferenceBlocks identical to MakeMKV's; only Duration remains 1 ms off (accepted). Test `Video_PictureBeforeTheAnchor_IsPlacedInWhole90kHzTicksLikeMakeMkv` fails without the fix.
	The 1 ms earlier frames after the angle join on Beauty and the Beast (22:22) may be the same cause; to re-check with B1.
- **Step:** probe `mkvcompare` also compares every frame time (SHA-256 of the times, laced frames at DefaultDuration steps): it flags the old rip and passes the fixed one.

## 2026-11-19 06:29
- **G4.1 (part), Lost in Translation:** IFO files copied to `reference/ifo/LOST_IN_TRANSLATION/` (git-ignored); test `LostInTranslationTests` (23 titles; movie 16: 25 chapters, AC3/DTS/AC3, 3 subtitle tracks; title 4 is "play all" of 5-9; same ten titles of at least 120 s as MakeMKV 2.0 lists). The first scan after inserting took about 2.5 min (the drive was busy reading the fresh disc; even Windows' own drive query waited).
	The movie's first three cells (24-26, 8:29) ripped by the GUI (test mode, with the mid-rip screenshot) and the CLI in about 2 min 15 s each: identical files.
	Title 9 (2:17) ripped by MakeMKV 2.0 (`makemkvcon64 mkv disc:0 2`) and by us: same video frames, times and ReferenceBlocks. Differences: our video keeps ~125 zero bytes the disc has after each GOP header (MakeMKV removes them; found with the new probe option `mkvinfo -saveblock T N file`, N = -1 saves the whole track); our AC3 track has 2 frames more at the end. ffmpeg decodes both of our files (video, AC3, DTS) with no errors.
- **Owner decision:** "we don't have to match makemkv exactly as long as the video plays." So the zero padding and the extra audio frames are recorded as accepted differences, not investigated further; the bar for test discs is clean decoding and correct tracks, times and chapters. Added to the plan's design decisions.

## 2026-11-19 06:32
- **G4.1 done, Dead Zone S4 D1** (the last test disc): scan in 7 s; IFO files copied to `reference/ifo/DEAD_ZONE_S4_D1/` (git-ignored); test `DeadZoneTests` (14 titles, 8 of at least 120 s, episode chapters/audio/CC, two audio tracks on the extras). Extra title 6 (2:19) ripped by the GUI (test mode) and the CLI: identical files, and identical in every compared property (incl. every frame time) to MakeMKV's `B2_t04.mkv`; ffmpeg decodes it with no errors. Kept short at the owner's request (no long scans).
	G4.1 is complete: A Very Long Engagement, Lost in Translation and Dead Zone re-checked; Gypsy set aside (degraded disc). G4 removed from Next steps.

## 2026-11-19 06:38
- **Owner decision:** G7.4 (real damaged-disc test) moved to Future: the owner has no damaged disc.
- **B1 (chapter times, Beauty and the Beast title 16), checked with the disc in the drive:** new probe `chapters <drive> <title> [-stream]` lists the title's cells (angle block, flags, IFO time, start on the title timeline) and with `-stream` the start measured from two nav packs per cell (first VOBU start, last VOBU end; 20 s for the title).
	Against MakeMKV's chapters: from the IFO times 13 of 23 within 1 ms, the others +-17 to 100 ms; from the stream 16 within 1 ms, the others off by +83 (4), -34 (2) or +17 (1) ms. The differences are whole video fields (16.7 ms), so MakeMKV seems to use the first picture of each chapter, but neither timeline reproduces it. The errors do not add up, so no chapter is off by more than about 0.1 s.
	**Decision:** under the owner's rule ("we don't have to match makemkv exactly as long as the video plays"), chapter starts within ~0.1 s are accepted; no ripper change (the stream timeline would mean rewriting the header at the end for a partial gain). B1 removed from Next steps; the difference is listed in the plan's accepted differences.
	Not re-checked: whether the half-tick fix also removed the 1 ms earlier frames after the angle join (needs the 20-minute rip; skipped, the owner is short on time).
- **Next steps is now empty:** the DVD work planned so far is done; the next project is the owner's choice from Future.

## 2026-11-19 06:41
- **Owner decision:** Blu-ray moves from Future into Next steps, with a full plan (BR1-BR6): probe the drive and disc (incl. why MakeMKV fails with this drive), parse playlists/clip info into a title list, AACS decryption with a per-disc key from a `KEYDB.cfg` file the owner supplies, transport-stream demuxing, MKV output for H.264 / DTS-HD / TrueHD / AC3 / PGS first, then CLI and GUI.
	**Decisions (in the plan):** read the files through Windows' UDF file system (as MakeMKV and libbluray do); one title per playlist (as MakeMKV); the program never downloads keys; UHD, BD+, 3D and menus out of scope for now. No MakeMKV reference rips exist, so the bar is a file that plays (ffmpeg) with duration, chapters and tracks matching the playlist.
	Main risk: discs with AACS bus encryption need the drive's authentication even with the key; BR1 shows whether that applies.
- Status line: 63 unit tests (was 55).

## 2026-11-19 07:17
- **Owner:** switched to branch `blu-ray`; unplugged the DVD drive and connected a Blu-ray drive; inserted The Goonies (Blu-ray). Owner left me to work alone: long operations allowed, commits on `blu-ray` only. The owner put a `keydb.cfg` key file in `%LOCALAPPDATA%\aacs\` (so the program looks there as well as in `%APPDATA%\aacs\`, libaacs's place).
- **BR1 done (probe the drive and the disc):**
	New `RipDvd.Core/Bluray/WindowsScsiDrive` (INQUIRY and GET CONFIGURATION through IOCTL_SCSI_PASS_THROUGH) and probe commands `bddrive <drive>` and `bdfiles <drive>`.
	Drive: HL-DT-ST BD-RE BP50NB40 (USB), firmware 1.02 (dated 2019-05-03); reads BD-ROM; AACS feature: version 1, bus encryption capable, binding nonce.
	Disc THE_GOONIES: 24 playlists, 22 clips, 47.9 GB of streams (two big clips, 25.6 and 20.5 GB); BD-J menus. AACS: MKB version 8, one unit key, disc ID CD9BD1A5…D8A2A8B4; bus encryption not enabled in either content certificate. Every stream unit is marked encrypted (copy-permission bits 3, only the clear first packet's sync byte readable). Stream files read fine through the Windows file system without any drive authentication.
	Key file: has an entry for this disc (VUK and unit key). So decryption needs only the key file.
	**Why MakeMKV "never worked":** `makemkvcon -r info disc:0` says Blu-ray support is shareware with a 30-day evaluation, asks whether to start it, and (unattended: no) ends with "Failed to open disc". Not a drive problem. I did not start the evaluation (owner's choice). Side note: MakeMKV sent a firmware dump of the drive "for analysis" on its own.
	Metadata copied for tests to git-ignored `reference/bdmv/THE_GOONIES/` (PLAYLIST, CLIPINF, index/MovieObject, AACS .inf/.cer).

## 2026-11-19 07:19
- **BR2 done (disc structure and title list):** `RipDvd.Core/Bluray/Mpls` (playlists: play items with clip, in/out time, connection condition, angles, the STN streams with PID, codec, format/rate, language; marks), `Clpi` (clip presentation time ranges, for sizes), `BdScanner` (one title per playlist, numbered by the playlist file: duration, chapters from the entry marks, size as each item's share of its clip file, video/audio/subtitles, duplicates = same clips and in/out times). Probe `bdtitles <drive> [-v]`.
	The Goonies: 24 playlists, no decoys. The movie (1:53:55, VC-1 1080p 23.976) is in six playlists over two clips: 00000/00026/00031 play clip 00113 (25.6 GB: AC3 5.1, TrueHD, 4 more AC3 tracks, 14 PGS streams), 00008/00016/00030 play clip 00112 (20.5 GB: AC3 stereo only, 7 PGS). Extras are VC-1 480i; two tiny clips are MPEG-2.
	**Consequence:** the first codecs to build are VC-1 (not H.264 as planned), AC3, TrueHD and PGS.
	**Decision:** Blu-ray titles are named by playlist number (`mkv E: 0`, file `<Disc> - playlist 00000.mkv`), as the DVD side uses the disc's own title numbers; reason: stable and traceable to the disc. Chapters: entry marks; a mark within 1 s of the end is dropped.
	Tests `GooniesBdTests` (on reference/bdmv/THE_GOONIES) and a made-up two-item playlist for chapter times.

## 2026-11-19 07:22
- **BR3 done (AACS decryption with a key file):** `RipDvd.Core/Bluray/Aacs.cs`: `KeyDb` finds `KEYDB.cfg` next to the program, in `%APPDATA%\aacs` or `%LOCALAPPDATA%\aacs` and reads the disc's line (disc ID = SHA-1 of AACS/Unit_Key_RO.inf; fields V = VUK, U = unit keys); `AacsDecryptor` takes the listed unit keys, or decrypts Unit_Key_RO.inf's keys with the VUK, and decrypts 6,144-byte aligned units (clear first 16 bytes; AES-128 CBC with key AES(unit key, first 16) XOR first 16 and the AACS IV; checked by the 0x47 sync byte of all 32 packets; copy-permission bits cleared; with several unit keys the one that gives valid packets is used). Clear errors when the key file or the disc's entry is missing; no AACS folder = not encrypted.
	Probe `bdkey <drive> [clip]` (prints no keys). The Goonies: the unit key derived from the VUK equals the listed one; 201 sample units over the whole 25.6 GB movie clip all decrypt to valid packets.
	7 unit tests with keys and data made up here (round trip, key choice, wrong key, clear units, VUK path, key-file line, missing entry).

## 2026-11-19 07:40
- **BR4 and BR5 (first version): Blu-ray ripping works end to end on The Goonies.**
	`Bluray/TsDemuxer` (188-byte TS packets of the wanted PIDs joined into PES packets; PTS/DTS; the PES stream ID extension, which tells TrueHD units 0x72 from their AC3 core 0x76 in the same PID), `Bluray/BdCodecs` (TrueHD framer: unit length from the header, rate and channels from the major sync, 1/1200 s per unit; E-AC3 framer; H.264 Annex B to length-prefixed NAL units and avcC; VC-1 sequence/entry-point headers, coded/display size with the aspect-ratio code, BITMAPINFOHEADER "WVC1" CodecPrivate; PGS display sets; Blu-ray LPCM to little-endian), `DtsFramer(keepExtension)` keeps the DTS-HD extension substream with its core frame (as MakeMKV), `Bluray/BdTitleRipper` (reads each play item's clip in 32-unit reads, decrypts, demuxes; title time = earlier items + (PTS - item in time); tracks: video, audio of the first item's STN table, TrueHD as two tracks (TrueHD and its AC3 core, named so), PGS; chapters from the marks; GOP-wise writing with the DVD cluster rule; unreadable units retried 3 times then skipped with a warning). Probes `bdrip <drive> <playlist> <out> [-mb N]` and `bdpes <drive> <clip> <pid>`.
	Playlist 15 (2:41 extra, VC-1 480i + AC3): 12 s; ffmpeg decodes it with no message. Movie (playlist 0), first 1.5 GB (7 min): VC-1 1080p 16:9, AC3 5.1, TrueHD 5.1 + AC3 core, 4 more AC3 tracks, 14 PGS tracks, 37 chapters; decodes with no error.
- **Bugs found and fixed on the way** (each with a unit test where one can catch it):
	- VC-1 is a "VfW" Matroska track, whose block times ffmpeg reads as decode times: writing presentation times gave thousands of "non monotonically increasing dts" messages and frames without times. Now: decode time (the PTS when a PES has no DTS) + the decoder delay of the first key frame (its PTS - DTS), so times rise in file order and key frames keep their presentation time.
	- PGS: a PES packet can hold several segments (the END segment rides after the object segment): segments are now walked one by one. Some "clear the screen" sets come without any END segment and waited for the next composition up to 33 s later, beyond what a cluster-relative block time can reach (the first full rip stopped with "Block -33032 ms from cluster"). Open sets are now closed 2 s after they start, with an END segment added. Subtitle sets are written in time order; any still impossible to place would be dropped and counted in a warning.
	- The STN table lists picture-in-picture subtitles apart from the normal ones; they are now kept apart (not ripped) instead of being read as normal subtitles. (The Goonies has none: its 14 PGS streams are all normal.)
	The MkvWriter error for a block out of cluster range now names the track and times.

## 2026-11-19 07:41
- **BR6 (part): the CLI rips Blu-rays.** `rip-dvd info <disc>` and `mkv <disc> <playlists|all> <folder>` detect a Blu-ray (BDMV/index.bdmv) and work on playlists: `info` lists them longest first by number with length, chapters, size, video, audio, subtitles, "same video as playlist N", angles, and whether the disc is AACS-encrypted and which key file would be used; `mkv E: 0 <folder>` writes `The Goonies - playlist 00000.mkv`; `all` = playlists at least --minlength long (`--no-duplicates` skips repeats); the language, chapter, name, overwrite and dry-run options work as for DVDs. `RipJob` takes Blu-ray items (`RipJobItem.ForPlaylist`), so progress, speed and remaining time are the same as for DVDs. Help texts updated (formats supported, key file locations, no UHD/BD+).
	Checked: `info E:` on The Goonies; `mkv E: 15 <folder> --dry-run` and a real rip of playlist 15 through the CLI.

## 2026-11-19 07:43
- **BR6 (part): the GUI shows and rips Blu-rays.** On scan, a Blu-ray's playlists fill the title list (longest first; the Title column is the playlist number; checked at first: at least 120 s and not a repeat; notes "same video as playlist N"); the cell area is hidden (no cell map on Blu-ray); the file-name hint says "- playlist NNNNN.mkv"; Start rips the checked playlists through the same job and progress panel; the status line warns when the disc is AACS-encrypted and no key file was found. Audio and Subtitles columns are capped in width with "…" and a tooltip (Blu-ray track lists are long). Screenshot `docs/gui-bluray-goonies.png`.

## 2026-11-19 07:46
- **BR8 (part): formats not on The Goonies, tested with a stream made by ffmpeg.** New probe `bdclip <file.m2ts> <out.mkv> <pid=coding>...` rips a loose .m2ts through `BdTitleRipper` with a one-item playlist made up in memory (new constructor taking an `Mpls`). Test stream (ffmpeg, `-f mpegts -mpegts_m2ts_mode 1`, 20 s): H.264 1080p with B-frames, E-AC3 5.1, DTS 5.1, Blu-ray LPCM stereo, AC3.
	Result: ffmpeg decodes every track with no message; first and last packet times of each track equal the source's (to the ms), except DTS, whose last frame is missing.
	**Bug fixed:** the LPCM track's header said the channel count guessed from the playlist (6) while the samples were stereo; channels, rate and bit depth are now taken from the first LPCM packet before the header is written.
	**Known, left as is:** the last DTS frame of a title (10.7 ms) is dropped, because the framer waits for an extension substream that may follow it.
	Not covered by this test: DTS-HD extensions (ffmpeg writes core only), E-AC3 dependent substreams, TrueHD from another encoder.

## 2026-11-19 08:16
- **BR7.1 done: the whole movie.** The Goonies playlist 00000 ripped with probe `bdrip` in 19 min 50 s (25.6 GB read, about 21 MB/s): 23.0 GB file, 1:53:55.36, no warnings. ffmpeg decodes the video and all 7 audio tracks with no message at all; every audio track ends at 6,835.33 s (the title is 6,835.36 s), so the sound stays in sync to the end; 37 chapters. The file is in the session scratchpad for the owner to play (BR7.2).

## 2026-11-19 17:50
- **Question answered: program name.** Owner: the program is now **LunaDR**.
	Done: the CLI is `lunadr.exe` and the GUI `lunadr-gui.exe` (not `LunaDR.exe`: on case-insensitive Windows it would clash with `lunadr.exe` in one folder);
	help texts, window and message-box titles, and the MKV "writing application" (`LunaDR 0.1`) use the new name; README and plan updated.
	GUI settings move to `%APPDATA%\LunaDR\gui-settings.json`; the old `%APPDATA%\rip-dvd` file is read until the new one is saved, so the owner's folder carries over.
	The repo, solution, projects and namespaces keep `rip-dvd` / `RipDvd.*` for now: renaming them touches every file and no user sees them.
- **Question deferred: MakeMKV evaluation.** Owner: "not now". Moved to the deferred questions.

## 2026-11-19 18:10
- **Question answered: TrueHD's AC3 core.** Owner: make it an option, on by default.
	Done: `RipOptions.TrueHdCore` (default true); CLI `--truehd-core=on|off`; GUI checkbox "AC3 copy of TrueHD" next to "Play a sound when done", shown only for a Blu-ray and remembered in the settings.
	Test `GooniesBdTests.TrueHdCore_IsATrackUnlessTurnedOff`: playlist 00000 has 7 audio tracks by default and 6 with the option off. GUI screenshot with The Goonies checked: the box shows and is checked.
- **BR7 done.** Owner played the Goonies rip: "It looks great!" Removed from Next steps.

## 2026-11-19 18:30
- **GUI: compact title rows** (owner: rows were taller than needed, with empty space under the text). The Fluent theme's minimum heights for the row and the checkbox are set to 0,
	and cell contents are centered vertically: rows are about 25 px instead of 33, so 12 titles fit where 9 did (screenshot with The Goonies).

## 2026-11-19 19:05
- **Blu-ray segment map in the GUI** (owner: a table like the DVD cell map for Blu-rays). A Blu-ray has no cells: a play item is a clip with an in and out time,
	and playlists can use different parts of one clip. New `Bluray/BdSegmentMap` cuts each clip at every play item start and end and every chapter start;
	each piece is a "segment" and becomes a cell of the existing `CellMap` (new `CellInfo.Clip`/`ClipIn`/`ClipOut`, `CellMap.Bluray`), so `CellMapView` draws it unchanged,
	grouped by clip (a clip's name is shown where there is room), with Blu-ray hover texts. `BdScanner.Scan` returns it as `Segments`, rows longest first like the title list.
	`Mpls.EntryMarks()` now holds the chapter-mark rule shared by `ChapterStartsNs` and the map.
	"Rip selected cells" stays DVD-only (disabled for Blu-rays; the hint says so). During a rip the playlist's segments fill by the share read, since the Blu-ray ripper reports by play item.
	The Goonies: the movie's two versions are two clips (00113 for playlists 0/26/31, 00112 for 8/16/30), each cut into 37-38 chapter segments; extras are small clips of their own.
	Tests: a synthetic map (partial clip use, chapter cuts, an unplayed gap left out) and The Goonies (duplicates share segments, 37 chapter starts, total length = playlist length).
	Screenshot: docs/gui-bluray-segments.png.

## 2026-11-19 19:20
- **GUI one notch smaller** (owner: font and window too big). Text 14 → 12 (the Fluent theme's `ControlContentThemeFontSize` resource in App.xaml, as its control styles read it, plus the window's FontSize),
	disc name 20 → 18, map labels 11/10 → 10/9, starting window 1280×900 → 1120×780; the Audio and Subtitles columns' maximum widths 360/200 → 290/170 so the Note column fits.

## 2026-11-19 19:40
- **GUI: title list and cell map on two tabs** (owner: work with one table at a time, smaller window). Tab "Titles" (default) holds the title list with Select all/None;
	tab "Cell map" holds the map with its legend, hover line and Rip selected cells/Clear, and is hidden when there is no map (the view goes back to Titles then).
	The splitter is gone; the status line and the whole ripping area stay below the tabs. Starting window 1120×780 → 1120×640.
	Test mode: `--tab cells` shows the map tab (`--cells` does so by itself). Screenshots of both tabs with The Goonies checked.

## 2026-11-19 21:44
- **Bug: the time left counted down about 5x too fast** (owner, watching a Blu-ray rip). Measured in the running GUI on Lord of the Rings: Return of the King (playlist 00100, 38 GB):
	the time left fell 32 s per 11 s of clock (43:01 → 42:29), the shown speed was 13.1 → 13.4 MB/s.
	Cause: the time left was the data still to read ÷ the average speed of the whole job. The drive reads slowly at first and faster later, so the average keeps rising,
	and a rising divisor shrinks the estimate a second time (here about 2 s per second on top of the 1 s for the data read). Not specific to Blu-rays; DVDs had it too.
	Fix: new `SpeedMeter` (`Ripping/SpeedMeter.cs`): the speed is measured over the last 60 s of reading (the whole of it while less has passed; nothing for the first 3 s).
	Tests `SpeedMeterTests`: after a slow start (5 MB/s for 2 min, then 17 MB/s) the meter's estimate falls exactly 60 s per 60 s, the old average fell 240 s per 60 s;
	zero before 3 s; samples older than a minute are forgotten.
	Not fixed, and not a bug: if the drive really speeds up steadily, a time left based on the current speed still falls a little faster than the clock.

## 2026-11-20 02:05
	The owner pasted a list of MakeMKV advanced features and asked which LunaDR lacks. Written up as [makemkv-feature-gap.md](makemkv-feature-gap.md) (done / partial / missing / out of scope per feature, with a proposed order) for later reference and future features.

## 2026-11-21 16:46
	Owner asked to finish the rename. Done: `RipDvd.slnx` -> `LunaDR.slnx`; projects and folders `RipDvd.Core/Cli/Gui/Core.Tests` -> `LunaDR.*`; namespaces, `InternalsVisibleTo`, project references, VS Code tasks/launch, README and CLAUDE.md updated (old entries in this log keep the old names).
	The settings folder was already `%APPDATA%\LunaDR` (no `rip-dvd` folder was left on this machine), so the code that fell back to `%APPDATA%\rip-dvd` was removed; GUI settings are read only from `%APPDATA%\LunaDR`.
	Only the repo folder is still named `rip-dvd` (the owner renames it). Build 0 warnings, 90 tests pass, format check clean.

## 2026-11-21 16:54
	Owner asked to move the files the tests use under tests/. `reference/ifo/` and `reference/bdmv/` (git-ignored, optional: their tests skip when missing) are now `tests/data/ifo/` and `tests/data/bdmv/`; the test code, .gitignore, CLAUDE.md and the plan point there. Nothing else under reference/ is read by tests. 90 tests pass.

## 2026-11-21 16:59
	Tidy-up at the owner's request: removed from git `reference/cellmaps/` (unused), `docs/gui-phase1-butterfly-effect.png` and `docs/gui-bluray-goonies.png` (superseded screenshots); untracked `reference/makemkv/` (kept on disk, now git-ignored: it lists the owner's discs and contains a local path); linked makemkv-feature-gap.md from the plan. Fixed a stray carriage return in this log that made git treat it as binary (f7412a4).
