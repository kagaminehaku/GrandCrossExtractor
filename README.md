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
- **🔁 Repack** (for mods and translations): packs edited files back into a new `.WAR` that the game reads.
  - Extract with **Convert on extract** off, edit the files and keep their names, then with the original archive open click **🔁 Repack...**, choose the folder with the edited files and where to save the new archive.
  - Entries with a file of the same name in that folder are compressed and encrypted again; the others are copied byte for byte. Put only the files you changed in the folder: the packer's compression is a little weaker than the original's, so repacking everything makes the archive slightly larger.
  - Before the new archive is saved, every packed entry is read back from it and compared with its file; nothing is written if one differs. The open archive itself cannot be overwritten: save elsewhere, close it, then put the new file in its place (keep a copy of the original).
  - Checked on all 59 archives of the 11 games (every entry reads back the same), and Azu Plus and Oreimo Plus with repacked archives play the same as the originals in [OpenShiina](https://github.com/kagaminehaku/OpenShiina).
  - Scenario `.TXT` files are Shift-JIS: the games cannot show characters that Shift-JIS lacks. Images cannot be repacked from PNG (there is no S25 encoder).
- **Compose layers** (off by default, needs Convert on extract):
  - Event CGs and character sprites are stored as a base picture plus separate eyes, mouth and effect layers. With this option each layered S25 is saved as the complete pictures the game shows instead of its separate frames.
  - The combinations come from the game's scenario scripts (the `.TXT` files in `*_T.WAR`), so that archive has to be in the same folder as the one you extract. Oreimo Plus picks its character expressions by number; those come from the expression table `MONTBL.BIN` in the same archive.
  - Files are named after the frames they combine, for example `ST_SENA@001+101+201.png`.
  - The few layered images the scripts never show are saved as separate frames.
- **▶ Play Story** (Oreimo Plus for now):
  - Open any archive of the game and click **▶ Play Story** to play the story with its pictures, voices, music, movies and choices, straight from the game folder.
  - It looks and plays like the game: the opening and title screen, the message window with its buttons (quick save / load, auto, save, load, skip, option, title, quit), the save and load pages (with the game's thumbnails, and the AUTO page that keeps the last nine scene starts and the quick save), the YES / NO dialogs, the OPTION page (volumes, screen mode, message speed, auto wait, skip read text only) and the backlog.
  - Animations, transitions and screen effects follow the engine's own scripts (START.SCN, EFCLIB.SCN).
  - Keys: click / Enter = next, right click = hide the text or close a page, mouse wheel up = backlog, hold Ctrl = skip, A = auto, Esc = chapter list, F11 = full screen. Move the mouse to the top for a tool bar with the chapter list, which starts the story from any scenario file.
  - Saves, settings and the messages read are kept in `%AppData%\GrandCrossExtractor`.

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

The solution has two main projects: `GrandCrossExtractor.Engine` holds the archive and image decoders and the story engine without any user interface, and `GrandCrossExtractor` is the Windows (WPF) application built on it. A third, `GrandCrossExtractor.StoryFlow`, keeps an older hand-written story flow aside and is not used by the program.

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
- Repacking takes the raw files only: S25 / MI4 pictures cannot be made from PNG yet.
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
