# LunaDR

Rips movies and TV episodes from a DVD or a Blu-ray into MKV files, much like [MakeMKV](https://www.makemkv.com/) does.
Everything is C# on .NET 10, with no external tools or native libraries needed at run time.

- **Command line** (`lunadr-cli`): lists a disc's titles and rips the ones you name.
- **Windows app** (`lunadr-gui`): scans the disc when it is inserted, shows the titles and which parts of the disc they share,
	and rips the titles you check, with progress and remaining time.

The output is laid out like MakeMKV's: the video and audio are copied unchanged; DVD subtitles become VobSub tracks;
closed captions become a text subtitle track; chapters are kept.
Rips from five test discs were compared with MakeMKV's rips of the same titles and matched: identical video and audio data, caption times within a few milliseconds.

## Requirements
- Windows 11 (the command line is written to be portable; see [docs/current-plan.md](docs/current-plan.md), "Platform").
- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- A DVD drive. CSS-protected discs are unlocked by the program itself.
- For Blu-rays: a Blu-ray drive, and for AACS-encrypted discs a key file (see "Blu-ray" below).

## Build
```
dotnet build LunaDR.slnx
dotnet test LunaDR.slnx
```
The unit tests (`tests/LunaDR.Core.Tests`) need no disc and take about a second.

## Command line
```
dotnet run --project src/LunaDR.Cli -- <command> [arguments] [options]
```
or run `src/LunaDR.Cli/bin/Debug/net10.0/lunadr-cli.exe` directly.

| Command | What it does |
|---|---|
| `lunadr-cli info E:` | List the titles: length, chapters, size, audio, subtitles, closed captions. Duplicates and "play all" compilations are marked. |
| `lunadr-cli mkv E: all C:\Rips` | Rip every title of at least 120 s to `C:\Rips\<Disc name> - title NN.mkv`. |
| `lunadr-cli mkv E: 1,3,6 C:\Rips` | Rip titles 1, 3 and 6 (the disc's own title numbers, as shown by `info`). |
| `lunadr-cli info E: --cells` | Also list every cell (a piece of video on the disc) with its number and the titles that play it. |
| `lunadr-cli mkv E: cells:30-33 C:\Rips` | Rip cells 30 to 33, joined into one file `<Disc name> - cells 30-33.mkv` (cells of one title set; one chapter per cell). |
| `lunadr-cli help mkv` | All options, with examples. |

The disc can be `disc:0` (the first DVD drive), a drive letter such as `E:`, or a folder that contains `VIDEO_TS`.
Useful `mkv` options: `--audio=eng`, `--subtitles=none`, `--captions=off`, `--no-compilations`, `--no-duplicates`, `--dry-run`, `--overwrite`.
Ctrl+C stops a rip cleanly; the unfinished file is removed.

## Blu-ray
`info` and `mkv` (and the Windows app) also work on a Blu-ray. Its titles are the disc's playlists, listed longest first and ripped by playlist number:
```
lunadr-cli info E:
lunadr-cli mkv E: 0 C:\Rips          (playlist 00000 -> C:\Rips\<Disc name> - playlist 00000.mkv)
```
- Formats: VC-1, H.264 and MPEG-2 video; AC3, E-AC3, DTS / DTS-HD, TrueHD (written as TrueHD plus its AC3 core; `--truehd-core=off` or the app's "AC3 copy of TrueHD" box leaves the core out) and LPCM audio; PGS subtitles.
	Tested on one disc so far (The Goonies: VC-1, AC3, TrueHD, PGS); the others are unit-tested only.
- Encrypted (AACS) discs need a key file in libaacs's `KEYDB.cfg` format that has the disc's entry, placed next to the program or in
	`%APPDATA%\aacs\` or `%LOCALAPPDATA%\aacs\`. The program never downloads keys. `lunadr-cli info` says which key file it found.
- Not supported: UHD (4K) discs, BD+ protection, 3D, menus.

## Windows app
```
dotnet run --project src/LunaDR.Gui
```
or run `src/LunaDR.Gui/bin/Debug/net10.0-windows/lunadr-gui.exe`.

1. Insert a disc (or pick the drive and click **Scan**). The title list shows every title; short titles are dimmed,
	and the Note column says when a title repeats another one or is a compilation of other titles.
2. The cell map below the list shows which titles play which parts ("cells") of the disc: blue cells belong to one title, orange cells are shared.
	Click a title to highlight the titles that share its cells; hover over a cell for its details.
3. Check the titles to rip, choose the folder and the start of the file names, and click **Start**.
	To rip just part of the disc, click cells in the map (Ctrl+click to add more, Shift+click for a range) and click **Rip selected cells**.
4. Two progress bars show the current title and all titles, with the time left; the cell map fills in green as each cell is read.
	Finished files are listed below the bars, with **Open folder**.
	Check **Play a sound when done** to hear the Windows notification sound at the end.

The folder and the sound setting are remembered in `%APPDATA%\LunaDR\gui-settings.json`.

## Project layout
| Folder | Contents |
|---|---|
| `src/LunaDR.Core` | The ripping library: IFO parsing, CSS, reading the disc, MPEG demuxing, the Matroska writer, scanning and ripping; `Bluray/` for Blu-rays (playlists, AACS, transport streams). |
| `src/LunaDR.Cli` | The command-line app. |
| `src/LunaDR.Gui` | The Windows app (WPF). |
| `src/DvdProbe` | A developer tool for investigating discs and comparing MKV files. |
| `docs` | [current-plan.md](docs/current-plan.md) (status, design decisions, next steps) and [log.md](docs/log.md) (history). |

## License
GPL version 2 or later (full text in [LICENSE](LICENSE)), because the CSS code is ported from [libdvdcss](https://www.videolan.org/developers/libdvdcss.html).
The ported files are `src/LunaDR.Core/Css/Css.cs`, `CssTables.cs` and `CssAuthentication.cs`; each begins with a LICENSE NOTE.

Whether ripping a particular disc is legal where you live is for you to determine.
