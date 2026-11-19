# Current plan

Process rules for this file are in [CLAUDE.md](../CLAUDE.md).
This file describes the present state only; the history is in [log.md](log.md).

## Status
- **Ripping works end to end on Windows.** CLI `src/RipDvd.Cli` (executable `rip-dvd`), modeled on makemkvcon; `rip-dvd help` / `help <command>` / `-h` explain everything:
	`info [disc] [--all] [--minlength=N]` lists titles by the disc's own title numbers (length, chapters, size, audio, subtitles, CC);
	`mkv <disc> <titles|all> <folder>` writes `<Disc name> - title NN.mkv`, titles like `1` or `1,3,6` (`all` = titles ≥ 120 s).
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

- **Full-disc test passed** (CLI `mkv E: all` on DEAD_ZONE_S4_D1, 8 titles, 30 min): the 3 titles with MakeMKV rips match on every compared property, including SHA-256 of all video/audio/caption payloads (one title: two caption times 1 ms off); all 8 decode with ffmpeg (log 14:34). Compare tool: probe `mkvcompare`.
- Speed: drive-bound and at least as fast as MakeMKV (e.g. 6.4 GB movie in 1,583 s vs MakeMKV 1,836 s).
- Library `src/RipDvd.Core`:
	`Ifo/` (IFO parsing), `Css/` (descrambling, title-key recovery, drive authentication), `Dvd/` (VOB reading with read-ahead, nav packs, Windows drive commands),
	`Mpeg/` (PES parsing; MPEG-2 video, AC3, DTS, LPCM framers; subpicture assembler), `Captions/` (EIA-608 decoder),
	`Matroska/` (EBML reader/writer, MKV writer), `Ripping/` (disc scanner, cell map with duplicate/compilation detection, title ripper with progress phases and cancellation, batch runner `RipJob` with remaining-time estimates, options, naming, VobSub header, languages).
- **GUI** `src/RipDvd.Gui` (`rip-dvd-gui.exe`, WPF): all three phases built: drive picker, automatic scan on disc insert, title list (all titles, short ones dimmed, duplicate/compilation notes), compact cell map with selection highlighting and hover details;
	a checkbox per title, output folder and file name (remembered in %APPDATA%\rip-dvd\gui-settings.json), Start/Cancel (Cancel asks first);
	progress bars with % / time left / MB/s for the title and for all titles, a list of finished files (or per-title errors), Open folder, and "Play a sound when done" (remembered).
	Output identical to the CLI's (Butterfly Effect titles 3 and 6). Test mode `--screenshot file.png [--drive E] [--rip 3,6 --to folder]`. Screenshot: [gui-phase3-butterfly-effect.png](gui-phase3-butterfly-effect.png).
- Probe `src/DvdProbe`: `drives`, `files`, `titles [-v]`, `scramble`, `crack` (old), `key`, `duration`, `bench`, `demux`, `mkvinfo [-dumptrack]`, `rip [-cells N]`.
- `README.md`: what the project is, build, CLI and GUI usage, layout, license.
- References: MakeMKV output listings in `reference/makemkv/`; reference rips in git-ignored `reference/rips/`; the owner's Plex library (D:) holds MakeMKV rips of many of the owner's DVDs.

## Design decisions

### Core (ripping library `RipDvd.Core` and the CLI)
- C# on .NET 10, all managed code (no native C). The drive is the bottleneck (descrambling 430 MB/s vs ≤ 22 MB/s from a drive).
- Self-contained like MakeMKV: no external tools at run time. ffmpeg is used only as a development check.
- DVD only; Blu-ray is out of scope.
- All ripping logic lives in the library; the CLI and the GUI only call it.
- Output: Matroska, laid out like MakeMKV's files (log 10:22, 12:10); streams copied unchanged except where MakeMKV converts:
	LPCM → little-endian `A_PCM/INT/LIT` in 1/30 s blocks; closed captions → `S_TEXT/UTF8`, and the CC data is removed from the video.
- Accepted differences from MakeMKV's files (owner: not important, not to be worked on): audio lacing breaks in slightly different places;
	caption durations 1–4 ms longer; Duration can differ by 1 ms; each cluster is 4–10 bytes smaller (element size encoding). None affects playback.
- Tracks: video; then each audio stream the title's PGC marks available (AC3, DTS, LPCM 16-bit); then VobSub tracks (16:9 uses each stream's "wide" substream, duplicates merged); then CC text.
	The first audio and first subtitle track are default (CC is default only if there's no VobSub). Tracks that end up empty are dropped. No Language element when the disc gives none.
	Audio channel counts come from the stream (the IFO said 5 for 5.1 DTS).
- Chapters: one per PGC program, from IFO cell times; none for a single-chapter title.
- Timing: each cell's stream time mapped to one title timeline (handles STC resets); times computed from field/sample counts so they don't drift.
	Duration = end of the longest audio/video track, whole milliseconds. CC pairs timed one per 1/29.97 s frame.
- Defaults as MakeMKV: keep all tracks and chapters; `all` skips titles under 120 s (they can still be ripped by number).
- Output naming (owner: clearer than MakeMKV's): `<Disc name> - title NN.mkv`, e.g. `Dead Zone S4 D1 - title 01.mkv`. Disc name = volume label with `_` → space, all-caps words capitalized, words with digits kept as written, `DVD` if no label; `--name` overrides.
	NN and the CLI's title arguments are the disc's own title numbers (as a DVD player numbers them), not MakeMKV's renumbered ids.
- CSS: recover title keys from the scrambled data (libdvdcss "AttackPattern"), accepting the first key that descrambles to valid MPEG.
	Before reading, run the CSS drive handshake (needed on a freshly inserted disc); on unprotected discs the drive rejects it, which is only reported if reading then fails.
- CSS code is ported from libdvdcss rather than linked: readable, no native DLL. libdvdcss could be an optional fallback if a disc defeats the port.
- License: GPL v2 or later because of the libdvdcss port. GPL-derived files: `src/RipDvd.Core/Css/Css.cs`, `Css/CssTables.cs`, `Css/CssAuthentication.cs`; each starts with a "LICENSE NOTE" block listing all of them.
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
The GUI's three phases are built (see Status). G4 waits on the owner (discs, license text), so work continues with G5–G7 meanwhile.
Owner's priority: G5 and G6 before G7.

**G4 – Finish the GUI** (Butterfly Effect is tested and the README is written; what is left needs the owner's hands)
1. *Owner:* insert the other four test discs one at a time (DEAD_ZONE_S4_D1, A_VERY_LONG_ENGAGEMENT_DISC1, LOST_IN_TRANSLATION, Gypsy vol. 1)
	so each can be scanned in the GUI and a short title ripped and compared with the CLI's output (`mkvcompare`).
2. Add the GPL v2 text as `LICENSE` (the download from gnu.org was refused by the network here; any copy of GPL v2 will do).

**G5 – Rip chosen cells** (owner's request: one cell, or several cells joined into one file)
1. Library: a rip from a list of cells instead of a title. Cells must all be in one title set (same video format, audio layout and CSS key); others are refused with a clear message.
	Track list, languages and subtitle palette come from a title that plays the first cell. The user's order is kept; each cell becomes a chapter.
2. CLI: a way to name cells, e.g. `rip-dvd mkv E: cells:5:1,5:3-5:6 <folder>` (title set:cell, as in the cell map), and a file name such as `<Disc name> - cells 5-1 to 5-6.mkv`.
3. GUI: select cells in the cell map (click, Ctrl+click, Shift+click for a range), then "Rip selected cells" next to Start.
4. Test: rip a single cell and a multi-cell selection; the joined file must play continuously (ffmpeg decode, check timestamps at the joins);
	ripping exactly a title's cells this way must give the same streams as ripping the title (`mkvcompare`).

**G6 – Rip progress in the cell map** (owner's request)
1. Library: rip progress also reports the cell being read and how far into it.
2. GUI: during a rip, the cell map marks finished cells and the cell being read (partly filled), for the title being ripped.

**G7 – Damaged discs: retry and skip read errors** (owner's request; MakeMKV does this)
1. On a read error: retry the sector a few times; if it still fails, skip to the next readable VOBU (the next ~0.5 s unit, which starts with a key frame) and keep ripping.
2. Report each skipped spot (title time and length) as a warning in the CLI and in the GUI's results list, instead of failing the whole title.
3. Test: simulate unreadable sectors in a test (a reader that fails on chosen sectors); a real test needs a scratched disc from the owner.

## Future (desirable, not scheduled)
- **Audio not yet supported:** 20/24-bit LPCM and MPEG audio. Needs a disc with one of them to test (MPEG audio is most likely on PAL or home-made DVDs); the owner has none.
- **Linux drive authentication** (`ICssDrive` via `ioctl` DVD_AUTH on /dev/sr0). Needs a Linux machine with a DVD drive to test; the owner has none.
- GUI track options (audio/subtitle languages, captions, chapters), as the CLI has; not in the first GUI version.
- Episode names from thetvdb.com (feasibility analysis done: [episode-matching-analysis.md](episode-matching-analysis.md); feasible, needs the owner's choice of data source and naming, see open questions): look up the series/season (e.g. from the volume label `DEAD_ZONE_S4_D1` → The Dead Zone, season 4, disc 1),
	match the ripped titles to episodes (by order and running time), and name the output files with the episode titles.

## Open questions
Deferred by the owner (answer later):
- **Episode naming: data source.** TheTVDB (paid per-user subscription PIN, has DVD order), or free TVmaze/TMDB (aired order), or TheTVDB with a free fallback? See [episode-matching-analysis.md](episode-matching-analysis.md).
- **Episode naming: file names.** Plex-library style `sNNeMM - Episode name.mkv`, optionally in `Series (year)/Season N/` folders?
