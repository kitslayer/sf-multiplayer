# Phase 6.15 — Chat-command admin interface

**Status:** design only. Captured from ALKA's `docs/MOD_CLIENT.md` — these are commands the patched `Assembly-CSharp.dll` already parses on the client and emits as `PktPlayerTalked` (msgType 12) packets to the server.

## Protocol shape

- **Inbound carrier:** `PktPlayerTalked` (msgType 12)
- **Channel:** `(playerIndex * 2) + 3` — per ALKA's MOD_CLIENT doc. So slot 0 talks on ch3, slot 1 on ch5, slot 2 on ch7, slot 3 on ch9. Our `SendBroadcastPrefix` already extracts channel from `__args[5]`; same routing works inbound via `data[data.Length - 1]`.
- **Body:** unverified, presumably a length-prefixed UTF-8 string. Worth dumping a few packets to confirm.

## Commands the patched DLL emits

From ALKA's docs (Spanish; translated):

| Command         | Who         | Effect                                            |
|-----------------|-------------|---------------------------------------------------|
| `/start`        | Lobby owner | Forces all players ready + starts the match       |
| `/options`      | Anyone      | Opens client-side options UI (server no-op)       |
| `/code`, `/room`| Anyone      | Echo the lobby's room code back to chat           |
| `/join CODE`    | Anyone      | Switch this client to a different lobby           |
| `/newlobby`     | Anyone      | Create + move to a new lobby (auto code)          |
| `/public`, `/private` | Owner | Toggle whether others can `/join` this lobby     |
| `/invite USER`  | Owner       | Mark a SteamID as allowed-to-join                 |

## What we'd implement on the server

1. **Pure relay → handler** for `PktPlayerTalked` on owner channels. Today we relay-to-others; for commands we need to ALSO parse and act.

   ```csharp
   case PktPlayerTalked:
       RelayBodyToOthers(cli, msgType, data, bodyOffset, bodyLen, channel);
       TryHandleChatCommand(cli, data, bodyOffset, bodyLen, channel);
       break;
   ```

2. **Body parse** — extract the string. Probably 1 byte length + N bytes UTF-8. Confirm with a wire dump.

3. **Command dispatch** — single switch:

   ```csharp
   private void TryHandleChatCommand(SfClient sender, byte[] data, int off, int len, byte channel)
   {
       string msg = ReadChatString(data, off, len);
       if (!msg.StartsWith("/")) return;
       var parts = msg.Split(' ');
       switch (parts[0].ToLowerInvariant())
       {
           case "/start": HandleStartCommand(sender); break;
           case "/code":
           case "/room":  SendChatTo(sender, $"Room: {LobbyCode}"); break;
           case "/join":  HandleJoinCommand(sender, parts); break;
           case "/newlobby": HandleNewLobbyCommand(sender); break;
           // ...
       }
   }
   ```

4. **Server → client chat** — we need to *send* a `PktPlayerTalked` back with the response. Same wire shape as inbound but originating from the server (`steamID = 0` in envelope, channel = recipient's owner-channel).

## Path A specifics

ALKA's server is Go, his lobby concept lives there. Ours is the headless SF process. For Path A v1 (multi-process sharding), commands like `/join CODE` would need to:
- Tell the connecting client to disconnect from this oracle
- Re-handshake with the target oracle on its port

That's a real disconnect/reconnect dance. The patched DLL doesn't natively do this — it'd need to be told to redirect via a custom packet, or kill and restart with new launch args.

For Path A v2 (in-process sharding, Phase 6.13 v2), `/join` is much cleaner: server moves the client's `LobbyCode` and starts routing their packets to the new shard.

So implementing `/join` properly is gated on v2 sharding. The other commands (`/start`, `/code`, `/options`, `/newlobby`) work in v1.

## Why this matters

- Real admin interface for hosts (today, host-side controls go through Steam options menus that don't exist on the dedicated path)
- Lobby code becomes user-visible (today we generate codes but never tell anyone)
- Unblocks the "in-game server browser" story when paired with `/join`

## What's needed before shipping

1. Confirm `PktPlayerTalked` body format on the wire (one log + capture session)
2. Decide on chat-response format (channel `(slot*2)+3` from server with `steamID=0`)
3. Plumb a sender for chat responses (server-initiated, doesn't use `RelayBodyTo*`)

Estimated effort: ~half a day for the core (`/start`, `/code`, `/newlobby`); `/join` waits for v2 sharding.
