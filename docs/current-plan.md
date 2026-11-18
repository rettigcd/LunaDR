# Current plan

Process rules for this file are in [CLAUDE.md](../CLAUDE.md).

## Status
- Repo created. Process documents (CLAUDE.md, this plan, log) in place.
- Probe console app `src/DvdProbe` scaffolded (net10.0); it can list optical drives and the
  files on a disc.
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
  list of kept titles (starting at 00). How MakeMKV picks the prefix (`C1`, `B2`, `B4`, ...)
  still has to be worked out.
- Self-contained: no ffmpeg or other external tools at run time (MakeMKV does everything itself).
- DVD only; Blu-ray is out of scope.
- Command-line app first. A MakeMKV-style GUI comes later and is secondary, so keep the ripping
  logic in a library the GUI can reuse.

## Next steps
1. Probe: parse `VIDEO_TS.IFO` and `VTS_xx_0.IFO` (title table, program chains, cells,
   durations, audio/subpicture attributes) and print a title list.
2. Compare our title list with MakeMKV's (durations, cell counts, sizes, which titles are
   skipped); adjust until they match. Work out MakeMKV's file name prefix rule.
3. Read raw sectors from the drive and detect CSS-scrambled sectors.
4. CSS: drive authentication through Windows DVD IOCTLs, title key recovery, descrambling.
5. Demux MPEG-2 program stream (video, AC3/DTS/LPCM/MPEG audio, subpictures) by following the
   program chain's cell order.
6. Matroska muxer: tracks, chapters, track language tags, VobSub subtitles.
7. Main CLI (`rip-dvd info`, `rip-dvd mkv <titles> <outdir>`), similar to `makemkvcon`.

## Future (desirable, not scheduled)
- GUI similar to MakeMKV's, on top of the same library (secondary to the CLI).
- Episode names from thetvdb.com: look up the series/season (e.g. from the volume label
  `DEAD_ZONE_S4_D1` → The Dead Zone, season 4, disc 1), match the ripped titles to episodes
  (by order and running time), and name the output files with the episode titles.

## Open questions
- (none)
