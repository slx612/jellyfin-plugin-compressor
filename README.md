# Jellyfin Compressor

Complemento nativo para **Jellyfin 10.11.6**. Comprime películas en el propio servidor,
con FFmpeg de Jellyfin, sin otro servicio ni contenedor obligatorio.

**v0.1.0 preliminar y privada.** Hay pruebas automatizadas y compresiones reales con
vídeos sintéticos. La instalación y el recorrido completo en un servidor Jellyfin
10.11.6 en funcionamiento todavía necesitan validación; no se ha instalado en una
biblioteca real.

## Funcionamiento

- Automatización **desactivada por defecto** y ninguna carpeta seleccionada.
- Inclusiones y exclusiones por carpetas; las exclusiones siempre prevalecen.
- **Analizar** comprueba elegibilidad; **Comprimir** crea trabajos manuales.
- Una película cada vez, pausa y horario opcional según la hora del servidor.
- HEVC, sin cambiar resolución, profundidad de color, audio, subtítulos ni capítulos.
  Se omiten HDR, Dolby Vision y estructuras no admitidas por esta v1.
- MKV, MP4 y M4V conservan **ruta, nombre y extensión completos**. No se convierte
  MP4 a MKV ni se crea una segunda película. Se conservan las fechas del archivo
  y la fecha de incorporación de la ficha; se actualiza la información de medios
  sobre el mismo identificador, sin escribir sobre los datos de reproducción.
- Se comprueba la estructura del resultado y se decodifica entero antes de sustituir.
  Si no alcanza el ahorro mínimo (15 % inicialmente), permanece el original.
- El original se copia a la carpeta elegida, fuera de todas las bibliotecas,
  y se verifica por SHA-256 antes del reemplazo atómico.
- Cada original tiene su plazo de retención, que comienza al completar la sustitución.
  La limpieza solo elimina originales registrados y vencidos cuando puede verificar
  que el resultado comprimido sigue disponible. Un conflicto aplaza la eliminación.
- Si Jellyfin informa de una reproducción activa, se aplaza el reemplazo.

La conservación de «recién añadidas», visto y progreso depende de mantener la misma
ficha. Ese comportamiento está implementado, pero **aún debe comprobarse de extremo
a extremo con un servidor y cliente Jellyfin**. No se presenta como validado solo
por compilar contra sus bibliotecas.

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

## Instalación de la versión privada

1. Descarga el ZIP y su SHA-256 desde [Releases](https://github.com/slx612/jellyfin-plugin-compressor/releases)
   usando tu cuenta autorizada de GitHub.
2. Detén Jellyfin y crea `Compressor_0.1.0.0` dentro de su directorio `plugins`.
3. Extrae allí `Jellyfin.Plugin.Compressor.dll`, `meta.json` y los archivos de licencia.
4. Arranca Jellyfin y abre **Panel de control → Jellyfin Compressor**.
5. Elige carpetas, destino de originales, retención y codificador. Guarda la
   configuración y comienza con una carpeta de prueba pequeña.

En Docker/Synology se utilizan las rutas **visibles dentro del servidor Jellyfin**,
no las del equipo donde está abierto el navegador. Jellyfin necesita lectura y
escritura en la biblioteca, su directorio de datos y la carpeta de originales.
Los enlaces simbólicos y junctions se rechazan en v1.

El repositorio privado no es un catálogo público de complementos. No pongas un
token de GitHub en una URL de repositorio Jellyfin. Esta entrega usa instalación
manual del paquete autenticado.

## Configuración y recuperación

La retención debe elegirse explícitamente entre 1 y 3650 días. Cambiarla afecta a
futuros trabajos: los ya encolados conservan su instantánea. Desactivar nuevas
compresiones no suspende la limpieza de originales ya vencidos. La limpieza se
revisa cada minuto, independientemente de la pausa y del horario de compresión.

Durante la retención, **Restaurar original** recupera el archivo si la película no
se está reproduciendo y ningún contenido ajeno ocupa su lugar. Por precaución,
tras una restauración ambas copias de cuarentena se conservan **sin caducidad**;
pueden retirarse manualmente tras comprobar el archivo restaurado. Las
transacciones abortadas también requieren revisión manual de sus copias.

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
./build-plugin.ps1 -Version 0.1.0.0
```

El ZIP y su SHA-256 quedan en `artifacts/`. Las dependencias de Jellyfin se
referencian al compilar; el paquete no incluye sus DLL ni un FFmpeg independiente.

Consulta [validación y límites](docs/validation.md), [diseño](docs/v1-design.md) y
[atribución al proyecto original](UPSTREAM.md). El código adaptado procede de
[Jellyfin Pre-Transcode](https://github.com/mugurc/jellyfin-plugin-pre-transcode),
con licencia GPL-3.0. Los espacios de nombres internos conservan `PreTranscode`
para facilitar comparaciones; el complemento tiene GUID, API, DLL y estado propios.
Este repositorio contiene únicamente el complemento Jellyfin.
