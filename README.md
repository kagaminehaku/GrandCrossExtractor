# GrandCross Extractor

A Windows tool for browsing, previewing and extracting the resources of visual novels by **[Grand Cross](https://vndb.org/p1824)**.
These games use the ShiinaRio engine and pack their data into encrypted `.WAR` archives. Every game needs its own decryption parameters.

The extractor is based on the ShiinaRio code from [GARbro](https://github.com/morkt/GARbro). It is focused on Grand Cross titles and adds data that GARbro does not have. See [Differences from GARbro](#differences-from-garbro).

## Supported games

All 11 Grand Cross titles are supported. Each one has been tested against the full set of its retail archives.

| Game | Japanese title | Release | VNDB |
|---|---|---|---|
| Ero-On! | えろおん！ | 2010-04-29 | [v11473](https://vndb.org/v11473) |
| Azu Plus | アズプラス | 2010-08-15 | [v7579](https://vndb.org/v7579) |
| Oreimo Plus | 俺妹プラス | 2010-12-31 | [v6035](https://vndb.org/v6035) |
| Homu☆Plus | ほむ☆プラス | 2011-08-14 | [v8019](https://vndb.org/v8019) |
| Yuru Plus | ゆるプラス | 2011-11-25 | [v10125](https://vndb.org/v10125) |
| Sena Plus | 星奈プラス | 2011-12-31 | [v10126](https://vndb.org/v10126) |
| Kuroneko Plus | 黒猫プラス | 2012-05-18 | [v10586](https://vndb.org/v10586) |
| Nyaru Plus | ニャルプラス | 2012-08-12 | [v10779](https://vndb.org/v10779) |
| Rikka Plus | 六花プラス | 2012-12-31 | [v11902](https://vndb.org/v11902) |
| Maki Fes! | マキフェス！ | 2014-12-30 | [v16484](https://vndb.org/v16484) |
| Re:Rem Plus | Re:レムプラス | 2018-03-31 | [v22991](https://vndb.org/v22991) |

In total about 12,800 archive entries were decrypted, and each one was checked for valid content.

## Features

- Opens the game's `.WAR` archives (WARC 1.7). The game is detected from the `.exe` in the same folder, or you can pick it by hand.
- Previews:
  - **Images:** S25 (multi-frame, with a frame selector), MI4, BMP, PNG and JPEG.
  - **Audio:** OGV, OGG, PAD and WAV, played inside the app.
  - **Scripts and text:** shown as Shift-JIS.
  - **Anything else:** shown as a hex dump.
- Filters entries by type and searches them by name.
- Extracts a single entry, a selection, or the whole archive.
- **Convert on extract** (on by default):
  - S25 and MI4 become PNG, one file per frame.
  - OGV becomes OGG.
  - PAD becomes WAV.

  Turn it off to save the raw decrypted files instead.
- **Compose layers** (off by default, needs Convert on extract):
  - Event CGs and character sprites are stored as a base picture plus separate eyes, mouth and effect layers. With this option each layered S25 is saved as the complete pictures the game shows instead of its separate frames.
  - The combinations come from the game's scenario scripts (the `.TXT` files in `*_T.WAR`), so that archive has to be in the same folder as the one you extract.
  - Files are named after the frames they combine, for example `ST_SENA@001+101+201.png`.
  - The few layered images the scripts never show are saved as separate frames.
- **▶ Play Story** (Oreimo Plus for now):
  - Open any archive of the game and click **▶ Play Story** to read the story with its pictures, voices, music, movies and choices, straight from the game folder.
  - Start from the beginning or from any chapter in the list. The route menu and the choices work as in the game.
  - Keys: click / Enter = next, right click = hide the text, mouse wheel up = backlog, hold Ctrl = skip, A = auto, Esc = menu, F11 = full screen.
  - Some character animations and screen effects are approximations of the engine's, and there is no saving.

## Requirements

- Windows 10 or later
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), or the .NET 10 SDK if you build from source

## Building

```
git clone https://github.com/kagaminehaku/GrandCrossExtractor.git
cd GrandCrossExtractor
dotnet build -c Release
```

The program is written to `bin/Release/GrandCrossExtractor/`. The build also copies the scheme data next to the executable: `Formats.Json` and the `ShiinaImage/` folder. The extractor needs both at run time.

## Usage

1. Run `GrandCrossExtractor.exe`.
2. Click **Open Archive...** and choose one of the game's `.WAR` files, for example `AZUPLUS_G.WAR`.
   - If the game's `.exe` is in the same folder, the scheme is chosen for you.
   - If not, pick the game from the **Scheme** list. The archive is reopened with that scheme.
3. Click an entry to preview it. Use **Extract Selected** or **Extract All** to save files.

## Scheme data

The decryption parameters for each game are stored in plain files:

- **`Formats.Json`** holds each game's keys, its extra cipher and its `.exe` name.
- **`ShiinaImage/Common.bin`** is the engine's built-in key image. All games share it.
- **`ShiinaImage/*.tail`** are the per-game key data appended to the shared image. A game with no tail file uses the shared image alone.

## Differences from GARbro

GARbro handles hundreds of engines. This project only covers Grand Cross games, and it differs from GARbro in these ways:

- **More games:** Ero-On!, Homu☆Plus, Sena Plus and Rikka Plus are not in GARbro's scheme database at the time of writing.
- **Corrected data:** GARbro's Nyaru Plus scheme has one wrong value in its `DecodeBin` table. This breaks a small number of entries.
- **Decompression errors are reported.** If an entry cannot be fully decompressed, the extractor reports an error instead of saving a truncated file.
- **Built on .NET 10.** GARbro targets .NET Framework 4.x.

## Limitations

- Only WARC 1.7 archives are supported. That is the format every Grand Cross game uses.
- The extractor cannot repack or modify archives.
- Windows only.

## Credits

- **[GARbro](https://github.com/morkt/GARbro)** by morkt: the original ShiinaRio archive, encryption and media format code, and the initial scheme data for several games. MIT License.
- **[NVorbis](https://github.com/NVorbis/NVorbis)**: Ogg Vorbis decoding for audio preview. MIT License.
- **[NAudio](https://github.com/naudio/NAudio)**: audio mixing for Play Story. MIT License.

## License

Released under the [MIT License](LICENSE). Portions derived from GARbro are Copyright (c) 2014-2020 morkt and are used under the MIT License.

## Disclaimer

This project is not affiliated with or endorsed by Grand Cross, the authors of the ShiinaRio engine, or the GARbro project. All game titles and trademarks belong to their respective owners.

Use this tool only with games you legally own. Do not redistribute the extracted game assets.
