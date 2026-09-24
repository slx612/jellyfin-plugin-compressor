# Jellyfin Compressor

Complemento nativo para **Jellyfin 10.11.6**. Comprime películas en el propio servidor,
con FFmpeg de Jellyfin, sin otro servicio ni contenedor obligatorio.

**EXPERIMENTAL — v0.1.5 para Jellyfin 10.11.6.** La versión inicial se probó
en un servidor aislado con vídeos sintéticos y dos usuarios: compresión, escaneo,
reinicio y restauración conservaron los estados de la biblioteca. El panel de
v0.1.2 se probó con respuestas simuladas y se abrió en el Jellyfin 10.11.6 de
destino tras instalar y reiniciar: cargaron Resumen, Carpetas y Ajustes, con la
automatización apagada y ninguna carpeta seleccionada. **No se ha sustituido ni
restaurado ningún medio real en el servidor de destino**; la v0.1.5 aún no se
ha instalado allí y las operaciones de cuota y retención ampliadas aún no se
han probado dentro del servidor.

## Funcionamiento

- Automatización **desactivada por defecto** y ninguna carpeta seleccionada.
- Inclusiones y exclusiones por carpetas; las exclusiones siempre prevalecen.
- El análisis y los lotes solo seleccionan películas de **más de 10 GB** por defecto.
  El mínimo es configurable en Ajustes; 0 lo desactiva. Se mide el archivo original
  en GB de 1024³ bytes. Al elegir una película concreta a mano se puede probar
  aunque sea menor; las demás comprobaciones de seguridad siguen vigentes.
- **Analizar** comprueba elegibilidad; **Comprimir** crea trabajos manuales.
  La búsqueda permite encolar una sola película; se aplican las mismas reglas
  de carpeta, estabilidad, reproducción y prevención de recompresión.
  Los análisis y las comprobaciones manuales continúan en segundo plano aunque
  se cierre la petición del navegador; el panel consulta su progreso y resultado.
- Una película cada vez, pausa y horario opcional según la hora del servidor.
- HEVC con resolución original o límite de 2160p, 1080p, 720p o 480p.
  El límite solo reduce: una película de 720p nunca se amplía a 1080p.
  Se conservan profundidad de color, audio, subtítulos y capítulos.
  Se omiten HDR, Dolby Vision y estructuras no admitidas por esta v1.
- MKV, MP4 y M4V conservan **ruta, nombre y extensión completos**. No se convierte
  MP4 a MKV ni se crea una segunda película. Se conservan las fechas del archivo
  y la fecha de incorporación de la ficha; se actualiza la información de medios
  sobre el mismo identificador, sin escribir sobre los datos de reproducción.
- Se comprueba la estructura del resultado y se decodifica entero antes de sustituir.
  Si no alcanza el ahorro mínimo (15 % inicialmente), permanece el original.
  El ajuste de calidad controla el codificador, no garantiza un tamaño final:
  una película ya comprimida puede crecer. La codificación se corta en cuanto
  el temporal supera el tamaño permitido para ese ahorro, antes de verificarlo.
- El original se copia a la carpeta elegida, fuera de todas las bibliotecas,
  y se verifica por SHA-256 antes del reemplazo atómico.
- Cada original tiene su plazo de retención, que comienza al completar la sustitución.
  La limpieza solo elimina originales registrados y vencidos cuando puede verificar
  que el resultado comprimido sigue disponible. Un conflicto aplaza la eliminación.
- Puede fijarse un límite para la carpeta de originales. Al alcanzarlo se eliminan
  primero los originales recuperables más antiguos, incluso antes de que venzan,
  siempre que la película comprimida y su ficha estén verificadas. Si no hay espacio
  que pueda liberarse sin riesgo, el original de la nueva película permanece en la
  biblioteca. Los archivos ajenos cuentan para el límite, pero nunca se eliminan.
- Si Jellyfin informa de una reproducción activa, se aplaza el reemplazo.

La conservación de **«recién añadidas», visto y progreso está comprobada en Jellyfin
10.11.6**, consultando las listas y datos que reciben los clientes. Se mantienen la
misma ficha, fecha de alta, posición de reproducción y datos de cada usuario tras
comprimir, escanear, reiniciar y restaurar. Consulta el alcance y la evidencia en
[validación](docs/validation.md); todavía falta la revisión visual de los clientes.

## No recomprimir películas

El historial visible es independiente del registro de contenidos. Se identifican
originales y resultados con el SHA-256 del archivo completo y se añade una marca
`JELLYFIN_COMPRESSOR` al contenedor.

Un resultado propio se omite después de reiniciar, moverlo, renombrarlo, cambiar de
perfil, limpiar el historial o eliminar el original vencido. Un original restaurado
también queda protegido. La v1 no ofrece recompresión forzada. Un intento sin ahorro
se recuerda para el perfil utilizado; cambiar de perfil permite un nuevo intento
sobre ese original.

El registro está en `<directorio de datos de Jellyfin>/jellyfin-compressor/identities`.
No debe borrarse para limpiar el historial. Si se pierde, la marca del contenedor
ofrece una protección adicional. No se promete reconocer todas las modificaciones
que otras aplicaciones hagan sobre los archivos.

## Instalación desde Jellyfin

1. En **Panel de control → Complementos → Repositorios**, pulsa **+** y añade
   `https://raw.githubusercontent.com/slx612/jellyfin-plugin-compressor/main/manifest.json`
   con el nombre `Jellyfin Compressor (Experimental)`.
2. Abre el **Catálogo** de complementos, instala **Jellyfin Compressor** y reinicia Jellyfin.
3. Abre su configuración. Elige carpetas, destino de originales, retención y
   codificador. Comienza con una carpeta de películas prescindibles.

El panel tiene cuatro pestañas: **Resumen** para analizar, buscar y encolar una película,
**Carpetas** para recorrer bibliotecas y marcar inclusiones o exclusiones, **Ajustes**
para automatización, originales y calidad, y **Actividad** para cola y restauración.
La carpeta de originales se puede buscar en el explorador del servidor. Este
solo permite elegir carpetas fuera de las bibliotecas; la ruta manual sigue disponible.
Los cambios en carpetas y ajustes se aplican al pulsar **Guardar cambios**. Las
acciones de Resumen se desactivan mientras haya cambios pendientes, para evitar
analizar o encolar películas con una selección de carpetas anterior.

El catálogo descarga el ZIP publicado en [Releases](https://github.com/slx612/jellyfin-plugin-compressor/releases).
Su `checksum` es el MD5 que Jellyfin utiliza para comprobar la descarga; la
versión publicada ofrece también un archivo SHA-256 para verificación independiente. El catálogo
solo anuncia versiones compatibles con Jellyfin 10.11.6.

En Docker/Synology se utilizan las rutas **visibles dentro del servidor Jellyfin**,
no las del equipo donde está abierto el navegador. Jellyfin necesita lectura y
escritura en la biblioteca, su directorio de datos y la carpeta de originales.
Los enlaces simbólicos y junctions se rechazan en v1.

Esta es una versión experimental. La compresión automática viene desactivada y
ninguna carpeta está seleccionada de fábrica. El código y el ZIP son públicos;
no hace falta ni debe usarse un token de GitHub en Jellyfin.

## Configuración y recuperación

La retención debe elegirse explícitamente entre 1 y 3650 días. Cambiarla afecta a
futuros trabajos: los ya encolados conservan su instantánea. Desactivar nuevas
compresiones no suspende la limpieza de originales ya vencidos. La limpieza se
revisa cada minuto, independientemente de la pausa y del horario de compresión.
El límite de tamaño se expresa en GB (0 = sin límite) y se aplica de inmediato a
la carpeta seleccionada; reducirlo también inicia una limpieza automática. Por
seguridad se incluyen todos los archivos al calcular el tamaño, pero solo se
borran originales de transacciones conocidas y verificadas. Cambiar la ruta de la
carpeta no mueve automáticamente los originales que ya estaban en la anterior;
estos conservan su vencimiento. Si cambias el destino durante una codificación,
el trabajo se omite antes de sustituir el archivo; vuelve a analizar la película.

Durante la retención, **Restaurar original** recupera el archivo si la película no
se está reproduciendo y ningún contenido ajeno ocupa su lugar. Tras verificar el
original restaurado y actualizar su ficha en Jellyfin, se elimina su copia de
cuarentena; también se retira una copia comprimida heredada de versiones previas
si está presente y coincide con el registro. Un fallo de verificación conserva las
copias para inspección. Las transacciones abortadas requieren revisión manual.

Los diarios de `jellyfin-compressor/transactions` permiten reconciliar un reinicio
antes de realizar nuevos trabajos. Incluyen la actualización pendiente de la ficha
de Jellyfin, por lo que un corte entre reemplazar y actualizar no se da por resuelto
hasta reintentar esa actualización. No edites los diarios mientras se ejecuta el
servidor. Una discrepancia bloquea las operaciones y aparece en el panel.

Hace falta espacio para el original retenido y los temporales: el proceso prioriza
la recuperación antes que liberar espacio inmediatamente. En el directorio de
datos se guarda el resultado temporal; en la biblioteca se prepara otra copia
para el intercambio atómico. Al terminar se retiran los temporales propios.

## Compilar y comprobar

Requiere .NET SDK 9 y FFmpeg/ffprobe en `PATH` para las pruebas de integración.

```powershell
dotnet test -c Release
./build-plugin.ps1 -Version 0.1.5.0
```

El ZIP y su SHA-256 quedan en `artifacts/`. Las dependencias de Jellyfin se
referencian al compilar; el paquete no incluye sus DLL ni un FFmpeg independiente.

Consulta [validación y límites](docs/validation.md), [diseño](docs/v1-design.md) y
[atribución al proyecto original](UPSTREAM.md). El código adaptado procede de
[Jellyfin Pre-Transcode](https://github.com/mugurc/jellyfin-plugin-pre-transcode),
con licencia GPL-3.0. Los espacios de nombres internos conservan `PreTranscode`
para facilitar comparaciones; el complemento tiene GUID, API, DLL y estado propios.
Este repositorio contiene únicamente el complemento Jellyfin.
