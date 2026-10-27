# Investigación: interrupciones «hacia los 6 minutos»

Estado (27/10/2026): **causa localizada fuera del PC (red Wi-Fi de la zona o el propio Sonos); mitigación implementada, corregida y validada con el Sonos Five real** (autotest 22/22, margen de ~1,2 s estable). Falta confirmar en uso normal que los cortes desaparecen.

## 1. Lo que dicen tus sesiones reales (26/10, 12:07–13:34)

| Sesión | Duración | Cómo terminó |
|---|---|---|
| 12:07 | 533 s | detenida por ti; reinicio 10 s después |
| 12:16 | 571 s | detenida por ti; **reinicio 0,6 s después** |
| 12:25 | 984 s | detenida por ti; luego saliste de la app |
| 12:50 | 767 s | detenida por ti; **reinicio 0,4 s después** |
| 13:02 | 393 s | detenida por ti |
| 13:11 | 23 min | cierre de sesión de Windows |

En todas: Sonos `PLAYING` el 100 % de las lecturas, 1 cliente, 0 reconexiones, 0 descartes, ritmo 192 kB/s, captura sin huecos. **Ninguna sesión la cortó Audiora ni el Sonos**: las reiniciaste tú, casi seguro al oír un corte. El corte es audible pero no quedaba registrado.

## 2. Descartado con pruebas

| Hipótesis | Prueba | Resultado |
|---|---|---|
| Fallo de captura, marcapasos, servidor o envío en el PC | **Prueba de resistencia de 9 min con detector por muestra** (seno continuo → captura → servidor → lector local que hace de Sonos; cualquier rotura de la onda se detecta aunque dure 1 muestra; validado con un corte deliberado de 300 ms, que detectó al instante) | **0 cortes en 9 min** en «Una aplicación» y en «Todo el PC»; 540,0 s de audio en 540,1 s; hueco máx. entre paquetes 29 ms / 66 ms |
| Deriva de reloj PC↔Sonos | Regresión del búfer estimado en 5 sesiones | −0,12…+0,18 ms/s (ruido) |
| Temporizadores / límites de 6 min en el código | Revisión de todos los timeouts y temporizadores | Ninguno cercano a 6 min; timeouts de 5 s solo al conectar; cabecera WAV válida para 6 h |
| Reconexión silenciosa del Sonos | Puerto TCP vigilado 150 s (incluye 128 MiB) | Sin cambios |
| Fuga de recursos | Proceso de 43 min muestreado 60 s | Handles y memoria estables |

## 3. Causa más probable

El audio sale del PC intacto. El Sonos reproduce **en directo con solo ~0,1–1 s de margen** (no puede adelantarse a un stream en vivo), así que **cualquier pausa de la red más larga que ese margen se oye como un corte** aunque el Sonos siga en `PLAYING`. Indicios en tus sesiones:
- el Sonos lee a ráfagas de recuperación (hasta 219 kB/s) en algunos segundos;
- sus respuestas tardan ~2 s cada 60–90 s (saltos del monitor);
- el Sonos está en la **zona Wi-Fi del PC** y la tarjeta Wi-Fi no está conectada a ninguna red: en ese estado Windows tiende a buscar redes periódicamente con la misma radio, lo que provoca microcortes en los dispositivos de la zona.

La periodicidad exacta de «~6 min» **no está confirmada**: hace falta correlacionar con la nueva telemetría (abajo).

## 4. Solución implementada (sin tocar timeouts ni límites de seguridad)

1. **Margen anti-cortes** (Ajustes → Audio, por defecto **1 s**; opciones 0/1/2/3 s). Al conectar, el Sonos recibe de golpe el último segundo de audio ya capturado y reproduce con ese retraso; así aguanta pausas de red de hasta ese tiempo sin cortarse. «Sin margen» mantiene exactamente el comportamiento anterior.
2. **Telemetría para confirmar la causa**:
   - columna `backlog_ms` en `stream-*.csv`: audio máximo esperando a que el Sonos lo recoja en cada segundo (crece solo si la red o el Sonos dejan de leer; mide lo que excede los búferes de TCP de Windows, ~1 s en local);
   - cada conexión/desconexión del Sonos (hora, puerto, cabeceras, motivo) en el CSV y en `app-events.log` («red: …»).

### Pruebas realizadas
- 6 pruebas nuevas del servidor (sin margen = igual que antes; con margen: exactamente los últimos N ms y luego directo, **sin hueco ni duplicado**; métrica de cola; registro de conexiones; lista blanca).
- 12 pruebas de copias de seguridad: siguen pasando.
- Resistencia de 7 min con margen de 1 s (detector por muestra): **0 cortes** en «Una aplicación» y en «Todo el PC»; unión margen→directo perfecta; 0 descartes; bloqueo máx. de lectura 31 / 67 ms.

### Validación con el Sonos Five (27/10)
- Primer autotest: **falló la comprobación del margen**. El Sonos se conecta 0,2–0,5 s después de iniciar la sesión (ya durante `SetAVTransportURI`), cuando la reserva aún no se ha llenado: recibía ~0,2 s de margen, no 1 s. Además la estimación del búfer descontaba los bytes enviados antes de `Play`.
- Corrección: en la primera conexión lo que falta del margen se envía como silencio al principio (el Sonos recibe siempre el margen completo); la estimación cuenta desde antes de `SetAVTransportURI`.
- Segundo autotest: **22/22 correcto**. Búfer estimado estable 1,1–1,4 s durante toda la sesión (antes 0,1–0,2 s; el salto 1,0 → 0,1 s a los ~7 s que aparece en todas las sesiones antiguas ya no se produce). Cola máx. 60 ms.

## 5. Pendiente

1. Usar Audiora normalmente; cuando oigas un corte, anota la hora: el CSV dirá si hubo cola (`backlog_ms` alto → red) o no (→ el propio Sonos).
2. Si con 1 s aún hay cortes, probar 2 s.

## 6. Nota sobre los registros leídos desde Claude

Claude es una app empaquetada: lo que lanza escribe en una copia virtual (`%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\...`) que oculta los archivos reales al leerlos. Las copias de pruebas se renombraron (`*.claude-tests.*`, reversible). Las comprobaciones antiguas de «ajustes intactos» miraban esa copia, no tus ajustes; los modos de prueba no guardan ajustes.
