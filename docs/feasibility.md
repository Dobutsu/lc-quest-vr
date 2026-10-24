# Lethal Company + LCVR on a Meta Quest 3 without a PC: how realistic is it

_Started 2026-10-16. Game: Lethal Company V81 (Steam build 22825947, Unity 2022.3.62f2). Mod: LCVR v1.5.0._

This follows the R.E.P.O. Quest port (RepoXR): the same approach (bake the mod's Harmony patches ahead of time,
export the game with AssetRipper, rebuild for Android with IL2CPP) and the same tools, copied and adapted.

## In short (stage 0, in progress)

| # | Question | Result |
|---|---|---|
| 1 | Is the game Mono (decompilable IL), not IL2CPP? | ✅ Mono, `Managed/Assembly-CSharp.dll` |
| 2 | Does LCVR match this game build? | ✅ `Assembly-CSharp` SHA-256 `5F7DB553…` is in LCVR's remote hash list for 1.5.0 ("V81 Patch") |
| 3 | Can LCVR's patches be baked? | ✅ 67 of 67 patch classes (2 compat classes skipped: MoreCompany, CullFactory), 0 new ILVerify errors |
| 4 | How much HDRP API does code use? | ✅ small: 65 members (game: lights' dimmer, decals, local fog, a few volume components; the rest is LCVR) |
| 5 | How much HDRP is in the assets? | ⚠️ everything: 775 of 920 materials are `HDRP/Lit`, 594 lights with HD data, 208 decals, 47 local volumetric fogs |
| 6 | Multiplayer with vanilla PC players | ✅ LAN path: a vanilla PC host in LAN mode answers a Unity Transport connection request from the Quest over Wi-Fi; Steam P2P: no ready path, see `docs/steam-p2p.md` |

**The main blocker is HDRP**, which does not run on Android. Unlike REPO (built-in pipeline, shaders recompiled
for Vulkan), the port has to move the game to URP 14. On the code side this is small; on the asset side it is a
mechanical material conversion plus a handful of hand-made shaders.

## Baking LCVR (`scripts/bake.sh`, `scripts/verify-bake.sh`)

`tools/HarmonyBaker` comes from the REPO port. LCVR shares RepoXR's layout (`[LCVRPatch(target, dependency)]` in
`LCVR.Patches`, runtime patcher `LCVR.Patches.HarmonyPatcher`), so the baker now derives those names from the mod
assembly. LCVR has no RPC prefixes: its VR networking rides on Dissonance messages.

| | universal | all |
|---|---|---|
| Patch classes applied | 12 | 67 (+2 compat skipped) |
| Methods baked into `Assembly-CSharp` | 14 | 124 |
| Other modified assemblies | DissonanceVoip, RP Core, XR Management | + InputSystem, Netcode, HDRP, XR Interaction Toolkit |
| Native engine methods hooked at call sites | none | `Camera.set_targetTexture`, `Cursor.set_visible`, `Cursor.set_lockState` |

Problems solved along the way:

- **A transpiler reads an asset bundle.** `InputPatches.DisableLegacyInputs` replaced a string with
  `AssetManager.NullActions.ToJson()` while patching; at bake time no bundle is loaded. Patch
  `patches/lcvr/0001-null-actions-at-runtime` emits a call that reads it when `PlayerActions` is constructed.
- **A stale transpiler that corrupts memory on PC.** `SpectatorGlobalAudioPatches` (on
  `StartOfRound.UpdatePlayerVoiceEffects`) counts 22 instructions back from `lowpassResonanceQ`. On V81 this leaves
  `set2D = false` storing into the `PlayerControllerB` object instead of `PlayerVoiceIngameSettings`. Mono runs it
  (writing a byte into an unrelated field of the player); IL2CPP would reject it. Patch
  `patches/lcvr/0002-spectator-voice-transpiler` matches the instruction pattern and lets the helper set `set2D`.
  Worth reporting upstream.
- **A patch on a closed generic type.** LCVR postfixes `BaseClient<NfgoServer, NfgoClient, NfgoConn>.ProcessReceivedPacket`
  (Dissonance). The baked body lands in the open definition `BaseClient<T0, T1, T2>`, so the transplanter now maps the
  closed instance and its arguments back onto the type's generic parameters. The game has only this instantiation.

Packages that get baked methods (InputSystem, Netcode, XR Management, XR Interaction Toolkit) will be built from
source in the Unity project, as in REPO, so those patches move into `patch_packages.py`. The HDRP and RP Core
patches go away or move to the URP side.

## HDRP API used by code (`tools/RefScan`)

`RefScan --target Unity.RenderPipelines.HighDefinition.Runtime --scan …` over `Assembly-CSharp`, firstpass, DunGen
and LCVR (output: `work/inventory/hdrp-refs.tsv`):

- **Game:** `HDAdditionalLightData.lightDimmer`; `DecalProjector` (material, fadeFactor, decalLayerMask);
  `LocalVolumetricFog.parameters` (albedo, meanFreePath); `CustomPassVolume`; volume components `DepthOfField`,
  `LiftGammaGain`, `MotionBlur`; `HDAdditionalCameraData` (clear color mode, custom frame settings, persistent history).
- **LCVR:** camera frame settings and `xrRendering`, `HDRenderPipelineAsset` settings (XR, volumetrics, LOD bias,
  fog budget), custom post-processes (`CustomPostProcessVolumeComponent`, `FullScreenCustomPass`), `LensDistortion`,
  `Vignette`, `ColorAdjustments`.

So an HDRP shim assembly (same type names, implemented on URP) stays small for code. The game's 33 `HDAdditionalCameraData`
and 594 `HDAdditionalLightData` components still have to exist as components (scenes reference them).

## Assets (`scripts/inventory.py`, `work/inventory/inventory.json`)

- 19 scenes (`level0`–`level18`), 1,054 MB of textures, 920 materials, 1,308 meshes, 305 particle systems, 5 VFX Graph assets.
- 182 shaders; materials by shader: `HDRP/Lit` 775, `HDRP/Unlit` 43, `HDRP/Decal` 23, `HDRP/LayeredLit` 8, Shader Graphs
  (`WavingGrass` 8, `WaterShaderHDRP` 5, `PosterizationFilter` 2, `HologramShader`, `WaterOnLensEffect`, `BlobShader`,
  `FurShader`), the game's own `Custom/Additive (Soft, Tintable)`, `Custom/LightningBoltShaderMesh`,
  `FullScreen/SpongePosterizeNew` (the posterize/outline look), ProBuilder, TextMeshPro.
- Lights: 363 point, 202 spot, 29 directional; 411 with shadows (a Quest budget will be needed, as in REPO).
- HDRP components: `HDAdditionalLightData` 594, `DecalProjector` 208, `Volume` 75, `VolumeProfile` 25,
  `LocalVolumetricFog` 47, `HDAdditionalCameraData` 33, `CustomPassVolume` 4. 27 cameras render into textures.
- Windows-only native plugins: `steam_api64`, `discord_game_sdk`, `opus` (Dissonance), `AudioPluginDissonance`,
  `phonon_fmod`, `NVUnityPlugin` / DLSS.

## LAN mode (multiplayer without Steam)

From the game code (`GameNetworkManager`, `MenuManager`, `PreInitSceneScript`):

- The launch screen picks **Online** (`InitScene`, `FacepunchTransport` over Steam P2P) or **LAN** (`InitSceneLANMode`,
  `disableSteam = true`, `UnityTransport`).
- In LAN mode the connection data is just `gameVersionNum` as ASCII; the host's approval checks the version, the player
  count (4) and that the game has not started. No SteamID, no kick list.
- The LAN scene's `UnityTransport` is serialized with `127.0.0.1:7777`, and there is no address field in the menu:
  a vanilla LAN client can only join its own machine. A host with "Allow remote connections" listens on `0.0.0.0:7777`.
  So the Quest client only has to set the friend's address (an IP field in the VR menu); the PC host stays vanilla.
- Netcode syncs scenes by a hash of their path: the port must keep the build's scene paths (`Assets/Scenes/*.unity`).

Live check (2026-10-16): the vanilla game (Steam install, in CrossOver) hosts in LAN mode with remote connections
allowed and listens on UDP `*:7777`. From the Quest 3 over Wi-Fi, a hand-made Unity Transport `ConnectionRequest`
(10 bytes: type 0, flags 0, an 8-byte session token, sent with toybox `nc -u`) gets an 18-byte `ConnectionAccept`
(`02 01`, our token, the host's token). The macOS firewall lets it through. Netcode on the host assigns client id 1,
then drops it on the connection timeout when no Netcode handshake follows; the game carries on. The full Netcode
handshake (config hash, approval) waits for a real client build.

`tools/LanProbe.Pc` (a BepInEx plugin that picks LAN and hosts or joins `-lanprobe-client=<ip>` with no clicks) is for
an automated two-instance test in a CrossOver copy; BepInEx did not load there yet (the bottle's `winhttp` override is
set for `REPO.exe` only), so the check above used the real game and a manual host.

## Stage 1: flat Android build on URP (2026-10-16)

A first APK runs on the Quest 3 as a flat window: launch screen, main menu in LAN mode, hosting on the headset, the
ship, and landing on Experimentation (moon scene loaded, dungeon generated). No pink materials.

```sh
scripts/export.sh              # AssetRipper -> work/export
scripts/make-project.sh        # -> work/project
scripts/build-apk.sh           # Unity: Prepare + BuildApk -> work/build/lc-quest-flat.apk
scripts/quest-diag.sh          # snapshot from the headset; also `click <button>`, `invoke <Type.Method>`
```

| Problem | Cause | Fix |
|---|---|---|
| HDRP does not run on Android | — | URP 14.0.12; HDRP's runtime DLL replaced by a shim of the same name and GUID (`tools/ShimMaker`, `scripts/hdrp_shim.py`): 296 types the assets and code use, serialized fields intact, renderer-bound bodies stubbed, setters keep their field store |
| Duplicate assemblies with URP's dependencies; Windows-only Input System | package DLLs in Assets/Plugins | `scripts/package_swap.py`: ugui, TMP 3.0.9, Input System 1.8.1, Mathematics, Burst, RP Core / Shader Graph / VFX 14.0.12 as packages; XR and HDRP config DLLs removed; Netcode, Transport, Collections stay the game's DLLs (they carry ILPP output) |
| Materials | 775 `HDRP/Lit` and more | `unity-tools/Editor/HdrpMaterialConverter.cs`: placeholder shaders keep the HDRP property blocks, values copied onto URP Lit/Unlit/Particles, URP's ShaderGUI sets keywords; the mask map serves as metallic and occlusion map |
| Lights | candela/lux under HDRP exposure | `scripts/convert_scenes.py`: `I · dimmer · e / π`, e at EV100 1.3 (to be tuned against the PC game) |
| IL2CPP: "Unresolved type reference System.Void ... scope UnityEngine.CoreModule" | UnityLinker injects "non-existent method" stubs for members the shim lacks (ProBuilder calls HDRP) | the shim scans every DLL that references HDRP |
| Cecil: stubs came out as empty P/Invoke methods | setting `PInvokeInfo = null` turns `pinvokeimpl` on | clear the info first, then the flag |
| Volume lookups throw every frame | `VolumeComponentMenuForRenderPipeline(typeof(HDRenderPipeline))` on HDRP volume components | attribute dropped from the shim |
| Online needs Steam | — | `LanBootstrap`: Online loads the LAN scene; the host to join comes from `files/lan_host.txt` |
| Voice | Dissonance imports `opus` and `AudioPluginDissonance` | `libopus.so` built with Dissonance's four ctl wrappers; `AudioPluginDissonance` (capture preprocessing) still missing: stage 4 |

Not yet: HDRP sky, volumetric fog, decals, the posterize custom pass, VFX Graph effects (stage 2); 24 FPS reported in
the flat window (not investigated).

## Stage 2: Lethal Company's look on URP (2026-10-16/17)

Calibrated against **reference renders**: a second project from the same export keeps the real HDRP package
(`scripts/make-reference.sh`) and renders the same views as the URP project (`scripts/reference-render.sh`,
`scripts/urp-render.sh`: `-neutral` for lighting only, `-composite` for the HUD camera's picture; extras place dungeon
rooms, switch volumes, lights and custom passes). `scripts/compare-set.sh` prints HDRP/URP brightness ratios per view.
On the outdoor views of Experimentation, Vow, Titan and the Company moon the URP build is now within 0–20 % of HDRP.

| Part | How |
|---|---|
| Outline/posterize pass ("LethalSponge") | rewritten for URP from the game's compiled shader; `CustomPassFeature` runs the game's CustomPassVolumes (the game still toggles them); fog taken out and put back as HDRP orders it |
| Volumes | `HdrpBridge` evaluates the HDRP volume stack per camera (HDRP's default profile included) and maps it onto URP: height fog and local volumetric fog with HDRP's blending and fades, fog colour from scattered sun and lamp light (colour temperature included), HDRI sky seen through the fog (`LCQuest/Sky`), post-processing (HDRP's grading measured as one tone curve → LUT, exposure with HDRP's clamp semantics) |
| Lights | candela/lux × exposure / π; Forward+ (per-object light limit hid the ship's lamps); URP light layers; additional-light shadows (lamps inside shades); shadowless fill lights ×0.4 (measured); soft large-radius lights dimmed; `lightDimmer` follows the game; the strongest shadow-casting sun is URP's main light |
| Lamp halos | additive camera-facing glows for the nearest lamps, scaled by the fog density (HDRP's volumetric glow) |
| Materials | HDRP Lit/Unlit/Decal and the game's graphs on URP; `LCQuest/LayeredLit` for the splat-mapped terrains; water, hologram, water-on-visor pass; decals mirrored as URP decal projectors with colour baked into their textures |

Findings worth remembering: AssetRipper's placeholder for HDRP's `DefaultFogVolume` shader graph left the first
reference renders without local fog (package Shader Graphs are now mapped too); the scenes set no static lighting sky,
so HDRP lights nothing with the sky (no ambient); fog variants were stripped by "Automatic" fog stripping;
`RenderSettings.fogColor` is gamma-encoded; `DynamicGI.UpdateEnvironment` is not synchronous on the device.

Open: Titan's eclipse tint and Rend's snow brightness unverified (the reference has no weather), VFX Graph effects
(bugs, spores, heat haze) not rendered, ColdOpen's posterize DrawRenderers pass, 24 FPS in the flat 2D window (CPU
main thread ~18 ms in a development build).

## Stage 3: LCVR in the APK (2026-10-17)

The VR build runs on the Quest 3: LCVR's launch scene (Local/Online), the VR main menu with LCVR's settings and
controls panels, hosting in LAN mode, the ship with LCVR's XR rig (XR Origin, hands, world-space HUD), landing on
Experimentation. Checked over adb without wearing the headset (`scripts/quest-test.sh`, `scripts/quest-shot.sh` for
the compositor's frame); controllers, interactions and the terminal keyboard still need a person in the headset.

```sh
scripts/make-project.sh        # VR by default (--flat: stage 1's plain game); bakes LCVR, imports its bundles
scripts/build-apk.sh lc-quest-vr -developmentBuild   # -adbLaunch instead for GPU traces (the profiler
                                                    # layer crashes on the development player's debug labels)
scripts/quest-test.sh --install ship   # or menu / moon
```

| Problem | Cause | Fix |
|---|---|---|
| LCVR's bundles are Windows/HDRP builds and its Unity project is not public | — | `scripts/export-lcvr.sh` (AssetRipper with the game's DLLs, LCVR.dll and XRI's DLL), `scripts/import_lcvr_assets.py` (container assets plus their GUID closure into Assets/LCVR; AssetRipper's DLL GUIDs are deterministic, so script references hold), rebuilt for Android by `LcvrBundles.cs` before the player; their manifest goes to the player build's code stripping |
| Packages compiled from source lose baked bodies | Input System, XRI are packages on Quest | HarmonyBaker `--callsite-hooks Unity.InputSystem` (the game's 25 calls to `InputSystem.actions` go through a wrapper); XRI's patch as a source edit (`scripts/patch_packages.py`, embedded without its tests) |
| No BepInEx | — | `tools/LCVR.Standalone`: BepInEx paths and logging, plugin info with a location, bundles extracted from the APK once per install; `[Replaces]` overrides for HarmonyX's Emit-based invoker and Dissonance's dependency check |
| Win32 dialogs, registry runtime search, game hash check; HDRP asset casts | LCVR is a Windows mod | `patches/lcvr/0003-android-standalone`; OpenXR features are added to the APK's (Meta Quest support stays) instead of replaced |
| IL2CPP linker: "Failed to resolve assembly MoreCompany" | LCVR's mod compatibility code | `0004-no-mod-compatibility` (no mods on Quest) |
| LCVR's launch scene never loaded | `PreInitSceneScript.Awake` → `DissonanceComms.TestDependencies` throws without AudioPluginDissonance (Windows-only); Unity then skips the script's Start, where LCVR's postfix loads the scene | override: the check passes; the preprocessor itself is stage 4 |
| `InitSceneManager.Awake` NullReference | HDRP's hidden "Default Volume" object | `0005-no-hdrp-default-volume` |
| World-space HUD text magenta | LCVR loads "TextMeshPro Always On Top" from its bundle by file name; the import had renamed it and left LCVR-only shaders out of the bundle list | original file names; LCVR's own shaders listed in the bundle |
| HUD font drawn by a placeholder | TMP's SSD shader starts with a BOM, so `package_swap.py` never matched its name | BOM-aware shader name pattern |
| VR main menu invisible: black, menu colours only in the stars (missed at first: the check looked at the stars and took the sound for the menu) | LCVR's backdrop spheres (Cloak.mat, black, transparent) follow the head; URP sorts transparents by distance to the bounds' centre, so they draw last, over the canvases. HDRP's front-face culling was dropped (always Back), and the Uncloak clip animates HDRP Unlit's `_UnlitColor` | `LcvrBundles.cs`: Cloak.mat at queue 2900, the clip retargeted to `_BaseColor`; the converter takes HDRP's forward cull mode (also the build-mode ghost outlines). `quest-diag.sh hide <path>`, canvases/renderers in the report and a flat render of the VR camera (`flat-*.png`, also over grey) to find such things |
| VR app launched over adb stops at "controllers required" | the controllers sleep while nobody wears the headset | development builds declare optional hand tracking (`DevHandTrackingManifest.cs`); `quest-test.sh` removes a leftover dialog |

Performance (VrApi, development build, ship on Experimentation, LCVR's default eye resolution 0.75): 24 fps, GPU frame
37 ms, CPU 30 % busy, so GPU-bound. Measured with `scripts/quest-gpu.sh` (switches in `QuestTuning.cs`): additional
light shadows ~6-10 ms, the posterize pass ~6 ms, decals/post ~1-3 ms; at render scale 0.3 still 10.7 ms. Stage 5.


## Stage 5: performance (2026-10-17, in progress)

Measured on the ship on Experimentation with `scripts/quest-trace.sh` (the GPU's render stage trace, one frame per
render pass; `adb shell ovrgpuprofiler -e io.lcvr.quest` before starting the game) and `dumpsys meminfo`. The headset
lay on a desk looking 48° up (mostly the ship's ceiling), so the absolute numbers are for that view.

| | Before | Now |
|---|---|---|
| FPS (VrApi) | 18 | 30 |
| GPU frame | 55-60 ms | 30-33 ms |
| Memory (PSS) | 2.68 GB, 1.2 GB left to the system | 2.0-2.2 GB, ~2 GB left |

| Cost | Found with | Change |
|---|---|---|
| Helmet visor's condensation plane, ~12 ms | transparent layer masks, then `quest-diag.sh hide` | switched off (`QuestPerformance`): a full-screen transparent lit quad 6 cm from the eyes, alpha 0, only glints; HDRP's reason for it (volumetric fog in front of the camera) does not apply |
| Additional lights' shadows, ~17 ms (9 lights: far floodlights and lamps 90-110 m away, two monitors' point lights at 6 maps each) | `quest-gpu.sh`/`quest-trace.sh` switches | shadow budget (`QuestPerformance.MaxShadowMaps`, 8 maps; spot 1, point 6): the lights that light most around the camera keep their shadows. 8 maps 7.5 ms, 4 6.5, 2 5.3, 0 2.5 (sun only) |
| Out of memory: the system's low memory killer took system apps (their "isn't responding" dialog then blocks launches, `quest-test.sh` dismisses it) and the game itself | memory section of `quest-diag.sh`'s report | textures without their top mip (`QuestPerformance.TextureMipLimit`, 881 → 460 MB); clips of 2 s or more Vorbis in memory (`QuestAudio`, 271 → 67 MB); shader code decompressed in at most 4 chunks of 4 MB per shader (157 → 125 MB) |

What is left (~32 ms in that view): the opaque pass ~20 ms, shadows 5-8 ms, full-screen passes ~5 ms (copy for the
outline pass, outline, post, final blit). The opaque pass is fragment-bound (95% of shader time, ~175 ALU per
fragment) but only partly resolution-bound: render scale 0.7 takes it from 19.6 to 15.9 ms. With every additional light
off it is still ~15 ms; layers Room (the ship) and Default cost most. `XRSettings.eyeTextureResolutionScale` (LCVR's
0.75) does not change the eye buffer here (2016×1760 per eye); URP's render scale does. Changing render scale or the
renderer at runtime allocates targets again and could tip the game into the memory killer (before the memory work).


Level views (headset lying level; `scripts/quest-bench.sh`: the ship, outside it, the facility's main entrance, after
landing on Experimentation; results also in work/logs/bench.txt): 37.6 / 29.8 / 21.5 ms a frame, the opaque pass
22.6 / 18.7 / 13.4 ms. Builds measured against it:

| Build | Ship | Outside | Entrance | Note |
|---|---|---|---|---|
| Lit (current) | 37.6 | 29.8 | 21.5 | |
| `LCQUEST_URP=forward,shvertex,nolodfade,nocookies` | 37.3 | 31.6 | 24.4 | Forward instead of Forward+, SH per vertex, no LOD cross-fade, no light cookies: no gain |
| `LCQUEST_LIT=simple` (Simple Lit for the 793 Lit materials) | 26.8 | 22.9 | 19.1 | Lit's own shading is the cost (per-stage counters: ~500 instructions a fragment, 2.1 fragments a pixel); but as converted the look breaks: lamps' light pools and the flashlight's highlight gone, the moon's ground and yellow railings grey |

Swapping a material's shader at runtime to compare (tried) breaks the materials' look; such comparisons need a build.


Facility rooms and enemies (2026-10-17): `quest-bench.sh` also measures three rooms inside (AI nodes at 0.3, 0.5,
0.75 of the list) and enemies spawned in front of the player (`QuestDiagnostics.Spawn`); `quest-test.sh moon` fixes the
map seed (`QuestDiagnostics.FixSeed 4242`), or each run has another facility. Repeated runs still differ by ±3 ms.

| Build | Ship | Outside | Entrance | Rooms inside |
|---|---|---|---|---|
| Lit | 37.6 | 31.7 | 24.4 | 20-22 |
| Lit, no reflection probes (now the default) | 30.4-31.8 | 26.3-27.4 | 23-27 | 14-21 |
| Lit, no specular highlights | 32.2 | 28.4 | | |

Reflection probes cost 5-7 ms: in Forward+ every pixel walks the probes of its cluster. Off for the Lit materials
(`HdrpMaterialConverter`, `LCQUEST_LIT=env` keeps them) and in `LCQuest/LayeredLit`; the look barely changes (metals
keep URP's ambient reflection colour). Specular highlights stay: the lamps' light pools on the ship's walls are
theirs. On the ship, runtime switches: shadow budget 2 instead of 8 saves ~3 ms but loses the floor's shadows and lets
the lamps light the walls through the frame; the outline pass ~3 ms; render scale 0.85 ~2 ms; the sun's shadow map
resolution and distance nothing measurable.


Memory, continued: the game's textures were ETC2 on the Quest (RGBA at 8 bits a pixel; the ASTC subtarget never
reached the player build). Player Settings' default format for Android is now ASTC (internal API before Unity 2023),
and `QuestTextures` sets ASTC 6x6 on every compressed texture: Unity's own ASTC choice was 4x4, as large as ETC2.
Textures 460 → ~230 MB (the report's 318 MB counts ~86 MB of the snapshot's own captures); the game at 1.9 GB after
landing on Experimentation. Vow, other moons: `quest-test.sh moon=<id>` (the flight takes ~20 s before landing).
On Vow the game reached 2.5 GB after the benchmark, ~0.7 GB of it graphics memory outside textures, render targets
and meshes (terrain, trees, driver buffers, Vulkan pipelines): next to look at. Vow's ship: shadows 7-10 ms, the
two exterior floodlights (44 m range, the forest in their shadow maps) the likely cost.

| Moon | Ship | Outside | Entrance | Rooms inside | With enemies |
|---|---|---|---|---|---|
| Experimentation | ~31 | ~27 | 23-27 | 14-21 | 14-15 |
| Vow | 33-47 | 36-40 | 20-21 | 13-21 | 13 |
| Titan | 30 | 29 | 24 | 13-20 | 10 |

## Next

- Steam P2P: see `docs/steam-p2p.md` (no ready path; LAN/IP for the first release, GNS + ICE as a time-boxed experiment).
- Stage 3 in the headset: tracking, both hands, interactions (ship lever, doors, terminal with the VR keyboard), a solo
  run; stage 4: voice (AudioPluginDissonance) and joining a PC host by IP from the VR menu; stage 5: GPU time (above).

## How to reproduce

```sh
cp -cR "<Steam>/steamapps/common/Lethal Company" work/game   # APFS clone, the install is never touched
gh release download v1.5.0 -R DaXcess/LCVR -p '*.zip' -D work/downloads && unzip work/downloads/LCVR-1.5.0.zip -d work/downloads/LCVR-1.5.0
scripts/bake.sh all && scripts/verify-bake.sh all
<venv>/bin/python scripts/inventory.py work/game --json work/inventory/inventory.json   # needs UnityPy
```

`work/` holds the game's files and everything derived from them, so it is in `.gitignore`.
