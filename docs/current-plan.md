# Current plan

Process rules for this file are in [CLAUDE.md](../CLAUDE.md).
This file describes the present state only; the history is in [log.md](log.md).

## Status
- **Ripping works end to end on Windows.** CLI `src/RipDvd.Cli` (executable `rip-dvd`), modeled on makemkvcon:
	`rip-dvd info [disc]` lists titles (MakeMKV-style ids over titles ≥ 120 s, with audio, subtitle and CC info);
	`rip-dvd mkv <disc> <id|all> <folder>` writes `title_tNN.mkv`. Disc = `disc:0`, `E:` or a folder; option `--minlength=N`.
- **Verified against MakeMKV on five discs** (byte-level comparisons with `mkvinfo`, plus ffmpeg decoding with no errors; the owner has played the files):

	| Disc | What it tested | Result |
	|---|---|---|
	| DEAD_ZONE_S4_D1 (TV) | AC3, CC→text, STC reset in a cell | episode and extra identical to MakeMKV (captions within a few ms) |
	| A_VERY_LONG_ENGAGEMENT_DISC1 (film) | 4 VobSub tracks, pulldown, dual layer, empty tracks | movie identical on every track |
	| LOST_IN_TRANSLATION | DTS; fresh disc needing drive authentication | opening identical to owner's MakeMKV rip |
	| Gypsy (2004) vol. 1 | 16-bit LPCM; disc without CSS | opening identical to owner's MakeMKV rip |
	| BUTTERFLY_EFFECT | DTS-ES 6.1 | opening identical to owner's MakeMKV rip |

- **Full-disc test passed** (CLI `mkv E: all` on DEAD_ZONE_S4_D1, 8 titles, 30 min): the 3 titles with MakeMKV rips match on every compared property, including SHA-256 of all video/audio/caption payloads (one title: two caption times 1 ms off); all 8 decode with ffmpeg (log 14:34). Compare tool: probe `mkvcompare`.
- Speed: drive-bound and at least as fast as MakeMKV (e.g. 6.4 GB movie in 1,583 s vs MakeMKV 1,836 s).
- Library `src/RipDvd.Core`:
	`Ifo/` (IFO parsing), `Css/` (descrambling, title-key recovery, drive authentication), `Dvd/` (VOB reading with read-ahead, nav packs, Windows drive commands),
	`Mpeg/` (PES parsing; MPEG-2 video, AC3, DTS, LPCM framers; subpicture assembler), `Captions/` (EIA-608 decoder),
	`Matroska/` (EBML reader/writer, MKV writer), `Ripping/` (disc scanner, title ripper, VobSub header, languages).
- Probe `src/DvdProbe`: `drives`, `files`, `titles [-v]`, `scramble`, `crack` (old), `key`, `duration`, `bench`, `demux`, `mkvinfo [-dumptrack]`, `rip [-cells N]`.
- References: MakeMKV output listings in `reference/makemkv/`; reference rips in git-ignored `reference/rips/`; the owner's Plex library (D:) holds MakeMKV rips of many of the owner's DVDs.

## Design decisions
- C# on .NET 10, all managed code (no native C). The drive is the bottleneck (descrambling 430 MB/s vs ≤ 22 MB/s from a drive).
- Self-contained like MakeMKV: no external tools at run time. ffmpeg is used only as a development check.
- DVD only; Blu-ray is out of scope. Command-line app first; a GUI later on top of the same library.
- Output: Matroska, laid out like MakeMKV's files (log 10:22, 12:10); streams copied unchanged except where MakeMKV converts:
	LPCM → little-endian `A_PCM/INT/LIT` in 1/30 s blocks; closed captions → `S_TEXT/UTF8`, and the CC data is removed from the video.
- Tracks: video; then each audio stream the title's PGC marks available (AC3, DTS, LPCM 16-bit); then VobSub tracks (16:9 uses each stream's "wide" substream, duplicates merged); then CC text.
	The first audio and first subtitle track are default (CC is default only if there's no VobSub). Tracks that end up empty are dropped. No Language element when the disc gives none.
	Audio channel counts come from the stream (the IFO said 5 for 5.1 DTS).
- Chapters: one per PGC program, from IFO cell times; none for a single-chapter title.
- Timing: each cell's stream time mapped to one title timeline (handles STC resets); times computed from field/sample counts so they don't drift.
	Duration = end of the longest audio/video track, whole milliseconds. CC pairs timed one per 1/29.97 s frame.
- Defaults as MakeMKV: keep all tracks and chapters; skip titles under 120 s; ids count only kept titles.
- Output naming: `title_tNN.mkv` for now. MakeMKV uses `<name>-<comment>_tNN.mkv` (name from the volume label, e.g. `A VERY LONG ENGAGEMENT DISC 1-B1_t00.mkv`), see next steps.
- CSS: recover title keys from the scrambled data (libdvdcss "AttackPattern"), accepting the first key that descrambles to valid MPEG.
	Before reading, run the CSS drive handshake (needed on a freshly inserted disc); on unprotected discs the drive rejects it, which is only reported if reading then fails.
- CSS code is ported from libdvdcss rather than linked: readable, no native DLL. libdvdcss could be an optional fallback if a disc defeats the port.
- License: GPL v2 or later because of the libdvdcss port. GPL-derived files: `src/RipDvd.Core/Css/Css.cs`, `Css/CssTables.cs`, `Css/CssAuthentication.cs`; each starts with a "LICENSE NOTE" block listing all of them.
	Any further GPL-derived file gets the same note and goes on this list.
- Reading the disc: through the file system (`<drive>\VIDEO_TS\*.VOB`), after drive authentication.

## Platform (Windows 11 now, Linux possible later)
Target is Windows 11. Keep Linux possible by putting OS-specific code behind small interfaces in `RipDvd.Core`, with one implementation per OS.

| Area | Status | Linux impact |
|---|---|---|
| .NET 10, C# | portable | none |
| IFO parsing, CSS, demux, MKV writing | portable (pure C#) | none |
| Locating the disc | `disc:N` / drive letter / folder (`DiscScanner.ResolveVideoTs`) | pass the mount point (e.g. `/media/<user>/<label>`); `disc:N` relies on `DriveInfo`, untested on Linux |
| Drive authentication | Windows: `Dvd/WindowsCssDrive` behind `Css/ICssDrive` | needs an `ICssDrive` using `ioctl` DVD_AUTH / DVD_READ_STRUCT on `/dev/sr0` |
| GUI (future) | not chosen | WinForms/WPF are Windows-only; MAUI has no Linux desktop. Use **Avalonia** if Linux matters. |

## Next steps
Not finished: 1–3 below.
1. **MakeMKV file naming:** work out the name part (why `DEAD_ZONE_S4_D1` gave no name but `A_VERY_LONG_ENGAGEMENT_DISC1` gave "A VERY LONG ENGAGEMENT DISC 1") and the comment part (`C1`, `B1`, `B2`, ...: MakeMKV's TINFO 49; guess: derived from the menus that lead to each title), then name files `<name>-<comment>_tNN.mkv`.
2. **Matroska details still differing from MakeMKV:** ReferenceBlock values are a placeholder (-1); we write more clusters (291 vs 270 on one title); audio lacing breaks in slightly different places; caption durations 1–4 ms longer; Duration can differ by 1 ms. None affects playback.
3. **CLI options** like makemkvcon's (track selection, etc.), once the defaults are settled.

## Future (desirable, not scheduled)
- **Audio not yet supported:** 20/24-bit LPCM and MPEG audio. Needs a disc with one of them to test (MPEG audio is most likely on PAL or home-made DVDs); the owner has none.
- **Linux drive authentication** (`ICssDrive` via `ioctl` DVD_AUTH on /dev/sr0). Needs a Linux machine with a DVD drive to test; the owner has none.
- GUI similar to MakeMKV's, on top of the same library (secondary to the CLI).
- Episode names from thetvdb.com: look up the series/season (e.g. from the volume label `DEAD_ZONE_S4_D1` → The Dead Zone, season 4, disc 1),
	match the ripped titles to episodes (by order and running time), and name the output files with the episode titles.

## Open questions
- (none)
