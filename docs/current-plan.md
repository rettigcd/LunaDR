# Current plan

Process rules for this file are in [CLAUDE.md](../CLAUDE.md).

## Status
- Repo created. Process documents (CLAUDE.md, this plan, log) in place.
- Probe console app `src/DvdProbe` (net10.0): commands `drives`, `files`, `titles [-v]`,
  `scramble`, `crack`.
- CSS works on the test disc: `RipDvd.Core/Css` recovers each title set's key from the
  scrambled data (no drive authentication needed) and descrambles sectors. All 14 title
  sets cracked and verified. `RipDvd.Core/Dvd/VobReader` reads a title set's VOBs as one
  run of sectors.
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
- CSS: recover title keys with libdvdcss's known-plaintext attack ("AttackPattern") on the
  scrambled sectors, take the key that several sectors agree on, then descramble. Drive
  authentication (and the player-key / disc-key route) is only added if a disc or drive
  needs it.
- Reading the disc: through the Windows file system (`E:\VIDEO_TS\*.VOB`) for now.
- All C#, no native C code. Measured: descrambling runs at 430 MB/s on one core and key
  recovery uses ~6 ms CPU per 2000 sectors, while a DVD drive delivers at most ~22 MB/s.
  The drive is the bottleneck; if a hot spot appears, optimize it in C# (Span, SIMD,
  parallelism) before considering native code.

## Platform (Windows 11 now, Linux possible later)
Target is Windows 11. Keep Linux possible by putting OS-specific code behind small
interfaces in `RipDvd.Core`, with one implementation per OS.

| Area | Status | Linux impact |
|---|---|---|
| .NET 10, C# | portable | none |
| IFO parsing, CSS, demux, MKV writing | portable (pure C#, byte arrays/streams) | none |
| Locating the disc (`E:\VIDEO_TS`, `DriveInfo`) | Windows-style paths in the probe | Linux mounts at e.g. `/media/<user>/<label>`; device is `/dev/sr0`. Take a path or device argument; don't assume drive letters. |
| Reading scrambled sectors via the file system | works on this Windows drive | Linux usually needs CSS drive authentication first (kernel `DVD_AUTH` ioctl), otherwise reads of scrambled sectors fail. |
| Drive authentication / raw sector reads (future) | not written yet | Windows: `DeviceIoControl` with `IOCTL_DVD_*` on `\\.\E:`. Linux: `ioctl` `DVD_AUTH`/`DVD_READ_STRUCT` on `/dev/sr0`. Needs an `IDiscDevice` interface with two implementations. |
| GUI (future) | not chosen | WinForms/WPF are Windows-only; MAUI has no Linux desktop. Use **Avalonia** if Linux matters. |
| Notification-sound hook | Claude Code settings only, not part of the app | none |
- Closed captions (EIA-608 in the MPEG-2 video user data) become a text subtitle track
  when the VTS video attributes have the line-21 flag set, as MakeMKV does ("CC→Text").
- Command-line app first. A MakeMKV-style GUI comes later and is secondary, so keep the ripping
  logic in a library the GUI can reuse.

## Next steps
1. Demux MPEG-2 program stream (video, AC3/DTS/LPCM/MPEG audio, subpictures) by following the
   program chain's cell order.
2. Episode durations: the stream's own timestamps (nav packs) give 42:33.017 for title 1,
   exactly the IFO cell times at 29.97 fps, yet MakeMKV shows 0:42:27. Rip title 1 with
   `makemkvcon` into a folder outside the repo and inspect the MKV (duration, tracks,
   chapters, codec private data). This also gives a reference file for the muxer.
3. Matroska muxer: tracks, chapters, track language tags, VobSub subtitles, CC→text.
4. Main CLI (`rip-dvd info`, `rip-dvd mkv <titles> <outdir>`), similar to `makemkvcon`.
5. Speed up key recovery (VTS 9 took 49 s, mostly reading the drive): stop at the first
   key that descrambles to valid MPEG, or recover the key while ripping.
6. Drive authentication through Windows DVD IOCTLs, only if a disc/drive refuses to read
   scrambled sectors (this drive reads them fine; MakeMKV had run before, so check again
   after a fresh disc insert).
7. Work out MakeMKV's file name prefix (`C1`, `B2`, ...). It is MakeMKV's per-title
   "comment" attribute (TINFO id 49). Guess: derived from the menus that link to each title.

## Future (desirable, not scheduled)
- GUI similar to MakeMKV's, on top of the same library (secondary to the CLI).
- Episode names from thetvdb.com: look up the series/season (e.g. from the volume label
  `DEAD_ZONE_S4_D1` → The Dead Zone, season 4, disc 1), match the ripped titles to episodes
  (by order and running time), and name the output files with the episode titles.

## Open questions
- **License.** The CSS code (`src/RipDvd.Core/Css`) is ported from libdvdcss, which is GPL
  v2+. That is fine for personal use, but if this program is ever distributed it would have
  to be released under the GPL. OK, or should CSS be rewritten from the published
  algorithm description instead? (The substitution table itself can't be avoided either way.)
- **Legal note.** Bypassing CSS may be restricted where you live (e.g. the US DMCA), even for
  discs you own. MakeMKV and libdvdcss do the same thing; noting it so it's your call.
