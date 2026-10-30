# Movimiento integrado de Audiora

30/10/2026. Rama `mejoras/movimiento-interfaz`, base `01b1d94` (main actual). La rama `mejoras/fase1-estabilidad-recuperacion` permanece intacta; no se ha fusionado ni eliminado. Esta entrega no integra ramas ajenas.

## Cambios

- `Controls/Motion.cs`: tiempos comunes 120/160/180 ms, spline de salida (0.23,1,0.32,1), relojes finitos sustituibles y finalización protegida por revisión. El valor actual se conserva al invertir; se retiran los relojes al terminar. Se conservan expresiones de binding mediante SetCurrentValue.
- `Theme/Theme.xaml` y `MainWindow.xaml`: feedback de pulsación por ratón en botones, interruptores y entrada breve de superficies de paneles. Los comandos no esperan al movimiento; teclado y preferencia de Windows usan respuesta inmediata. El cierre de popups sigue siendo inmediato para conservar captura y foco nativos. No se alteran comandos, listas o virtualización.
- `MainWindow.xaml.cs`: transición de página de 180 ms con opacidad y desplazamiento de 6 DIP, sustitución desde el valor visible al invertir y protección contra un cierre tardío de la página nueva. Cambiar la preferencia de Windows finaliza las transiciones de navegación y controles cargados. Suscripciones retiradas al descargar/cerrar.
- `MiniPlayerWindow.xaml.cs`: atenuación pasiva con el mismo sistema y preferencia de Windows; `VolumeHudWindow.cs`: aviso frecuente de teclado inmediato, conservando su tiempo de lectura.
- `AuroraBackground` y `WaveField`: composición decorativa estática, sin relojes perpetuos. La visualización real de audio y su analizador permanecen intactos.
- `App.xaml.cs`: `--preview-motion` abre una vista previa sin inicializar descubrimiento, recuperación ni reanudación de transmisión; desactiva la persistencia de ajustes como los otros modos de prueba. No usar controles de integración del sistema durante la vista previa. Permite `--screenshot archivo --page settings` y `--exit-after N` para pruebas acotadas.

No se modifican Core, red, captura, rutas de audio, traducciones ni el diseño de las pantallas. Sin dependencias nuevas ni instalaciones.

## Verificación

- Build Release: 0 errores, 0 avisos.
- Regresión: 154/154 (105 App, 37 Core, 12 Backup), frente a 149 existentes en esta base. Los 164 de la rama de estabilidad pertenecen a otra base y no se presentan como pruebas de esta rama.
- Cinco pruebas WPF en STA: cancelación inmediata de un reloj, binding conservado, 100 inversiones, 100 cambios del template de interruptor y continuidad al invertir con el reloj real en una superficie WPF. El último test detectó y permitió corregir un salto de un fotograma antes de la implementación final.
- Aplicación ejecutada en vista previa aislada; captura real de Ajustes revisada, salida 0. La instancia instalada del usuario permaneció abierta y no se inició transmisión de prueba.
- Control visual interactivo: la herramienta agotó el plazo esperando autorización de la ventana. No se afirma comprobación manual de pulsaciones, foco, teclado, apertura repetida de paneles ni valoración subjetiva de la fluidez. Pendientes esas comprobaciones y alternar la preferencia de animaciones de Windows en vivo. El camino inmediato equivalente está probado automáticamente; no se cambió la configuración de Windows para probarlo.

## Recuperación

Copia previa verificada SHA-256: `C:\Users\Admin\SonosFlow-Backups\SonosFlow_2026-10-30_12-47-36` (774 archivos). Los cambios están en una rama propia; main y la rama de estabilidad no se modifican. Para retirar esta entrega, revertir únicamente su commit en una rama de trabajo, después de revisar Git. No limpiar ni sobrescribir `.codex/`, que ya estaba sin seguimiento. No se instala ni se publica la aplicación.