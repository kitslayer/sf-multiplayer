# Servidor (oracle) vs Assembly del cliente

## Tu intuición (correcta a medias)

| Qué | Quién lo maneja | ¿Necesita Assembly v25 en el **cliente**? | ¿Necesita lógica en el **oracle**? |
|-----|-----------------|------------------------------------------|-----------------------------------|
| Cargar mapa al empezar ronda | **Oracle** `GameManager.StartMatch` + clientes reciben `MapChange` | Cliente: sí (lee `-address`/`-port`, aplica `MapChange`) | **Sí** — Unity headless carga escena aditiva |
| Armas ya puestas en el mapa | Oracle `CheckForGroundWeapons` → `GroundWeaponsInit` (31) | Cliente aplica msg 31 (stock) | **Sí** — `mTempPreSpawnedWeapons` en el servidor |
| Plataformas / GhostPlatform | Oracle `MapInfoSync` + snapshot **v26.6 mapState** | Cliente `SetData` + lerp | **Sí** — timers corren en escena del servidor |
| Barriles / serpientes / NSO | Oracle física + snapshot NSO | Cliente reconcilia v26 | **Sí** |

El **Assembly v25 no se instala en el VPS**. El oracle usa el `Assembly-CSharp.dll` de su carpeta `sf-oracle/install`, pero el comportamiento de mapa lo empuja **SFHeadlessHost** (Harmony + init manual).

## Bug que rompía todo (v0.2.2 fix)

`InvokeOracleStartMatch()` cargaba **siempre escena 6** (Desert3), aunque `/start`, `/map` o la ronda enviaran otro índice en `MapChange`.

- Clientes: mapa correcto (paquete `MapChange`)
- Servidor: mapa **equivocado** → `mapSync=0`, sin armas de mapa, sin lógica de ese nivel

**Fix:** usar `_currentSceneIndex` y recargar oracle en cada ronda (`ScheduleOracleReloadCurrentMap`).

## Secuencia correcta al `/start`

1. `FireMatchStart` → `BroadcastMapChange` (clientes) + `ScheduleOracleReloadCurrentMap`
2. Oracle → `GameManager.StartMatch(scene correcto)` → `StartMapSequence` → escena aditiva
3. Tras settle → `RunPostMapLoadServerInit`: registra `MapInfoSyncableBase`, `InitSyncedObjects`, `CheckForGroundWeapons`, reenvía msg 31
4. Snapshots v26.6 con `mapSync` + `mapState`

## Si sigue fallando

En log del VPS buscar:

```
[v26.6] Oracle will load additive scene NN
[v26.6] PostMapLoad init scene='...' buildIndex=NN
[P6.8] Cached GroundWeaponsInit count=N   (N>0 en mapas con armas)
[v26.6] mapSync=X mapState=Y               (X,Y>0 en mapas con plataformas)
```
