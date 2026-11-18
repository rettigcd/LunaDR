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
