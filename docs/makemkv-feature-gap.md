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
| Blu-ray playlists as titles (one title per playlist) | Done | numbered by the playlist file |
| DVD angles | Partial | only the first angle is ripped; MakeMKV also offers each further angle as a title |
| Blu-ray seamless branching (different versions sharing video segments) | Partial | the output is right: each play item keeps only what falls between its in and out times, and the cell map shows shared segments; but each play item reads its whole clip, so it is slower than needed; needs the clip's EP map (BR9) |
| Decoy playlists (obfuscation) | Missing | not filtered (BR9) |
| Manual DVD mode (rearrange chapters, pick cells, rebuild titles, split one title into episodes) | Partial | the cell map and "Rip selected cells" cover picking cells; no rearranging chapters, no splitting one title into several files |
| Detecting movie / episode / extra / alternate cut | Missing | only duplicates and compilations are marked; episode naming is an open question |

## 3. Streams

| MakeMKV feature | Status | Notes |
|---|---|---|
| Remux without re-encoding | Done | |
| Several audio tracks, languages | Done | |
| Commentary tracks | Partial | a commentary that is one of the playlist's normal audio streams is kept, as ordinary audio (not labeled or flagged as commentary) |
| Secondary audio and video (Blu-ray picture-in-picture commentary) | Missing | only the primary video and audio are read; the secondary streams, and the subtitles for them, are skipped |
| Lossless audio: TrueHD | Done | with the AC3 core as an option (default on) |
| Lossless audio: DTS-HD MA | Partial | the DTS core is written; the channel count shows the core's; no real DTS-HD disc checked yet (BR8) |
| E-AC3 | Partial | dependent substreams (7.1) are not joined to their main frames |
| Subtitles: VobSub, PGS | Done | |
| Blu-ray text subtitles (TextST) | Missing | only PGS is ripped; text subtitles are not read |
| Forced subtitle flags and track metadata | Missing | the forced flag (DVD and PGS) is not read or written; only the language and the default flag are written (no track names or commentary / hearing-impaired flags, as far as I can tell) |
| Chapters | Done | |
| Closed captions to text | Done | |
| 20/24-bit LPCM, MPEG audio | Missing | needs a disc to test |
| 3D (MVC) | Out of scope | |
| HDR (HDR10, Dolby Vision) | Out of scope | UHD only, with HEVC video |
| Expert profiles, default stream-selection rules | Partial | CLI `--audio=`, `--subtitles=`, `--captions=`, `--chapters=`; no saved rules, no track options in the GUI |

## 4. Backup and recovery

| MakeMKV feature | Status | Notes |
|---|---|---|
| Full disc backup, DVD and Blu-ray (copies the file structure, keeps menus) | Missing | LunaDR extracts titles to MKV only |
| Decrypted backup (decrypt the video files while copying) | Missing | needs the full backup first |
| Read retries | Partial | DVD: 3 retries, then skip to the next readable VOBU with a warning; Blu-ray: 3 retries per 6 KB unit, units that still fail become zeros (counted). Both tested with simulated bad sectors only. The number of retries is fixed; MakeMKV lets the user configure it |
| Debug logging (diagnostics for problem discs) | Partial | Blu-ray rips write a progress CSV (`%APPDATA%\LunaDR\progress-logs`), meant for the time-left estimate; there is no log of read errors, drive commands or disc structure |
| Configurable cache sizes, direct I/O | Missing | no setting for it; read sizes are chosen in the code |
| Damaged-disc recovery | Partial | MakeMKV is not a recovery tool either (badly unreadable sectors still fail it); we skip or zero what can't be read and warn. Never tested on a real damaged disc |

## 5. Automation

| MakeMKV feature | Status | Notes |
|---|---|---|
| Command line (`info`, `mkv`, `disc:N`) | Done | `lunadr-cli` |
| Machine-readable output (`-r`: titles, streams, progress, errors as lines) | Missing | |
| `backup` command | Missing | see section 4 |
| Streaming server | Missing | |

## 6. Other expert features

| MakeMKV feature | Status | Notes |
|---|---|---|
| Default selection rules for languages, subtitles and audio types | Partial | same as "Expert profiles" in section 3: the CLI has `--audio=` and `--subtitles=`; no saved rules, no GUI options |
| Custom output naming | Partial | `--name` and the `<Disc name> - title NN` scheme; no templates |
| Custom metadata properties | Missing | |
| Decryption diagnostics per disc | Partial | the AACS errors name the disc ID and the key file searched; `lunadr-cli info` says which key file it found; probe `bdkey`. No report of what was tried or why decryption failed |
| Several drives | Partial | drive picker; nothing more |
| Automatic retrieval of UHD decryption keys | Out of scope | LunaDR never downloads keys (owner's rule), and UHD (AACS 2) is out of scope |
| Adjustable cache sizes and direct I/O | Missing | see section 4 |

## Suggested order (proposal, not decided)

1. Manual DVD mode: split a title into episodes, rearrange chapters (the cell map is the base).
2. `-r` automation output.
3. Check a real DTS-HD disc (BR8; Lord of the Rings: Return of the King is a candidate), then the E-AC3 and DTS-HD channel-count gaps.
4. Full disc backup.
5. GUI track options and saved default rules.
6. Blu-ray EP map and decoy playlists (BR9), when a disc needs it.

BD+, UHD, 3D and LibreDrive are large or hardware-specific.
