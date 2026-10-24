# Multiplayer (stage 4)

The Quest has no Steam client, so the port always runs the game's LAN mode and joins a host by IP. A vanilla
Online (Steam) lobby is reachable only through the Steam bridge, a PC program that joins the lobby for the
Quest player (below; why there is no direct way: docs/steam-p2p.md).

## How a session works

- **Host: a PC with the plain game.** Launch in LAN mode, Host, "Allow remote connections", Confirm. It listens on
  UDP 7777. Players can join only while the ship is in orbit.
- **Quest players:** the main menu has an address panel to the right of the buttons. Type the host's IP on its keypad
  ("Del" erases), then "Join LAN session". The address is kept (`lan_host.txt` in the app's files). The panel also
  shows the headset's own addresses, for when a Quest hosts.
- **Other PC players:** vanilla LAN joins only 127.0.0.1. `LanJoin-PC.zip` (BepInEx 5 x64 and
  `tools/LanJoin.Pc`), unpacked into the game's folder, adds a "Host IP" field beside "Join LAN session". The address
  is kept in `BepInEx/config/lcquest.lanjoin.cfg`. Blank joins the same PC, as vanilla does.
- Everyone runs the same game version (V81; the menu shows it under the buttons).
- Voice: Dissonance as in the game. On the Quest, `native/dissonance` stands in for AudioPluginDissonance, which the
  game ships for desktop only. It passes the microphone through and detects speech by level, with no noise
  suppression or echo cancellation. Headphones avoid echo for the others.

## Remote players

LAN mode needs the host's port reachable from every player:

- **Tailscale** (or ZeroTier) on every device: the PC client, and the Android app on the Quest (sideloaded). Join the
  host's 100.x address. The Quest's panel lists its Tailscale address too.
- **Or a port forward:** the host's router forwards UDP 7777 to the host PC, and players join the router's public IP.

## Joining an Online lobby: the Steam bridge

For friends who host the usual way (Online, Steam lobby) with no mods. The Quest player needs a Windows PC with
Steam logged in to **their own** account (which owns the game; not the host's account, not the host's PC), on the
same network as the headset. That PC stays on while they play.

1. On the PC: unpack `SteamBridge.zip` and run `LCQuestSteamBridge.exe`. Steam shows the account playing Lethal
   Company. The window shows the address to type on the Quest and lists friends who are in Lethal Company lobbies.
   Allow it through the Windows firewall (private networks) when asked.
2. Pick the lobby: the host invites this account from the game (accept in Steam: "Join game"), or click the friend
   in the window. With only one friend in a lobby, that one is used.
3. On the Quest: main menu, address panel, "Via Steam bridge: On", type the PC's address, "Join LAN session". The
   line under the toggle says what happened ("Joined ...", or why not).

The bridge does what the game's Online join does: checks the lobby (version, "joinable", not full), joins it, and
connects to the host over Steam networking. It then relays each message between the host and the headset (TCP port
7777, `unity-tools/Runtime/BridgeProtocol.cs`). To the host and the other players the Quest player is the bridge's
Steam account, with its name. When the headset leaves, the bridge leaves the lobby; it then waits for the next join.

The window's log is also written to `LCQuestSteamBridge.log` next to the exe (what to send when something goes
wrong). `--console` runs it without the window (stdin: a friend's number, `a` for automatic, `q`), as on the Mac in
the CrossOver Steam bottle for testing; `--screenshot <folder>` renders the window's states with sample data.

Pieces: `tools/SteamBridge` (the PC program: Avalonia window, Facepunch.Steamworks), `SteamBridgeTransport` (the Quest's transport for
this join; hosting stays on LAN), and an Android override of Steamworks' `Friend.Name` (`LCVR.Standalone`), which an
Online host's player-name RPC calls on every client: the names come from the bridge.

## Packaging a test build

`scripts/package-friends.sh` puts `lc-quest-vr.apk`, `LanJoin-PC.zip` and `SteamBridge.zip` in `work/release`. The APK contains the game's
files: only for friends who own the game, never published.

## Tested

- 18.10: the Quest joined a vanilla PC LAN host (CrossOver on the Mac, same Wi-Fi); voice came through.
- 24.10: the Steam bridge (in the Mac's CrossOver Steam bottle): the Quest joined a friend's vanilla Online lobby
  (invite accepted in Steam), connected over Steam in 2 s and played ~9 minutes; the user: "everything works". The
  host's lobby closed at landing (the game leaves it then) without affecting the session. Steam names on both sides,
  voice both ways, landed and ran through the facility.
- Not yet: the LanJoin plugin on a PC, Tailscale on the Quest, a port forward, more than two players.
