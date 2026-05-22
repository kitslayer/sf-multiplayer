package main

import (
	"net"
	"time"
)

//Client holds a session with a lobby
type Client struct {
	Lobby *Lobby //The lobby that's hosting this client

	//The actual client details
	Addr *net.UDPAddr
	//LastTick time.Time
	PingInMs float64
	Closed   bool

	//The players on this client
	SteamID CSteamID
	Players []*Player

	//Client session tracking
	Paused bool //If the player is marked as paused, will make the lobby ignore the player's automatic ready-up
	ClientInit *Packet //Cached ClientInit packet for lobby migration

	//ProtocolVersion is what the client advertised in its clientRequestingIndex
	//packet. 25 = legacy (relay-mode), 26 = SFNetcodeV2-patched (server-
	//authoritative). The server picks code paths accordingly.
	ProtocolVersion int

	//inputBucket / inputBucketStart track input rate per second for the
	//PlayerInput anticheat rate limit.
	inputBucket      int
	inputBucketStart int64 // unix seconds; resets the bucket when t differs

	//projectileBucket / projectileBucketStart cap the number of unique
	//projectiles a client can claim to have fired per second. Sustained
	//full-auto SF weapons cap around 15–20 rps; we allow 30/s as a generous
	//ceiling. Anything beyond is dropped + logged.
	projectileBucket      int
	projectileBucketStart int64
}

//ProjectileRateBudget allows up to maxProjectilesPerSec NEW projectile
//SyncIndices per second per client. Returns true if this projectile may be
//spawned; false if the client is over budget.
func (client *Client) ProjectileRateBudget(now time.Time) bool {
	const maxProjectilesPerSec = 30
	sec := now.Unix()
	if sec != client.projectileBucketStart {
		client.projectileBucketStart = sec
		client.projectileBucket = 0
	}
	if client.projectileBucket >= maxProjectilesPerSec {
		return false
	}
	client.projectileBucket++
	return true
}

// InputRateBudget allows up to ~80 PlayerInput packets per second per client.
// Legitimate clients send at 60Hz; the buffer covers jitter / brief bursts.
// Returns true when the call is allowed; false when over budget.
func (client *Client) InputRateBudget(now time.Time) bool {
	const maxPerSec = 80
	sec := now.Unix()
	if sec != client.inputBucketStart {
		client.inputBucketStart = sec
		client.inputBucket = 0
	}
	if client.inputBucket >= maxPerSec {
		return false
	}
	client.inputBucket++
	return true
}

//NewClient returns a new client
func NewClient(lobby *Lobby, addr *net.UDPAddr, steamID uint64, playerCount int, clientInit *Packet) *Client {
	newClient := &Client{
		Lobby: lobby,
		Addr:  addr,
		//LastTick: time.Now(),
		SteamID: NewCSteamID(steamID),
		Players: make([]*Player, playerCount),
		ClientInit: clientInit,
	}

	for i := 0; i < playerCount; i++ {
		newClient.Players[i] = &Player{
			Client: newClient,
			Index:  -1,
			Health: lobby.GetMaxHealth(),
		}
	}

	return newClient
}

//Close closes a client
func (client *Client) Close() {
	if client.Closed {
		return
	}
	//TODO: Send a kick packet to tell the player they were kicked
	client.Addr = nil
	client.PingInMs = 0
	client.SteamID = NewCSteamID(0)
	client.Players = nil
	client.Closed = true
}

//IsClosed returns if the client is closed
func (client *Client) IsClosed() bool {
	return client.Closed
}

//GetPlayerCount returns how many players are playing from this client
func (client *Client) GetPlayerCount() int {
	return len(client.Players)
}
