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
	`Matroska/` (EBML reader/writer, MKV writer), `Ripping/` (disc scanner, title ripper, VobSub header, languages).
- Probe `src/DvdProbe`: `drives`, `files`, `titles [-v]`, `scramble`, `crack` (old), `key`, `duration`, `bench`, `demux`, `mkvinfo [-dumptrack]`, `rip [-cells N]`.
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

## Next steps: the GUI (Windows only)
Owner's phases: (1) scan the disc and show what's on it, including which titles share cells, in a compact graphical cell map;
(2) the user selects titles and clicks Start; (3) ripping with progress and remaining time for the current title and for all titles, then an optional sound.
Framework and approach: see Design decisions → UI.

**G0 – Groundwork in the library (testable without a GUI)**
1. *(done)* Move the cell-map model into `RipDvd.Core` (`Ripping/CellMap`: cells with title set, VOB/cell id, sectors, length; per title: playlist positions and chapter starts; shared-cell and duplicate/compilation info). Probe `cellmap` and the GUI both use it.
2. *(done)* Ripping progress and cancelling: `TitleRipper.Rip` reports a progress record (phase: unlocking / finding key / ripping / finishing; sectors done and total; bytes; elapsed) and takes a `CancellationToken`; a cancelled rip deletes its partial file.
3. *(done)* A small batch runner (`Ripping/RipJob`): rips a list of titles in order and reports per-title and overall progress, including remaining-time estimates from the measured read speed (weighted by sectors, so short and long titles count fairly).

**G1 – Phase 1: scan and present**
1. WPF project and main window: drive picker (optical drives with their disc names), Scan button, status line; scanning runs in the background so the window stays responsive.
2. Title list with **all** titles: number, length, chapters, size, audio, subtitles, CC, and a flag column (duplicate of N / compilation of …); short titles shown dimmed.
3. Compact graphical cell map (rows = titles, columns = cells in disc order, grouped by title set), every cell the same narrow width: a filled block where a title uses a cell, a mark where a chapter starts, cells used by several titles highlighted. Hovering shows cell details (id, sectors, length, which titles use it); selecting a title highlights its cells and the other titles that share them.
4. Scan automatically when a disc is inserted; clear the view when it is removed.

**G2 – Phase 2: select and start**
1. A checkbox per title; initially checked: titles ≥ 120 s that are not duplicates or compilations; quick buttons "select all" / "none".
2. Output folder (with Browse) and disc name used in the file names (editable, from the volume label); remembered between runs (settings file in %APPDATA%).
3. Start button: validates (something selected, folder writable, existing files → ask overwrite/skip).

**G3 – Phase 3: rip with progress**
1. Progress view: current title (name, % done, remaining time, MB/s) and all titles (% done, remaining time, n of m); a list of finished files with any warnings.
2. Cancel button (stops after confirmation, removes the partial file).
3. When done: summary, "Open folder" button, optional sound (checkbox, remembered): the Windows default notification sound.
4. Errors (e.g. unreadable disc) shown per title without stopping the other titles.

**G4 – Finish**
1. Test on the five test discs (incl. Butterfly Effect's shared cells), check output equals the CLI's (`mkvcompare`).
2. README section / screenshots; log and plan updated.

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
