Lethal Company VR for Quest (and Pico) - test build for friends
================================================================

Only for people who own Lethal Company on Steam. Do not share these files further: the APKs contain the game.

Files
  lc-quest-vr.apk   Quest 2, Quest 3, Quest 3S, Quest Pro. Install with SideQuest (developer mode on).
  lc-pico-vr.apk    Pico 4. Not tested on a Pico yet.
  lc-pico-vr-diag.apk  the Pico build that logs everything (slower). If the Pico build fails, install this one,
                    play until it fails, connect the Pico to a PC over USB (allow file access on the headset) and
                    send the folder Android/data/io.lcvr.quest/files/logs.
  SteamBridge.zip   for a Quest player who joins a friend's normal Online (Steam) game. See below.
  LanJoin-PC.zip    for PC players who join a LAN game hosted on another machine.

On the headset the first screen has one button, LOCAL: the game always runs in LAN mode there (no Steam on it).
Everyone needs the same game version (V81).

Joining friends who host the normal way (Online, Steam), with no mods on their side
  1. On a Windows PC on the same network as the headset, with Steam logged in to YOUR account (which owns the game;
     not the host's PC), unpack SteamBridge.zip and run LCQuestSteamBridge.exe. Allow it through the firewall.
     The PC stays on while you play.
  2. The host clicks Host (Online) and stays in orbit. They invite you from the game (accept in Steam), or you click
     their name in the bridge window.
  3. On the headset: main menu, the address panel on the right: "Via Steam bridge: On", type the address the bridge
     window shows, then "Join LAN session".
  Step by step, with what to do when it fails: STEAM-BRIDGE.txt (English and Russian).
  If it does not work, send LCQuestSteamBridge.log (next to the exe).

LAN games (a PC hosts in LAN mode)
  The host: LAN, Host, "Allow remote connections". It must be reachable on UDP port 7777 (same network, Tailscale,
  or a port forward). On the headset: "Via Steam bridge: Off", type the host's IP, "Join LAN session".
  Other PC players: unpack LanJoin-PC.zip into the game's folder; a "Host IP" field appears next to Join.
