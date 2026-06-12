===================================================================
   sf-multiplayer  -  Stick Fight: The Game  -  Oracle mod (client)
   kitslayer
===================================================================

This package contains ONLY what you need to play (a release build):
the compiled mod + BepInEx. No source code in here.

Contents:
  INSTALL-sf-multiplayer.bat    -> 1-CLICK install (automatic)
  UNINSTALL-sf-multiplayer.bat  -> revert to vanilla
  StickFight-DropIn\            -> the files, laid out EXACTLY as they
                                   go inside your Stick Fight folder
  README.txt                    -> this file

-------------------------------------------------------------------
  OPTION A — AUTOMATIC INSTALL (1 click, recommended)
-------------------------------------------------------------------
  1) Close Stick Fight if it's open.
  2) Extract the whole zip somewhere (don't run it from inside the
     zip preview window).
  3) Double-click  INSTALL-sf-multiplayer.bat
  4) Accept the administrator prompt.
  5) Wait for "INSTALL COMPLETE".

  What the installer does:
   - Finds Stick Fight on its own (any Steam library, any drive).
   - BACKS UP your Assembly-CSharp.dll (-> Assembly-CSharp.dll.vanilla.bak).
   - Copies BepInEx + the plugins + the patched Assembly-CSharp.
   - Leaves a "Play-StickFight.bat" shortcut on your desktop.

-------------------------------------------------------------------
  OPTION B — MANUAL INSTALL (copy & paste)
-------------------------------------------------------------------
  1) Close Stick Fight.
  2) Open your Stick Fight folder:
       Steam -> Stick Fight -> Properties -> Installed Files ->
       Browse...   (usually:
       C:\Program Files (x86)\Steam\steamapps\common\StickFightTheGame )
  3) BACKUP (important): copy
       StickFight_Data\Managed\Assembly-CSharp.dll
     somewhere safe (or rename it to Assembly-CSharp.dll.vanilla.bak).
  4) Open the  StickFight-DropIn\  folder from this package.
  5) Select ALL of its contents and paste them INTO your Stick Fight
     folder. When asked to replace, say YES
     (this swaps Assembly-CSharp.dll for the patched one).
     -> The structure already matches: every file lands where it
        belongs (winhttp.dll, doorstop_config.ini, BepInEx\...,
        StickFight_Data\...).
  6) (Optional) In Steam -> Stick Fight -> Properties -> Launch
     Options, paste:   -address 69.53.117.43 -port 1337

-------------------------------------------------------------------
  HOW TO PLAY
-------------------------------------------------------------------
  - Open Stick Fight (via the desktop shortcut, or via Steam if you
    set the launch options).
  - PLAY ONLINE -> QUICK MATCH.
  - In the map lobby, type in chat:  /start

-------------------------------------------------------------------
  TROUBLESHOOTING
-------------------------------------------------------------------
  Did the mod load at all?
   - Launch the game once, quit, and check that
     BepInEx\LogOutput.log now exists in your Stick Fight folder.
   - No BepInEx folder -> the installer hit the wrong path. Re-run
     INSTALL-sf-multiplayer.bat and paste your real install path
     when asked.
   - LogOutput.log exists and mentions "Loading [SFClientRecon" ->
     the mod is fine; your issue is connection-side (below).

  Files keep disappearing after install?
   - Your antivirus is quarantining BepInEx's winhttp.dll (a common
     false positive). Add an exclusion for the Stick Fight folder
     and re-run the installer.

  Stuck on "Connecting to the server..."?
   - Check the launch options have  -address 69.53.117.43 -port 1337
   - Make sure nothing blocks outbound UDP.
   - Back out to the menu and hit PLAY ONLINE again; first attempt
     after a fresh boot sometimes needs a second try.

  Game updated, or you ran "Verify integrity of game files"?
   - Steam restored the vanilla Assembly-CSharp.dll, which silently
     turns the mod off. Just re-run the installer.

  Still stuck? Open an issue at
  github.com/kitslayer/sf-multiplayer/issues and attach
  BepInEx\LogOutput.log + StickFight_Data\output_log.txt.

-------------------------------------------------------------------
  HOW TO REVERT (back to vanilla)
-------------------------------------------------------------------
  AUTOMATIC:
   - Double-click  UNINSTALL-sf-multiplayer.bat
     (restores your original Assembly-CSharp.dll, removes the
      plugins and disables BepInEx).

  MANUAL:
   1) Close Stick Fight.
   2) In  StickFight_Data\Managed\  replace Assembly-CSharp.dll with
      your backup (Assembly-CSharp.dll.vanilla.bak -> Assembly-CSharp.dll).
   3) Delete  BepInEx\plugins\SFClientRecon.dll  and  SFServerBrowser.dll
   4) To fully disable BepInEx: set  enabled = false  in
      doorstop_config.ini (or delete winhttp.dll).
   5) Remove the -address launch options from Steam.
   - If anything is left broken: Steam -> Stick Fight -> Properties ->
     Installed Files -> Verify integrity of game files (restores
     everything).

-------------------------------------------------------------------
  NOTES
-------------------------------------------------------------------
  - If you have MelonLoader: it coexists with BepInEx. If the online
    menu misbehaves, temporarily rename version.dll while playing on
    the oracle.
  - Default server: 69.53.117.43 : 1337
  - Discord: kitslayer
===================================================================
