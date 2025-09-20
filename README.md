# WindowCaptureMod
Window Capture Modification For EmuVR Leveraging Melonloader &amp; The WIGUx/Umbrella Modification
----------------
Releases
----------------
https://github.com/Earthbadger/WindowCaptureMod/releases
----------------
Prerequisites for Binary
----------------
- EmuVR
- WIGUx/Umbrella (available through TeamGT's Arcade Club under "ugc-share" in the EmuVR Discord or in the WIGUx Telegram)
----------------
Installation
----------------
1. Ensure all prerequisites are installed.
2. Put WindowCaptureMod.dll directly into your EmuVR Mods folder.
----------------
How to Add Your Own Games
----------------
This mod works by using the same standard as the existing Retroarch capture core standard in EmuVR `.win` files.

1.  **Create a Game Folder:**
    Create a new folder for your Captured games inside your `EmuVR\Games\` directory. For example: `EmuVR\Games\My PC Games\`.

2.  **Create a .bat Launcher:**
    Inside your new folder, create a batch file (`.bat`) to launch your game. For example, create `MyGame.bat`.
    The content should be simple, like:
    `cd /d "Path To Game"
     "MyGame.exe"`
    (Replace "MyGame.exe" with the actual name of your game's executable.)

4.  **Create a .win "File":**
    In the same folder, create a new text file and rename it to have a `.win` extension (e.g., `MyGame.win`).
    Open this file with a text editor and write the **exact name of the game's main window title OR the name of its process executable** (e.g., `MyGame.exe`). Using the process name is usually more reliable.

    Example `MyGame.win` content:
    `MyGame.exe`

    The "Capture Core Companion" from TeamGT's EmuVRX project can be used as a simple way to create compatible files for this mod 

5.  **Scan for Games:**
    Run the EmuVR Game Scanner and add the new folder you created and click "Scan Games For EmuVR". It will automatically detect your new `.win` files as game cartridges. You can now grab them and insert them into any console to start playing!
----------------
Why Create This?
----------------
My reasoning for creating a mod that seperates window capture into its own thing is purely due to the fact of how EMUVR itself currently handles window capture.

As it stands EMUVR uses Retroarch with a core dedicated to capturing windows, The issue with this is that due to the version of Retroarch that EMUVR uses this causes issues with fast paced and latency sensitive games such as rhythm games, for example when running a game through EMUVR's stock implimentation for capturing windows I would notice that games running in a window would look like were running at a lower framerate than they would outside of EMUVR.

I Initially thought this was due to a framerate or refresh rate missmatch between the captured games and EMUVR itself however this was not the case, causing me to dig deeper into how the RetroArch core works. 

For context I am using two high refresh rate screens (540Hz primary & 240Hz secondary) and EMUVR will typically run at 60 FPS if you are running the game in "Desktop Mode", This varies when you are running the game in VR as the game will run at the framerate tied to the headsets refresh rate (72Hz, 80Hz, 90Hz 120Hz for Meta headsets).

What I noticed was that whenever a game was running there would be a yellow border around that window that would look as though it was flickering while capturing the window. I initially came up with the theory that this flickering was somehow related to the stuttering and reduced framerate I was seeing on the screens. Something that I noticed that was running the game that I wanted to be captured in fullscreen would cause the window to stop flickering resulting in a much smoother experience however the stuttering was still occasionally happening and the yellow border around the captured window would show this.

As this was not ideal I went looking at the source code used for the window capture core itself and noticed that there was an update to it that leveraged the Vulkan rendering API for lower latency when capturing. The problem being is that the version of RetroArch used by EMUVR  "1.7.5" does not support the Vulkan renderer meaning that this was the best result you could get with the capture core which gave me the idea to try creating my own mod that could be used to get low latency window capture without relying on RetroArch's window capture core.
