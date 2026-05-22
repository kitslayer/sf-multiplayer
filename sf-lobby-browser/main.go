// sf-lobby-browser is a tiny terminal tool that polls the /lobbies endpoint
// of a Stick Fight dedicated server, prints the active lobbies, and lets the
// user pick one. The selected room code is copied to the clipboard when a
// clipboard tool is available, and always echoed back loudly for easy copy.
//
// Zero non-stdlib dependencies; cross-compiles cleanly to Windows.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"runtime"
	"strconv"
	"strings"
	"time"
)

// LobbyInfo mirrors the server-side struct in StickFightDedicatedSrv/server.go.
// Only the fields we actually display are required to be present in the JSON.
type LobbyInfo struct {
	RoomCode   string `json:"roomCode"`
	Owner      string `json:"owner"`
	OwnerID    string `json:"ownerId"`
	Players    int    `json:"players"`
	MaxPlayers int    `json:"maxPlayers"`
	Map        string `json:"map"`
	Public     bool   `json:"public"`
	GameMode   string `json:"gameMode"`
	CreatedAt  string `json:"createdAt"`
}

// LobbyList mirrors the /lobbies response envelope.
type LobbyList struct {
	Address string       `json:"address"`
	Count   int          `json:"count"`
	Lobbies []*LobbyInfo `json:"lobbies"`
}

func main() {
	addr := flag.String("server", "127.0.0.1:1337", "Stick Fight dedicated server address (host:port)")
	rawJSON := flag.Bool("json", false, "Print the raw /lobbies JSON response and exit")
	timeout := flag.Duration("timeout", 5*time.Second, "HTTP request timeout")
	flag.Parse()

	list, err := fetchLobbies(*addr, *timeout)
	if err != nil {
		fmt.Fprintf(os.Stderr, "error: %v\n", err)
		os.Exit(1)
	}

	if *rawJSON {
		enc := json.NewEncoder(os.Stdout)
		enc.SetIndent("", "  ")
		_ = enc.Encode(list)
		return
	}

	printHeader(list)

	if len(list.Lobbies) == 0 {
		fmt.Println()
		fmt.Println("  (no active lobbies — start one in-game with a private match, then re-run)")
		return
	}

	printLobbies(list.Lobbies)

	choice := promptChoice(len(list.Lobbies))
	if choice < 0 {
		fmt.Println("Cancelled.")
		return
	}

	selected := list.Lobbies[choice]
	announceSelection(selected)
}

func fetchLobbies(addr string, timeout time.Duration) (*LobbyList, error) {
	url := "http://" + addr + "/lobbies"
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, fmt.Errorf("build request: %w", err)
	}

	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf("contact %s: %w", url, err)
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(io.LimitReader(resp.Body, 4<<20))
	if err != nil {
		return nil, fmt.Errorf("read response: %w", err)
	}

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("server returned %s: %s", resp.Status, strings.TrimSpace(string(body)))
	}

	var list LobbyList
	if err := json.Unmarshal(body, &list); err != nil {
		return nil, fmt.Errorf("parse JSON (server may not support /lobbies yet): %w", err)
	}

	return &list, nil
}

func printHeader(list *LobbyList) {
	fmt.Println()
	fmt.Printf("  Stick Fight server: %s\n", list.Address)
	fmt.Printf("  Active lobbies:     %d\n", list.Count)
	fmt.Println(strings.Repeat("-", 72))
}

func printLobbies(lobbies []*LobbyInfo) {
	// Column widths
	const (
		wIdx   = 3
		wRoom  = 8
		wPlrs  = 7
		wMode  = 10
		wVis   = 8
		wOwner = 18
	)
	fmt.Printf("  %-*s  %-*s  %-*s  %-*s  %-*s  %-*s  %s\n",
		wIdx, "#",
		wRoom, "ROOM",
		wPlrs, "PLAYERS",
		wMode, "MODE",
		wVis, "VIS",
		wOwner, "OWNER",
		"MAP",
	)
	for i, lb := range lobbies {
		vis := "private"
		if lb.Public {
			vis = "public"
		}
		players := fmt.Sprintf("%d/%d", lb.Players, lb.MaxPlayers)
		owner := truncate(lb.Owner, wOwner)
		fmt.Printf("  %-*d  %-*s  %-*s  %-*s  %-*s  %-*s  %s\n",
			wIdx, i+1,
			wRoom, lb.RoomCode,
			wPlrs, players,
			wMode, truncate(lb.GameMode, wMode),
			wVis, vis,
			wOwner, owner,
			lb.Map,
		)
	}
	fmt.Println(strings.Repeat("-", 72))
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	if n <= 1 {
		return s[:n]
	}
	return s[:n-1] + "…"
}

// promptChoice reads a 1-based selection from stdin. Returns -1 on cancel
// (blank line, "q", EOF) and a 0-based index otherwise.
func promptChoice(n int) int {
	reader := bufio.NewReader(os.Stdin)
	for {
		fmt.Printf("Pick a lobby [1-%d, blank to cancel]: ", n)
		line, err := reader.ReadString('\n')
		if err != nil {
			fmt.Println()
			return -1
		}
		line = strings.TrimSpace(line)
		if line == "" || strings.EqualFold(line, "q") {
			return -1
		}
		i, err := strconv.Atoi(line)
		if err != nil || i < 1 || i > n {
			fmt.Printf("  invalid selection %q; enter a number between 1 and %d.\n", line, n)
			continue
		}
		return i - 1
	}
}

func announceSelection(lb *LobbyInfo) {
	fmt.Println()
	fmt.Println(strings.Repeat("=", 72))
	fmt.Printf("  Room code:  %s\n", big(lb.RoomCode))
	fmt.Printf("  Owner:      %s\n", lb.Owner)
	fmt.Printf("  Map:        %s\n", lb.Map)
	fmt.Printf("  Players:    %d / %d   (%s, %s)\n", lb.Players, lb.MaxPlayers, lb.GameMode, visStr(lb.Public))
	fmt.Println(strings.Repeat("=", 72))

	if err := copyToClipboard(lb.RoomCode); err != nil {
		fmt.Printf("  (clipboard unavailable: %v — copy manually)\n", err)
		return
	}
	fmt.Println("  Room code copied to clipboard.")
}

func visStr(public bool) string {
	if public {
		return "public"
	}
	return "private"
}

// big wraps a short string in spaces so it stands out in the terminal even
// without ANSI styling.
func big(s string) string {
	return "  " + s + "  "
}

// copyToClipboard tries platform-appropriate clipboard tools without pulling
// in any non-stdlib dependencies. Order:
//   - macOS:   pbcopy
//   - Windows: clip.exe
//   - Linux:   wl-copy (Wayland), then xclip, then xsel
// Returns an error if none are available.
func copyToClipboard(s string) error {
	var attempts [][]string
	switch runtime.GOOS {
	case "darwin":
		attempts = [][]string{{"pbcopy"}}
	case "windows":
		attempts = [][]string{{"clip"}}
	default: // linux, bsd, etc.
		attempts = [][]string{
			{"wl-copy"},
			{"xclip", "-selection", "clipboard"},
			{"xsel", "--clipboard", "--input"},
		}
	}

	var lastErr error
	for _, args := range attempts {
		if _, err := exec.LookPath(args[0]); err != nil {
			lastErr = err
			continue
		}
		cmd := exec.Command(args[0], args[1:]...)
		cmd.Stdin = strings.NewReader(s)
		if err := cmd.Run(); err != nil {
			lastErr = fmt.Errorf("%s: %w", args[0], err)
			continue
		}
		return nil
	}
	if lastErr == nil {
		lastErr = fmt.Errorf("no clipboard tool found")
	}
	return lastErr
}
