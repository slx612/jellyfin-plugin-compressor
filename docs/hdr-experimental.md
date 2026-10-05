# HDR10, HDR10+ y Dolby Vision 8.1: soporte experimental

La copia completa de Wonka pasó sus comprobaciones técnicas el 5 de octubre de
2026. La actualización 0.1.13 integra esa ruta GPU en el complemento;
un fragmento validado activó HDR10+ en un móvil Realme, pero siguen pendientes
la película completa en ese cliente y la reproducción Dolby Vision. La evidencia y los límites
de esta prueba se detallan abajo.

## Ruta NVENC integrada en 0.1.13

Solo selección manual de una película HEVC MKV con 10 bits, BT.2020/PQ y Docker/Linux
x86-64. HDR10, HDR10+ y Dolby Vision tienen opciones separadas, todas apagadas por
defecto; el lote y la automatización siguen excluyendo HDR. Se usa NVENC p5 Main10
con la calidad y el límite de resolución elegidos, sin ampliar vídeos menores.
No se cambia a libx265 si la GPU no funciona.

Antes de codificar se comprueba NVENC con vídeo sintético, se examinan los metadatos
HDR10 de todo el HEVC y se extraen los metadatos dinámicos. Esta versión requiere
mastering display y content light estáticos uniformes, HDR10+ perfil B de una
ventana y Dolby Vision 8.1 con RPU y base HDR10 sin capa de mejora. Los presets de
áreas activas Dolby deben tener los cuatro márgenes en cero y cubrir todos los
fotogramas; otras estructuras se rechazan con un motivo visible.

Tras codificar se restauran los SEI HDR10 exactos, se reinsertan los RPU Dolby y
el JSON HDR10+ completos, y se monta el MKV con los tiempos y la duración nominal
de fotograma originales. Se comparan todos los metadatos dinámicos extraídos y
los paquetes de audio y subtítulos. Después siguen las comprobaciones del
complemento: pistas, capítulos, señal HDR, fotogramas completos, decodificación,
identidad del archivo y ahorro mínimo, antes de guardar el original y sustituir.
La sustitución conserva el mismo archivo y la misma ficha de Jellyfin.

El detector usa una sola decodificación con filtros de conteo: una rama sin HDR10+
o Dolby puede contener cero fotogramas sin hacer fallar el codificador de FFmpeg.
Los porcentajes del panel corresponden a la fase actual; reinserción y lectura
de paquetes muestran su fase sin inventar un porcentaje. La codificación usa
GPU, mientras el análisis y la verificación usan CPU.

La prueba de la película completa y la reproducción HDR10+ del móvil justifican
esta opción experimental; no acreditan todos los títulos ni reproducción Dolby
Vision en un cliente compatible. La prueba de reinserción integrada usa un
fragmento real con FFmpeg Jellyfin 7.1.3-3, dovi_tool 2.3.4, hdr10plus_tool 1.7.2
y MKVToolNix 102. La prueba completa de Wonka sigue siendo la del diagnóstico
anterior, con su interrupción y continuación descritas abajo, no una nueva
compresión completa hecha desde el panel del complemento.

## Prueba completa sin sustituir el original

`scripts/HdrValidation` es un diagnóstico que llama a las funciones de codificación,
detección y verificación de esta misma rama. Se publica como ejecutable autónomo
Linux x86-64 y se ejecuta dentro del contenedor Jellyfin existente. No instala
el complemento ni llama al servicio de sustitución o cuarentena. Requiere un MKV
en `/Peliculas` y una carpeta nueva bajo
`/config/data/jellyfin-compressor/hdr-validation`.

`scripts/run-hdr-validation.sh` verifica los SHA-256 del paquete y lo lanza con
prioridad reducida. Conserva el original, el candidato, el registro y las pruebas
en una carpeta separada. Necesita espacio libre de cuatro veces el tamaño de la
película más 5 GiB; libx265 en CPU puede tardar horas. Se comparan también los RPU
Dolby completos extraídos con `dovi_tool`, además del HDR10+ y las verificaciones
de la ruta de producción. `status.json` y el registro se actualizan cada 30 segundos.

La lectura inicial consulta también un máximo de 64 paquetes de vídeo: en algunos
MKV, los metadatos HDR10 estáticos aparecen en los fotogramas y no en la cabecera
del flujo. Se conservan el perfil y los indicadores Dolby de la cabecera, se
normalizan los valores HDR10 y se rechazan los conflictos entre cabecera y muestra.
Después sigue siendo obligatorio el análisis completo de todos los fotogramas.
Este análisis completo compara presencia y cantidades de metadatos; la comparación
de valores HDR10 estáticos se limita a la muestra inicial. Los valores HDR10+ y RPU
se comparan completos mediante las herramientas correspondientes.

Solo un `result.json` con `TechnicalChecksPassed`, `OriginalUnchanged` y
`MinimumSavingsMet` verdaderos acredita la prueba técnica. Siempre conserva
`ClientPlaybackVerified: false`: aún hay que reproducir el candidato en el cliente
habitual y confirmar imagen, HDR/Dolby, audio y subtítulos. Si falla o se cancela,
se guarda `failure.txt`; no se sustituye ni se elimina ningún original.
Para detener un diagnóstico lanzado en segundo plano se debe enviar SIGTERM a su
proceso verificado: cancela sus tareas y detiene sus procesos hijos. SIGINT puede
quedar ignorada al heredar el estado de señales de `nohup`.

En Docker/Synology con Jellyfin 10.11.6 se ha comprobado también la disponibilidad
de NVENC con una RTX 2060, usando el mismo usuario que ejecuta Jellyfin. La prueba
sintética produjo 24 fotogramas a 1920×1080, HEVC Main 10 y `yuv420p10le`. Esto
acredita la codificación GPU de 10 bits dentro del contenedor; no demuestra todavía
la conservación de HDR10+, Dolby Vision ni la reducción de resolución de películas
con metadatos dinámicos. La prueba completa de Wonka por CPU y a resolución original
se canceló antes de terminar la codificación y no produjo un candidato validado.

La prueba corta de Wonka con NVENC, CQ20 y máximo 1080p se mantiene aparte del
complemento instalado. Sus dos primeros intentos se detuvieron antes de codificar:
el lanzador AppImage de MKVToolNix intercaló su listado de extracción alrededor del
JSON de identificación, incluso dividiendo una ruta. La lectura reconoce ese
listado concreto y rechaza otros mensajes fuera del JSON. Se comprobó la corrección
con los registros reales de ambos intentos y con títulos UTF-8; la suite local
pasó 414 pruebas con Jellyfin FFmpeg 7.1.3 para Windows. En ese punto aún faltaban
las comprobaciones HDR/Dolby del fragmento y su reproducción antes de ampliar
la compatibilidad del complemento.

### Fragmento real con NVENC: 3 de octubre de 2026

El tercer intento codificó los primeros ~60 segundos de Wonka con la RTX 2060,
NVENC p5 CQ20, Main10 y máximo 1080p: pasó de 98.786.271 a 22.779.933 bytes,
con salida 1920×802 y 1.443 fotogramas. La codificación duró unos 21 segundos.
El RPU completo y el JSON HDR10+ de ese candidato coincidían con los del fragmento
original, pero **la verificación lo rechazó correctamente; no se sustituyó la película**.

Se identificaron dos problemas distintos. El parche NVENC de Jellyfin FFmpeg
7.1.3-3 ordena incorrectamente las primarias del mastering display y trunca algunos
valores al convertirlos desde números racionales. Además, el remux de HEVC con
marcas de tiempo externas cambió `DefaultDuration` de 41.708.333 a 42.000.000 ns.
El contador de fotogramas usaba la sincronización automática de FFmpeg: contabilizaba
1.434 en vez de los 1.443 realmente presentes. Cada salida `framecrc` del detector
usa ahora `-fps_mode passthrough`, manteniendo las mismas exigencias de conservación.
Una prueba de regresión con 48 fotogramas HDR reprodujo el fallo anterior: solo contaba 25.

Un diagnóstico separado reparó únicamente los mensajes SEI estáticos 137/144 del
fragmento comprimido, copiando sus bytes exactos del fragmento original después de
comprobar que no cambiaban a lo largo de la muestra. Verificó que los demás NAL y
mensajes SEI, incluido Dolby y HDR10+, permanecían intactos. `mkvpropedit` restauró
solo `DefaultDuration` en el candidato; `--default-duration` durante el remux no
resolvía el problema al usar marcas de tiempo externas. Las marcas de tiempo
posteriores a la reparación coincidían dentro de 1 ms con las del origen.

El candidato corregido pesa 22.780.196 bytes: ahorro del 76,94 % **en esa muestra**.
Las comprobaciones nativas locales confirmaron 1.443 fotogramas de cada tipo,
HDR estático exacto, Dolby 8.1, 23,976 fps, audio y subtítulos idénticos, capítulos
y marcador del compresor. El original de 22.226.034.037 bytes permanece en su sitio.
La reparación es todavía un prototipo separado: no está integrada en la ruta NVENC
del complemento ni habilita automáticamente ninguna película HDR.

La verificación autónoma terminó correctamente dentro del contenedor Jellyfin
el mismo día, en 44 segundos, sin otra codificación ni acceso a la película completa.
Usó las funciones reales `DynamicHdrDetector` y `CompressionPolicy.VerificationError`:
confirmó los 1.443 fotogramas, los RPU y JSON HDR10+ completos idénticos, el HDR10
estático, las pistas, los capítulos, la sincronización y `DefaultDuration` originales.
`gpu-clip-verification-20261003T181249Z-5890/result.json` contiene
`TechnicalChecksPassed: true`; la salida del proceso fue 0. **Tras ese diagnóstico
quedaban pendientes la reproducción en el móvil y una película completa**. El nuevo código de recuento
compila y se ejecutó correctamente en este diagnóstico Linux, pero la suite .NET
actual queda bloqueada por el Control de aplicaciones de Windows (`0x800711C7`);
las 414 aprobadas corresponden al estado anterior a este cambio. No se ha publicado
una nueva versión del complemento ni se ha instalado esta ruta experimental.

Referencias: [parche NVENC de Jellyfin 7.1.3-3](https://github.com/jellyfin/jellyfin-ffmpeg/blob/v7.1.3-3/debian/patches/0035-add-hdr-metadata-for-nvenc-hevc-encoder.patch),
[marcas de tiempo y duración en mkvmerge](https://mkvtoolnix.download/doc/mkvmerge.html),
[edición de cabeceras con mkvpropedit](https://mkvtoolnix.download/doc/mkvpropedit.html).

### Prueba completa con GPU: 4 de octubre de 2026

El paquete autónomo `gpu-full-bundle-20261004` está copiado al NAS y verificado
mediante SHA-256 de sus 334 archivos. El usuario lanzó la prueba
`gpu-full-20261004T195001Z-623`; terminó la codificación y las comprobaciones del
vídeo, pero **falló durante la verificación de audio/subtítulos y no quedó validada
en esa ejecución**.
Usa los mismos ajustes del fragmento aprobado: NVENC p5 CQ20, Main10 y máximo
1920×1080, sin ampliar vídeos menores. La salida y los temporales se guardan fuera
de la biblioteca; no instala el complemento ni sustituye el original.

La corrección de SEI procesa bloques de 1 MiB y limita el tamaño de cada NAL.
Las pruebas locales confirmaron que produce exactamente los mismos bytes que la
reparación independiente del fragmento. También verificaron el análisis de su JSON
HDR10+ completo y rechazaron formas incompatibles. En la película completa se
exigen metadatos HDR estáticos uniformes, HDR10+ de una ventana y áreas Dolby sin
desplazamiento, con cobertura continua de todos los fotogramas.

El lanzador `run-gpu-full-test.sh` registra progreso y tiempos por fase, comprueba
los metadatos completos, las marcas de tiempo, las pistas y la identidad SHA-256
del original antes y después. Tiene un límite total de seis horas y exige al menos
un 15 % de ahorro. `stop-gpu-full-test.sh` valida los cinco argumentos exactos del
proceso antes de enviar SIGTERM. La compresión utiliza GPU; el examen final de
fotogramas decodifica cada archivo una vez en CPU. La reproducción sigue pendiente
hasta comprobarla en el dispositivo del usuario.

La copia completa pesa 4.150.921.223 bytes frente a 22.226.034.037 del original:
ahorro de tamaño del 81,32 %, con salida 1920×802 de 10 bits. La fase de codificación
GPU duró 3.738,79 segundos (1 h 2 min 19 s). El estado final registró 11.083,01
segundos (3 h 4 min 43 s) desde el arranque hasta el fallo; este tiempo incluye
preparación y verificaciones, pero no una validación completa satisfactoria.

El vídeo original y la salida se decodificaron hasta el final: ambos contienen
167.204 fotogramas, todos con HDR10+, RPU Dolby, mastering display y content light.
Los RPU completos y el JSON HDR10+ coinciden byte a byte. Pasaron las comprobaciones
de HDR estático, señal de color, duración nominal, marcas de tiempo, pistas y
capítulos anteriores a la fase de audio. La reparación restauró 669 mensajes de
cada tipo SEI estático sin cambiar los demás NAL ni los metadatos dinámicos.

FFmpeg terminó con código 255 al generar el CRC de audio/subtítulos del original,
unos 42 segundos después de entrar en esa fase. El registro de error no contiene
una explicación: **no se conoce todavía qué provocó la interrupción**. El CRC
parcial alcanza aproximadamente el minuto 24; no se llegó a examinar el audio
del candidato ni a comparar ambas pistas. Tampoco se ejecutaron las huellas
SHA-256 finales del original y de la salida. No existe `result.json` de éxito.
El original sigue en su ubicación, con tamaño y fecha de modificación sin cambios;
no se sustituyó ni eliminó. Los informes se conservaron en
`artifacts/gpu-full-result-20261004/partial-result.json`, junto con los registros.
Estas verificaciones pendientes se completaron por separado al día siguiente.

### Verificación final de la copia: 5 de octubre de 2026

Se examinó la copia existente desde Windows a través de SMB, usando Jellyfin
FFmpeg para copiar todos los paquetes de audio y subtítulos a archivos `framecrc`.
Ambos procesos terminaron con código 0 y los CRC completos coincidieron exactamente.
No se volvió a codificar ni se sustituyó ningún archivo de la biblioteca.

El SHA-256 del original coincide con la huella tomada antes de la prueba:
`952aa94223dc188ac3879d1b888d27450c1e79e6fcb9e375f3d3fdfba3685cea`.
El candidato tiene SHA-256
`9967d3d77c8a8c0c4562a27f43204ff9352dc5792b3df9a12907c660c5fee45a`.
Los tamaños y las fechas de modificación de ambos archivos permanecieron iguales
durante estas comprobaciones. Se confirmó también que el candidato conserva el
tamaño y la fecha que tenía al pasar los controles de vídeo/HDR del NAS.

El informe `artifacts/gpu-full-result-20261004/followup-20261005/result.json`
registra `TechnicalChecksPassed: true`, `OriginalUnchanged: true`,
`OriginalReplaced: false` y `MinimumSavingsMet: true`. Combina las comprobaciones
completas de vídeo/HDR realizadas en Linux con las de audio/subtítulos y huellas
realizadas en Windows. La ejecución original del NAS sigue teniendo salida 1;
su interrupción no se ha explicado ni se ha demostrado resuelta dentro del contenedor.

La verificación adicional duró 1.161,68 segundos (19 min 22 s). Sumada a los
11.083,01 segundos de la ejecución del NAS, son 3 h 24 min 5 s de trabajo medido
en dos ejecuciones separadas, con 1 h 2 min 19 s de codificación GPU. Esta suma
no es el tiempo continuo entre el lanzamiento y la validación final.

El resultado completo mantiene el ahorro del 81,32 % y los metadatos técnicos
HDR10, HDR10+ y Dolby Vision 8.1, con salida 1920×802 de 10 bits. El ahorro incluye
la reducción desde 4K. `ClientPlaybackVerified` sigue siendo falso: falta comprobar
imagen, activación HDR/Dolby, audio y subtítulos en el móvil. El siguiente paso es
esa reproducción; después se puede integrar la ruta GPU como opción experimental
para una película individual, manteniendo la compresión automática desactivada.

El usuario confirmó después que había visto la copia en el PC y que la calidad
visual le parecía muy buena. Se abrió también desde el NAS en el Reproductor
multimedia de Windows: permitió reproducir y buscar escenas alrededor de los
minutos 20, 60, 100 y 109. En las capturas revisadas no se observaron defectos
graves de imagen. Fue una revisión breve de muestras, sin comparación simultánea
con el original, escucha de audio ni prueba de subtítulos. El reproductor no mostró
un indicador que acreditase la activación HDR/Dolby en esa pantalla; la reproducción
en el móvil sigue pendiente. Se conserva este alcance en
`artifacts/gpu-full-result-20261004/followup-20261005/pc-playback-review.json`.

La consulta posterior de Configuración → Sistema → Pantalla → HDR en el LG
14Z90S-G.AD78B mostró, para la pantalla interna, «Streaming de vídeo HDR:
Compatible» con su interruptor desactivado y «Juegos HDR, aplicaciones y mucho
más: No es compatible». Se consultaron estos valores sin cambiarlos. La prueba
visual en ese PC no acredita por sí sola que se activase HDR o Dolby Vision;
queda pendiente comprobarlo con un cliente y una pantalla compatibles.

### Reproducción HDR10+ en Realme: 5 de octubre de 2026

El usuario conectó un Realme RMX3371 con Android 14 y autorizó su manejo por ADB.
La pantalla anunció los tipos HDR `[2, 3, 4]`: HDR10, HLG y HDR10+, sin Dolby Vision.
Se copió al móvil únicamente el fragmento corregido de unos 60 segundos, de
22.780.196 bytes, y se abrió con el reproductor nativo `com.coloros.video`.
El SHA-256 en el móvil coincidió con el del fragmento validado en el NAS:
`af1a8098541745267aacbcef9c680f067b8c2195a41ac10bd4a84c8351b78175`.

Mientras se reproducía el vídeo, SurfaceFlinger mostró su capa en
`BT2020_ITU_PQ (298188800)`, con `hdr metadata types=7`, y el diagnóstico del
fabricante indicó `Current Color Mode: hdr10plus`. Esto acredita la activación
del modo HDR10+ con este fragmento en el reproductor nativo del Realme. El vídeo
terminó y se devolvió el móvil a la pantalla de inicio; la copia de prueba permanece
en `/storage/emulated/0/Movies/JellyfinHdrTest-20261005/Wonka-comprimida-1080p-HDR.mkv`.

Esta prueba no mide la luminancia ni la precisión del mapeo de tonos del panel;
tampoco verifica la película completa, la escucha de audio, los subtítulos, el
cliente Jellyfin ni Dolby Vision. La pantalla no anuncia compatibilidad Dolby.
El informe está en
`artifacts/gpu-clip-repair-20261003/realme-playback-20261005.json`.
Los identificadores HDR se interpretan según la
[documentación de Android](https://developer.android.com/reference/android/view/Display.HdrCapabilities).

## Historial: primera ruta CPU, anterior a las pruebas GPU de octubre

Los párrafos siguientes documentan el estado de la primera implementación con
libx265. En ese momento seguía en una rama y un PR, sin versión instalable; el
servidor tenía 0.1.11 y omitía HDR/Dolby. Sus restricciones y pruebas pendientes
describen aquella ruta CPU. El alcance actual de NVENC está al principio de este
documento y la evidencia posterior de octubre figura en las secciones anteriores.

El complemento ofrece tres permisos independientes, apagados por defecto: HDR10 experimental, HDR10+ experimental y Dolby Vision 8.1 experimental. Solo se aplican a una película elegida individualmente; los análisis en lote y la compresión automática omiten HDR incluso si los permisos están activados. Se requiere `libx265` de FFmpeg, que codifica en CPU. No se convierte la imagen a SDR.

La primera fase admite vídeo HDR10 de 10 bits con primarias BT.2020, transferencia PQ y matriz BT.2020nc. Para Dolby Vision exige además perfil 8.1, base HDR10 y RPU, sin capa de mejora. Dolby Vision y HDR10+ conservan de momento la resolución original; la reducción de tamaño se intenta mediante recodificación HEVC. HLG y otros perfiles de Dolby Vision siguen fuera de esta fase. Jellyfin informa de HDR10+ para algunas películas con Dolby Vision; se consulta su catálogo además del sondeo del archivo. Como ambas fuentes pueden omitir metadatos dinámicos presentes solo en fotogramas, antes de codificar se examinan todos los fotogramas con FFmpeg. La comprobación puede tardar varios minutos. Por eso el análisis rápido puede mostrar una película como apta preliminarmente y el trabajo omitirla más tarde.

Antes de sustituir un original, se comparan la señal de color, los metadatos HDR10 estáticos presentes en el origen, la configuración Dolby Vision/RPU y el número de fotogramas que llevan RPU y metadatos HDR estáticos. También se mantienen las comprobaciones existentes de pistas, capítulos, duración, decodificación completa y ahorro mínimo. Si una comprobación falla, el original permanece en su sitio y el temporal no se publica. El mismo número de fotogramas con RPU no demuestra por sí solo que sus valores ni la reproducción Dolby Vision sean idénticos; queda pendiente la comparación más profunda y la prueba en un cliente habitual.

El FFmpeg 7.1.3 usado por Jellyfin contiene código para pasar metadatos HDR10 y RPU a `libx265`. Una prueba local con la compilación oficial para Windows y fragmentos temporales de dos segundos confirmó que x265 necesita VBV/HRD para abrir Dolby Vision y que, al configurarlo, la salida mantiene HDR10, el perfil Dolby Vision 8.1 y RPU en los primeros fotogramas. Otro fragmento HDR10 se redujo de 3840×1608 a 1920×804 conservando la señal de color y los metadatos estáticos. La recodificación directa no conserva HDR10+; por eso se usa una ruta separada de reinyección. *Wonka*, *Querido Santa* y *Nadie 2* mostraron HDR10+ por fotograma pese a no aparecer en la consulta inicial del catálogo. Todavía falta probar un archivo completo, la compilación Linux del servidor y la reproducción en un cliente Dolby Vision. Hasta entonces el soporte no se considera validado para uso real.

Para HDR10+ se probó además la opción `dhdr10-info` de x265 con un JSON extraído del original. La compilación oficial de Jellyfin FFmpeg 7.1.3 para Windows respondió `--dhdr10-info disabled. Enable HDR10_PLUS in cmake`; por tanto, esa opción no resuelve el problema en esa compilación. La rama de desarrollo integra [`hdr10plus_tool`](https://github.com/quietvoid/hdr10plus_tool) y [`mkvmerge`](https://mkvtoolnix.download/): extrae HDR10+ del MKV original, lo inserta en el HEVC ya comprimido y reconstruye el MKV. Solo funciona en Linux x86-64 por ahora; el ZIP incluye ambas herramientas y comprueba sus SHA-256 antes de ejecutarlas. El permiso HDR10+ debe activarse expresamente y solo permite una película MKV elegida a mano. Si además tiene Dolby Vision 8.1, debe activarse su permiso independiente. Se comparan el JSON HDR10+ completo, el número total de fotogramas, los metadatos HDR/RPU, el audio, los subtítulos, las marcas de tiempo del vídeo (tolerancia de 1 ms por redondeo), los capítulos y la estructura del contenedor antes de cualquier sustitución. En un fragmento de 97 fotogramas la orquestación del complemento conservó HDR10+ y RPU en los 97, dos audios, dos subtítulos y dos capítulos sintéticos. Aún faltan una película completa en el servidor Docker/Synology y la reproducción en cliente; **no está listo para instalar ni activar sobre originales**.
