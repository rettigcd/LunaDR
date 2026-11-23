# Brand images

- `lunadr-logo.png`: the logo with the "DR" subscript (original, 1254 x 1254, transparent). Shown in the GUI's header.
- `lunadr-icon.png`: the same disc without the subscript (original, 1254 x 1254, transparent). The source of the icon.
- `lunadr.ico`: the program icon (16 to 256 px), used by `lunadr-cli.exe`, `LunaDR.exe` and the main window. Made from `lunadr-icon.png`.
- `lunadr-logo-256.png`: `lunadr-logo.png` at 256 px, built into the GUI (the original is too big to embed).

- `lunadr-watermark-640.png`: `lunadr-icon.png` at 640 px, drawn very faint and large over the GUI window.

`lunadr.ico`, `lunadr-logo-256.png` and `lunadr-watermark-640.png` are made by `make-icon.ps1` (Windows PowerShell with System.Drawing):
`powershell -File assets\make-icon.ps1 -Assets assets`. Run it again after changing either original image.
