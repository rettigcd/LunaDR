# MakeMKV advanced features compared with LunaDR

Reference for future features (written 2026-11-20).
The list of MakeMKV features is from a summary the owner pasted; the status column describes LunaDR on that date.
The live plan ([current-plan.md](current-plan.md)) says what is actually scheduled; this file is the wider list to choose from.

Status: **Done**, **Partial**, **Missing**, **Out of scope** (decided not to do for now).

## 1. Disc decryption

| MakeMKV feature | Status | Notes |
|---|---|---|
| CSS (DVD) | Done | port of libdvdcss, plus the drive handshake |
| AACS 1 (Blu-ray) | Done | per-disc key from the `KEYDB.cfg` the owner supplies; keys are never downloaded |
| BD+ | Out of scope | needs a virtual machine to run the disc's program |
| AACS 2 (UHD) | Out of scope | also needs HEVC video |
| LibreDrive (low-level drive access) | Missing | only matters for UHD and some firmware; the owner's drive works without it |
| Key database integration | Partial | reads `KEYDB.cfg`; no download (by design) |

## 2. Title and playlist identification

| MakeMKV feature | Status | Notes |
|---|---|---|
| Duplicate titles and "play all" compilations | Done | `DiscScanner`; `--no-duplicates`, `--no-compilations` |
| Minimum title length | Done | `--minlength`, default 120 s |
| DVD chapters, Blu-ray entry marks as chapters | Done | |
| DVD angles | Partial | only the first angle is ripped; MakeMKV also offers each further angle as a title |
| Blu-ray seamless branching (parts of clips) | Missing | each play item reads its whole clip; needs the clip's EP map (BR9) |
| Decoy playlists (obfuscation) | Missing | not filtered (BR9) |
| Manual DVD mode (rearrange chapters, pick cells, rebuild titles, split one title into episodes) | Partial | the cell map and "Rip selected cells" cover picking cells; no rearranging chapters, no splitting one title into several files |
| Detecting movie / episode / extra / alternate cut | Missing | only duplicates and compilations are marked; episode naming is an open question |

## 3. Streams

| MakeMKV feature | Status | Notes |
|---|---|---|
| Remux without re-encoding | Done | |
| Several audio tracks, languages | Done | |
| Lossless audio: TrueHD | Done | with the AC3 core as an option (default on) |
| Lossless audio: DTS-HD MA | Partial | the DTS core is written; the channel count shows the core's; no real DTS-HD disc checked yet (BR8) |
| E-AC3 | Partial | dependent substreams (7.1) are not joined to their main frames |
| Subtitles: VobSub, PGS | Done | |
| Forced subtitle flags | Missing | not checked |
| Chapters | Done | |
| Closed captions to text | Done | |
| 20/24-bit LPCM, MPEG audio | Missing | needs a disc to test |
| 3D (MVC) | Out of scope | |
| HDR metadata | Out of scope | UHD only |
| Expert profiles, default stream-selection rules | Partial | CLI `--audio=`, `--subtitles=`, `--captions=`, `--chapters=`; no saved rules, no track options in the GUI |

## 4. Backup and recovery

| MakeMKV feature | Status | Notes |
|---|---|---|
| Full disc backup (keeps menus and structure) | Missing | |
| Decrypted backup | Missing | |
| Read retries | Done | 3 retries, then skip to the next readable VOBU with a warning (tested with simulated bad sectors only) |
| Debug logging | Missing | |
| Configurable cache sizes, direct I/O | Missing | |

## 5. Automation

| MakeMKV feature | Status | Notes |
|---|---|---|
| Command line (`info`, `mkv`, `disc:N`) | Done | `lunadr` |
| Machine-readable output (`-r`: titles, streams, progress, errors as lines) | Missing | |
| `backup` command | Missing | see section 4 |
| Streaming server | Missing | |

## 6. Other expert features

| MakeMKV feature | Status | Notes |
|---|---|---|
| Custom output naming | Partial | `--name` and the `<Disc name> - title NN` scheme; no templates |
| Custom metadata properties | Missing | |
| Decryption diagnostics per disc | Missing | |
| Several drives | Partial | drive picker; nothing more |

## Suggested order (proposal, not decided)

1. Manual DVD mode: split a title into episodes, rearrange chapters (the cell map is the base).
2. `-r` automation output.
3. Check a real DTS-HD disc (BR8; Lord of the Rings: Return of the King is a candidate), then the E-AC3 and DTS-HD channel-count gaps.
4. Full disc backup.
5. GUI track options and saved default rules.
6. Blu-ray EP map and decoy playlists (BR9), when a disc needs it.

BD+, UHD, 3D and LibreDrive are large or hardware-specific.
