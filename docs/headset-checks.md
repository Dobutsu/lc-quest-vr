# Headset checks

Changes that compiled but have not been seen on the Quest yet. `scripts/quest-test.sh` prints the open items when it
finishes; tick an item (`- [x]`, with the date and what was seen) once it is checked, or turn it into an issue if it fails.

## #2 No lens distortion, Panini, CA or film grain in the headset (3b937cb)

- [x] `scripts/quest-diag.sh` report: the headset camera's bridge line shows `ca 0.00 grain 0.00 lens 0.00`
      (the radar MapCamera may still show `lens 0.45`, which is expected). 18.10: MainCamera `vignette 0.00 lens 0.00
      ca 0.00 grain 0.00`, MapCamera `grain 0.06`
- [x] Insanity: no warp and no colour fringes. Trigger: `scripts/quest-diag.sh invoke "PlayerControllerB.JumpToFearLevel 1 false"`
      (invoke needs every argument), then a snapshot while the filter fades in. 18.10 (work/diag/vr/check2-fear.jpg):
      no warp, no fringes on the ship. The report then said `ca 0.79`: its flat render of the camera is not stereo,
      so the bridge does not zero it for that frame
- [x] Damage: the red vignette still flashes (LCVR's VolumeManager.TakeDamage). Trigger:
      `invoke "LCVR.Rendering.VolumeManager.TakeDamage"`. 18.10: report `vignette 0.44` half a second after; the
      screenshot (check2-damage2.jpg) shows only reddish corners, the capture crops the eye's edges

## #4 LOD0 within half its reach (Lod0Reach 0.5)

Measured and looked at on the headset with live tuning (18.10, shots work/diag/vr/lod0reach-*.jpg); the build's
default has not been worn.
- [x] Walking through Vow's or March's forest: trees switching to their next level at ~10 m do not pop noticeably.
      18.10 (worn, Vow): the switch is visible but does not matter; the user would keep the simplified level everywhere
- [x] Looking down at the feet, on the ship and outside: no black box (the local body's later LOD levels, held
      at the first). 18.10 (worn): none at the feet. An intermittent black sliver at the bottom edge of metacam shots was seen with every LOD
      setting and with the body's renderers hidden; it may be the capture

## #6 Narrow rain drops near the eyes (RainMaxSize 0.03)

- [x] Measure: `scripts/quest-bench.sh --level 2 --weather 1 rain` with the build and with
      `invoke "LCQuest.QuestTuning.Set static:LCQuest.QuestPerformance.RainDensity 1"` and `... RainMaxSize 0.5` (the
      game's), outside the ship; record the transparent pass in issue #6. 22.10 (quest-trace.sh toggling live, Vow by
      the ship's door, rainy and stormy): no difference beyond ±1 ms noise, even with no rain at all; see issue #6
- [ ] Rainy and stormy weather (weather=1, 2), worn: the rain still reads as rain from the ship's door and in the open,
      drops passing close to the eyes are thin streaks, not smears; the splashes on the ground still show

## #11 Trees drawn with LCQuest/Leaves (CheapLeaves, LeavesPixelLights)

Measured and compared in screenshots (22.10, work/diag/vr/leaves2-*.jpg); not worn yet.
- [ ] Vow's or March's forest by day and at night with the flashlight, worn: the trees read as before (no specular on
      bark or needles is expected), leaf edges keep their cutout, no flicker of the lights across trunks

## #12 Ship cameras: body cam only while shown, no shadows in texture cameras (IdleBodyCamOff, TextureCameraShadows)

Unmeasured on the moon: the cameras draw only while their monitor is in view, and a headset nobody wears cannot be
turned to face it (`tp` yaw does not turn the view in VR).
- [ ] Worn, on a moon inside the ship facing the monitors: the radar map and the door camera's screen still show (the
      door camera's picture now has no shadows); `scripts/quest-diag.sh` report's `renders` lines: HeadMountedCamera
      near 0/s while the radar follows you. Then `scripts/quest-trace.sh "static:LCQuest.HdrpBridge.TextureCameraShadows 1"`
      and `... 0` to measure the shadow cost of the small cameras

## Facility lights beyond their HDRP fade distance culled (HdrpBridge.LightFade)

No frame gain measured in the factory (the costly lamps are the ~25 within 30 m); kept because HDRP drops them too.
- [ ] Worn, in a long factory hallway: lamps farther than ~30 m do not pop on or off visibly as you walk towards
      them (HDRP faded them over the last few metres, this cuts); the halos of far lamps behave the same

## Shorter facility lights (LCVR settings > Performance > Shorter Facility Lights, HdrpBridge.FacilityLightRange 0.5)

Measured unworn (23.10, Experimentation): factory hall 21.3 -> 16.2 ms, wall view 21.6 -> 13.6; the flat snapshots
(work/diag) barely differ, the near walls are lit by night vision, which keeps its range. Manor (interior=1): 21.2 ->
14.2, 21.4 -> 17.0, 16.4 -> 12.7 ms; mineshaft (interior=4): 13.8-14.3 -> 12.9 ms.
- [ ] The option shows in LCVR's settings menu under Performance, with its description; switching it off in the menu
      brings the full ranges back without a restart
- [ ] Worn, factory / manor / mines: rooms still read as lit around their lamps, no hard edges of light on walls; the
      flashlight and night vision unchanged; lamp halos as before

## Outlines from 4 neighbours (CustomPassFeature.SpongeTaps 4, the diagonals)

Measured unworn (23.10, Experimentation, factory): final pass 4.5-5.0 -> 3.2-3.3 ms in the hall, 3.8 -> 3.2 at a wall;
the outlines as a whole cost ~2.3 ms. Headset shots at 8 and 4 differ on 0.13% of pixels.
- [ ] Worn, in the factory and outside: outlines on thin things (railings, pipes, grates, the ship's edges) as solid
      as before, no shimmer or broken lines while turning the head

## Steam bridge (tools/SteamBridge, SteamBridgeTransport)

Needs a friend hosting Online (vanilla, another Steam account) and a Windows PC (or the Mac's CrossOver bottle) with
Steam logged in as the Quest player and LCQuestSteamBridge.exe running (docs/multiplayer.md).
- [x] Main menu: the address panel's "Via Steam bridge" key and the status line under it fit in the panel frame.
      24.10: they stick out of the frame a little to the right; the user: looks fine
- [ ] With no friend in a lobby: "Join LAN session" fails with the bridge's reason on the status line, the menu recovers
- [x] Joined a friend's Online lobby in orbit: the host and the headset see each other, names are Steam names
      (no "Nameless"/"Player"), both hear each other's voice. 24.10: real names, voice clear both ways
- [ ] A PC friend joins after the headset: the headset survives the host's name RPC and shows the new name
- [x] Landing on a moon and back with the bridge: no stalls beyond what LAN has. 24.10: landed, ran through the facility
- [ ] Leaving from the headset: the bridge window says "The headset left" and the account leaves the lobby
- [ ] After a bridge session, hosting from the headset (LAN) still works

## Quest 2, Pico, HUD distance, launch screen (24.10)

- [x] Quest 2 runs the Quest APK: 24.10 automated run to Experimentation, correct image; 27 fps in the ship, 23 at the
      door, CPU-bound (main thread 35-41 ms)
- [ ] Worn: tips, warnings and notifications at 0.75 m (patches/lcvr/0007) read comfortably and at the same size
- [ ] Launch screen: only LOCAL, centred where the two buttons were (patches/lcvr/0006); the main menu works
- [ ] Pico 4: lc-pico-vr.apk starts, the microphone prompt comes in the main menu and voice works (docs/pico.md)
- [ ] Diagnostic build (lc-quest-vr-diag, DiagnosticLog) on a Quest: files/logs/<time>/game.txt fills with stats every
      5 s and logcat.txt with the process's log (logcat started through Runtime.exec)
