package main

import (
	"flag"
	"math/rand"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/JoshuaDoes/logger"
	"github.com/StickFightDev/steamcmd"
	"github.com/microcosm-cc/bluemonday"
)

//Command-line flags and their defaults
var (
	//SteamCMD login
	steamKey      = ""
	steamUsername = "anonymous"
	steamPassword = ""
	steamCmdDir   = ""

	//Server config
	address       = "0.0.0.0:1337"
	maxBufferSize = 8192
	maxLobbies    = 100
	defaultPublic = false //If true, new lobbies are created public (auto-joinable by anyone)
	mapsDir       = "/home/miles/sf-multiplayer/maps" //Phase 5 M1: dumped map JSONs (tools/dump-sf-maps.py output)
	replayDir     = ""                                //Phase 5 M5: if set, every lobby writes a binary replay log to this dir
	//                      ; if false (default), new lobbies are private (room code only)
	//                      so the comp scene can have invite-only matches

	//Logging
	verbosityLevel  = 0
	logPlayerUpdate = false
)

//The server itself
var (
	log        *logger.Logger     //Console logger
	scmd       *steamcmd.SteamCmd //SteamCMD
	server     *Server            //StickFightDev server
	randomizer *rand.Rand         //Seed for random numbers

	stripTags *bluemonday.Policy
)

// registerFlags wires up our CLI options. Called from main() rather than init()
// so `go test` (whose own flag set must register first) doesn't try to parse
// the test runner's args against our schema.
func registerFlags() {
	flag.StringVar(&steamKey, "steamKey", steamKey, "The API key to use when asking Steam for usernames")
	flag.StringVar(&steamUsername, "username", steamUsername, "The username for the Steam account that owns Stick Fight")
	flag.StringVar(&steamPassword, "password", steamPassword, "The password for the Steam account that owns Stick Fight")
	flag.StringVar(&steamCmdDir, "steamCmdDir", steamCmdDir, "The directory holding the root of your SteamCmd install")
	flag.StringVar(&address, "address", address, "The IP and port to serve on")
	flag.IntVar(&maxBufferSize, "maxBufferSize", maxBufferSize, "The maximum buffer size of expected incoming packets")
	flag.IntVar(&maxLobbies, "maxLobbies", maxLobbies, "The maximum amount of lobbies to allow")
	flag.IntVar(&verbosityLevel, "verbosity", verbosityLevel, "The verbosity level of debug log output")
	flag.BoolVar(&logPlayerUpdate, "logPlayerUpdate", logPlayerUpdate, "Enables logging playerUpdate packets")
	flag.BoolVar(&defaultPublic, "publicLobbies", defaultPublic, "If set, new lobbies are public (auto-joinable). Default false = private/invite-only.")
	flag.StringVar(&mapsDir, "mapsDir", mapsDir, "Directory containing landfall-N.json files from tools/dump-sf-maps.py")
	flag.StringVar(&replayDir, "replayDir", replayDir, "If non-empty, each lobby writes a binary snapshot/event replay log to <replayDir>/<roomCode>-<unixTs>.sfreplay")
}

func init() {
	// Logger has to be created in init because much of the package init logic
	// (and the lobby goroutines) references `log` before main() runs. We
	// initialize at the default verbosity here; main() may reconfigure after
	// flag.Parse() completes (currently it doesn't — verbosityLevel is read at
	// log-call time).
	log = logger.NewLogger("sf:srv", verbosityLevel)
	stripTags = bluemonday.StrictPolicy()
}

func main() {
	registerFlags()
	flag.Parse()
	// Update logger's verbosity now that flags are parsed.
	log = logger.NewLogger("sf:srv", verbosityLevel)

	var err error

	//Initialize steamcmd (skipped for local smoke tests — only needed for Workshop map downloads)
	if steamKey != "" || steamCmdDir != "" {
		log.Info("Logging into Steam...")
		scmd = steamcmd.New(steamUsername, steamPassword)
		if verbosityLevel == 2 {
			scmd.Debug = true
		}
		if err := scmd.EnsureInstalled(); err != nil {
			log.Fatal(err)
		}
		if err := scmd.CheckLogin(); err != nil {
			log.Fatal(err)
		}
	} else {
		log.Info("Skipping steamcmd init (no -steamKey or -steamCmdDir provided)")
	}

	log.Trace("Seeding randomizer...")
	randomizer = rand.New(rand.NewSource(time.Now().UnixNano()))

	log.Trace("Loading default levels...")
	os.Mkdir("maps", 0755)

	//Phase 5 M1: load dumped map JSONs from /home/miles/sf-multiplayer/maps/
	//(or whatever -mapsDir points at). Each lobby's SpawnWeaponRandom will
	//then use real spawn positions from the dumped scenes. Falls back to the
	//legacy heuristic for scenes without dumped data.
	if mapsDir != "" {
		n, err := LoadMapsFromDir(mapsDir)
		if err != nil {
			log.Warn("Could not load all maps from ", mapsDir, ": ", err)
		}
		log.Info("Loaded ", n, " dumped maps from ", mapsDir)
	}

		for i := int32(1); i <= 124; i++ {
			if i == 0 || i == 102 {
				continue
			}
			defaultLevels = append(defaultLevels, newLevelLandfall(i))
		}

	if scmd != nil {
		lobbyLevels, err = LoadWorkshopMaps(
			2362135194, 2362150591, 2362151526, 2362151645,
			2362151790, 2362151892, 2362152017, 2362152135,
		)
		if err != nil {
			log.Fatal(err)
		}
	} else {
		log.Info("Skipping Workshop map download (steamcmd not initialized); registering IDs only")
		for _, id := range []uint64{
			2362135194, 2362150591, 2362151526, 2362151645,
			2362151790, 2362151892, 2362152017, 2362152135,
		} {
			lobbyLevels = append(lobbyLevels, newLevelCustomOnline(id))
		}
	}
/*	defaultLevels, err = LoadWorkshopMaps(
		2200042304, 2200047921, 2200051799, 2200056261,
		2200058789, 2200062744, 2200069103, 2200073817,
		2200078748, 2200086047, 2200090348, 2200092415,
		2200098344, 2200100283, 2200102885, 2200106023,
		2200107893, 2200109408, 2200112035, 2200113733,
		2200116667, 2200118707, 2200119774, 2200122075,
		2200123719, 2200126432, 2200129454, 2200131581,
		2200137191, 2200140347, 2200142489, 2200145858,
		2200147947, 2200152837, 2200157521, 2200161014,
		2200163529, 2200166476, 2200561630, 2200566979,
		2200572201, 2200577287, 2200582772, 2200585235,
		2200614142, 2200631612, 2205919112, 2205950305,
		2205969235, 2205978243, 2206006449, 2206027577,
		2206041190, 2206047592, 2206225526, 2206344259,
		2206360656, 2206388990, 2206397608, 2206407020,
		2206417499, 2206431577, 2206435705, 2206453774,
		2206460543, 2206464628, 2206467263, 2206518302,
		2206539363, 2206542211, 2208826631, 2208831597,
		2208836118, 2208843238, 2208847724, 2208851046,
		2208859746, 2208864249, 2208897753, 2208910916,
		2208914173, 2208916640, 2208919069, 2208928980,
		2208932119, 2208933857, 2208943514, 2208946630,
		2208996342, 2209006315, 2209010129, 2209020283,
		2209033059, 2209046306, 2209407522, 2209422159,
		2209838063, 2209860643, 2209878071, 2209902974,
		2209906828,
	)
	if err != nil {
		log.Error(err)
	}*/

	//Run the server
	log.Info("Starting the server...")
	server = NewServer(address)
	go server.Run()
	defer server.Close()

	log.Trace("Waiting for exit call from system")
	sc := make(chan os.Signal, 1)
	signal.Notify(sc, syscall.SIGINT)
	<-sc

	log.Trace("SIGINT received!")
	log.Info("Good-bye!")
}
