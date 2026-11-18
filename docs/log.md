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
