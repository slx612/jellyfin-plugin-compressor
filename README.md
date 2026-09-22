# Jellyfin Compressor

Complemento para Jellyfin 10.11.6 que reduce el tamaño de las películas con
selección de carpetas, cuarentena temporal y protección frente a recompresión.

Estado: preparación de la v1. Todavía no hay una versión instalable propia.

## Requisitos de la v1

- Compresión manual y automatización desactivada al instalar.
- Carpetas incluidas y excluidas, con prioridad de las exclusiones.
- Validación del resultado antes de sustituir la película.
- Originales en una carpeta elegida por el administrador, con retención
  configurable y eliminación posterior.
- El comprimido conserva la ubicación y el nombre base del original.
- Registro persistente para no recomprimir películas ya procesadas, aunque se
  muevan o se borre el original de cuarentena.

El [diseño de la v1](docs/v1-design.md) concreta el comportamiento previsto.

## Base técnica prevista

Adaptación de [Jellyfin Pre-Transcode](https://github.com/mugurc/jellyfin-plugin-pre-transcode),
conservando su licencia GPL-3.0 y atribución al incorporar su código.
Este repositorio es independiente y contiene únicamente el proyecto para Jellyfin.

Mientras el repositorio sea privado, las futuras pruebas se instalarán descargando
el paquete autenticado desde GitHub. No se publicarán credenciales en un catálogo.
