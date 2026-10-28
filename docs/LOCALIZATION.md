# Localización de Audiora

Audiora se muestra en 12 idiomas. El idioma se elige en el primer arranque y se puede cambiar en **Ajustes → Idioma**. El cambio se aplica en vivo, sin reiniciar y sin cortar la transmisión. Los registros (`app-events.log`, `stream-*.csv`) y los identificadores técnicos (`SonosFlow`, `%APPDATA%\SonosFlow`, claves HKCU) no dependen del idioma.

## Arquitectura

| Pieza | Archivo | Qué hace |
|---|---|---|
| Registro de idiomas | `src/SonosFlow.App/Localization/Languages.cs` | Lista única de idiomas: código, nombre en inglés, nombre nativo y si es RTL. También contiene `Fallback = "en"` y `Source = "es"`, `Resolve(tag)` (por ejemplo es-MX→es, pt-PT→pt-BR, zh-CN→zh-Hans; zh-TW/HK→sin equivalente) y `SuggestFromSystem`. |
| Textos | `Localization/Strings/<código>/<área>.json` | JSON plano `"clave": "texto"`, con un archivo por área (`main`, `mini`, `settings`, `backup`, `tray`, `status`, `errors`, `source`, `onboarding`, `setup`). Se incrustan en el ensamblado como `i18n/<código>/<área>.json`, así que no hay archivos sueltos que se puedan perder. |
| Motor | `Localization/Loc.cs` | Singleton `INotifyPropertyChanged` con indexador. La búsqueda sigue la cadena **idioma actual → inglés → español → `⟦clave⟧`**; una clave ausente nunca hace fallar la app y queda anotada en `MissingKeys`. Formatea con la cultura del idioma (`F`) y atrapa los formatos rotos. También resuelve plurales (`Plural`) y los mensajes codificados del Core (`Message("clave\|arg")`). |
| XAML | `Localization/T.cs` | `{loc:T clave}` crea un enlace al indexador de `Loc`, de modo que al cambiar de idioma toda la interfaz se refresca sola. |
| Plurales | `Localization/PluralRules.cs` | Categorías CLDR por idioma (`.zero/.one/.two/.few/.many/.other`) como sufijos de la clave. |
| Arranque | `Localization/LanguageStartup.cs` + `App.xaml.cs` (`ChooseLanguage`) | Decide si se muestra la bienvenida (ver más abajo). |
| Bienvenida | `Views/LanguageWindow.xaml(.cs)` | Selector con búsqueda y nombres nativos. El idioma sugerido por Windows aparece primero, y al seleccionar un idioma la ventana lo muestra ya traducido. |
| Ajustes | `Views/SettingsView.xaml` (sección Idioma) + `ViewModels/MainViewModel.Language.cs` | Cambio en vivo; guarda `Language` y `LanguageOnboardingDone`. |
| Core | `src/SonosFlow.Core/Streaming/SessionMessages.cs` | El motor de audio no contiene texto de interfaz: emite claves estables (`core.speakerUnreachable`, …) que la app traduce. Así los CSV quedan iguales en todos los idiomas. |
| Instalador | `src/SonosFlow.Setup/SetupText.cs` | Usa los mismos `setup.json` (incrustados) y elige el idioma de Windows; para árabe aplica `RightToLeftLayout`. |

### Por qué este diseño

No se usaron `.resx` porque los JSON planos son fáciles de revisar, de comparar en un diff y de pasar a un traductor. El instalador (.NET Framework 4.8) puede leer los mismos archivos, y las pruebas detectan automáticamente las claves que falten o sobren. El enlace al indexador permite cambiar de idioma en vivo sin recrear ventanas, algo que `x:Static` no permite. Los mensajes de estado que se guardan en memoria (`StatusDetail`, `LastErrorText`) se almacenan como `Func<string>`, así que también cambian de idioma en vivo.

## Primer arranque y actualizaciones

| Situación | Resultado |
|---|---|
| Instalación nueva (no existe `%APPDATA%\SonosFlow\settings.json`) | Aparece la bienvenida con el idioma de Windows sugerido. Al confirmar se guarda el idioma y `LanguageOnboardingDone = true`. |
| Arranque normal con el idioma ya guardado | Se usa el idioma guardado y no aparece la bienvenida. |
| Actualización desde una versión anterior sin idiomas (hay `settings.json` pero no tiene `Language`) | Se sigue en **español**, que era el idioma de la app, y se guarda. No aparece la bienvenida. |
| Valor guardado inválido o idioma retirado | Se repara con el idioma soportado más cercano (o con la sugerencia del sistema) y se guarda. |
| `settings.json` existe pero no se puede leer (dañado o truncado) | Esos ajustes ya estaban perdidos (la app arranca con los valores por defecto, como antes). Se guarda una copia del archivo dañado (`settings.unreadable-<fecha>.json`) y se vuelve a mostrar la bienvenida, en lugar de imponer el español. |
| Volver a una versión anterior y luego actualizar otra vez | La versión anterior reescribe `settings.json` sin el idioma. Al actualizar de nuevo se aplica el caso «actualización»: español, sin bienvenida. El idioma se cambia en Ajustes. |
| Desinstalar y reinstalar | La desinstalación **no** borra `settings.json` (solo accesos directos, entradas HKCU y la carpeta del programa), así que se conservan el idioma y los demás ajustes. Para volver a ver la bienvenida hay que borrar ese archivo a mano; la app nunca lo hace. |
| Se cierra la bienvenida (X o Esc) | Se usa el idioma sugerido, se guarda y la app se abre con normalidad. |
| Modos de prueba (`--screenshot*`, `--selftest-*`, `--lang`) | Nunca muestran la bienvenida ni guardan el idioma. |

Lo cubre `tests/SonosFlow.App.Tests/LanguageStartupTests.cs`.

## Matriz de idiomas

Todos los idiomas incluyen todas las claves (las pruebas lo exigen). Fechas y números usan la cultura del idioma, siempre con calendario gregoriano (`Languages.CultureFor`). Las traducciones distintas del español original las hizo IA y **necesitan revisión nativa** antes de considerarse definitivas.

| Código | Idioma | Nombre nativo | Cultura de formato | Dirección | Completitud | Plurales | Notas de fuente | Revisión nativa |
|---|---|---|---|---|---|---|---|---|
| es | Spanish | Español | es | LTR | 100 % (original) | one/other | Segoe UI | Texto original |
| en | English | English | en | LTR | 100 % | one/other | Segoe UI | Recomendable |
| pt-BR | Portuguese (Brazil) | Português (Brasil) | pt-BR | LTR | 100 % (IA) | one/other | Segoe UI | **Sí** |
| fr | French | Français | fr | LTR | 100 % (IA) | one/other | Segoe UI | **Sí** |
| de | German | Deutsch | de | LTR | 100 % (IA) | one/other | Segoe UI; textos ~30 % más largos | **Sí** |
| it | Italian | Italiano | it | LTR | 100 % (IA) | one/other | Segoe UI | **Sí** |
| ru | Russian | Русский | ru | LTR | 100 % (IA) | one/few/many | Segoe UI (cirílico) | **Sí** |
| zh-Hans | Chinese (Simplified) | 简体中文 | zh-Hans | LTR | 100 % (IA) | other | Microsoft YaHei UI (sustitución automática de WPF) | **Sí** |
| ja | Japanese | 日本語 | ja | LTR | 100 % (IA) | other | Yu Gothic UI (sustitución automática) | **Sí** |
| ko | Korean | 한국어 | ko | LTR | 100 % (IA) | other | Malgun Gothic (sustitución automática) | **Sí** |
| hi | Hindi | हिन्दी | hi | LTR | 100 % (IA) | one/other | Nirmala UI (sustitución automática) | **Sí** |
| ar | Arabic | العربية | ar (gregoriano) | **RTL** | 100 % (IA) | zero/one/two/few/many/other | Segoe UI (árabe incluido) | **Sí** |

**No incluidos por ahora** (candidatos razonables): bengalí, indonesio, urdu (RTL), turco, vietnamita, tailandés, persa (RTL) y chino tradicional. Se prefirió entregar 12 idiomas completos antes que muchos a medias. Mientras no exista una traducción propia, zh-TW/zh-HK caen en la sugerencia de inglés en lugar de recibir chino simplificado.

## Derecha a izquierda (árabe)

- Cada ventana enlaza `FlowDirection` a `Loc.Instance.FlowDirection`, así que el diseño se refleja en vivo: la navegación y el logo pasan a la derecha y los botones de la ventana a la izquierda.
- **Las imágenes nunca se reflejan** (logo, carátula, iconos de apps): un estilo implícito en `App.xaml` y la carátula del mini reproductor fijan `LeftToRight`. La carátula de la ventana principal usa un `ImageBrush`, porque un `Image` con `UniformToFill` se recortaba mal en RTL.
- **Textos que no son de la app** (título y artista de la canción, nombres de idiomas) toman la dirección de su primer carácter fuerte (`Localization/AutoDirection.cs`, como `dir="auto"` en HTML). Así «Português (Brasil)» o «(Video Oficial)» no pierden los paréntesis de sitio. Las marcas Unicode LRE/RLE no sirven en WPF; se probó y no cambian nada.
- Se mantienen de izquierda a derecha a propósito: el botón de transmitir, el anillo del espectro y los controles de reproducción del mini reproductor. El deslizador de volumen sí se refleja (en RTL se llena de derecha a izquierda, como en Windows en árabe).
- El instalador usa `RightToLeft` y `RightToLeftLayout` cuando Windows está en árabe.
- **Verificación:** capturas de la ventana principal, Ajustes, el mini reproductor y la bienvenida en árabe, cotejadas con una captura real de la pantalla. `RenderTargetBitmap` dibujaba invertida la captura del contenido de la ventana principal; `Infrastructure/Snapshot.cs` la corrige para que las capturas de prueba coincidan con la pantalla.
- **Limitaciones:** no lo ha revisado un usuario nativo. Las frases árabes que incluyen nombres latinos («Sonos», «YouTube Music», atajos como Ctrl+Alt+↑) dependen del algoritmo bidi de WPF. El menú de la bandeja del sistema sigue la dirección de Windows. El instalador en árabe no se ha visto en pantalla, porque necesitaría Windows en árabe.

## Accesibilidad y teclado

- Los nombres de accesibilidad (`AutomationProperties.Name`) y los tooltips usan claves traducidas.
- En la bienvenida: el foco empieza en la búsqueda, las flechas ↑/↓ mueven la selección, Enter confirma y Esc mantiene la sugerencia.
- En Ajustes el selector de idioma es un ComboBox normal con búsqueda por tecla según el nombre visible (`TextSearch.TextPath="Display"`).

## Cómo añadir un idioma

1. Añadir una línea en `Languages.All` (código, nombre en inglés, nombre nativo, RTL). Si hace falta, completar `Resolve` (variantes regionales) y `PluralRules.CategoriesOf` (categorías CLDR).
2. Crear la carpeta `src/SonosFlow.App/Localization/Strings/<código>/`. La forma más cómoda es escribir un JSON plano con todas las claves y ejecutar:
   ```
   python -I tools/i18n/split_validate.py <código> ruta\al\plano.json
   ```
   El script comprueba que no falten ni sobren claves, las categorías de plural, los marcadores `{0}`/`{1:0.#}`, las llaves sueltas y la longitud. Solo si todo es correcto reparte el JSON en archivos por área.
3. Ejecutar `dotnet test SonosFlow.sln -c Release`: la prueba de cobertura recorre automáticamente el nuevo idioma.
4. Revisar con `SonosFlow.exe --lang <código> --screenshot captura.png --delay 3000` (con `--page settings`, `--screenshot-mini` y `--screenshot-welcome` para las demás pantallas).

**Clave nueva:** se añade en `es` y en `en` y, para los demás idiomas, se pasa la lista de claves que pide la prueba `Every_language_has_exactly_the_english_keys` por el mismo script. Las claves se escriben en XAML con `{loc:T área.clave}` y en C# con `L.T/F/Plural`. La prueba `Every_key_used_in_the_code_exists_in_english` falla si el código usa una clave que no existe.

## Pruebas automáticas

`tests/SonosFlow.App.Tests` (xUnit):
- **Cobertura** (`LocalizationCoverageTests`): el registro coincide con los idiomas incrustados; cada idioma tiene exactamente las claves del inglés; los marcadores coinciden y el formato es seguro; están todas las categorías de plural; no hay claves duplicadas entre áreas; toda clave usada en el código existe; el texto Unicode (árabe, CJK, devanagari, cirílico) se carga real; y la longitud en idiomas largos es razonable.
- **Comportamiento** (`LocalizationBehaviourTests`): clave ausente, cambio de idioma/cultura/dirección, notificaciones, plurales, mensajes codificados y respaldo.
- **Resolución y arranque** (`LanguageResolutionTests`, `LanguageStartupTests`): etiquetas de Windows y todos los casos de instalación.
- **Prueba real con el Sonos** (`SelfTest`, comprobación 1e): con la página de Ajustes abierta y durante la transmisión, cambia a árabe → japonés → alemán → idioma original. Comprueba que no hay reconexiones, que sigue en PLAYING, que siguen llegando bytes y que hay una sola captura. Comprueba además que no cambian la acción al cerrar, el margen anti-cortes, el temporizador, el altavoz, el volumen ni el modo de fuente: las listas de opciones son estables y sus elementos se comparan por clave, así que un cambio de idioma no puede vaciar una selección ni escribir null en un ajuste.
- **Instalador:** `Installer_texts_are_strict_json` comprueba que los `setup.json` no tienen comentarios ni comas finales, que el lector del instalador (.NET Framework) no admite.

## Limitaciones conocidas

- Las traducciones (excepto el español) son de IA: falta revisión nativa, sobre todo en los textos casi legales (`settings.about.disclaimer`, instalador) y en terminología como «stalls», «drops» o «Loudness».
- El detalle técnico de algunos errores poco frecuentes de la herramienta de copias de seguridad del proyecto (`SonosFlow.Backup`) se muestra en español. Es una herramienta de desarrollo y su registro es en español.
- Los mensajes que genera el propio Windows (excepciones del sistema) aparecen en el idioma de Windows.
- El menú contextual de la bandeja del sistema se traduce, pero su dirección la decide Windows.
