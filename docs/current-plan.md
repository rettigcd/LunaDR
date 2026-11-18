# Current plan

Process rules for this file are in [CLAUDE.md](../CLAUDE.md).

## Status
- Repo created. Process documents (CLAUDE.md, this plan, log) in place.
- Probe console app `src/DvdProbe` (net10.0): commands `drives`, `files`, `titles [-v]`.
- Library `src/RipDvd.Core` started. `Ifo/` parses VIDEO_TS.IFO (title table) and
  VTS_xx_0.IFO (stream attributes, chapters, program chains, cells).
- `titles` output matches MakeMKV on the test disc for: title list, cell counts, chapter
  counts, sizes (exact bytes), which titles are under 120 s, audio track counts, aspect
  ratio, and which titles have closed captions. Not yet matching: episode durations
  (MakeMKV is 3–5 s shorter) and the file name prefix.
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
- A title's size = sum of its cells' sectors × 2048 (matches MakeMKV exactly).
- A stream is real only if the title's program chain marks it "available"; the stream
  counts in the VTS attribute table are not trusted (the test disc declares 8 audio and
  32 subtitle streams but uses 1–2 audio and none).
- Closed captions (EIA-608 in the MPEG-2 video user data) become a text subtitle track
  when the VTS video attributes have the line-21 flag set, as MakeMKV does ("CC→Text").
- Command-line app first. A MakeMKV-style GUI comes later and is secondary, so keep the ripping
  logic in a library the GUI can reuse.

## Next steps
1. Read raw sectors from the drive and detect CSS-scrambled sectors.
2. CSS: drive authentication through Windows DVD IOCTLs, title key recovery, descrambling.
3. Demux MPEG-2 program stream (video, AC3/DTS/LPCM/MPEG audio, subpictures) by following the
   program chain's cell order.
4. Measure real durations from the stream timestamps and compare with MakeMKV's episode
   durations (IFO says 42:30.46 for title 1, MakeMKV says 0:42:27).
5. Matroska muxer: tracks, chapters, track language tags, VobSub subtitles, CC→text.
6. Main CLI (`rip-dvd info`, `rip-dvd mkv <titles> <outdir>`), similar to `makemkvcon`.
7. Work out MakeMKV's file name prefix (`C1`, `B2`, ...). It is MakeMKV's per-title
   "comment" attribute (TINFO id 49). Guess: derived from the menus that link to each title.

## Future (desirable, not scheduled)
- GUI similar to MakeMKV's, on top of the same library (secondary to the CLI).
- Episode names from thetvdb.com: look up the series/season (e.g. from the volume label
  `DEAD_ZONE_S4_D1` → The Dead Zone, season 4, disc 1), match the ripped titles to episodes
  (by order and running time), and name the output files with the episode titles.

## Open questions
- (none)
