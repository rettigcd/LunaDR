# Current plan

Process rules for this file are in [CLAUDE.md](../CLAUDE.md).

## Status
- Repo created. Process documents (CLAUDE.md, this plan, log) in place.
- Probe console app `src/DvdProbe` scaffolded (net10.0); it can list optical drives and the
  files on a disc.
- Test disc available: drive E:, volume `DEAD_ZONE_S4_D1` (TV episodes, 14 title sets).
- MakeMKV is installed at `C:\Program Files (x86)\MakeMKV` and can be used as a reference.

## Design decisions
- Language/runtime: C# on .NET 10.
- Output container: Matroska (.mkv), as MakeMKV.
- Default: keep all video, audio and subtitle tracks and the chapters (MakeMKV default).
- Default minimum title length: 120 seconds; shorter titles are skipped (MakeMKV default).
- Output file naming follows MakeMKV: `<title name>_t<NN>.mkv`.
- Self-contained: no ffmpeg or other external tools at run time (MakeMKV does everything itself).

## Next steps
1. Probe: run `makemkvcon -r info disc:0` on the test disc and save the output as a reference
   for what title list we should produce.
2. Probe: parse `VIDEO_TS.IFO` and `VTS_xx_0.IFO` (title table, program chains, cells,
   durations, audio/subpicture attributes) and print a title list.
3. Compare our title list with MakeMKV's; adjust until they match.
4. Read raw sectors from the drive and detect CSS-scrambled sectors.
5. CSS: drive authentication through Windows DVD IOCTLs, title key recovery, descrambling.
6. Demux MPEG-2 program stream (video, AC3/DTS/LPCM/MPEG audio, subpictures) by following the
   program chain's cell order.
7. Matroska muxer: tracks, chapters, track language tags, VobSub subtitles.
8. Main CLI (`rip-dvd info`, `rip-dvd mkv <titles> <outdir>`), similar to `makemkvcon`.

## Open questions
- Should there be a GUI eventually (like MakeMKV), or is a command-line app enough?
- Blu-ray support is out of scope unless the owner asks for it. Correct?
