# Current plan

Process rules for this file are in [CLAUDE.md](../CLAUDE.md).
This file describes the present state only; the history is in [log.md](log.md).

## Status
- **Ripping works end to end on Windows.** CLI `src/RipDvd.Cli` (executable `rip-dvd`), modeled on makemkvcon; `rip-dvd help` / `help <command>` / `-h` explain everything:
	`info [disc] [--all] [--minlength=N] [--cells]` lists titles by the disc's own title numbers (length, chapters, size, audio, subtitles, CC); `--cells` adds the numbered cell table;
	`mkv <disc> <titles|all> <folder>` writes `<Disc name> - title NN.mkv`, titles like `1` or `1,3,6` (`all` = titles ≥ 120 s);
	`mkv <disc> cells:LIST <folder>` rips chosen cells (e.g. `cells:23-27,30`, one title set) joined into `<Disc name> - cells LIST.mkv`, one chapter per cell.
	`mkv` options: `--name`, `--audio=LANGS|all|none`, `--subtitles=LANGS|all|none`, `--captions=on|off`, `--chapters=on|off`, `--overwrite` (default: skip existing files), `--dry-run`, `--minlength`, `--no-compilations`, `--no-duplicates`.
	Disc = `disc:0`, `E:` or a folder.
- **Verified against MakeMKV on five discs** (byte-level comparisons with `mkvinfo`, plus ffmpeg decoding with no errors; the owner has played the files):

	| Disc | What it tested | Result |
	|---|---|---|
	| DEAD_ZONE_S4_D1 (TV) | AC3, CC→text, STC reset in a cell | episode and extra identical to MakeMKV (captions within a few ms) |
	| A_VERY_LONG_ENGAGEMENT_DISC1 (film) | 4 VobSub tracks, pulldown, dual layer, empty tracks | movie identical on every track |
	| LOST_IN_TRANSLATION | DTS; fresh disc needing drive authentication | opening identical to owner's MakeMKV rip |
	| Gypsy (2004) vol. 1 | 16-bit LPCM; disc without CSS | opening identical to owner's MakeMKV rip |
	| BUTTERFLY_EFFECT | DTS-ES 6.1 | opening identical to owner's MakeMKV rip |
	| Beauty and the Beast (Disney) | interleaved multi-angle cells, audio gap at a seamless join | title 16 (angle 1, 1:31): video, 3 audio, 2 VobSub identical to MakeMKV incl. times; differences in B1 and log 22:22 |

- **Full-disc test passed** (CLI `mkv E: all` on DEAD_ZONE_S4_D1, 8 titles, 30 min): the 3 titles with MakeMKV rips match on every compared property, including SHA-256 of all video/audio/caption payloads (one title: two caption times 1 ms off); all 8 decode with ffmpeg (log 14:34). Compare tool: probe `mkvcompare`.
- Speed: drive-bound and at least as fast as MakeMKV (e.g. 6.4 GB movie in 1,583 s vs MakeMKV 1,836 s).
- Library `src/RipDvd.Core`:
	`Ifo/` (IFO parsing), `Css/` (descrambling, title-key recovery, drive authentication), `Dvd/` (VOB reading with read-ahead, nav packs, Windows drive commands),
	`Mpeg/` (PES parsing; MPEG-2 video, AC3, DTS, LPCM framers; subpicture assembler), `Captions/` (EIA-608 decoder),
	`Matroska/` (EBML reader/writer, MKV writer), `Ripping/` (disc scanner, cell map with duplicate/compilation detection, title ripper with progress phases, cell progress and cancellation, batch runner `RipJob` with remaining-time estimates, cell selection, options, naming, VobSub header, languages).
	Interleaved (multi-angle) cells are read unit by unit following the nav packs; read errors are retried 3 times and then skipped to the next readable VOBU, with a warning.
- **GUI** `src/RipDvd.Gui` (`rip-dvd-gui.exe`, WPF): all three phases built: drive picker, automatic scan on disc insert, title list (all titles, short ones dimmed, duplicate/compilation notes), compact cell map with selection highlighting and hover details;
	a checkbox per title, output folder and file name (remembered in %APPDATA%\rip-dvd\gui-settings.json), Start/Cancel (Cancel asks first);
	progress bars with % / time left / MB/s for the title and for all titles, a list of finished files (or per-title errors), Open folder, and "Play a sound when done" (remembered).
	Cells can be selected in the cell map (click, Ctrl+click, Shift+click) and ripped into one file ("Rip selected cells"); during a rip the cell map shows progress (green strip above the cells, green blocks on the ripped title's row).
	Output identical to the CLI's (Butterfly Effect titles 3 and 6; Beauty and the Beast cells 32,45 = title 5). Test mode `--screenshot file.png [--drive E] [--rip 3,6 | --cells 32,45] [--to folder] [--midshot file.png]`.
	Screenshot: [gui-phase3-butterfly-effect.png](gui-phase3-butterfly-effect.png).
- Probe `src/DvdProbe`: `drives`, `files`, `titles [-v]`, `scramble`, `crack` (old), `key`, `duration`, `bench`, `demux`, `mkvinfo [-dumptrack]`, `rip [-cells N] [-bad sectors]`, `cellmap`, `navwalk`, `copyright`, `canceltest`, `mkvcompare`.
- **Unit tests** `tests/RipDvd.Core.Tests` (xUnit, `dotnet test RipDvd.slnx`, 55 tests, < 1 s, no disc needed): regression tests for every bug in the log that a test could catch
	(LPCM pointer, palette, duration, caption and pulldown timing, references, clusters, empty tracks, DTS channels, interleaved angles, angle chapters, audio gaps, read errors, sizes),
	plus cell selection, naming, languages, duplicates/compilations, nav packs and the MKV writer.
	Tests on real IFO files read git-ignored `reference/ifo/<disc>/` (now: Beauty and the Beast) and are skipped without it.
	The sector reader takes an `ISectorSource`, so tests serve made-up sectors (`BadSectorSource` simulates a damaged disc).
- `README.md`: what the project is, build, CLI and GUI usage, layout, license.
- References: MakeMKV output listings in `reference/makemkv/`; reference rips in git-ignored `reference/rips/`; the owner's Plex library (D:) holds MakeMKV rips of many of the owner's DVDs.

## Design decisions

### Core (ripping library `RipDvd.Core` and the CLI)
- C# on .NET 10, all managed code (no native C). The drive is the bottleneck (descrambling 430 MB/s vs ≤ 22 MB/s from a drive).
- Self-contained like MakeMKV: no external tools at run time. ffmpeg is used only as a development check.
- Testing (owner): xUnit unit tests for the core run before every commit; every bug fix gets a test that fails without it. Rules that used to live inside `TitleRipper.Rip`
	are small internal classes (`VideoReferences`, `ClusterRule`, `RipTiming`, `AudioContinuity`) so they can be tested. Disc comparisons with MakeMKV stay the end-to-end check.
- DVD only for now; Blu-ray is a future feature (see Future), to start after the current Next steps.
- All ripping logic lives in the library; the CLI and the GUI only call it.
- Output: Matroska, laid out like MakeMKV's files (log 10:22, 12:10); streams copied unchanged except where MakeMKV converts:
	LPCM → little-endian `A_PCM/INT/LIT` in 1/30 s blocks; closed captions → `S_TEXT/UTF8`, and the CC data is removed from the video.
- Accepted differences from MakeMKV's files (owner: not important, not to be worked on): audio lacing breaks in slightly different places;
	caption durations 1–4 ms longer; Duration can differ by 1 ms; each cluster is 4–10 bytes smaller (element size encoding). None affects playback.
	Also accepted (not yet confirmed by the owner): after an angle-block join some video frames are 1 ms earlier than MakeMKV's (sub-ms rounding);
	caption decoding can differ where MakeMKV drops lines (Beauty and the Beast: ours has 38 more caption lines, e.g. "Oh, look out!" where MakeMKV has " )").
- Tracks: video; then each audio stream the title's PGC marks available (AC3, DTS, LPCM 16-bit); then VobSub tracks (16:9 uses each stream's "wide" substream, duplicates merged); then CC text.
	The first audio and first subtitle track are default (CC is default only if there's no VobSub). Tracks that end up empty are dropped. No Language element when the disc gives none.
	Audio channel counts come from the stream (the IFO said 5 for 5.1 DTS).
- Chapters: one per PGC program, from IFO cell times (cells of other angles not counted); none for a single-chapter title.
- Angles (as MakeMKV's first-angle title): only the first angle is ripped. Interleaved cells are read unit by unit (ILVU) following the nav packs' DSI pointers.
	MakeMKV also offers each further angle as a separate title; we don't (yet).
- Timing: each cell's stream time mapped to one title timeline (handles STC resets); times computed from field/sample counts so they don't drift.
	Audio gaps shorter than one frame (at seamless joins) are closed and the shift kept, as MakeMKV does; longer gaps are kept as silence.
- Chosen cells (owner): cells are numbered in cell-map order (title set, then disc position); a selection must be in one title set; the order given is the playback order;
	each cell becomes a chapter named "Cell N"; tracks come from a title playing the first cell. File name `<Disc name> - cells LIST.mkv`.
- Damaged discs (as MakeMKV): a media read error (CRC, sector not found, read fault, I/O device) is retried 3 times, then reading continues at the next readable VOBU of the cell;
	each skip is a warning with the title time and approximate length. Other errors (e.g. a locked drive refusing encrypted sectors) still fail the title.
	Sectors that read fine but hold no MPEG-2 pack (Gypsy: runs of all-zero sectors) are dropped one by one and counted the same way.
	Duration = end of the longest audio/video track, whole milliseconds. CC pairs timed one per 1/29.97 s frame.
- Defaults as MakeMKV: keep all tracks and chapters; `all` skips titles under 120 s (they can still be ripped by number).
- Output naming (owner: clearer than MakeMKV's): `<Disc name> - title NN.mkv`, e.g. `Dead Zone S4 D1 - title 01.mkv`. Disc name = volume label with `_` → space, all-caps words capitalized, words with digits kept as written, `DVD` if no label; `--name` overrides.
	NN and the CLI's title arguments are the disc's own title numbers (as a DVD player numbers them), not MakeMKV's renumbered ids.
- CSS: recover title keys from the scrambled data (libdvdcss "AttackPattern"), accepting the first key that descrambles to valid MPEG.
	Before reading, run the CSS drive handshake (needed on a freshly inserted disc); on unprotected discs the drive rejects it, which is only reported if reading then fails.
	First ask the disc's copyright descriptor whether it uses CSS at all (as libdvdcss does); if not, skip the handshake and the key search (which read up to 800 MB first).
	When it can't be asked (a VIDEO_TS folder on a hard disk), the key search runs as before.
- CSS code is ported from libdvdcss rather than linked: readable, no native DLL. libdvdcss could be an optional fallback if a disc defeats the port.
- License: GPL v2 or later because of the libdvdcss port; the full text is in `LICENSE` at the repo root. GPL-derived files: `src/RipDvd.Core/Css/Css.cs`, `Css/CssTables.cs`, `Css/CssAuthentication.cs`; each starts with a "LICENSE NOTE" block listing all of them.
	Any further GPL-derived file gets the same note and goes on this list.
- Overlapping titles (`DiscScanner`, from cell lists; cells under 2 s ignored): a **duplicate** plays the same cells as an earlier title; a **compilation** ("play all") consists only of cells of 2+ other titles and isn't inside any single one. `info` marks both; `mkv … all --no-compilations / --no-duplicates` skips them (default keeps them, like MakeMKV). Probe `cellmap` shows the titles-vs-cells table.
- Reading the disc: through the file system (`<drive>\VIDEO_TS\*.VOB`), after drive authentication.

### UI (Windows GUI `RipDvd.Gui`)
- Windows only (owner). The command-line app came first; the GUI is built on the same library.
- WPF on .NET 10 with the built-in Fluent (Windows 11) theme, `ThemeMode="System"` (owner: WPF is fine; WinUI 3 was considered but its table control is still experimental and it needs extra runtime/packaging).
- Plain MVVM: view models with `INotifyPropertyChanged`, no third-party packages, so all code is readable C# and XAML.
- The GUI shows and calls library types (`DiscScanner`, `CellMap`, `TitleRipper`, the batch runner); it contains no ripping logic of its own.
- Scans automatically when a disc is inserted (owner).
- Title list shows **all** titles, short ones included; duplicates and compilations are marked; the user checks/unchecks each title (owner).
	Initially checked: titles ≥ 120 s that are neither duplicates nor compilations (proposed default; the owner may change it).
	No track options in the first version: all tracks are kept, as with the CLI default.
- Cell map: every cell drawn the same narrow width (owner); a cell's length and details are shown on hover.
	Clicking a cell selects it (Ctrl+click adds/removes, Shift+click selects a range; click order = playback order); selected columns get a green band; "Rip selected cells" rips them.
	During a rip: a strip above the cells fills green cell by cell (current cell partly), and the ripped title's blocks turn green.
- Completion sound: the Windows default notification sound, for now (owner).

## Platform (Windows 11 now, Linux possible later)
Target is Windows 11. Keep Linux possible by putting OS-specific code behind small interfaces in `RipDvd.Core`, with one implementation per OS.

| Area | Status | Linux impact |
|---|---|---|
| .NET 10, C# | portable | none |
| IFO parsing, CSS, demux, MKV writing | portable (pure C#) | none |
| Locating the disc | `disc:N` / drive letter / folder (`DiscScanner.ResolveVideoTs`) | pass the mount point (e.g. `/media/<user>/<label>`); `disc:N` relies on `DriveInfo`, untested on Linux |
| Drive authentication | Windows: `Dvd/WindowsCssDrive` behind `Css/ICssDrive` | needs an `ICssDrive` using `ioctl` DVD_AUTH / DVD_READ_STRUCT on `/dev/sr0` |
| GUI | WPF, Windows only (owner: the GUI will be Windows-only) | none for the CLI; the GUI would need porting (e.g. Avalonia) if ever wanted on Linux |

## Next steps
What is left needs the owner's hands or is a small follow-up from the Beauty and the Beast disc.

**G4 – Finish the GUI** (Butterfly Effect and Beauty and the Beast are tested and the README is written)
1. *Owner:* insert the other four test discs one at a time (DEAD_ZONE_S4_D1, A_VERY_LONG_ENGAGEMENT_DISC1, LOST_IN_TRANSLATION, Gypsy vol. 1)
	so each can be scanned in the GUI and a short title ripped and compared with the CLI's output (`mkvcompare`).
	This also re-checks those discs after the changes of 2026-11-18 evening (angle cells, chapter timing, closing small audio gaps), which should not affect them.

**B1 – Chapter times after angle blocks** (Beauty and the Beast title 16: 10 of 23 chapter starts differ from MakeMKV's by 17–100 ms)
1. MakeMKV seems to time chapters from the stream (where each cell actually starts on the title timeline), we from the IFO cell playback times.
	Check that idea, and if it holds, write the chapters from the stream timeline (they are written in the header before the clusters, so this needs reserved space or a rewrite at the end).

**G7.4 – Damaged discs: real test** *Owner:* a scratched or dirty DVD, if one turns up (G7 itself is built and tested with simulated bad sectors).

## Future (desirable, not scheduled)
- **Blu-ray probing and ripping** (owner; after the current Next steps). The owner has a Blu-ray drive that never worked with MakeMKV, and no example Blu-ray rips.
	1. Probe: identify the drive (model, firmware, reported Blu-ray/AACS features) and read the disc structure (`BDMV`: `.mpls` playlists, `.clpi` clip info, `.m2ts` streams); find out why MakeMKV fails with this drive.
	2. Rip: playlists as titles; demux the MPEG transport stream; copy H.264/VC-1/MPEG-2 video, AC3/DTS/TrueHD/DTS-HD/LPCM audio and PGS subtitles into MKV.
	3. AACS decryption (AES) using a per-disc key from a key file the owner supplies (the libaacs `KEYDB.cfg` approach); unencrypted discs (e.g. home-burned BD-R) work without one. BD+ discs need more.
		Owner's note: https://fvonline-db.bplaced.net/ contains keys we can use.
	4. Testing without reference rips: ffmpeg decoding, and durations/chapters/tracks checked against the disc's playlists.
- **Audio not yet supported:** 20/24-bit LPCM and MPEG audio. Needs a disc with one of them to test (MPEG audio is most likely on PAL or home-made DVDs); the owner has none.
- **Linux drive authentication** (`ICssDrive` via `ioctl` DVD_AUTH on /dev/sr0). Needs a Linux machine with a DVD drive to test; the owner has none.
- GUI track options (audio/subtitle languages, captions, chapters), as the CLI has; not in the first GUI version.
- Episode names from thetvdb.com (feasibility analysis done: [episode-matching-analysis.md](episode-matching-analysis.md); feasible, needs the owner's choice of data source and naming, see open questions): look up the series/season (e.g. from the volume label `DEAD_ZONE_S4_D1` → The Dead Zone, season 4, disc 1),
	match the ripped titles to episodes (by order and running time), and name the output files with the episode titles.

## Open questions
Deferred by the owner (answer later):
- **Episode naming: data source.** TheTVDB (paid per-user subscription PIN, has DVD order), or free TVmaze/TMDB (aired order), or TheTVDB with a free fallback? See [episode-matching-analysis.md](episode-matching-analysis.md).
- **Episode naming: file names.** Plex-library style `sNNeMM - Episode name.mkv`, optionally in `Series (year)/Season N/` folders?
