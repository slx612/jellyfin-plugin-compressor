# Validación de la versión preliminar

Fecha: 2026-09-23. Objetivo: Jellyfin 10.11.6, .NET 9.

## Comprobado localmente

`dotnet test -c Release`: **342 pruebas correctas, 0 fallidas, 0 omitidas**.
Windows, SDK .NET 9.0.318 y FFmpeg/ffprobe de Jellyfin disponibles en PATH.

La batería conserva las pruebas de la base upstream y añade comprobaciones de:

- Automatización apagada: tarea programada, postescaneo, evaluación de nuevos
  elementos y reclamación de trabajos automáticos. Los trabajos manuales pueden
  avanzar aunque existan trabajos automáticos pendientes.
- Instantánea de perfil e identidad conservada al persistir y volver a cargar la cola.
- Límites de carpetas, exclusiones anidadas y separación de cuarentena.
- SHA-256 conservado tras renombrar; cambios de contenido detectados; registro
  corrupto tratado como error, sin permitir recompresión.
- Publicación con misma ruta/extensión/fecha, original recuperable y retención.
- Fuente modificada, salida ausente o ajena: no sobrescribir ni purgar su original.
- Recuperación de publicación atómica pendiente de registrar; actualización de la
  ficha pendiente conservada tras reinicio y fallo transitorio.
- Limpieza del historial/retención separada del registro de identidad, y limpieza
  de temporales que respeta trabajos activos y archivos con nombres ajenos.
- Tres pruebas reales con FFmpeg: MKV, MP4 y M4V sintéticos, con dos pistas de audio
  en idiomas distintos, subtítulo forzado y capítulo. Compresión HEVC, comparación
  de pistas/disposiciones/capítulos, decodificación y restauración exacta por SHA-256.

El JavaScript del panel pasa `node --check`; sus identificadores y referencias DOM
se comprueban sin duplicados. Compila contra los paquetes oficiales Jellyfin
10.11.6. La revisión independiente detectó y se corrigieron actualizaciones
pendientes tras reinicios, temporales huérfanos, desacuerdo en el horario y el
límite que ocultaba originales restaurables tras 100 transacciones.

## Pendiente antes de considerar la v1 estable

No se ha instalado ni ejecutado el complemento dentro de un Jellyfin real durante
esta entrega. La revisión automática de permisos rechazó el comando para arrancar
un servidor de prueba aislado, sin indicar una causa concreta. No se intentó
sortear ese rechazo.

Por ello falta comprobar con servidor y cliente:

1. Instalación, carga de servicios y funcionamiento visual completo del panel.
2. Antes/después de comprimir: mismo `ItemId`, `DateCreated`, estado visto,
   posición de reproducción, favoritos y orden de recién añadidas.
3. Biblioteca con monitor en tiempo real, escaneo concurrente y reinicio entre
   reemplazo y actualización de información de medios.
4. Comportamiento de permisos, fechas, espacio insuficiente y copia entre volúmenes
   en el sistema de archivos del servidor de destino; pruebas de cortes reales.
5. Codificadores de GPU en el hardware del servidor (solo libx265 probado aquí).

Los diarios y operaciones atómicas cubren interrupciones del proceso comprobadas
mediante estados persistidos. No equivalen a una certificación de durabilidad ante
cortes eléctricos, averías de disco o escrituras simultáneas de otros programas.
La versión es un paquete de prueba privado; la automatización permanece apagada
por defecto y la primera prueba debe hacerse con copias de medios prescindibles.
