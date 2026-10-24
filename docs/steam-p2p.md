# Steam P2P without the Steam client: can the Quest join an Online lobby?

_Research of 2026-10-16. Question: can a Quest client with no Steam client join a vanilla PC host in **Online** mode
(Steam lobby + `FacepunchTransport`), not just in LAN mode?_

## In short

**Not by reusing anything ready-made, and not cheaply.** The full Valve path (Steam Datagram Relay) has no
independent client anywhere, and its relay code is closed. A narrower path looks possible but unproven:
**open-source GameNetworkingSockets (GNS) over ICE (direct UDP with NAT traversal), with a Steam-signed certificate
from SteamKit2 and Steam's own signaling relayed through the CM connection.** The first unknown, and the cheapest to
settle, is the CM message that carries P2P signals: one capture answers it. Two more follow (see option 2): whether a
vanilla host accepts an ICE-only connection, and trusting Steam's certificate authority in GNS. Until then LAN/IP stays
the plan.

| Piece | What the PC host expects | Available without the Steam client? |
|---|---|---|
| Lobby | Steam lobby with the host as owner | ✅ SteamKit2 (already done in the REPO port's SteamShim) |
| Transport API | `SteamNetworkingSockets.ConnectRelay(steamId)` = `ConnectP2P` to the host's SteamID, virtual port 0 | — |
| Identity certificate | Ed25519 key signed by Steam's CA for app 1966720 | ✅ SteamKit2 `SteamNetworking.RequestNetworkingCertificate` (EMsg `ClientNetworkingCertRequest`, 5621) — not yet tried; presumably needs an account that owns the game, and it must differ from the host's |
| Signaling (rendezvous) | `CMsgSteamNetworkingP2PRendezvous` delivered by the "Steam backend" | ❓ goes through the CM; the container message is unknown (see below) |
| Route: SDR relays | relay sessions, signed session requests, relay ping protocol | ❌ closed source, no independent implementation; protobufs and SDR config are public |
| Route: ICE (direct) | ICE candidates exchanged in the signals; allowed by the host's "share IP" Steam setting (friends by default) | ✅ in open-source GNS, with STUN; the WebRTC-based ICE needs the Google WebRTC submodule (`USE_STEAMWEBRTC`), and GNS also has a native ICE client (default on iOS; untested on Android) |
| Session crypto + SNP framing | the same GNS wire protocol | ✅ open-source GNS (same codebase as Steam's), but it must trust Steam's CA: the open-source build ships no root key, so Valve's public key goes into `STEAMNETWORKINGSOCKETS_HARDCODED_ROOT_CA_KEY` at build time |

## What the game does

`Facepunch Transport for Netcode for GameObjects.dll` (decompiled): the client calls
`SteamNetworkingSockets.ConnectRelay<ConnectionManager>(targetSteamId)`, the host
`SteamNetworkingSockets.CreateRelaySocket<SocketManager>()`, after `SteamNetworkingUtils.InitRelayNetworkAccess()`.
These Facepunch.Steamworks wrappers call the native Steamworks `ConnectP2P` / `CreateListenSocketP2P`: identity-based P2P, routed over SDR and,
where allowed, direct ICE. The transport sets no connection config, so the Steam client's defaults apply (including
which ICE candidates may be shared).

## Findings

**Open-source GNS** ([README](https://github.com/ValveSoftware/GameNetworkingSockets)): has P2P with ICE and a
pluggable signaling interface (`ISteamNetworkingConnectionSignaling`, payload `CMsgSteamNetworkingP2PRendezvous`);
SDR is compiled out (`STEAMNETWORKINGSOCKETS_ENABLE_SDR`, `CConnectionTransportP2PSDR` is not in the repo). Steam's
authentication and signaling service are Steam-only.

**The Steam client** (`steamclient64.dll` in the CrossOver bottle, strings and RTTI only): signaling is
`CP2PConnectionSignaling_SteamBackend`, incoming signals `CProcessP2PSignalFromSteamBackend` (and
`...FailureFromSteamBackend`), outgoing `SendP2PRendezvousTask`; next to them `k_EMsgClientP2PIntroducerMessage`,
`CMsgClientNetworkingCertRequest/Reply`, `CMsgClientNetworkingMobileCertRequest/Reply`, and the SDR config URL
`%s/ISteamApps/GetSDRConfig/v1?appid=%u`. The old `MsgClientP2PIntroducerMessage` (SteamKit `steammsg.steamd`:
SteamID, `EIntroducerRouting` {P2PVoiceChat = 1, P2PNetworking = 2}, 1450 data bytes) is the obvious candidate for the
container, but GNS allows signals up to 4200 bytes (`k_cbMaxSendMessagDataInRSVP`), so this is **not confirmed**.

**SteamKit2** has `SteamNetworking.RequestNetworkingCertificate(appId, ed25519PublicKey)` →
`NetworkingCertificateCallback`. Nothing for P2P signaling or SDR.

**Steam Link for Quest** (`com.valvesoftware.steamlink`, pulled from the user's headset; strings only): `libmain.so`
statically links Valve's `SteamNetworkingSocketsLib` for arm64 Android with symbols — `CConnectionTransportP2PSDR`,
`CConnectionTransportP2PICE`, `CMsgSteamDatagramP2PSessionRequest`, `GetSDRConfig`. It has no CM logon and no Steam
backend signaling: certificates and signals come from the user's own PC Steam client over Remote Play
(`CRemoteClient_AllocateSDR`). Proprietary, so not usable as a library; useful only as a reference for the SDR wire
protocol if that route is ever taken.

**Elsewhere:** no independent SDR client exists. GitHub has SDR "server pickers" that firewall relays and the
SKYNET emulator, which emulates SDR locally for offline play. The SDR protobufs are public
([SteamDatabase/Protobufs](https://github.com/SteamDatabase/Protobufs): `steamdatagram_messages_sdr.proto`,
`steamnetworkingsockets_messages*.proto`).

## Options

1. **LAN/IP (current plan).** Works today with a vanilla host (`docs/feasibility.md`). Over the internet the host needs
   a forwarded UDP 7777 or a VPN (Tailscale, ZeroTier, Radmin VPN).
2. **GNS + ICE + Steam signaling (research prototype).** SteamKit2 logs in, requests the certificate, sends the
   ConnectRequest signal to the host through the CM; open-source GNS (built for arm64 Android, flat C API) runs ICE
   and the session. Custom signaling takes a C++ `ISteamNetworkingConnectionSignaling` object, so a small native glue
   library is needed between GNS and C#. No relays: fails when both NATs are hostile, and needs the host's Steam to
   allow sharing its IP with the Quest account. That is friends only by default, so in practice this joins friends'
   lobbies, not strangers' public ones. Open question: whether the host's Steam client accepts a peer that offers
   ICE but no SDR route. Steps:
   - capture the CM traffic of a real connection (NetHook2 in the CrossOver Steam client while it joins or hosts an
     Online lobby with a friend) to identify the signal container;
   - request a networking certificate for app 1966720 with SteamKit2 (one call, harmless);
   - a Mac prototype: GNS `ConnectP2PCustomSignaling` with a SteamKit-backed signaling object, against a friend's
     vanilla Online host; then port to Android and into a `FacepunchTransport` replacement.
   Rough cost: 1–2 days to the capture verdict, then 2–4 weeks if it works.
3. **Full SDR client.** Reimplement the relay protocol from the protobufs and Steam Link's symbols. Months, with
   nothing to compare against; not worth it while option 2 is open.
4. **Steam bridge.** A PC or Mac that stays on, running the real Steam client logged in as the Quest player plus a
   small Steamworks app that calls `ConnectP2P` to the host and forwards the transport's messages to the Quest over
   UDP. Uses only the official API, works with any host; the cost is that a computer must be on. A fallback if
   option 2 fails. **Built** (2026-10-23): `tools/SteamBridge`, see docs/multiplayer.md.

**Legal note.** Options 2 and 3 emulate Valve's networking protocols for multiplayer, which the Steam Subscriber
Agreement 2.G(ii) addresses directly — a stronger case than the REPO port's SteamKit login (see the REPO port's
`docs/public-release.md`). LAN mode and option 4 avoid it.

## Recommendation

Keep LAN/IP as the multiplayer of the first release. Take option 2 only as a time-boxed experiment, starting with
the NetHook capture (needs a friend online in Lethal Company) and the certificate request; decide after that.
