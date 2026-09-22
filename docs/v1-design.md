# Diseño de Jellyfin Compressor v1

Fecha: 2026-09-22. Destino confirmado: Jellyfin 10.11.6.
Estado: diseño aprobado por el usuario el 2026-09-22; implementación pendiente.

## Objetivo y distribución

Un complemento instalable en Jellyfin, sin otro contenedor ni servicio obligatorio.
Repositorio privado `slx612/jellyfin-plugin-compressor`, con historial independiente.
No se incorporan archivos, documentación, medios, estado ni historial Git del
compresor DSM anterior. Se reutilizan sus lecciones de funcionamiento.

Se propone adaptar Jellyfin Pre-Transcode, revisión
`0e0a923465043f15c87e3db49ef0f380932ebb05`, conservando GPL-3.0 y atribuciones.
Se aprovecharán la integración con Jellyfin, FFmpeg, el panel y las pruebas;
se añadirá el manejo de originales y un registro duradero de contenidos.
El complemento tendrá nombre, GUID, directorio de estado y metadatos propios.

## Configuración inicial y alcance

- Compresión automática desactivada. Ningún evento ni tarea periódica puede
  descubrir o iniciar nuevas compresiones sin activación explícita.
- Una película a la vez. La cola manual solo se llena al pulsar Comprimir.
- Ninguna carpeta incluida al instalar. Las exclusiones prevalecen sobre cualquier
  inclusión, tanto al encolar como al ejecutar un trabajo previamente encolado.
- Selección de rutas dentro de las bibliotecas del servidor; se validan los
  límites por componentes de ruta, no por prefijos de texto.
- El destino de originales debe configurarse antes de sustituir archivos y
  quedar fuera de las bibliotecas. Siempre queda excluido del procesamiento.
- Retención configurable en días, obligatoria antes de la primera sustitución.
  La interfaz puede sugerir siete días; guardar la configuración confirma el plazo.
- Conservar resolución, audio, subtítulos, capítulos e idiomas. La v1 omite HDR
  y Dolby Vision; no promete conservación de sus metadatos dinámicos.
- HEVC como perfil inicial, con codificador elegido entre los disponibles.
  No se presupone que todos los servidores dispongan de GPU NVIDIA.
- Ahorro mínimo inicial del 15 %. Si no se alcanza, se descarta el temporal y se
  recuerda el intento para no repetirlo automáticamente con el mismo perfil.

## Flujo de una película

1. Confirmar selección, exclusiones, archivo estable, permisos y configuración.
2. Comprobar el registro de contenidos y descartar películas ya procesadas.
3. Guardar una instantánea del perfil y del original para el trabajo.
4. Ejecutar FFmpeg a un temporal, manteniendo intacto el original.
5. Validar salida: FFmpeg correcto, archivo legible, duración compatible, vídeo,
   pistas requeridas y ahorro. Un fallo conserva el original.
6. Guardar el original en el destino elegido bajo una ruta única que conserve su
   estructura relativa. Si el destino está en otro volumen, copiar, cerrar y
   verificar contenido antes de permitir la retirada de la fuente.
7. Publicar el comprimido mediante un cambio de nombre en el volumen de la
   biblioteca. Una transacción persistente permite recuperar cada paso.
8. Registrar original y resultado, finalizar la sustitución y actualizar Jellyfin.

`Película.mkv` conserva su nombre completo al producir MKV. Si se convierte
`Película.mp4` a MKV, el resultado es `Película.mkv`. Si esa ruta ya pertenece
a otro archivo, se bloquea el trabajo: no se sobrescribe ni se inventa un nombre.
No se sustituye una película mientras Jellyfin indique que se está reproduciendo.

## Originales, vencimiento y recuperación

La retención empieza cuando se completa correctamente la sustitución, no cuando
se encola o se inicia FFmpeg. La política de cada original queda registrada:
cambiar la configuración afecta a sustituciones futuras.

La limpieza elimina exclusivamente originales registrados, vencidos y cuya ruta
y contenido coincidan con el registro. Archivos desconocidos, enlaces simbólicos,
transacciones incompletas o discrepancias bloquean esa eliminación. No se hace
un borrado recursivo de la carpeta elegida.

Antes de purgar se comprueba que el resultado publicado sigue disponible. Si ha
cambiado de ruta y no puede localizarse con certeza, se aplaza la purga y se informa.
La limpieza de cuarentena tiene una tarea separada de la compresión automática:
desactivar nuevas compresiones no anula los vencimientos ya configurados.

Durante la retención se ofrece restauración manual. No se sobrescribe contenido
ajeno o modificado; cualquier conflicto se muestra para resolverlo. Un reinicio
reconcilia las transacciones incompletas antes de aceptar nuevas sustituciones.

## Protección frente a recompresión

El historial visible y el registro de contenidos son independientes. Se guardan
huellas del original y del resultado, perfil, relación entre ambos y estado.
El registro del resultado no se elimina al limpiar historial o cuarentena.

La identidad se comprueba antes de encolar y otra vez antes de ejecutar. Una
película ya comprimida se omite al moverla, renombrarla, reiniciar Jellyfin o cambiar
el perfil. La ruta y el códec no bastan para decidir si es nueva.

El contenido idéntico se reconoce por SHA-256; se puede cachear por ruta, tamaño y
fecha mientras el archivo permanezca estable. Una ruta nueva requiere calcular
la huella. Se añade una marca al contenedor como defensa adicional; si solo se
conserva esa marca tras perder el registro, se omite y se muestra para revisión.
No se promete reconocer cualquier modificación hecha por aplicaciones externas.

La v1 no ofrece recompresión forzada de resultados propios. Un original restaurado
sigue registrado y no se vuelve a encolar automáticamente.

## Panel y permisos

Configuración, acciones y registros accesibles solo a administradores de Jellyfin.
El panel incluye selección de carpetas, exclusiones, destino y retención, perfil,
botones Analizar/Comprimir, cola con progreso e historial, y originales con vencimiento.
Debe distinguir Ya comprimida, Sin ahorro, Excluida, En cola, En curso y Error.
Los temporales y la cuarentena no se incorporan a la biblioteca.

## Validación y primera entrega

Pruebas de automatización apagada en todas las entradas, exclusiones anidadas,
no recompresión tras mover/renombrar/purgar, archivos modificados durante el trabajo,
colisiones, permisos insuficientes, destino en otro volumen y cortes entre pasos.
La limpieza debe probarse con archivos ajenos, vencimientos y salidas ausentes.

Compilación y pruebas contra Jellyfin 10.11.6; integración de FFmpeg con medios
sintéticos y comprobación del panel. El primer paquete será una versión preliminar
privada. La compilación por sí sola no equivale a una prueba en el servidor real.
No se instala ni se ejecuta sobre una biblioteca real como parte de la publicación.

## Límites de la v1

Sin HDR/Dolby Vision, sin procesamiento distribuido, sin soporte prometido para
Jellyfin 12 y sin migración de datos del compresor anterior. Las optimizaciones
adicionales se valorarán después de verificar el recorrido completo de una película.
