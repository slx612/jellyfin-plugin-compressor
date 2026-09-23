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

## Comprobado en un servidor Jellyfin 10.11.6

El 2026-09-23 se ejecutó la [prueba reproducible de identidad y estados](identity-regression-test.md)
con la DLL del paquete privado `v0.1.0-alpha.1`, sin cambios en el código del complemento.
SHA-256 de la DLL: `321bd1a9cef01e40969a293143cedfafe0176a28383dcd19cfc00a5be714a56f`.

Entorno: Windows, servidor portable oficial 10.11.6, FFmpeg de Jellyfin 8.1.2,
libx265, HTTP exclusivo en localhost, datos y biblioteca desechables. Tres películas
sintéticas de diez minutos (MKV, MP4 y M4V), con fechas de alta distintas y monitor
en tiempo real activado. Dos usuarios con películas vistas y a medias diferentes:
posiciones iniciales de 120 y 180 segundos, respectivamente.

Las seis comparaciones conservaron exactamente los datos de ambos usuarios:

| Momento | Resultado |
| --- | --- |
| Después de comprimir los tres archivos | Correcto |
| Después de un escaneo completo | Correcto |
| Después de detener y arrancar Jellyfin | Correcto |
| Después de escanear tras el reinicio | Correcto |
| Después de restaurar los tres originales | Correcto |
| Después del escaneo final | Correcto |

Campos comparados: identificador de la ficha, ruta, `DateCreated`, clave de datos
del usuario, visto, posición en ticks, contador y fecha de reproducción, favorito,
listas y orden de recién añadidas, recién añadidas sin ver y continuar viendo.
Los tres trabajos finalizaron con reducción real de tamaño y el códec HEVC
actualizado en Jellyfin. La restauración recuperó el SHA-256 original de cada archivo.
También se comprobaron instalación y carga de servicios, automatización apagada
y selección de carpetas vacía de fábrica.

La evidencia resumida sin contraseñas ni rutas personales está en
[identity-10.11.6.json](validation/identity-10.11.6.json). Las bases de datos,
instantáneas completas, registros y medios de prueba quedan en `artifacts/`,
excluidos de Git. El comparador también detectó 11 alteraciones independientes
introducidas deliberadamente en sus campos y listas protegidos.

La prueba espera a que `/health` indique `Healthy`: el servidor de arranque de
Jellyfin también responde a `/System/Info/Public` antes de inicializar la API.
El cierre permite hasta tres minutos para la optimización de SQLite de Jellyfin.

## Pendiente antes de considerar la v1 estable

1. Funcionamiento visual completo del panel y de los clientes de reproducción.
2. Escaneo concurrente con el reemplazo y reinicio entre la publicación del archivo
   y la actualización de información de medios. Aquí se escaneó y reinició después
   de completar los trabajos, con el monitor en tiempo real activado.
3. Comportamiento de permisos, fechas, espacio insuficiente y copia entre volúmenes
   en el sistema de archivos del servidor de destino; pruebas de cortes reales.
4. Codificadores de GPU en el hardware del servidor (solo libx265 probado aquí).

Los diarios y operaciones atómicas cubren interrupciones del proceso comprobadas
mediante estados persistidos. No equivalen a una certificación de durabilidad ante
cortes eléctricos, averías de disco o escrituras simultáneas de otros programas.
La versión es un paquete de prueba privado; la automatización permanece apagada
por defecto y la primera prueba debe hacerse con copias de medios prescindibles.

## Ampliación de cuota de cuarentena

La prueba real de Jellyfin descrita arriba corresponde a la DLL de `v0.1.0-alpha.1`.
La cuota configurable y la limpieza tras restaurar son posteriores: sus pruebas de
servicio comprueban orden de eliminación, archivos ajenos, bloqueo por actualización
pendiente, reducción del límite sin nuevas compresiones y limpieza después de
verificar la restauración. **La ampliación aún no se ha ejecutado dentro de un
servidor Jellyfin ni en el NAS.**
