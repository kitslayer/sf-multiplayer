using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFLevelDumper
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.level-dumper";
        public const string PluginName = "SFLevelDumper";
        public const string PluginVersion = "1.0.0";

        // Landfall build indices to iterate. 0 is the lobby, 102 is the stats
        // scene (see StickFightDedicatedSrv/levels.go IsLobby/IsStats).
        public const int FirstSceneIndex = 1;
        public const int LastSceneIndex = 124;
        public const int StatsSceneIndex = 102;

        // Track which scenes (by buildIndex or scene-name hash for workshop)
        // we've already dumped, so the runtime listener doesn't re-dump on every
        // map change.
        private static readonly HashSet<string> AlreadyDumped = new HashSet<string>();
        private static bool RuntimeMode;

        internal static ManualLogSource Log;
        internal static string OutputDir;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} {PluginVersion} loading...");

            // Default output dir. Can be overridden via env var SF_LEVELDUMPER_OUT
            // so the plugin works on Windows or Linux installs.
            OutputDir = Environment.GetEnvironmentVariable("SF_LEVELDUMPER_OUT");
            if (string.IsNullOrEmpty(OutputDir))
            {
                // Prefer the dev path if it exists; otherwise drop next to the SF exe.
                const string devPath = "/home/miles/sf-multiplayer/maps";
                if (Directory.Exists("/home/miles/sf-multiplayer"))
                {
                    OutputDir = devPath;
                }
                else
                {
                    OutputDir = Path.Combine(Application.dataPath ?? ".", "..", "LevelDumps");
                }
            }

            try { Directory.CreateDirectory(OutputDir); }
            catch (Exception e) { Log.LogError($"Couldn't create output dir {OutputDir}: {e}"); }

            Log.LogInfo($"Output dir: {OutputDir}");
            Log.LogInfo($"Total scenes in build: {SceneManager.sceneCountInBuildSettings}");

            // Two modes: SF_LEVELDUMPER_BATCH=1 (or default if SF was launched
            // headlessly) iterates every build-scene up-front. SF_LEVELDUMPER_RUNTIME=1
            // (the new "follow-along" mode) just hooks SceneManager.sceneLoaded
            // and dumps any scene as it loads — handles workshop maps that aren't
            // in the build settings.
            string runtimeFlag = Environment.GetEnvironmentVariable("SF_LEVELDUMPER_RUNTIME");
            string batchFlag = Environment.GetEnvironmentVariable("SF_LEVELDUMPER_BATCH");
            if (runtimeFlag == "1" || (string.IsNullOrEmpty(batchFlag) && Application.isPlaying))
            {
                RuntimeMode = true;
                SceneManager.sceneLoaded += OnSceneLoadedRuntime;
                Log.LogInfo("SFLevelDumper: runtime mode — will dump scenes as they load (incl. workshop). Set SF_LEVELDUMPER_BATCH=1 to revert to batch sweep.");
            }
            else
            {
                StartCoroutine(DumpAllScenes());
            }
        }

        // OnSceneLoadedRuntime fires once per SceneManager.LoadScene call. We
        // dump the scene only if we haven't seen it before; output filenames
        // are landfall-N.json for build scenes (N = buildIndex) and
        // workshop-{sanitizedName}.json for streamed/workshop content.
        private void OnSceneLoadedRuntime(Scene scene, LoadSceneMode mode)
        {
            try
            {
                int buildIdx = scene.buildIndex;
                string key;
                string outName;
                if (buildIdx >= 0)
                {
                    key = "build-" + buildIdx;
                    outName = $"landfall-{buildIdx}.json";
                }
                else
                {
                    // buildIndex == -1 → streamed/workshop scene loaded by name/path.
                    string safe = (scene.name ?? "unknown").Replace("/", "_").Replace("\\", "_").Replace(" ", "_");
                    key = "name-" + safe;
                    outName = $"workshop-{safe}.json";
                }
                if (AlreadyDumped.Contains(key))
                {
                    return;
                }
                AlreadyDumped.Add(key);

                // Skip the lobby (0) and stats (102) for build scenes — they have
                // no meaningful weapon/spawn data and bloat the output dir.
                if (buildIdx == 0 || buildIdx == StatsSceneIndex)
                {
                    return;
                }

                // Defer one frame so any Awake/Start handlers (spawn-point registration,
                // weapon-pickup InitGroundWeapon) have run.
                StartCoroutine(DumpAfterFrame(scene, outName));
            }
            catch (Exception e)
            {
                Log.LogError($"OnSceneLoadedRuntime threw: {e}");
            }
        }

        private IEnumerator DumpAfterFrame(Scene scene, string outName)
        {
            yield return null;
            yield return null;
            LevelDump dump;
            try
            {
                dump = ExtractCurrentScene(scene.buildIndex);
                dump.name = scene.name ?? string.Empty;
            }
            catch (Exception e)
            {
                Log.LogError($"Extracting scene {scene.name} threw: {e}");
                yield break;
            }
            try
            {
                string path = Path.Combine(OutputDir, outName);
                File.WriteAllText(path, JsonWriter.Serialize(dump));
                Log.LogInfo($"Dumped {scene.name} → {outName} ({dump.staticColliders.Count} colliders, {dump.playerSpawns.Count} player spawns, {dump.weaponSpawns.Count} weapon spawns)");
            }
            catch (Exception e)
            {
                Log.LogError($"Writing {outName} threw: {e}");
            }
        }

        private IEnumerator DumpAllScenes()
        {
            // Wait a moment after startup to let bootstrap init complete.
            yield return new WaitForSeconds(1.0f);

            int sceneCount = SceneManager.sceneCountInBuildSettings;
            int last = Math.Min(LastSceneIndex, sceneCount - 1);

            for (int i = FirstSceneIndex; i <= last; i++)
            {
                if (i == StatsSceneIndex) continue;

                AsyncOperation op = null;
                try
                {
                    Log.LogInfo($"Loading scene {i}...");
                    op = SceneManager.LoadSceneAsync(i, LoadSceneMode.Single);
                }
                catch (Exception e)
                {
                    Log.LogError($"LoadSceneAsync({i}) threw: {e}");
                    continue;
                }
                if (op == null)
                {
                    Log.LogWarning($"Scene {i} returned null AsyncOperation; skipping.");
                    continue;
                }
                while (!op.isDone) yield return null;

                // Wait one frame for Awake/Start, then a bit more for any
                // pre-spawn coroutines (e.g. ground-weapon registration in
                // WeaponPickUp.Awake).
                yield return null;
                yield return new WaitForSeconds(0.5f);

                LevelDump dump;
                try
                {
                    dump = ExtractCurrentScene(i);
                }
                catch (Exception e)
                {
                    Log.LogError($"Extracting scene {i} threw: {e}");
                    continue;
                }

                try
                {
                    string path = Path.Combine(OutputDir, $"landfall-{i}.json");
                    File.WriteAllText(path, JsonWriter.Serialize(dump));
                    Log.LogInfo($"Wrote {path}  (static={dump.staticColliders.Count} spawns={dump.playerSpawns.Count} weapons={dump.weaponSpawns.Count} killboxes={dump.killboxes.Count} sync={dump.syncableObjects.Count})");
                }
                catch (Exception e)
                {
                    Log.LogError($"Write scene {i} threw: {e}");
                }
            }

            Log.LogInfo("All scenes processed. Quitting.");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // ----- Extraction --------------------------------------------------

        private static LevelDump ExtractCurrentScene(int sceneIndex)
        {
            Scene scene = SceneManager.GetActiveScene();
            LevelDump d = new LevelDump
            {
                sceneIndex = sceneIndex,
                name = scene.name ?? string.Empty,
            };

            // Collect every active GameObject in the scene's root hierarchy.
            GameObject[] roots = scene.GetRootGameObjects();
            List<GameObject> all = new List<GameObject>(1024);
            foreach (var r in roots) CollectActive(r, all);

            HashSet<int> seenColliderIds = new HashSet<int>();

            foreach (GameObject go in all)
            {
                // MapInfo carries player spawn transforms.
                MapInfo mi = go.GetComponent<MapInfo>();
                if (mi != null && mi.spawnPoints != null)
                {
                    foreach (Transform t in mi.spawnPoints)
                    {
                        if (t == null) continue;
                        d.playerSpawns.Add(new V3Wrap { pos = ToArr(t.position) });
                    }
                }

                // WeaponPickUp components mark weapon spawns. We use them
                // regardless of whether they're ground weapons (flyUpAfter ==
                // +inf) — the server can decide later.
                WeaponPickUp wp = go.GetComponent<WeaponPickUp>();
                if (wp != null)
                {
                    d.weaponSpawns.Add(new V3Wrap { pos = ToArr(go.transform.position) });
                }

                // Killboxes: KillingFloor (Y-plane kill) and KIllAllOutOfRange
                // (radius kill — see KIllAllOutOfRange.GO()).
                if (go.GetComponent<KillingFloor>() != null)
                {
                    AABB ab = ComputeAABB(go);
                    d.killboxes.Add(new BoxObj
                    {
                        pos = ToArr(ab.center),
                        size = ToArr(ab.size),
                    });
                }
                if (go.GetComponent<KIllAllOutOfRange>() != null)
                {
                    // Radius-style; encode the 2.2-unit reach used in KIllAllOutOfRange.
                    d.killboxes.Add(new BoxObj
                    {
                        pos = ToArr(go.transform.position),
                        size = new float[] { 4.4f, 4.4f, 4.4f },
                    });
                }

                // Syncable objects: anything with NetworkSyncableObject (or its
                // subclasses like DestructiblePiece) needs server sync.
                NetworkSyncableObject nso = go.GetComponent<NetworkSyncableObject>();
                if (nso != null)
                {
                    d.syncableObjects.Add(new SyncObj
                    {
                        pos = ToArr(go.transform.position),
                        type = SafeTypeName(nso) + ":" + go.name,
                    });
                }
            }

            // Static colliders: iterate every collider, skip ones attached to
            // a Rigidbody (dynamic) and skip ones that already belong to a
            // weapon pickup / syncable object (we recorded those separately).
            // Note: GetComponentsInChildren<T>(true) would catch inactive too,
            // but we only want active.
            Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>();
            foreach (Collider col in colliders)
            {
                if (col == null) continue;
                if (!col.gameObject.activeInHierarchy) continue;
                if (col.isTrigger) continue;
                if (col.attachedRigidbody != null) continue;

                // Skip player rigs / weapon pickups / explicit syncable objects.
                if (col.GetComponentInParent<WeaponPickUp>() != null) continue;
                if (col.GetComponentInParent<NetworkSyncableObject>() != null) continue;
                if (col.GetComponentInParent<KillingFloor>() != null) continue;

                StaticCollider sc = DescribeCollider(col);
                if (sc != null) d.staticColliders.Add(sc);
            }

            return d;
        }

        private static StaticCollider DescribeCollider(Collider col)
        {
            Transform tf = col.transform;
            Quaternion rot = tf.rotation;
            Vector3 worldScale = tf.lossyScale;

            if (col is BoxCollider bx)
            {
                Vector3 worldCenter = tf.TransformPoint(bx.center);
                Vector3 worldSize = new Vector3(
                    Mathf.Abs(bx.size.x * worldScale.x),
                    Mathf.Abs(bx.size.y * worldScale.y),
                    Mathf.Abs(bx.size.z * worldScale.z));
                return new StaticCollider
                {
                    pos = ToArr(worldCenter),
                    rot = ToArr(rot),
                    size = ToArr(worldSize),
                    kind = "Box",
                };
            }
            if (col is SphereCollider sp)
            {
                Vector3 worldCenter = tf.TransformPoint(sp.center);
                float maxScale = Mathf.Max(Mathf.Abs(worldScale.x), Mathf.Abs(worldScale.y), Mathf.Abs(worldScale.z));
                float d = sp.radius * maxScale * 2f;
                return new StaticCollider
                {
                    pos = ToArr(worldCenter),
                    rot = ToArr(rot),
                    size = new float[] { d, d, d },
                    kind = "Sphere",
                };
            }
            if (col is CapsuleCollider cp)
            {
                Vector3 worldCenter = tf.TransformPoint(cp.center);
                // Compute approximate worldspace size from the capsule's local
                // shape; collapse to a bounding box.
                float radius = cp.radius * Mathf.Max(Mathf.Abs(worldScale.x), Mathf.Abs(worldScale.z));
                float height = Mathf.Max(cp.height * Mathf.Abs(worldScale.y), radius * 2f);
                return new StaticCollider
                {
                    pos = ToArr(worldCenter),
                    rot = ToArr(rot),
                    size = new float[] { radius * 2f, height, radius * 2f },
                    kind = "Capsule",
                };
            }
            if (col is MeshCollider mc)
            {
                Bounds wb = mc.bounds; // worldspace AABB
                return new StaticCollider
                {
                    pos = ToArr(wb.center),
                    // MeshCollider rotation isn't meaningful for an AABB; report identity.
                    rot = new float[] { 0f, 0f, 0f, 1f },
                    size = ToArr(wb.size),
                    kind = "Mesh",
                };
            }
            return null;
        }

        private static AABB ComputeAABB(GameObject go)
        {
            Bounds? combined = null;
            Renderer[] rs = go.GetComponentsInChildren<Renderer>();
            foreach (var r in rs)
            {
                if (combined == null) combined = r.bounds;
                else { var b = combined.Value; b.Encapsulate(r.bounds); combined = b; }
            }
            Collider[] cs = go.GetComponentsInChildren<Collider>();
            foreach (var c in cs)
            {
                if (combined == null) combined = c.bounds;
                else { var b = combined.Value; b.Encapsulate(c.bounds); combined = b; }
            }
            if (combined.HasValue)
            {
                return new AABB { center = combined.Value.center, size = combined.Value.size };
            }
            return new AABB { center = go.transform.position, size = Vector3.one };
        }

        private static void CollectActive(GameObject go, List<GameObject> sink)
        {
            if (go == null) return;
            if (!go.activeInHierarchy) return;
            sink.Add(go);
            foreach (Transform child in go.transform)
            {
                CollectActive(child.gameObject, sink);
            }
        }

        private static string SafeTypeName(object o)
        {
            if (o == null) return "null";
            return o.GetType().Name;
        }

        private static float[] ToArr(Vector3 v) => new float[] { v.x, v.y, v.z };
        private static float[] ToArr(Quaternion q) => new float[] { q.x, q.y, q.z, q.w };
    }

    // ----- Schema --------------------------------------------------------

    internal struct AABB
    {
        public Vector3 center;
        public Vector3 size;
    }

    internal class LevelDump
    {
        public int sceneIndex;
        public string name;
        public List<StaticCollider> staticColliders = new List<StaticCollider>();
        public List<V3Wrap> playerSpawns = new List<V3Wrap>();
        public List<V3Wrap> weaponSpawns = new List<V3Wrap>();
        public List<BoxObj> killboxes = new List<BoxObj>();
        public List<SyncObj> syncableObjects = new List<SyncObj>();
    }

    internal class StaticCollider
    {
        public float[] pos;
        public float[] rot;
        public float[] size;
        public string kind;
    }

    internal class V3Wrap
    {
        public float[] pos;
    }

    internal class BoxObj
    {
        public float[] pos;
        public float[] size;
    }

    internal class SyncObj
    {
        public float[] pos;
        public string type;
    }

    // ----- JSON writer ---------------------------------------------------

    internal static class JsonWriter
    {
        public static string Serialize(LevelDump d)
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append('{');
            Key(sb, "sceneIndex"); sb.Append(d.sceneIndex); sb.Append(',');
            Key(sb, "name"); Str(sb, d.name ?? ""); sb.Append(',');
            Key(sb, "staticColliders"); Arr(sb, d.staticColliders, WriteStatic); sb.Append(',');
            Key(sb, "playerSpawns"); Arr(sb, d.playerSpawns, WriteV3Wrap); sb.Append(',');
            Key(sb, "weaponSpawns"); Arr(sb, d.weaponSpawns, WriteV3Wrap); sb.Append(',');
            Key(sb, "killboxes"); Arr(sb, d.killboxes, WriteBox); sb.Append(',');
            Key(sb, "syncableObjects"); Arr(sb, d.syncableObjects, WriteSync);
            sb.Append('}');
            return sb.ToString();
        }

        private static void WriteStatic(StringBuilder sb, StaticCollider s)
        {
            sb.Append('{');
            Key(sb, "pos"); FArr(sb, s.pos); sb.Append(',');
            Key(sb, "rot"); FArr(sb, s.rot); sb.Append(',');
            Key(sb, "size"); FArr(sb, s.size); sb.Append(',');
            Key(sb, "kind"); Str(sb, s.kind ?? "");
            sb.Append('}');
        }

        private static void WriteV3Wrap(StringBuilder sb, V3Wrap v)
        {
            sb.Append('{');
            Key(sb, "pos"); FArr(sb, v.pos);
            sb.Append('}');
        }

        private static void WriteBox(StringBuilder sb, BoxObj b)
        {
            sb.Append('{');
            Key(sb, "pos"); FArr(sb, b.pos); sb.Append(',');
            Key(sb, "size"); FArr(sb, b.size);
            sb.Append('}');
        }

        private static void WriteSync(StringBuilder sb, SyncObj s)
        {
            sb.Append('{');
            Key(sb, "pos"); FArr(sb, s.pos); sb.Append(',');
            Key(sb, "type"); Str(sb, s.type ?? "");
            sb.Append('}');
        }

        private static void Arr<T>(StringBuilder sb, List<T> items, Action<StringBuilder, T> w)
        {
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                w(sb, items[i]);
            }
            sb.Append(']');
        }

        private static void FArr(StringBuilder sb, float[] arr)
        {
            sb.Append('[');
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    F(sb, arr[i]);
                }
            }
            sb.Append(']');
        }

        private static void F(StringBuilder sb, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                sb.Append('0');
                return;
            }
            sb.Append(v.ToString("G7", CultureInfo.InvariantCulture));
        }

        private static void Key(StringBuilder sb, string k)
        {
            sb.Append('"'); sb.Append(k); sb.Append('"'); sb.Append(':');
        }

        private static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("X4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
