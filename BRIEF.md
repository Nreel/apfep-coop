# Co-op mod for "A Place for Every Potion" — environment contract

Game: Steam appid 5189500, Unity 6000.3.23f1 IL2CPP (metadata v39), version 1.2.6,
x64, Windows. Test install: `L:/agent test space/apfep-coop/install` (MelonLoader
v0.7.3 staged: `version.dll` proxy + steam_appid.txt=5189500). Real install untouched.

Loader verified working: MelonLoader v0.7.3 boots the game (Il2Cpp game type,
interop assemblies generated, Harmony available via MelonLoader).

## Dump sources (READ-ONLY inventory files)
Cpp2IL dummy DLLs (full type/method signatures; bodies are stubs):
`L:/agent test space/apfep-coop/install/MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/*.dll`

Text inventories already extracted (one type + its methods/fields per line):
- `L:/agent test space/apfep-coop/decomp/CodeBase.list.txt` (546k lines) — game framework/services
- `L:/agent test space/apfep-coop/decomp/Common.list.txt` (210k) — shared data/types
- `L:/agent test space/apfep-coop/decomp/Assembly-CSharp.list.txt` (151k) — game assemblies + plugins
- `L:/agent test space/apfep-coop/decomp/com.rlabrecque.steamworks.net.list.txt` (142k) — Steamworks.NET wrapper

You can run more lists with:
`cmd /c powershell -NoProfile -Command "dotnet exec 'L:\agent test space\apfep-coop\tools\DecompTool\bin\Release\net9.0\DecompTool.dll' --list '<dll>'"`
(note: native exe invocation quoting is unreliable in this shell; use `dotnet exec`,
or the DecompTool source at `tools/DecompTool`.)

## Mod build target
- C# MelonLoader plugin (net6, `install/MelonLoader/Plugins` for test runs).
- Harmony patches on Il2Cpp types via Il2CppInterop (AccessTools over interop types).
- Networking: Steam lobbies + ISteamNetworkingMessages relay via the game's own
  bundled `com.rlabrecque.steamworks.net.dll` (Steamworks.NET managed wrapper) —
  the bundled steam_api64.dll exports the full flat API (observed). UDP localhost
  fallback transport for two-instance QA on one PC.
- Model: host-authoritative state sync, puppet players, snapshot-on-join.

## Deliverable for this slice
A write-up file `L:/agent test space/apfep-coop/ARCHITECTURE.md` with labelled
observations (exact type/method names from the dumps — no guesses; say unknown
when the dump can't answer).
