# Stage 5 (GPU performance): where it stands, 24.10

Target: 72 fps, 13.9 ms a frame, **including** the compositor's ~1.3 ms of GPU preemption (`scripts/quest-trace.sh`
now prints our own time and the compositor's separately). The rooms matter most: most of the game is played in them.

## Measuring

- **Build for traces without `-developmentBuild`**, with `-adbLaunch`:
  `scripts/refresh-runtime.sh && scripts/build-apk.sh lc-quest-vr -adbLaunch --build-only`
  (a full build without `--build-only` after Prepare-level changes).
  - The development player marks its GPU work with Vulkan debug labels, and the headset's profiler layer crashes on
    them within minutes (`QGLStateCache::EndDebugRegionInfo`).
  - On a release build, 36 views plus two series ran without a crash.
  - `-adbLaunch` declares optional hand tracking, so the build starts over adb while the controllers sleep; otherwise
    the system's "Switch to Controllers" dialog blocks the launch.
  - The last APK in work/build/lc-quest-vr.apk is such a build, with the `depthPriming` key, and is installed on the
    headset.
- Before any adb work, take the headset lock, which is shared with other sessions:
  - `mkdir ~/.quest-headset.lock` plus an `owner` file.
  - After the test: `scripts/quest-off.sh`, then `rm -rf` the lock.
- Profiler: `adb shell ovrgpuprofiler -e io.lcvr.quest` before starting the game.
- Start the game: `scripts/quest-test.sh [--install] moon=0 [interior=0|1|4]` (factory, manor or mineshaft; seed
  4242).
- Views:
  - `scripts/quest-views.sh scan [fractions]` traces every view.
  - `scripts/quest-views.sh ab "<frac yaw>"... -- "<key> <test> <default>"...` A/B tests QuestTuning keys.

## Done this stretch (commits)

- bd890eb: the outlines read 4 neighbours, not 8 (`CustomPassFeature.SpongeTaps`), −0.5…0.9 ms.
  - Outlines as a whole cost ~1.7 ms.
- 8a08c33: release builds for traces; `interior=` in quest-test.sh.
  - Shorter facility lamps (on by default, an LCVR setting):
    - Manor: 21.2 → 14.2 ms.
    - Mineshaft: 14 → 12.9 ms.
- 2228781: traces leave the compositor's preemption out.
  - The final pass's 3.2 vs 4.0 ms was that preemption, not our content.
- The commit after it: the `-adbLaunch` flag and the `depthPriming` QuestTuning key (not yet measured).

## Where the frame goes: factory, worst hall (`node 0.4 yaw 90`), release build

Own time ~14.8 ms plus compositor 1.3 ms.

| Change | Gain |
|---|---|
| All local lamps off (already at range ×0.5) | −2.6 ms (halos −0.5 of it) |
| renderScale 0.8 | −2.2 ms |
| Outlines off | −1.7 ms |
| Shadows | ≈0 |

- The main pass without lamps is still ~7.6 ms:
  - Binning ~1.5–1.9 ms.
  - The rest is URP Lit shading: ~500 instructions a fragment, **2.1 fragments a pixel**, measured earlier
    (docs/feasibility.md).
  - The level is static-batched into big Combined Meshes, so front-to-back sorting can't stop the overdraw.
- Other views:
  - Factory scan: 10.4–17 ms (most 11–14).
  - Manor's worst: ~17.
  - Mineshaft: ~13.

## Next steps

1. **Depth priming** (in the installed build, not yet measured). In the factory, compare the frame and passes with
   and without priming:
   ```
   scripts/quest-test.sh moon=0
   scripts/quest-views.sh ab "0.4 90" "0.1 0" -- "depthPriming 2 0"
   ```
   Then take a headset shot with `scripts/quest-shot.sh`. Shaders without a DepthOnly pass (our LayeredLit, Leaves,
   Halo, the game's shader graphs) may vanish under ZTest Equal. Expected:
   - Lit shaded once per pixel instead of 2.1 times: up to ~3 ms of the main pass.
   - CopyDepth (0.7 ms) may go too.
   - Cost: a second geometry pass (~1.5 ms binning).

   If it pays, set `depthPrimingMode` in UrpSetup and check the look, outside too.
2. Render-resolution option in LCVR's settings (like ShorterFacilityLights in QuestSettings.cs): 90% ≈ −1.1 ms,
   80% ≈ −2.2 ms in the hall.
3. FFR (#10): pays little while the main pass draws into an intermediate texture. Foveation only applies to the
   swapchain image, so only the final pass (~3.2 ms) would benefit.
4. Open headset checks (docs/headset-checks.md) need a wearer: outlines with 4 neighbours, shorter lamps (menu entry
   and look), LightFade pops, #12 monitors, #6 rain, #11 trees.
