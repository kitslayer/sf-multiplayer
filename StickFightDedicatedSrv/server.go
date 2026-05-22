package main

import (
	"bufio"
	"fmt"
	"io"
	"net"
	"net/http"
	"runtime"
	"strconv"
	"sync"
	"time"

	swearfilter "github.com/JoshuaDoes/gofuckyourself"
)

var (
	swears = []string{" "}
)

//Server holds a Stick Fight dedicated server
type Server struct {
	Addr string

	//Session
	Running bool
	Sock    *net.UDPConn
	HTTP    *net.TCPListener
	Lobbies []*Lobby
	Filter  *swearfilter.SwearFilter

	//Anticheat / connection rate-limit state. Tracks the timestamp of the last
	//accept-style packet from each IP so a single host can't flood the server
	//with new clientRequestingAccepting / clientRequestingIndex packets and
	//exhaust the lobby slot table.
	connectRateMu sync.Mutex
	connectRate   map[string][]time.Time
}

const (
	connectsPerWindow  = 8                //max accept-class packets per source IP per window
	connectRateWindow  = 10 * time.Second //sliding window length
)

//allowConnect returns true if this source IP hasn't exceeded the per-window
//connection budget. Called on the accept-class packets only — gameplay packets
//are unaffected so existing players don't get throttled.
func (srv *Server) allowConnect(ipKey string) bool {
	srv.connectRateMu.Lock()
	defer srv.connectRateMu.Unlock()
	if srv.connectRate == nil {
		srv.connectRate = make(map[string][]time.Time)
	}
	now := time.Now()
	cutoff := now.Add(-connectRateWindow)
	hist := srv.connectRate[ipKey]
	keep := hist[:0]
	for _, t := range hist {
		if t.After(cutoff) {
			keep = append(keep, t)
		}
	}
	if len(keep) >= connectsPerWindow {
		srv.connectRate[ipKey] = keep
		return false
	}
	keep = append(keep, now)
	srv.connectRate[ipKey] = keep
	return true
}

//Status holds server statistics
type Status struct {
	Address string `json:"address"`
	Online bool `json:"online"`
	Lobbies int `json:"lobbies"`
	MaxLobbies int `json:"maxLobbies"`
	Players int `json:"playersOnline"`
}

//LobbyInfo holds publicly-visible info for a single lobby, suitable for a
//lobby browser. Kept intentionally small/string-only so external tools don't
//need to know the server's internal types.
type LobbyInfo struct {
	RoomCode   string `json:"roomCode"`
	Owner      string `json:"owner"`      //Steam username when resolvable, otherwise the SteamID as a decimal string, otherwise ""
	OwnerID    string `json:"ownerId"`    //SteamID as a decimal string (may be "0" before the first client joins)
	Players    int    `json:"players"`
	MaxPlayers int    `json:"maxPlayers"`
	Map        string `json:"map"`        //e.g. "Landfall map: 7" or "Steam Workshop map: 123456"
	Public     bool   `json:"public"`
	GameMode   string `json:"gameMode"`   //Concrete game-mode type name (Stock, Duel, GunGame, Tournament)
	CreatedAt  string `json:"createdAt"`  //RFC3339
}

//LobbyList holds the publicly-visible lobby list response shape.
type LobbyList struct {
	Address string       `json:"address"`
	Count   int          `json:"count"`
	Lobbies []*LobbyInfo `json:"lobbies"`
}

//gameModeName returns a short human name for a game mode, derived from the
//concrete type. Falls back to "Unknown" when gm is nil.
func gameModeName(gm GameMode) string {
	if gm == nil {
		return "Unknown"
	}
	// e.g. "main.Stock" -> "Stock", "*main.GunGame" -> "GunGame"
	name := fmt.Sprintf("%T", gm)
	if i := len(name) - 1; i >= 0 {
		for j := len(name) - 1; j >= 0; j-- {
			if name[j] == '.' {
				return name[j+1:]
			}
		}
		_ = i
	}
	return name
}

//LobbyList returns a snapshot of currently-running lobbies, formatted for the
//lobby browser endpoint. Skips lobbies that are not running.
func (srv *Server) LobbyList() *LobbyList {
	out := &LobbyList{
		Address: srv.Addr,
		Lobbies: make([]*LobbyInfo, 0, len(srv.Lobbies)),
	}
	for _, lobby := range srv.Lobbies {
		if lobby == nil || !lobby.IsRunning() {
			continue
		}
		ownerName := lobby.LobbyOwner.GetUsername()
		ownerID := fmt.Sprintf("%d", lobby.LobbyOwner.ID)
		if ownerName == "" {
			ownerName = ownerID
		}
		mapName := ""
		if lobby.CurrentLevel != nil {
			mapName = lobby.CurrentLevel.String()
		}
		info := &LobbyInfo{
			RoomCode:   lobby.LobbyRoomCode,
			Owner:      ownerName,
			OwnerID:    ownerID,
			Players:    lobby.GetPlayerCount(false),
			MaxPlayers: lobby.MaxPlayers,
			Map:        mapName,
			Public:     lobby.Public,
			GameMode:   gameModeName(lobby.GameMode),
			CreatedAt:  lobby.LobbyCreationTime.UTC().Format(time.RFC3339),
		}
		out.Lobbies = append(out.Lobbies, info)
	}
	out.Count = len(out.Lobbies)
	return out
}

//MapsSummary holds the response shape for the /maps debug endpoint.
type MapsSummary struct {
	Loaded    int             `json:"loaded"`
	Scenes    []MapsSceneInfo `json:"scenes"`
}

//MapsSceneInfo holds the per-scene summary for /maps.
type MapsSceneInfo struct {
	SceneIndex   int32 `json:"sceneIndex"`
	Statics      int   `json:"staticColliders"`
	PlayerSpawns int   `json:"playerSpawns"`
	WeaponSpawns int   `json:"weaponSpawns"`
	Killboxes    int   `json:"killboxes"`
	Syncables    int   `json:"syncableObjects"`
}

//MapsSummary returns a snapshot of loaded map data. Used by the /maps endpoint
//for verifying Phase 5 deployment without grepping JSON files manually.
func (srv *Server) MapsSummary() *MapsSummary {
	out := &MapsSummary{
		Loaded: len(loadedMaps),
		Scenes: make([]MapsSceneInfo, 0, len(loadedMaps)),
	}
	for sceneIndex, m := range loadedMaps {
		if m == nil {
			continue
		}
		out.Scenes = append(out.Scenes, MapsSceneInfo{
			SceneIndex:   sceneIndex,
			Statics:      len(m.StaticColliders),
			PlayerSpawns: len(m.PlayerSpawns),
			WeaponSpawns: len(m.WeaponSpawns),
			Killboxes:    len(m.Killboxes),
			Syncables:    0, // we don't track per-scene SyncableObject right now (server-side; M5)
		})
	}
	return out
}

//InviteResponse is returned by the /invite HTTP endpoint.
type InviteResponse struct {
	Status   string `json:"status"`             //"ok" | "error"
	Message  string `json:"message,omitempty"`
	RoomCode string `json:"roomCode,omitempty"`
	SteamID  string `json:"steamId,omitempty"`
	Players  int    `json:"players,omitempty"`
}

//AddInvite adds steamStr to the Invited list of the lobby identified by code.
//Called from the /invite HTTP endpoint. Validates inputs and returns a JSON-shaped
//response so the lobby browser / launcher tool can react.
func (srv *Server) AddInvite(code, steamStr string) *InviteResponse {
	if code == "" {
		return &InviteResponse{Status: "error", Message: "missing 'code' query param"}
	}
	if steamStr == "" {
		return &InviteResponse{Status: "error", Message: "missing 'steam' query param"}
	}
	steamID, err := strconv.ParseUint(steamStr, 10, 64)
	if err != nil || steamID == 0 {
		return &InviteResponse{Status: "error", Message: "invalid 'steam' (expected decimal SteamID64)"}
	}

	lobby := srv.GetLobbyByCode(code)
	if lobby == nil {
		return &InviteResponse{Status: "error", Message: "no lobby with that room code"}
	}

	//Skip if already invited (idempotent).
	for _, inv := range lobby.Invited {
		if inv.ID == steamID {
			return &InviteResponse{
				Status: "ok", Message: "already invited",
				RoomCode: code, SteamID: steamStr,
				Players: lobby.GetPlayerCount(false),
			}
		}
	}

	lobby.Invited = append(lobby.Invited, NewCSteamID(steamID))
	log.Info("Invited SteamID ", steamID, " to lobby ", code)
	return &InviteResponse{
		Status: "ok", Message: "invited",
		RoomCode: code, SteamID: steamStr,
		Players: lobby.GetPlayerCount(false),
	}
}

//NewServer returns a new server running on the specified UDP address
func NewServer(addr string) *Server {
	srv := &Server{
		Addr:    addr,
		Lobbies: make([]*Lobby, 0),
		Filter:  swearfilter.NewSwearFilter(true, swears...),
	}

	return srv
}

//Status returns the current server statistics
func (srv *Server) Status() *Status {
	players := 0
	for i := 0; i < len(srv.Lobbies); i++ {
		players += srv.Lobbies[i].GetPlayerCount(false)
	}

	return &Status{
		Address: srv.Addr,
		Online: srv.Running,
		Lobbies: len(srv.Lobbies),
		MaxLobbies: maxLobbies,
		Players: players,
	}
}

//IsRunning returns true if the server is currently running
func (srv *Server) IsRunning() bool {
	return srv.Running
}

//Close closes the server
func (srv *Server) Close() {
	if !srv.IsRunning() {
		return
	}

	log.Info("Closing server!")

	for _, lobby := range srv.Lobbies {
		lobby.Close()
	}

	srv.Sock.Close()
	srv.HTTP.Close()
	srv.Running = false
}

//Run starts the server and ticks it until it's closed
func (srv *Server) Run() {
	if srv.Running {
		srv.Close()
	}

	udpAddr, err := net.ResolveUDPAddr("udp4", srv.Addr)
	if err != nil {
		log.Fatal("Unable to resolve UDP address for udp4 address ", srv.Addr)
	}
	log.Trace("Resolved UDP address for udp4 address ", srv.Addr)

	sock, err := net.ListenUDP("udp4", udpAddr)
	if err != nil {
		log.Fatal("Unable to listen on UDP address ", udpAddr)
	}
	log.Trace("Listening on UDP address ", udpAddr)
	srv.Sock = sock

	tcpAddr, err := net.ResolveTCPAddr("tcp", srv.Addr)
	if err != nil {
		log.Fatal("Unable to resolve TCP address for tcp address ", srv.Addr)
	}
	log.Trace("Resolved TCP address for tcp address ", srv.Addr)

	httpSock, err := net.ListenTCP("tcp", tcpAddr)
	if err != nil {
		log.Fatal("Unable to listen on TCP address ", tcpAddr)
	}
	log.Trace("Listening on TCP address ", tcpAddr)
	srv.HTTP = httpSock

	srv.Running = true
	log.Info("Server is running!")

	for i := 0; i < runtime.NumCPU(); i++ {
		go srv.ReadPackets()
	}
	go srv.ReadHTTP()

	for srv.Running {
		if !srv.Running {
			break
		}

		time.Sleep(time.Millisecond * 1000)
	}
}

//ReadPackets starts reading packets and handles them
func (srv *Server) ReadPackets() {
	buffer := make([]byte, maxBufferSize)

	for srv.Running {
		if !srv.Running {
			break
		}

		//Reset the buffer
		buffer = make([]byte, maxBufferSize)

		//Block until a packet is read into the buffer
		n, addr, err := srv.Sock.ReadFromUDP(buffer)
		if err != nil {
			log.Error(addr, ": ", err)
			continue
		}

		//Trim the buffer
		buffer = buffer[:n]

		//Handle the packet
		go srv.Handle(buffer, addr)
	}
}

//ReadHTTP starts reading HTTP packets and handles them
func (srv *Server) ReadHTTP() {
	buffer := make([]byte, maxBufferSize)

	for srv.Running {
		if !srv.Running {
			break
		}

		//Reset the buffer
		buffer = make([]byte, maxBufferSize)

		//Block until a client is encountered
		tcpConn, err := srv.HTTP.AcceptTCP()
		if err != nil {
			log.Error(srv.HTTP.Addr(), ": ", err)
			continue
		}
		log.Trace("Accepted TCP client: ", tcpConn.RemoteAddr())
		//defer tcpConn.Close()

		//Block until a packet is read into the buffer
		n, err := tcpConn.Read(buffer)
		if err != nil {
			log.Error(tcpConn.RemoteAddr(), ": ", err)
			continue
		}

		//Trim the buffer
		buffer = buffer[:n]

		packet, err := NewPacketFromBytes(buffer)
		if err != nil {
			log.Error("unable to create packet from bytes: ", err)
			continue
		}

		if packet.Type != packetTypeHTTP {
			log.Error("expected packet type HTTP over TCP")
			continue
		}

		/*resp, err := http.ReadResponse(bufio.NewReader(packet), nil)
		if err != nil {
			log.Error("unable to convert HTTP response: ", err)
			continue
		}*/
		resp := &http.Response{
			Status: "200 OK",
			StatusCode: 200,
			Proto: "HTTP/1.1",
			ProtoMajor: 1,
			ProtoMinor: 1,
			Body: io.NopCloser(bufio.NewReader(packet)),
			ContentLength: int64(len(packet.Bytes())),
		}

		/*n, err = tcpConn.Write(packet.Bytes())
		if err != nil {
			log.Error(tcpConn.RemoteAddr(), ": ", err)
			continue
		}*/

		err = resp.Write(tcpConn)
		if err != nil {
			log.Error(tcpConn.RemoteAddr(), ": ", err)
			continue
		}

		//tcpConn.CloseWrite()
		err = tcpConn.Close()
		log.Trace("Closed TCP client: ", err)
	}
}

//SendPacket sends a packet to a destination address
func (srv *Server) SendPacket(packet *Packet, addr *net.UDPAddr) {
	srv.Sock.WriteToUDP(packet.AsBytes(), addr)

	if packet.ShouldLog() {
		log.Trace("Sent to ", addr, ": ", packet)
	}
}

//Handle handles a packet for the server
func (srv *Server) Handle(buffer []byte, addr *net.UDPAddr) {
	//Read the buffer into a packet
	packet, err := NewPacketFromBytes(buffer)
	if err != nil {
		log.Error("unable to create packet from bytes to handle: ", err)
		return //Goodbye false packet!
	}

	if packet.Type == packetTypeHTTP {
		return
	}

	//Set the source address of the packet
	packet.Src = addr

	//Log the packet
	if packet.ShouldLog() {
		log.Trace("Received from ", addr, ": ", packet)
	}

	if lobby := srv.GetLobbyByAddr(packet.Src); lobby != nil {
		lobby.Handle(packet)
		return
	}

	switch packet.Type {
	case packetTypePing:
		srv.ClientPong(packet.Src, packet.Bytes())

	case packetTypeClientRequestingAccepting:
		//Anticheat: rate-limit the very first packet of a connection so a single
		//IP can't allocate unlimited client slots / lobbies. Existing players
		//already in a lobby went through GetLobbyByAddr above and bypass this.
		if !srv.allowConnect(packet.Src.IP.String()) {
			log.Warn("Anticheat: refusing clientRequestingAccepting from ", packet.Src.IP, " (rate limited)")
			return
		}
		srv.ClientAccept(packet.Src)

	case packetTypeClientRequestingIndex:
		//Peek the client's SteamID from the packet so we can prefer lobbies that
		//have already invited this player. (The packet's first 8 bytes are a
		//uint64 LE SteamID per ClientInit's expected layout.) Don't advance the
		//packet position — ClientInit re-reads from the start.
		var joinerSteamID uint64
		if packet.ByteCapacity() >= 8 {
			savedOffset := packet.ByteOffset()
			packet.SeekByte(0, false)
			joinerSteamID = packet.ReadU64LENext(1)[0]
			packet.SeekByte(savedOffset, false)
		}

		//Priority 1: any running lobby that has explicitly invited this SteamID
		//(survives even private/invite-only lobbies — that's the whole point).
		for _, lobby := range srv.Lobbies {
			if lobby == nil || !lobby.IsRunning() {
				continue
			}
			if lobby.GetPlayerCount(false) >= lobby.MaxPlayers {
				continue
			}
			if joinerSteamID == 0 || !lobby.IsInvited(joinerSteamID) {
				continue
			}
			//IsInvited returns true on Public lobbies too — skip those here so they
			//flow through the Priority-2 loop and we don't accidentally double-handle.
			if lobby.Public {
				continue
			}
			invitedHit := false
			for _, inv := range lobby.Invited {
				if inv.ID == joinerSteamID {
					invitedHit = true
					break
				}
			}
			if !invitedHit {
				continue
			}
			if err := lobby.ClientInit(packet); err == nil {
				log.Info("Routed invited client ", packet.Src, " into lobby ", lobby.LobbyRoomCode)
				return
			}
		}

		//Priority 2: existing PUBLIC lobby with room (open auto-join).
		for _, lobby := range srv.Lobbies {
			if lobby == nil || !lobby.IsRunning() || !lobby.Public {
				continue
			}
			if lobby.GetPlayerCount(false) >= lobby.MaxPlayers {
				continue
			}
			if err := lobby.ClientInit(packet); err == nil {
				return
			}
		}

		lobby, err := NewLobby(srv, "") //Create a new lobby with a random room code
		if err != nil {
			log.Error("unable to create new lobby: ", err)
			srv.ClientReject(addr, err.Error())
			return
		}
		err = lobby.ClientInit(packet)
		if err != nil {
			log.Error("unable to init client into new lobby: ", err)
			srv.ClientReject(addr, err.Error())
			return
		}
		srv.LobbyAdd(lobby)

	case packetTypeKickPlayer:
		//Just so we handle this if the client isn't in a lobby yet

	case packetTypePlayerUpdate,
		packetTypePlayerTookDamage,
		packetTypePlayerInput,
		packetTypeObjectUpdate,
		packetTypeObjectHello,
		packetTypeClientLeft:
		//These are in-lobby packets from a client whose lobby died (e.g. the
		//server was restarted). Silently drop instead of polluting the log;
		//the client will rejoin on its next clientRequestingAccepting.
		return

	default:
		log.Error(fmt.Sprintf("Unhandled packet from %s: %s", packet.Src, packet))
	}
}

//ClientPong responds to a ping with a pong
func (srv *Server) ClientPong(addr *net.UDPAddr, data []byte) {
	packetPingResponse := NewPacket(packetTypePingResponse, 0, 0)
	if dataLen := int64(len(data)); dataLen > 0 {
		packetPingResponse.Grow(dataLen)
		packetPingResponse.WriteBytesNext(data)
	}
	srv.SendPacket(packetPingResponse, addr)
}

//ClientAccept accepts a client
func (srv *Server) ClientAccept(addr *net.UDPAddr) {
	packetClientAccepted := NewPacket(packetTypeClientAccepted, 1, 0)
	srv.SendPacket(packetClientAccepted, addr)
	log.Debug("Accepted client ", addr)
}

//ClientReject rejects a client
func (srv *Server) ClientReject(addr *net.UDPAddr, reason string) {
	packetClientInit := NewPacket(packetTypeClientInit, 0, 0)
	packetClientInit.Grow(1)
	if reason != "" {
		reasonBytes := []byte(reason)
		packetClientInit.Grow(int64(len(reasonBytes)))
		packetClientInit.WriteBytes(0x1, reasonBytes)
	}
	srv.SendPacket(packetClientInit, addr)
	if reason != "" {
		log.Debug("Rejected client ", addr, " with reason: ", reason)
	} else {
		log.Debug("Rejected client ", addr)
	}
}

//GetLobbyByAddr returns the lobby that the address is found in
func (srv *Server) GetLobbyByAddr(addr *net.UDPAddr) *Lobby {
	if len(srv.Lobbies) > 0 {
		for i := 0; i < len(srv.Lobbies); i++ {
			if srv.Lobbies[i] != nil && srv.Lobbies[i].IsRunning() && len(srv.Lobbies[i].Clients) > 0 {
				for clientIndex := 0; clientIndex < len(srv.Lobbies[i].Clients); clientIndex++ {
					if srv.Lobbies[i].Clients[clientIndex].Addr.String() == addr.String() {
						//We found the lobby that this client is in!
						return srv.Lobbies[i]
					}
				}
			}
		}
	}

	//We didn't find the lobby, they must be new!
	return nil
}

//GetLobbyByCode returns the lobby matching the room code
func (srv *Server) GetLobbyByCode(code string) *Lobby {
	if len(srv.Lobbies) > 0 {
		for i := 0; i < len(srv.Lobbies); i++ {
			if srv.Lobbies[i] != nil && srv.Lobbies[i].IsRunning() && srv.Lobbies[i].LobbyRoomCode == code {
				return srv.Lobbies[i]
			}
		}
	}

	return nil
}

//LobbyAdd adds the specified lobby to the server
func (srv *Server) LobbyAdd(lobby *Lobby) {
	srv.Lobbies = append(srv.Lobbies, lobby)
}

//GetClientByAddr returns the client with a matching address
func (srv *Server) GetClientByAddr(addr *net.UDPAddr) (int, *Client) {
	for _, lobby := range srv.Lobbies {
		if lobby.Clients == nil || len(lobby.Clients) == 0 {
			continue
		}

		for clientIndex, client := range lobby.Clients {
			if client.Addr.String() == addr.String() {
				return clientIndex, client
			}
		}
	}
	return -1, nil
}

//GetClientBySteamID returns the client with a matching SteamID
func (srv *Server) GetClientBySteamID(steamID CSteamID) *Client {
	for _, lobby := range srv.Lobbies {
		if lobby.Clients == nil || len(lobby.Clients) == 0 {
			continue
		}

		for _, client := range lobby.Clients {
			if client.SteamID.CompareCSteamID(steamID) {
				return client
			}
		}
	}
	return nil
}

//GetClientBySteamUsername returns the client with a matching Steam username
func (srv *Server) GetClientBySteamUsername(steamUsername string) *Client {
	for _, lobby := range srv.Lobbies {
		if lobby.Clients == nil || len(lobby.Clients) == 0 {
			continue
		}

		for _, client := range lobby.Clients {
			if client.SteamID.GetUsername() == steamUsername {
				return client
			}

			if client.SteamID.GetNormalizedUsername() == steamUsername {
				return client
			}
		}
	}
	return nil
}

//HasSwear checks if the given message has a swear
func (srv *Server) HasSwear(message string) (tripped bool) {
	trippedWords, err := srv.Filter.Check(message)
	if err != nil {
		tripped = true
	}
	if len(trippedWords) > 0 {
		tripped = true
	}
	if tripped {
		log.Trace("[CHAT] Message has tripped words ", trippedWords, ": ", message)
	}
	return
}
