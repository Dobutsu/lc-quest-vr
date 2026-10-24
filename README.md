# Lethal Company VR on Quest 2, Quest 3 and Pico 4, standalone

Play [Lethal Company](https://store.steampowered.com/app/1966720/Lethal_Company/) in VR on a Meta Quest 2, Quest 3 or
Pico 4 without a PC, and play with your friends on PC. Their game stays vanilla, with no mods.

The game is built **from your own copy of Lethal Company** on Steam. This repository and its releases contain no game
files.

## Built on LCVR by DaXcess

This port is built on **[LCVR](https://github.com/DaXcess/LCVR)** by **[DaXcess](https://github.com/DaXcess)**. LCVR
provides all of the VR: head and hand tracking, motion controls, VR interactions with the ship and items, the VR HUD
and menus, and support for VR and flatscreen players in the same game.

This project does not add VR. It takes LCVR and makes it run on the headset itself:

- bakes LCVR's patches into the game;
- builds the game for Android, with the HDRP renderer replaced by URP for the mobile GPU;
- adds what a PC normally provides: voice chat on Android and a way to join Steam lobbies.

If you enjoy Lethal Company in VR, star [LCVR](https://github.com/DaXcess/LCVR) and get it on
[Thunderstore](https://thunderstore.io/c/lethal-company/p/DaXcess/LethalCompanyVR/) for PC VR.

**Please do not report problems with this port to LCVR or DaXcess.** Open an issue here instead. This is an unofficial
project that DaXcess does not maintain or endorse. The port's own changes to LCVR live in
[`patches/lcvr`](patches/lcvr); they are free for LCVR to take if any of them are useful.

## What works

- **VR on the headset**, on the ship and on the moons, inside the facilities, at about 30 FPS on a Quest 3 (the
  performance work is ongoing). The Quest 2 runs the same build more slowly (23–27 FPS on the first moon).
- **Playing with friends on PC, voice chat included:**
  - friends host in **LAN** mode and you join by their IP (they need UDP port 7777 reachable: the same network,
    Tailscale, or a port forward);
  - or friends host the usual **Online** game and you join through the **Steam bridge**, a small Windows program
    that runs next to Steam on your own PC (logged in to your account) and joins their lobby for the headset. It uses
    only the official Steam client. Tested with a friend's vanilla game: Steam names, voice both ways, landing and
    the facility.
- **Hosting from the headset** in LAN mode.

**Pico 4**: the builder converts the Quest build with [ovrport](https://github.com/ovrport/app). Not tested on a Pico
yet; see [`docs/pico.md`](docs/pico.md).

## How to get it

Download **LCQuestBuilder** for Windows from [Releases](../../releases/latest) and follow its `README.txt`. In short:

1. You need Windows 10/11 (64-bit), 16 GB RAM, about 60 GB of free disk space, Lethal Company on Steam, a Quest 2,
   Quest 3 or Pico 4 in developer mode with a USB cable, and a free Unity account.
2. The builder finds your game and downloads everything else itself: Unity 2022.3, AssetRipper, Python packages and
   the LCVR release, each from its original source with a pinned SHA-256. It then builds the APK on your PC.
3. The first build takes one to a few hours: Unity compiles the game's shaders and data once.
4. It installs the APK on the headset. The game appears in the Library under *Unknown Sources*.

For Online lobbies, also download **LCQuestSteamBridge** from the same release page.

**Do not share the APK.** It contains the game's files. Everyone builds their own from their own copy.

## How it works

```
your Lethal Company (Steam)
  └─ AssetRipper ──────────► Unity project (the game's scenes, prefabs, scripts as DLLs)
  └─ HarmonyBaker ─────────► LCVR's Harmony patches baked into the game's assemblies (no runtime patching on IL2CPP)
  └─ scripts/ (Python) ────► the export made fit for URP and Android: an HDRP shim, shaders, scenes, textures
  └─ Unity 2022.3 (IL2CPP, OpenXR, Vulkan) ─► Android APK ─► Quest
                                                         └─ ovrport ─► Pico
```

- `builder/`: the Windows builder (Avalonia app plus a CLI) that runs the whole pipeline ([docs/builder.md](docs/builder.md)).
- `tools/SteamBridge`: the Steam bridge ([docs/multiplayer.md](docs/multiplayer.md)).
- `tools/LCVR.Standalone`: starts LCVR without BepInEx; Android overrides.
- `tools/HarmonyBaker`, `tools/AssemblyRenamer`, `tools/ShimMaker`: make the Mono mod and HDRP work under IL2CPP and URP.
- `patches/lcvr`: the changes to LCVR. `unity-tools/`: the Unity editor and runtime helpers. `native/`: Android libraries.
- `docs/`: design notes, from [`feasibility.md`](docs/feasibility.md) (how the port came together) on.

Development happens on a Mac with the same pipeline as scripts (`scripts/make-project.sh`, `scripts/build-apk.sh`).

## Legal

This is an unofficial fan project. It is not affiliated with or endorsed by Zeekerss (Lethal Company), DaXcess (LCVR),
Valve or Meta. Lethal Company and its assets belong to Zeekerss. The repository contains only the port's own code. Every
player builds the game from a copy they own.

Licensed under the **GNU GPL v3.0** (see [LICENSE](LICENSE)), the same license as LCVR, which the port includes and
modifies. Third-party components and their licenses are listed in [`builder/SOURCES.md`](builder/SOURCES.md) and
[`builder/release/THIRD-PARTY-NOTICES.txt`](builder/release/THIRD-PARTY-NOTICES.txt).

## Credits

- **[DaXcess](https://github.com/DaXcess)**: [LCVR](https://github.com/DaXcess/LCVR), the VR mod this port is built on.
- **Zeekerss**: [Lethal Company](https://store.steampowered.com/app/1966720/Lethal_Company/)
- [AssetRipper](https://github.com/AssetRipper/AssetRipper), [BepInEx](https://github.com/BepInEx/BepInEx) and
  [HarmonyX](https://github.com/BepInEx/HarmonyX), [Facepunch.Steamworks](https://github.com/Facepunch/Facepunch.Steamworks),
  [Avalonia](https://github.com/AvaloniaUI/Avalonia), [Opus](https://opus-codec.org/) and
  [ovrport](https://github.com/ovrport/app) (Pico).
- Port by **[Dobutsu](https://github.com/Dobutsu)**.

**Made with AI.** Most of the code, scripts and documentation here were written by
[Claude Code](https://claude.com/claude-code), Anthropic's AI coding agent, under Dobutsu's direction. Builds are
tested by people on a real Quest 3, including multiplayer with friends on PC.
