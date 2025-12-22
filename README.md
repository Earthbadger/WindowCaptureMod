# Window Capture Mod for EmuVR

MelonLoader Window Capture Mod For EmuVR Built Using Spout And Windows Graphics Capture 

## Features

*   **High Performance Capture:** Uses the Windows Graphics Capture API via `SpoutWGCSender` for fast, low-latency window capture.

## Installation

1.  Download the latest release.
2.  Copy `WindowCaptureMod.dll` to your `EmuVR\Mods` folder.
3.  Create a folder named `WindowCapture` inside `EmuVR\UserData`.
4.  Copy `SpoutWGCSender.exe` to `EmuVR\UserData\WindowCapture\`.

## Usage

### 1. Basic Window Capture
To capture a specific window (e.g., a browser, a media player, or a running game):
1.  Create a `.win` file in your Game System Folder `EmuVR\Games\(Example System)`
2.  Open the file and type the exact title or the name of the `.exe` of the Application you want to capture.
    *   Example: `Mozilla Firefox`
    *  Example: `Firefox.exe`
3.  Scan your games in EmuVR.
4.  Insert the cartridge/disc into a system and turn it on.

### 2. Auto-Launching
To have EmuVR launch the game for you:
1.  Create your `.win` file as described above (e.g., `Doom.win`) containing the window title or `.exe` name (e.g., `DOOM Eternal`).
2.  Create a Batch file (`.bat`) with the **exact same name** in the same folder (e.g., `Doom.bat`).
3.  Inside the `.bat` file, put the command to launch your game.
    *   Example: `start "" "C:\Games\Doom Eternal\DOOMEternalx64vk.exe"`
    *   Steam Example: `start steam://rungameid/782330`
4.  When you turn on the System in EmuVR, the mod will execute the `.bat` file and wait for the window specified in the `.win` file to appear.

### 3. Hiding the Cursor
To hide the mouse cursor in the capture, add `--no-cursor` after the window title in your `.win` file.

**Example `MyGame.win`:**
```text
"My Game Window Title" --no-cursor
```

## Requirements
*   MelonLoader (installed with Wigu/WIGUx/Umbrella)
*   Windows 10 (version 1809 or later) or Windows 11 (required for WGC API).

## Troubleshooting

*   **Black Screen:** Ensure the window you are trying to capture is not minimized.
*   **Game doesn't launch:** Check your `.bat` file by running it manually outside of EmuVR to ensure it works.
*   **Window not found:** Ensure the text in your `.win` file matches the window title exactly (case-insensitive).


## Why Create This

My reasoning for creating a mod that seperates window capture into its own thing is purely due to the fact of how EMUVR itself currently handles window capture.

As it stands EMUVR uses Retroarch with a core dedicated to capturing windows, The issue with this is that due to the version of Retroarch that EMUVR uses this causes issues with fast paced and latency sensitive games such as rhythm games, for example when running a game through EMUVR's stock implimentation for capturing windows I would notice that games running in a window would look like were running at a lower framerate than they would outside of EMUVR.

I Initially thought this was due to a framerate or refresh rate missmatch between the captured games and EMUVR itself however this was not the case, causing me to dig deeper into how the RetroArch core works. 

For context I am using two high refresh rate screens (540Hz primary & 240Hz secondary) and EMUVR will typically run at 60 FPS if you are running the game in "Desktop Mode", This varies when you are running the game in VR as the game will run at the framerate tied to the headsets refresh rate (72Hz, 80Hz, 90Hz 120Hz for Meta headsets).

What I noticed was that whenever a game was running there would be a yellow border around that window that would look as though it was flickering while capturing the window. I initially came up with the theory that this flickering was somehow related to the stuttering and reduced framerate I was seeing on the screens. Something that I noticed that was running the game that I wanted to be captured in fullscreen would cause the window to stop flickering resulting in a much smoother experience however the stuttering was still occasionally happening and the yellow border around the captured window would show this.

As this was not ideal I went looking at the source code used for the window capture core itself and noticed that there was an update to it that leveraged the Vulkan rendering API for lower latency when capturing. The problem being is that the version of RetroArch used by EMUVR  "1.7.5" does not support the Vulkan renderer meaning that this was the best result you could get with the capture core which gave me the idea to try creating my own mod that could be used to get low latency window capture without relying on RetroArch's window capture core.
