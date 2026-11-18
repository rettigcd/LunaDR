# Current plan

Process rules for this file are in [CLAUDE.md](../CLAUDE.md).

## Status
- **Ripping works end to end.** Main CLI `src/RipDvd.Cli` (executable `rip-dvd`), modeled on makemkvcon:
	`rip-dvd info [disc]` lists titles (MakeMKV-style ids over titles ≥ 120 s);
	`rip-dvd mkv <disc> <id|all> <folder>` writes `title_tNN.mkv`. Disc = `disc:0`, `E:` or a folder. Option `--minlength=N`.
- **Fourth test disc** Gypsy (2004) vol. 1 (LPCM, not CSS-protected): 16-bit LPCM → A_PCM/INT/LIT matches the owner's MakeMKV rip (log 12:58).
- **Third test disc** `LOST_IN_TRANSLATION` (DTS): needed CSS drive authentication (now done, Windows) and DTS audio (now done); the movie's opening matches the owner's MakeMKV rip on the Plex drive (log 12:34).
- **Second test disc** `A_VERY_LONG_ENGAGEMENT_DISC1` (film, 2:13:20, dual layer, French audio, 4 VobSub subtitle tracks, CC): the movie matches MakeMKV's file on every track, including all 4 subtitle tracks (log 12:10). Trailer matches too, with MakeMKV's rules for dropping empty tracks and single chapters.
- Output checked against MakeMKV on the first test disc: episode 1 and extra 6 have identical video/audio bytes, frames, durations, chapters; the CC→text track matches in text (timing within a few ms).
	The files play (owner) and decode with 0 errors (ffmpeg). Speed: at least as fast as MakeMKV (title 2: 341 s vs 434 s).
- Repo created. Process documents (CLAUDE.md, this plan, log) in place.
- Probe console app `src/DvdProbe` (net10.0): commands `drives`, `files`, `titles [-v]`,
	`scramble`, `crack` (old), `key`, `duration`, `bench`, `demux`, `mkvinfo [-dumptrack]`, `rip`.
- Demuxing works: `Mpeg/ProgramStreamParser` + `Dvd/TitleSectorReader` produce the same
	video/audio bytes MakeMKV puts in its MKV (checked on title 6). Key finding is now fast
	(`Css/TitleKeyFinder`, 0.1–0.4 s per title set).
- `Matroska/EbmlReader` reads MKV files (probe `mkvinfo`); MakeMKV reference rips are in
	git-ignored `reference/rips/makemkv/`.
- CSS works on the test disc: `RipDvd.Core/Css` recovers each title set's key from the
	scrambled data (no drive authentication needed) and descrambles sectors. All 14 title
	sets cracked and verified. `RipDvd.Core/Dvd/VobReader` reads a title set's VOBs as one
	run of sectors.
- Library `src/RipDvd.Core` started. `Ifo/` parses VIDEO_TS.IFO (title table) and
	VTS_xx_0.IFO (stream attributes, chapters, program chains, cells).
- `titles` output matches MakeMKV on the test disc for: title list, cell counts, chapter
	counts, sizes (exact bytes), which titles are under 120 s, audio track counts, aspect
	ratio, and which titles have closed captions. MakeMKV's title-list durations are ~6 s
	shorter on episodes, but its MKV files use the true stream duration (which we match).
	Not yet matching: the file name prefix.
- Test disc available: drive E:, volume `DEAD_ZONE_S4_D1` (TV episodes, 14 title sets).
- MakeMKV is installed at `C:\Program Files (x86)\MakeMKV` and can be used as a reference.
- MakeMKV reference output for the test disc saved in `reference/makemkv/` (default 120 s
	minimum and `--minlength=0`). MakeMKV keeps 8 of 14 titles: 4 episodes (title sets 1–4,
	42–43 min) and 4 extras (title sets 6–9, 2–3.5 min).

## Design decisions
- Language/runtime: C# on .NET 10.
- Output container: Matroska (.mkv), as MakeMKV.
- Default: keep all video, audio and subtitle tracks and the chapters (MakeMKV default).
- Default minimum title length: 120 seconds; shorter titles are skipped (MakeMKV default).
- Output file naming follows MakeMKV: `<prefix>_t<NN>.mkv`, where `NN` is the position in the
	list of kept titles (starting at 00). Until MakeMKV's prefix rule (`C1`, `B2`, ...) is worked out, the prefix is `title` (MakeMKV's usual name when it has no better one).
- Self-contained: no ffmpeg or other external tools at run time (MakeMKV does everything itself).
- DVD only; Blu-ray is out of scope.
- A title's size = sum of its cells' sectors × 2048 (matches MakeMKV exactly).
- MKV duration = measured stream duration (pictures + pulldown), as in MakeMKV's files.
- MKV layout copies MakeMKV's (details in log 2026-11-18 10:22); streams are copied
	unchanged ("lossless"), timestamps shifted so the title starts at 0.
- A stream is real only if the title's program chain marks it "available"; the stream
	counts in the VTS attribute table are not trusted (the test disc declares 8 audio and
	32 subtitle streams but uses 1–2 audio and none).
- CSS: recover title keys with libdvdcss's known-plaintext attack ("AttackPattern") on the
	scrambled sectors, take the key that several sectors agree on, then descramble. Drive
	authentication (and the player-key / disc-key route) is only added if a disc or drive
	needs it.
- CSS is ported to C# rather than calling libdvdcss: readable, self-contained like MakeMKV,
	no native DLL to build/ship per OS. If a disc or drive defeats the C# code, add libdvdcss
	as an optional fallback behind the same interface.
- Closed captions (EIA-608 in the MPEG-2 video user data) become a text subtitle track
	when the VTS video attributes have the line-21 flag set, as MakeMKV does ("CC→Text").
- Command-line app first. A MakeMKV-style GUI comes later and is secondary, so keep the ripping
	logic in a library the GUI can reuse.
- License: the program is GPL (v2 or later) because the CSS code is ported from libdvdcss.
	GPL-derived files: `src/RipDvd.Core/Css/Css.cs`, `src/RipDvd.Core/Css/CssTables.cs`, `src/RipDvd.Core/Css/CssAuthentication.cs`; each
	starts with a "LICENSE NOTE" block. Any further code ported from GPL sources must get the
	same note and be added to this list and to those notes.
- Reading the disc: through the Windows file system (`E:\VIDEO_TS\*.VOB`) for now.
- All C#, no native C code. Measured: descrambling runs at 430 MB/s on one core and key
	recovery uses ~6 ms CPU per 2000 sectors, while a DVD drive delivers at most ~22 MB/s.
	The drive is the bottleneck; if a hot spot appears, optimize it in C# (Span, SIMD,
	parallelism) before considering native code.

## Platform (Windows 11 now, Linux possible later)
Target is Windows 11. Keep Linux possible by putting OS-specific code behind small
interfaces in `RipDvd.Core`, with one implementation per OS.

| Area | Status | Linux impact |
|---|---|---|
| .NET 10, C# | portable | none |
| IFO parsing, CSS, demux, MKV writing | portable (pure C#, byte arrays/streams) | none |
| Locating the disc (`E:\VIDEO_TS`, `DriveInfo`) | Windows-style paths in the probe | Linux mounts at e.g. `/media/<user>/<label>`; device is `/dev/sr0`. Take a path or device argument; don't assume drive letters. |
| Reading scrambled sectors via the file system | works after drive authentication | Same on Linux once authenticated. |
| Drive authentication | Windows done: `Dvd/WindowsCssDrive` (`DeviceIoControl`, `IOCTL_DVD_*` on `\\.\E:`) behind `Css/ICssDrive` | Linux: implement `ICssDrive` with `ioctl` `DVD_AUTH`/`DVD_READ_STRUCT` on `/dev/sr0`. |
| GUI (future) | not chosen | WinForms/WPF are Windows-only; MAUI has no Linux desktop. Use **Avalonia** if Linux matters. |
| Notification-sound hook | Claude Code settings only, not part of the app | none |

## Next steps
1. Matroska polish: ReferenceBlock values are a placeholder (-1); we write more clusters than MakeMKV (291 vs 270 on title 6). Files already play (owner) and decode cleanly (ffmpeg).
2. CLI polish: rip all titles of this disc with `rip-dvd mkv E: all` as an end-to-end test; MakeMKV-style output naming prefix (step 5); options for track selection later.
3. Audio still missing: 20/24-bit LPCM (16-bit done) and MPEG audio (no disc in the owner's library has it). Worth testing: a DTS-ES 6.1 disc (Se7en, Dirty Dancing, The Mask, X-Men: The Last Stand, Butterfly Effect DC).
4. Drive authentication on Linux (`ioctl` DVD_AUTH on /dev/sr0) behind `ICssDrive`. Windows is done (`WindowsCssDrive`).
5. Work out MakeMKV's file name prefix (`C1`, `B2`, ...). It is MakeMKV's per-title
	"comment" attribute (TINFO id 49). Guess: derived from the menus that link to each title.

## Future (desirable, not scheduled)
- GUI similar to MakeMKV's, on top of the same library (secondary to the CLI).
- Episode names from thetvdb.com: look up the series/season (e.g. from the volume label
	`DEAD_ZONE_S4_D1` → The Dead Zone, season 4, disc 1), match the ripped titles to episodes
	(by order and running time), and name the output files with the episode titles.

## Open questions
