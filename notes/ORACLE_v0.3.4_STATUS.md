# Oracle v0.3.4 — estado realista (May 2026)

## Qué está funcionando en VPS (69.53.117.43:1337)

| Área | Estado | Notas |
|------|--------|--------|
| Conexión + `/start` + rondas | OK | MapChange, StartMatch, avance solo |
| Armas del cielo | OK | Timer propio `_oracleNextSkyWeaponAt` (v0.3.1+) |
| Armas del suelo / Factory | OK | GroundWeaponsInit + fuzzy cliente |
| **Lava / cintas / props animados** | OK con delay | `MapInfo` (msg 32) + `CodeAnimation` con `m_ShallSync` |
| Plataformas fantasma / pilares / rutas | OK con delay | `MapInfoSync` + snapshot v26 `mapSync` / `mapState` |
| Halloween / boss (100–109) | Parcial | StartCountDown diferido en cliente; boss setup en servidor |
| Cajas / NSO | Débil | Lag, desapariciones — issue aparte |

## Delay de 3 segundos (intencional en v0.3.4)

Tras cada carga de mapa el oracle espera **`SF_PRE_COMBAT_DELAY` = 3 s** (por defecto) antes de:

- armas (suelo y cielo),
- `StartCountDown` / `inFight`,
- broadcast `MapInfo`.

**No quitar sin probar:** en v0.3.5 (gracia = 0) se rompió de nuevo la estabilidad de mapas. El delay da tiempo a cargar escena + sincronizar stickmen.

## Completitud honesta (~70 % del loop “como Steam host”)

**Hecho:** protocolo v25 relay, snapshot v26 (jugadores, NSO, mapSync, mapState, proyectiles), headless host, cliente BepInEx, deploy VPS + scripts Windows.

**Falta / débil:**

- Cajas y cadenas (NSO autoridad + filtros destrucción).
- Reducir delay sin romper (timing fino post-carga).
- Per-map tuning automático (hoy: log `Map profile` + bootstrap genérico).
- Tests automatizados en CI para mapas 88 (Lava), Factory, Xmas.
- Binarios no van en git (`*.dll` en `.gitignore`) — compilar con `deploy-physics-fix.ps1`.

## Por dónde seguir (mapas)

1. **Mantener gracia 3 s** hasta tener métricas por mapa (log: `Map profile`, `MapInfo sent=`, `mapSync=`).
2. **MapInfo:** solo `CodeAnimation` + `m_ShallSync` y `EnableObjectsPerPlayer`; el resto va por snapshot.
3. **Opcional:** bajar gracia a 2 s mapa a mapa con `SF_PRE_COMBAT_DELAY`, no global a 0.
4. **NSO/cajas:** issue separado — no mezclar con lava/Factory.

## Build / deploy

```powershell
.\deploy-physics-fix.ps1 -DeployVps          # servidor
.\deploy\instalar-cliente-oracle.ps1         # cliente Steam
```

Versiones: `SFHeadlessHost` / `SFClientRecon` **0.3.4**.
