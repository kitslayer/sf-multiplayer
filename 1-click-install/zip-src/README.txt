===================================================================
   sf-multiplayer  -  Stick Fight: The Game  -  Mod Oracle (cliente)
   kitslayer
===================================================================

Este paquete contiene SOLO lo necesario para jugar (release): el mod ya
compilado + BepInEx. No trae el codigo fuente.

Contenido:
  INSTALAR-sf-multiplayer.bat     -> instalacion 1-CLICK (automatica)
  DESINSTALAR-sf-multiplayer.bat  -> revertir a vanilla
  StickFight-DropIn\              -> los archivos, ordenados EXACTAMENTE como
                                      van dentro de la carpeta de Stick Fight
  README.txt                      -> esto

-------------------------------------------------------------------
  OPCION A — INSTALACION AUTOMATICA (1 clic, recomendada)
-------------------------------------------------------------------
  1) Cerra Stick Fight si esta abierto.
  2) Doble clic en  INSTALAR-sf-multiplayer.bat
  3) Acepta el permiso de administrador.
  4) Esperar a "INSTALACION COMPLETA".

  El instalador:
   - Encuentra Stick Fight solo (cualquier biblioteca de Steam).
   - Hace BACKUP de tu Assembly-CSharp.dll (-> Assembly-CSharp.dll.vanilla.bak).
   - Copia BepInEx + los plugins + el Assembly-CSharp parcheado.
   - Deja un acceso directo "Jugar-StickFight.bat" en el escritorio.

-------------------------------------------------------------------
  OPCION B — INSTALACION MANUAL (copiar y pegar)
-------------------------------------------------------------------
  1) Cerra Stick Fight.
  2) Abri la carpeta de Stick Fight:
       Steam -> Stick Fight -> Propiedades -> Archivos instalados ->
       Examinar...   (o normalmente:
       C:\Program Files (x86)\Steam\steamapps\common\StickFightTheGame )
  3) BACKUP (importante): copia
       StickFight_Data\Managed\Assembly-CSharp.dll
     a otro lado (o renombralo a Assembly-CSharp.dll.vanilla.bak).
  4) Abri la carpeta  StickFight-DropIn\  de este paquete.
  5) Selecciona TODO su contenido y pegalo DENTRO de la carpeta de
     Stick Fight. Cuando pregunte por reemplazar, decir SI
     (reemplaza Assembly-CSharp.dll por el parcheado).
     -> La estructura ya coincide: cada archivo cae en su lugar
        (winhttp.dll, doorstop_config.ini, BepInEx\..., StickFight_Data\...).
  6) (Opcional) En Steam -> Stick Fight -> Propiedades -> Opciones de
     lanzamiento, pega:   -address 69.53.117.43 -port 1337

-------------------------------------------------------------------
  COMO JUGAR
-------------------------------------------------------------------
  - Abri Stick Fight (por el acceso directo del escritorio, o por Steam
    si pusiste las opciones de lanzamiento).
  - PLAY ONLINE -> QUICK MATCH.
  - En el lobby del mapa, escribi en el chat:  /start

-------------------------------------------------------------------
  COMO REVERTIR (volver a vanilla)
-------------------------------------------------------------------
  AUTOMATICO:
   - Doble clic en  DESINSTALAR-sf-multiplayer.bat
     (restaura tu Assembly-CSharp.dll original, quita los plugins y
      desactiva BepInEx).

  MANUAL:
   1) Cerra Stick Fight.
   2) En  StickFight_Data\Managed\  reemplaza Assembly-CSharp.dll por tu
      backup (Assembly-CSharp.dll.vanilla.bak -> Assembly-CSharp.dll).
   3) Borra  BepInEx\plugins\SFClientRecon.dll  y  SFServerBrowser.dll
   4) Para desactivar BepInEx del todo: en doorstop_config.ini pone
      enabled = false   (o borra winhttp.dll).
   5) Quita las opciones de lanzamiento -address de Steam.
   - Si algo quedo mal: Steam -> Stick Fight -> Propiedades -> Archivos
     instalados -> Verificar integridad de los archivos (restaura todo).

-------------------------------------------------------------------
  NOTAS
-------------------------------------------------------------------
  - Si tenes MelonLoader: convive con BepInEx. Si el menu online falla,
    renombra version.dll temporalmente mientras jugas en el oracle.
  - Servidor por defecto: 69.53.117.43 : 1337
  - Discord: kitslayer
===================================================================
