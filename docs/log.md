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
