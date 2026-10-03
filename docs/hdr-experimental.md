# HDR10, HDR10+ y Dolby Vision 8.1: soporte experimental en desarrollo

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

Este trabajo **no está publicado ni instalado**. La versión 0.1.11 instalada sigue omitiendo HDR y Dolby Vision. No actives estos interruptores sobre originales hasta completar una prueba de codificación y reproducción real.

El complemento ofrece tres permisos independientes, apagados por defecto: HDR10 experimental, HDR10+ experimental y Dolby Vision 8.1 experimental. Solo se aplican a una película elegida individualmente; los análisis en lote y la compresión automática omiten HDR incluso si los permisos están activados. Se requiere `libx265` de FFmpeg, que codifica en CPU. No se convierte la imagen a SDR.

La primera fase admite vídeo HDR10 de 10 bits con primarias BT.2020, transferencia PQ y matriz BT.2020nc. Para Dolby Vision exige además perfil 8.1, base HDR10 y RPU, sin capa de mejora. Dolby Vision y HDR10+ conservan de momento la resolución original; la reducción de tamaño se intenta mediante recodificación HEVC. HLG y otros perfiles de Dolby Vision siguen fuera de esta fase. Jellyfin informa de HDR10+ para algunas películas con Dolby Vision; se consulta su catálogo además del sondeo del archivo. Como ambas fuentes pueden omitir metadatos dinámicos presentes solo en fotogramas, antes de codificar se examinan todos los fotogramas con FFmpeg. La comprobación puede tardar varios minutos. Por eso el análisis rápido puede mostrar una película como apta preliminarmente y el trabajo omitirla más tarde.

Antes de sustituir un original, se comparan la señal de color, los metadatos HDR10 estáticos presentes en el origen, la configuración Dolby Vision/RPU y el número de fotogramas que llevan RPU y metadatos HDR estáticos. También se mantienen las comprobaciones existentes de pistas, capítulos, duración, decodificación completa y ahorro mínimo. Si una comprobación falla, el original permanece en su sitio y el temporal no se publica. El mismo número de fotogramas con RPU no demuestra por sí solo que sus valores ni la reproducción Dolby Vision sean idénticos; queda pendiente la comparación más profunda y la prueba en un cliente habitual.

El FFmpeg 7.1.3 usado por Jellyfin contiene código para pasar metadatos HDR10 y RPU a `libx265`. Una prueba local con la compilación oficial para Windows y fragmentos temporales de dos segundos confirmó que x265 necesita VBV/HRD para abrir Dolby Vision y que, al configurarlo, la salida mantiene HDR10, el perfil Dolby Vision 8.1 y RPU en los primeros fotogramas. Otro fragmento HDR10 se redujo de 3840×1608 a 1920×804 conservando la señal de color y los metadatos estáticos. La recodificación directa no conserva HDR10+; por eso se usa una ruta separada de reinyección. *Wonka*, *Querido Santa* y *Nadie 2* mostraron HDR10+ por fotograma pese a no aparecer en la consulta inicial del catálogo. Todavía falta probar un archivo completo, la compilación Linux del servidor y la reproducción en un cliente Dolby Vision. Hasta entonces el soporte no se considera validado para uso real.

Para HDR10+ se probó además la opción `dhdr10-info` de x265 con un JSON extraído del original. La compilación oficial de Jellyfin FFmpeg 7.1.3 para Windows respondió `--dhdr10-info disabled. Enable HDR10_PLUS in cmake`; por tanto, esa opción no resuelve el problema en esa compilación. La rama de desarrollo integra [`hdr10plus_tool`](https://github.com/quietvoid/hdr10plus_tool) y [`mkvmerge`](https://mkvtoolnix.download/): extrae HDR10+ del MKV original, lo inserta en el HEVC ya comprimido y reconstruye el MKV. Solo funciona en Linux x86-64 por ahora; el ZIP incluye ambas herramientas y comprueba sus SHA-256 antes de ejecutarlas. El permiso HDR10+ debe activarse expresamente y solo permite una película MKV elegida a mano. Si además tiene Dolby Vision 8.1, debe activarse su permiso independiente. Se comparan el JSON HDR10+ completo, el número total de fotogramas, los metadatos HDR/RPU, el audio, los subtítulos, las marcas de tiempo del vídeo (tolerancia de 1 ms por redondeo), los capítulos y la estructura del contenedor antes de cualquier sustitución. En un fragmento de 97 fotogramas la orquestación del complemento conservó HDR10+ y RPU en los 97, dos audios, dos subtítulos y dos capítulos sintéticos. Aún faltan una película completa en el servidor Docker/Synology y la reproducción en cliente; **no está listo para instalar ni activar sobre originales**.
