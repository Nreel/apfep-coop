# ApfepCoop — co-op mod for "A Place for Every Potion"

Unity 6000.3.23f1 IL2CPP, Steam appid 5189500 (CubeQuad). MelonLoader + Harmony patches on the
game's own systems; host-authoritative state sync.

## What works today (verified)

Two instances over localhost UDP (`tools/qa.ps1`, 11 checks, pass 2026-10-31):

- Host opens a lobby (Steam) or binds a UDP port (QA mode); client joins by room code.
- Host is authoritative for game state and save data. `GameStateMachine.Enter(eGameState)`
  broadcasts transitions; the puppet remaps its own transitions onto the host's state, and a
  host-side `Playing` from a puppet lobby becomes a local `LoadLevel` so the level scene loads
  before gameplay.
- Host snapshots (gold, diamonds, pours/undos counters, skills, shelves, persistent buckets) go
  to puppets; deltas keep them current. Snapshots are applied onto the puppet's live game
  objects, so its HUD shows the host's run.
- Every peer broadcasts its hero position/rotation at ~10 Hz; each instance renders the other's
  hero as an avatar in the lab.
- Puppets never write saves (suppressed at `BackgroundSaveWriter.Write`) and never push undo
  records; their own move input is zeroed, so they follow the host's session instead of
  diverging into a separate run.

## Layout

```
mod/ApfepCoop/     Melon plugin source (CoopPlugin.cs, Networking.cs, SyncState.cs, Puppets.cs, Wire.cs)
tools/qa.ps1       automated two-instance sync test
tools/DecompTool/  Cpp2IL dump wrapper (run_dump.bat)
install.bat        copies loader + mod into a game install
uninstall.bat      removes them
ARCHITECTURE.md    reverse-engineered game systems (state machine, saves, hero, buckets)
BRIEF.md           build brief from the co-op playbook triage pass
```

Not committed: the game itself, two local test copies of the install (`install/` host lane,
`install-b/` client lane), the MelonLoader release files they use (`MelonLoader/` folder plus the
`version.dll` injector), and the Cpp2IL dump in `decomp/`. For the QA harness or `install.bat`
to work, place a MelonLoader 0.7.3 release under `install/MelonLoader` + `install/version.dll`
and keep two game copies at `install/` and `install-b/` beside this repo.

## Build

```
cd mod/ApfepCoop
dotnet build          # -> bin/Debug/net6.0/ApfepCoop.dll
```

## Install / uninstall (real game install)

```
install.bat           "E:\SteamLibrary\steamapps\common\A Place for Every Potion"
uninstall.bat         "E:\SteamLibrary\steamapps\common\A Place for Every Potion"
```

`install.bat` copies `install/MelonLoader/`, the `version.dll` injector and the built
`ApfepCoop.dll` into the game folder, and writes `steam_appid.txt` (5189500) so the game's
Steamworks calls get an app id when the game is launched outside Steam — without it the mod's
Steam lobby path stalls at startup. `uninstall.bat` removes them. Steam "verify integrity" also
restores a clean install. The script refuses to run if the game exe, injector or a built dll is
missing. Cycle tested: uninstall → install → `tools/qa.ps1` passes on the reinstalled lane.

## Play

Host: launch the game normally. In `MelonLoader/Latest.log` look for
`[apfep] steam lobby ready: room code 765...` — that decimal id is the room code.

Client: `APFEP_JOIN` must be set before the game starts (the mod reads it once at startup). Pick
one way:

- **One Command Prompt window** (lasts only for that window):
  ```bat
  set APFEP_JOIN=765...
  cd /d "E:\SteamLibrary\steamapps\common\A Place for Every Potion"
  "A Place for Every Potion.exe"
  ```
- **PowerShell**, same session:
  ```powershell
  $env:APFEP_JOIN = "765..."
  & "E:\SteamLibrary\steamapps\common\A Place for Every Potion\A Place for Every Potion.exe"
  ```
- **Permanent for your Windows account** (works with Steam launches; clear it after playing):
  ```bat
  setx APFEP_JOIN 765...      :: later: setx APFEP_JOIN ""
  ```

Steam's launch-options box adds arguments, not env vars, so launching the client through Steam
without `setx` will not join. Launch it outside Steam instead; Steam still connects (the game
ships its own Steamworks). Both players need the game plus Steam running. The mod has no in-game
UI; everything is env vars plus the log lines above.

QA-only mode (one PC, no Steam lobby): host `set APFEP_QA_PORT=24710`, client
`set APFEP_JOIN=24710`. `APFEP_SMOKE_AUTO=1` makes an instance drive the menu itself (used by
the QA harness).

## QA

```
powershell -File tools/qa.ps1 -Port 24710 -Seconds 30    # exit 0 = pass
```

Asserts: patches installed on both lanes, mutual peer discovery, both lanes store and broadcast
puppet moves, puppet follows into the lab, both report `Playing`, progress mirror matches the
host, no plugin errors ("sync patch ... failed" lines are the documented il2cpp limitation).

## Known issues

- Steam lobby transport is implemented but only localhost UDP is end-to-end verified; a
  two-account Steam lobby playtest is still outstanding.
- Generic game methods cannot be Harmony-patched under il2cpp interop (`SaveImpl`,
  `PersistentDataService.Save`, `Enter(eGameState, TPayload)` fail with "IL Compile Error").
  Equivalent hooks sit on patchable choke points (`Enter(eGameState)`, `AssignNewState`,
  `BackgroundSaveWriter.Write`, `UndoService.Record`).
- The puppet loads its own current level; if the host's level differs, the lab layouts differ
  (progress/state still mirrors). Forcing the puppet onto the host's level id is the next step.
- Tested with two players; 3+ over a lobby is untested.
- Puppets cannot interact with potions (input suppression); today co-op means one host drives
  the lab and puppets follow and appear in it.
