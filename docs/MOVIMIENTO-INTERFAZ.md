# Movimiento integrado de Audiora

30/10/2026. Rama `mejoras/movimiento-interfaz`, base `01b1d94` (main actual). La rama `mejoras/fase1-estabilidad-recuperacion` permanece intacta; no se ha fusionado ni eliminado. Esta entrega no integra ramas ajenas.

## Cambios

- `Controls/Motion.cs`: tiempos comunes 120/160/180/240 ms, spline de salida (0.23,1,0.32,1), relojes finitos sustituibles y finalización protegida por revisión. El valor actual se conserva al invertir; se retiran los relojes al terminar. Se conservan expresiones de binding mediante SetCurrentValue.
- `Theme/Theme.xaml` y `MainWindow.xaml`: feedback de pulsación por ratón en botones, interruptores y entrada breve de superficies de paneles. Los comandos no esperan al movimiento; teclado y preferencia de Windows usan respuesta inmediata. El cierre de popups sigue siendo inmediato para conservar captura y foco nativos. No se alteran comandos, listas o virtualización.
- `MainWindow.xaml.cs`: transición de página con fundido de 180 ms y desplazamiento lateral de 24 DIP en 240 ms, sustitución desde el valor visible al invertir y protección contra un cierre tardío de la página nueva. Cambiar la preferencia de Windows finaliza las transiciones de navegación y controles cargados. Suscripciones retiradas al descargar/cerrar.
- `MiniPlayerWindow.xaml.cs`: atenuación pasiva con el mismo sistema y preferencia de Windows; `VolumeHudWindow.cs`: aviso frecuente de teclado inmediato, conservando su tiempo de lectura.
- `AuroraBackground` y `WaveField`: composición decorativa estática, sin relojes perpetuos. La visualización real de audio y su analizador permanecen intactos.
- `App.xaml.cs`: `--preview-motion` abre una vista previa sin inicializar descubrimiento, recuperación ni reanudación de transmisión; desactiva la persistencia de ajustes como los otros modos de prueba. No usar controles de integración del sistema durante la vista previa. Permite `--screenshot archivo --page settings` y `--exit-after N` para pruebas acotadas.

No se modifican Core, red, captura ni rutas de audio. Se conservan los textos existentes y se añaden dos claves traducidas para la opción de sonidos. Sin dependencias nuevas ni instalaciones.

## Verificación

- Build Release: 0 errores, 0 avisos.
- Regresión actual: 162/162 (113 App, 37 Core, 12 Backup), frente a 149 existentes en esta base. Los 164 de la rama de estabilidad pertenecen a otra base y no se presentan como pruebas de esta rama.
- Cinco pruebas WPF en STA: cancelación inmediata de un reloj, binding conservado, 100 inversiones, 100 cambios del template de interruptor y continuidad al invertir con el reloj real en una superficie WPF. El último test detectó y permitió corregir un salto de un fotograma antes de la implementación final.
- Aplicación ejecutada en vista previa aislada; captura real de Ajustes revisada, salida 0. La instancia instalada del usuario permaneció abierta y no se inició transmisión de prueba.
- Control visual interactivo: la herramienta agotó el plazo esperando autorización de la ventana. No se afirma comprobación manual de pulsaciones, foco, teclado, apertura repetida de paneles ni valoración subjetiva de la fluidez. Pendientes esas comprobaciones y alternar la preferencia de animaciones de Windows en vivo. El camino inmediato equivalente está probado automáticamente; no se cambió la configuración de Windows para probarlo.

## Recuperación

Copia previa verificada SHA-256: `C:\Users\Admin\SonosFlow-Backups\SonosFlow_2026-10-30_12-47-36` (774 archivos). Los cambios están en una rama propia; main y la rama de estabilidad no se modifican. Para retirar esta entrega, revertir únicamente su commit en una rama de trabajo, después de revisar Git. No limpiar ni sobrescribir `.codex/`, que ya estaba sin seguimiento. No se instala ni se publica la aplicación.
## Ampliación tras la aclaración del usuario: interacción y sonidos (HISTÓRICO: la parte de sonidos fue sustituida por la integración descrita al final)

- El indicador de selección de la barra lateral se desplaza entre los elementos reales (posición y altura medidas), sin mover controles ni interceptar el foco. Pantallas entrantes: 24 DIP laterales, 240 ms, curva espacial (0.32,0.72,0,1), dirección invertida en RTL. Paneles: 10 DIP en 240 ms. Botones: hover 1.02 y pulsación 0.97, con reversión continua. Teclado y movimiento desactivado siguen siendo inmediatos.
- Nueva opción «Sonidos de la interfaz» en General, con título y descripción en doce idiomas. `InterfaceSounds=true` es el valor compatible con JSON anteriores; se puede desactivar. En la vista previa la elección solo vive en memoria.
- `Infrastructure/UiFeedback.cs` genera un toque original de 48 ms, PCM mono 48 kHz/16 bit, con envolvente suave y amplitud máxima teórica 0.035. No usa grabaciones ni cambia volúmenes del sistema. `SettingsView.xaml.cs` confirma cambios de CheckBox/ComboBox posteriores a interacción, no el simple cargado de bindings; limita repetición a 140 ms y libera recursos al descargar.
- Se suprime durante Starting/Streaming/Recovering y se detiene al iniciarse una sesión. Una vista previa junto a otra instancia de SonosFlow también se silencia, porque no puede descartar que esa instancia capture todo el PC. Para escuchar el feedback en la prueba manual, cerrar la instancia instalada antes de abrir el ejecutable nuevo. No se cierra la app del usuario desde las pruebas.
- Pruebas adicionales: las dos curvas con inversión real del reloj; formato/duración/amplitud y bordes silenciosos del WAV; cuatro combinaciones de preferencia/streaming, instancia paralela y compatibilidad de ajustes antiguos. Regresión actual 162/162, build sin avisos, captura actualizada de Ajustes revisada y salida 0.
- No se ha verificado acústicamente el timbre a través del dispositivo del usuario ni valorado manualmente su volumen subjetivo. La interacción visual por herramienta sigue pendiente por su permiso. La captura estática y los tests no se presentan como esa comprobación.
- Archivos adicionales: `Infrastructure/UiFeedback.cs`, `Infrastructure/AppSettings.cs`, `ViewModels/MainViewModel.cs`, `Views/SettingsView.xaml(.cs)`, doce `Localization/Strings/*/settings.json`, `UiFeedbackTests.cs` y ampliación de MotionTests. Sin cambios de Core.
- Copia previa a esta ampliación verificada SHA-256: `C:\Users\Admin\SonosFlow-Backups\SonosFlow_2026-10-30_13-22-07` (777 archivos). El commit anterior `67ee109` permite retirar únicamente esta ampliación después de revisar los cambios pendientes.

## Integración con `main` (31/10/2026): un único sistema de sonidos

Al integrar esta rama con `main` (que ya incluía «Audiora Enchanted») se unificaron los sonidos de interfaz:

- **Un solo motor.** Se retira `Infrastructure/UiFeedback.cs` (el `SoundPlayer` de la confirmación de 48 ms) y su prueba. Los sonidos de interfaz los reproduce únicamente el sistema de `main` (`SoundService` / `SoundUiHooks`), que ya confirma los cambios de interruptor y de selección (`ToggleOn`, `ToggleOff`, `ConfigChanged`), enruta al dispositivo elegido y garantiza que ningún sonido llegue al Sonos (`SoundGate`, veto final y cancelación al iniciar la captura).
- **Un solo ajuste.** `InterfaceSounds` existe una vez (`AppSettings` y `MainViewModel.Sounds.cs`) y tiene un único interruptor, en Ajustes → Sonidos. Se elimina el interruptor duplicado de General y sus dos claves `settings.general.sounds.*` en los doce idiomas.
- **Se conserva** todo el movimiento y la interacción visual de esta rama (indicador de la barra lateral, transiciones, botones, `--preview-motion`, `Motion.cs`, `AuroraBackground`, `WaveField`).
- La vista previa `--preview-motion` no crea el servicio de sonidos (modo de captura): no toca ningún dispositivo de audio.

