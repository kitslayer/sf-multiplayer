package main

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"time"

	crunch "github.com/superwhiskers/crunch/v3"
	"github.com/JoshuaDoes/json"
)

const (
	sophSize = 5
	eophSize = 9
)

//Packet holds a Stick Fight network packet
type Packet struct {
	*crunch.Buffer //Holds the data buffer, and provides additional methods to directly read and write on this buffer

	Timestamp uint32       //The timestamp of the packet
	Src       *net.UDPAddr //The source UDP socket where this packet came from, nil if sending a packet
	Channel   int          //The channel for this packet to travel through
	SteamID   CSteamID     //The Steam ID of the user who sent or is intended to receive this packet
	Type      PacketType   //The type of the packet, to allow easy packet creation

	pos int //io.Reader
}

func (packet *Packet) Read(p []byte) (n int, err error) {
	//packetBytes := packet.AsBytes()
	packetBytes := packet.Bytes()
	if packet.Type != packetTypeHTTP {
		packetBytes = packet.AsBytes()
	}

	start := packet.pos
	for i := 0; i < len(p); i++ {
		if (start + i) >= len(packetBytes) {
			return packet.pos - start, io.EOF
		}
		p[i] = packetBytes[start + i]
		packet.pos++
	}
	return packet.pos - start, nil
}

//NewPacket returns a new deserialized Stick Fight network packet
func NewPacket(packetType PacketType, channel int, steamID uint64) *Packet {
	return &Packet{crunch.NewBuffer(make([]byte, 0)), uint32(time.Now().Unix()), nil, channel, NewCSteamID(steamID), packetType, 0}
}

//NewPacketFromBytes returns a Stick Fight network packet deserialized from bytes
func NewPacketFromBytes(data []byte) (packet *Packet, err error) {
	if len(data) < sophSize+eophSize {
		return nil, errors.New("packet size too small")
	}

	buf := crunch.NewBuffer(data)
	dataLen := int64(len(data) - sophSize - eophSize)

	//Determine if we're dealing with HTTP GET requests first
	test := buf.ReadBytesNext(3) //GET is 3 bytes
	buf.SeekByte(0, false) //Reset our position in the buffer
	if string(test) == "GET" {
		packetGet := NewPacket(packetTypeHTTP, 0, 0)
		packetGet.Grow(int64(len(data)))
		packetGet.WriteBytesNext(data)
		req, err := http.ReadRequest(bufio.NewReader(packetGet))
		if err != nil {
			return nil, err
		}

		switch req.URL.Path {
			case "/status": {
				packetStatus := NewPacket(packetTypeHTTP, 0, 0)
				statusJSON, err := json.Marshal(server.Status(), false)
				if err != nil {
					return nil, err
				}
				packetStatus.Grow(int64(len(statusJSON)))
				packetStatus.WriteBytesNext(statusJSON)
				return packetStatus, nil
			}
			case "/lobbies": {
				packetLobbies := NewPacket(packetTypeHTTP, 0, 0)
				lobbiesJSON, err := json.Marshal(server.LobbyList(), false)
				if err != nil {
					return nil, err
				}
				packetLobbies.Grow(int64(len(lobbiesJSON)))
				packetLobbies.WriteBytesNext(lobbiesJSON)
				return packetLobbies, nil
			}
			case "/maps": {
				//Debug endpoint: which scenes have dumped JSON data loaded.
				packetMaps := NewPacket(packetTypeHTTP, 0, 0)
				mapsJSON, err := json.Marshal(server.MapsSummary(), false)
				if err != nil {
					return nil, err
				}
				packetMaps.Grow(int64(len(mapsJSON)))
				packetMaps.WriteBytesNext(mapsJSON)
				return packetMaps, nil
			}
			case "/invite": {
				//Reserve a slot in a private lobby for a specific SteamID.
				//GET /invite?code=ROOMCODE&steam=STEAMID — adds the SteamID to
				//that lobby's Invited list so the next clientRequestingIndex
				//from this SteamID auto-routes there (see server.go priority 1).
				q := req.URL.Query()
				code := q.Get("code")
				steamStr := q.Get("steam")
				resp := server.AddInvite(code, steamStr)
				respJSON, jerr := json.Marshal(resp, false)
				if jerr != nil {
					return nil, jerr
				}
				packetInvite := NewPacket(packetTypeHTTP, 0, 0)
				packetInvite.Grow(int64(len(respJSON)))
				packetInvite.WriteBytesNext(respJSON)
				return packetInvite, nil
			}
		}

		return nil, errors.New(fmt.Sprintf("unhandled GET: %s", req.URL.Path))
	}

	//Official start of packet header
	//Size: 5 bytes + data
	//0x0  (4 bytes, uint32) - Packet timestamp
	//0x4  (1 byte,  byte)   - Packet type
	//0x5… (x bytes)         - Packet data, to be interpreted by the packet type's handler
	packet = NewPacket(packetTypeNull, 0, 0)
	packet.Timestamp = buf.ReadU32LENext(1)[0]
	packet.Type = PacketType(buf.ReadByteNext())
	if dataLen > 0 {
		packet.Grow(dataLen)
		packet.WriteBytesNext(buf.ReadBytesNext(dataLen))
		packet.SeekByte(0, false) //Seek back to the start of the packet for the next read/write
	}

	//Custom end of packet header, directly following the packet's data
	//Size so far: 9 bytes
	//0x0 (8 bytes, uint64) - The Steam ID of the user who is the intended recipient of the packet
	//0x8 (1 byte,  int)    - The channel, which was originally handled by Steam's networking library, and is required by the game's packet handling logic
	packet.SteamID = NewCSteamID(buf.ReadU64LENext(1)[0])
	packet.Channel = int(buf.ReadByteNext())

	return packet, nil
}

func (packet *Packet) String() string {
	str := fmt.Sprintf("[%d %d]", packet.Channel, packet.Timestamp)
	if packet.SteamID.ID != 0 {
		str += " " + packet.SteamID.GetUsername()
	}
	str += fmt.Sprintf(": %d:%s %v", packet.Type, packet.Type.String(), packet.Bytes())

	return str
}

//AsBytes returns a Stick Fight network packet serialized as bytes
func (packet *Packet) AsBytes() []byte {
	dataLen := int(packet.ByteCapacity())
	buf := crunch.NewBuffer(make([]byte, sophSize+dataLen+eophSize))

	buf.WriteU32LENext([]uint32{packet.Timestamp})
	buf.WriteByteNext(byte(packet.Type))
	if dataLen > 0 {
		buf.WriteBytesNext(packet.Bytes())
	}
	buf.WriteU64LENext([]uint64{packet.SteamID.ID})
	buf.WriteByteNext(byte(packet.Channel))

	return buf.Bytes()
}

//ShouldLog returns true if this packet should be logged
func (packet *Packet) ShouldLog() bool {
	switch packet.Type {
	case packetTypePlayerUpdate:
		if !logPlayerUpdate {
			return false
		}
	}

	return true
}

//ShouldCheckTime returns true if this packet should have its timestamp checked
func (packet *Packet) ShouldCheckTime() bool {
	switch packet.Type {
	case packetTypePing, packetTypePingResponse, packetTypeClientReadyUp, packetTypePlayerUpdate, packetTypePlayerTalked, packetTypePlayerForceAdded, packetTypePlayerForceAddedAndBlock, packetTypePlayerLavaForceAdded, packetTypePlayerFallOut, packetTypePlayerWonWithRicochet, packetTypePlayerTookDamage, packetTypeClientRequestingWeaponThrow:
		return false
	}

	return true
}

//PacketType is the type of a packet, which determines how to interpret the data associated with the packet
type PacketType byte

func (packetType PacketType) String() string {
	switch packetType {
	case packetTypeHTTP:
		return "HTTP"
	case packetTypePing:
		return "ping"
	case packetTypePingResponse:
		return "pingResponse"
	case packetTypeClientJoined:
		return "clientJoined"
	case packetTypeClientRequestingAccepting:
		return "clientRequestingAccepting"
	case packetTypeClientAccepted:
		return "clientAccepted"
	case packetTypeClientInit:
		return "clientInit"
	case packetTypeClientRequestingIndex:
		return "clientRequestingIndex"
	case packetTypeClientRequestingToSpawn:
		return "clientRequestingToSpawn"
	case packetTypeClientSpawned:
		return "clientSpawned"
	case packetTypeClientReadyUp:
		return "clientReadyUp"
	case packetTypePlayerUpdate:
		return "playerUpdate"
	case packetTypePlayerTookDamage:
		return "playerTookDamage"
	case packetTypePlayerTalked:
		return "playerTalked"
	case packetTypePlayerForceAdded:
		return "playerForceAdded"
	case packetTypePlayerForceAddedAndBlock:
		return "playerForceAddedAndBlock"
	case packetTypePlayerLavaForceAdded:
		return "playerLavaForceAdded"
	case packetTypePlayerFallOut:
		return "playerFallOut"
	case packetTypePlayerWonWithRicochet:
		return "playerWonWithRicochet"
	case packetTypeMapChange:
		return "mapChange"
	case packetTypeWeaponSpawned:
		return "weaponSpawned"
	case packetTypeWeaponThrown:
		return "weaponThrown"
	case packetTypeClientRequestingWeaponThrow:
		return "clientRequestingWeaponThrow"
	case packetTypeClientRequestingWeaponDrop:
		return "clientRequestingWeaponDrop"
	case packetTypeWeaponDropped:
		return "weaponDropped"
	case packetTypeWeaponWasPickedUp:
		return "weaponWasPickedUp"
	case packetTypeClientRequestingWeaponPickUp:
		return "clientRequestingWeaponPickUp"
	case packetTypeObjectUpdate:
		return "objectUpdate"
	case packetTypeObjectSpawned:
		return "objectSpawned"
	case packetTypeObjectSimpleDestruction:
		return "objectSimpleDestruction"
	case packetTypeObjectDestructionCollision:
		return "objectDestructionCollision"
	case packetTypeGroundWeaponsInit:
		return "groundWeaponsInit"
	case packetTypeMapInfo:
		return "mapInfo"
	case packetTypeMapInfoSync:
		return "mapInfoSync"
	case packetTypeWorkshopMapsLoaded:
		return "workshopMapsLoaded"
	case packetTypeStartMatch:
		return "startMatch"
	case packetTypeObjectHello:
		return "objectHello"
	case packetTypeOptionsChanged:
		return "optionsChanged"
	case packetTypeKickPlayer:
		return "kickPlayer"
	case packetTypeClientLeft:
		return "clientLeft"
	case packetTypeLobbyType:
		return "lobbyType"
	case packetTypeRequestingOptions:
		return "requestingOptions"
	case packetTypePlayerInput:
		return "playerInput"
	case packetTypeWorldStateSnapshot:
		return "worldStateSnapshot"
	case packetTypeServerEvent:
		return "serverEvent"
	}

	return fmt.Sprintf("unknown%d", packetType)
}

const (
	packetTypePing PacketType = iota
	packetTypePingResponse
	packetTypeClientJoined
	packetTypeClientRequestingAccepting
	packetTypeClientAccepted
	packetTypeClientInit
	packetTypeClientRequestingIndex
	packetTypeClientRequestingToSpawn
	packetTypeClientSpawned
	packetTypeClientReadyUp
	packetTypePlayerUpdate
	packetTypePlayerTookDamage
	packetTypePlayerTalked
	packetTypePlayerForceAdded
	packetTypePlayerForceAddedAndBlock
	packetTypePlayerLavaForceAdded
	packetTypePlayerFallOut
	packetTypePlayerWonWithRicochet
	packetTypeMapChange
	packetTypeWeaponSpawned
	packetTypeWeaponThrown
	packetTypeClientRequestingWeaponThrow
	packetTypeClientRequestingWeaponDrop
	packetTypeWeaponDropped
	packetTypeWeaponWasPickedUp
	packetTypeClientRequestingWeaponPickUp
	packetTypeObjectUpdate
	packetTypeObjectSpawned
	packetTypeObjectSimpleDestruction
	packetTypeObjectInvokeDestructionEvent
	packetTypeObjectDestructionCollision
	packetTypeGroundWeaponsInit
	packetTypeMapInfo
	packetTypeMapInfoSync
	packetTypeWorkshopMapsLoaded
	packetTypeStartMatch
	packetTypeObjectHello
	packetTypeOptionsChanged
	packetTypeKickPlayer
	packetTypeClientLeft
	packetTypeLobbyType
	packetTypeRequestingOptions
	//— Phase 5 v26 protocol additions (M2+) ——————————————————————————
	//These IDs are unused by the patched v25 client, so v25 clients will treat
	//them as unhandled and log a warning. v26-aware clients (after the
	//SFNetcodeV2 BepInEx patch ships) will consume them.
	packetTypePlayerInput        //client → server, 60Hz: stick + buttons + sequence
	packetTypeWorldStateSnapshot //server → client, 30Hz: batched entity positions
	packetTypeServerEvent        //server → client, reliable: damage/spawn/death events
	packetTypeHTTP               = 254
	packetTypeNull               = 255
)

//ProtocolVersionLegacy is what stock + patched-v25 clients send in their
//clientRequestingIndex packet's protocolVersion byte. Server keeps relay-mode
//behavior for these clients.
const ProtocolVersionLegacy = 25

//ProtocolVersionAuthoritative is what a v26-aware (SFNetcodeV2-patched) client
//will advertise. Triggers the server-authoritative code paths (M2-M5).
const ProtocolVersionAuthoritative = 26
