# Jellyfin Compressor v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Entregar una versión preliminar privada, instalable en Jellyfin 10.11.6,
con selección de carpetas, originales temporales y protección contra recompresión.

**Architecture:** Adaptar Pre-Transcode conservando sus componentes de Jellyfin y
FFmpeg. Incorporar un registro de contenido y transacciones persistentes, separados
del historial de trabajos. Todas las entradas manuales y automáticas comparten
las comprobaciones de selección, identidad y publicación.

**Tech Stack:** C#, .NET 9, paquetes Jellyfin 10.11.6, FFmpeg/FFprobe del servidor,
HTML/JavaScript integrados, JSON persistente mediante System.Text.Json, xUnit y
Moq ya usados por la base. Sin nuevo servicio ni base de datos externa.

**Spec:** [Diseño aprobado](../../v1-design.md).

## Global Constraints

- Destino confirmado: Jellyfin 10.11.6.
- Compresión automática desactivada.
- Una película a la vez.
- Ninguna carpeta incluida al instalar.
- Las exclusiones prevalecen sobre cualquier inclusión.
- La v1 omite HDR y Dolby Vision.
- Ahorro mínimo inicial del 15 %.
- El historial visible y el registro de contenidos son independientes.
- La v1 no ofrece recompresión forzada de resultados propios.
- No se instala ni se ejecuta sobre una biblioteca real como parte de la publicación.
- Repositorio privado independiente; no copiar material del compresor DSM.
- Conservar GPL-3.0 y atribución del código de Pre-Transcode incorporado.

## Review Focus

1. Dos carpetas llamadas `Películas` en volúmenes distintos: copias sin colisiones
   y exclusiones comparadas por componentes, no por prefijos (tareas 2 y 4).
2. Un reinicio después de mover un archivo pero antes de guardar el estado:
   recuperación por huellas reales sin borrar archivos desconocidos (tarea 4).
3. La película empieza a reproducirse durante FFmpeg: diferir publicación y
   volver a comprobar antes del cambio de archivos (tarea 6).
4. Borrar historial, cambiar perfil o mover una salida: el contenido no vuelve
   a FFmpeg, y su original no se purga si la salida no puede localizarse (3 y 5).
5. Destino en otro disco, desconectado o sin espacio: conservar fuente y registro,
   sin publicar una copia parcial ni limpiar datos ajenos (4 y 5).

## Organización y decisiones de implementación

Trabajar en `C:/Users/sergi/Documents/jellyfin-plugin-compressor`, repositorio nuevo,
independiente del directorio inicial. Crear `codex/v1` en este repositorio. No hace
falta un worktree del compresor anterior: traería el historial equivocado.

Importar únicamente el árbol de Pre-Transcode en la revisión
`0e0a923465043f15c87e3db49ef0f380932ebb05`. Nunca copiar su `.git`, artefactos ni
un manifiesto que ofrezca instalar versiones del proyecto original como propias.
Conservar los nombres internos `Jellyfin.Plugin.PreTranscode` y sus carpetas para
reducir cambios innecesarios; cambiar identidad, assembly de salida, rutas API,
estado y presentación a Jellyfin Compressor.

Identidad propia:

```text
Nombre: Jellyfin Compressor
GUID: 274af2b7-724c-41e9-82e7-56c3e80139c1
Assembly: Jellyfin.Plugin.Compressor
API: /JellyfinCompressor
Estado: <Jellyfin DataPath>/jellyfin-compressor/
Primera etiqueta: v0.1.0-alpha.1
```

La entrega usa MKV y HEVC, conservando resolución y pistas. Los perfiles heredados
que permitan perder pistas, cambiar resolución o sustituir sin cuarentena no se
ofrecen ni se aceptan por API en esta v1. Se mantienen las clases internas útiles
de la base. Copiar audio, subtítulos y adjuntos compatibles; si una pista no se
puede preservar, bloquear la película con motivo legible.

El estado nuevo reside en `Safety/`: `FolderPolicy.cs`, `ContentRegistry.cs`,
`ReplacementRecord.cs`, `ReplacementService.cs` y `QuarantineService.cs`. No crear
una abstracción de almacenamiento con una sola implementación. JSON por registro,
escritura a temporal, `Flush(true)` y publicación en el mismo directorio; un fallo
de persistencia impide avanzar al siguiente cambio de archivos.

## Tarea 1: Base propia compilable y configuración inicial segura

**Archivos:** importar los proyectos y pruebas de la revisión indicada, `LICENSE`,
`.editorconfig`, `jellyfin.ruleset`, `nuget.config`, `Directory.Build.props`;
modificar `Jellyfin.Plugin.PreTranscode/Plugin.cs`,
`Jellyfin.Plugin.PreTranscode/Jellyfin.Plugin.PreTranscode.csproj`,
`Jellyfin.Plugin.PreTranscode/Configuration/PluginConfiguration.cs`,
`Jellyfin.Plugin.PreTranscode/Configuration/ConfigurationInitializer.cs`,
`Jellyfin.Plugin.PreTranscode.Tests/Jellyfin.Plugin.PreTranscode.Tests.csproj`;
crear `UPSTREAM.md`, `global.json`,
`Jellyfin.Plugin.PreTranscode.Tests/CompressorDefaultsTests.cs`.

**Interfaces:** conservar tipos heredados; añadir estas propiedades de configuración.
`Enabled` sigue permitiendo desactivar todo el complemento y no sustituye al
interruptor específico de automatización.

```csharp
public bool AutomaticCompressionEnabled { get; set; } = false;
public List<string> IncludedFolders { get; set; } = new();
public List<string> ExcludedFolders { get; set; } = new();
public string QuarantineDirectory { get; set; } = string.Empty;
public int RetentionDays { get; set; } = 0; // obligatorio: 1..3650
public double MinSavingsPercent { get; set; } = 15;
```

- [ ] Importar en `codex/v1` sin sobrescribir README, diseño ni plan propios;
  guardar origen, SHA y fecha en `UPSTREAM.md`, incluyendo modificaciones propias.
- [ ] Escribir primero una prueba que fija el comportamiento de instalación:

```csharp
[Fact]
public void InstallationDoesNotAuthorizeAnyCompression()
{
    var config = new PluginConfiguration();
    ConfigurationInitializer.Normalize(config);
    Assert.False(config.AutomaticCompressionEnabled);
    Assert.Empty(config.IncludedFolders);
    Assert.Empty(config.ExcludedFolders);
    Assert.Equal(0, config.RetentionDays);
    Assert.Equal(string.Empty, config.QuarantineDirectory);
    Assert.Equal(1, config.MaxConcurrentJobs);
    Assert.Equal(15, config.MinSavingsPercent);
}
```

- [ ] Ejecutar `dotnet test --filter CompressorDefaultsTests`: inicialmente falla
  porque no existen las propiedades. Añadirlas y cambiar el perfil inicial a
  HEVC/MKV, audio copy, resolución unchanged, salida con cuarentena. No sembrar
  reglas que conviertan toda la biblioteca ni confirmar retención automáticamente.
- [ ] Cambiar referencias Jellyfin de ambos proyectos a `10.11.6`; usar SDK
  `9.0.100` con `rollForward: latestFeature`, evitando salto automático a otra major.
  Si falta SDK, instalar .NET 9 en un directorio de herramientas fuera del repo.
- [ ] Cambiar GUID, nombre, assembly, páginas, rutas y directorio de estado. Buscar
  referencias residuales de identidad: se permiten namespaces y atribuciones,
  no referencias de instalación/actualización al paquete original.
- [ ] Ejecutar `dotnet test -c Release` y `git diff --check`; registrar el resultado
  real de la base, sin declarar cobertura de las funciones aún pendientes.
- [ ] Commit: `chore: establish private Jellyfin Compressor plugin baseline`.

## Tarea 2: Carpetas y entradas manuales/automáticas unificadas

**Archivos:** crear `Jellyfin.Plugin.PreTranscode/Safety/FolderPolicy.cs` y
`Jellyfin.Plugin.PreTranscode/Library/CompressionRequestOrigin.cs`; modificar
`Library/ItemEvaluator.cs`, `Library/LibraryMonitor.cs`,
`Library/PreTranscodeScanTask.cs`, `Library/PreTranscodeSweepTask.cs`,
`Api/PreTranscodeController.cs`, `Jobs/TranscodeJob.cs`, `Jobs/QueueProcessor.cs`
(todos bajo `Jellyfin.Plugin.PreTranscode/`); crear pruebas
`FolderPolicyTests.cs`, `AutomaticCompressionTests.cs` en el proyecto de pruebas.

**Interfaces:**

```csharp
internal enum CompressionRequestOrigin { Manual, Automatic }
internal sealed record FolderDecision(bool Allowed, string Reason, string? Root);
// FolderPolicy: recibe las raíces reales de Jellyfin, nunca las confía al cliente.
internal static FolderDecision Evaluate(
    string path, PluginConfiguration config, IReadOnlyList<string> libraryRoots);
// ItemEvaluator: analizar devuelve candidatos; encolar es una acción distinta.
internal Task<int> SweepAsync(IProgress<double>? progress,
    CancellationToken cancellationToken, CompressionRequestOrigin origin);
```

- [ ] Probar `Evaluate` con un directorio temporal, `Movies`, `Movies2` y
  `Movies/Keep`. Incluir `Movies`, excluir `Movies/Keep`:

```csharp
var config = new PluginConfiguration
{
    IncludedFolders = new() { movies },
    ExcludedFolders = new() { Path.Combine(movies, "Keep") }
};
Assert.True(FolderPolicy.Evaluate(Path.Combine(movies, "film.mkv"), config, roots).Allowed);
Assert.False(FolderPolicy.Evaluate(Path.Combine(sibling, "film.mkv"), config, roots).Allowed);
Assert.False(FolderPolicy.Evaluate(Path.Combine(movies, "Keep", "film.mkv"), config, roots).Allowed);
```

  `movies`, `sibling` y `roots` se crean en cada prueba con `Path.Combine` y
  `Directory.CreateTempSubdirectory`; no usar rutas de medios reales. Cubrir
  además rutas relativas, `..`, enlaces/junctions, UNC y raíz fuera de Jellyfin.
- [ ] Ejecutar `dotnet test --filter FolderPolicyTests` y observar el fallo.
  Implementar normalización absoluta, pertenencia por componentes y rechazo de
  enlaces en cada componente existente. Exigir inclusión, respetar exclusión
  y rechazar cuarentena dentro o por encima de una raíz de biblioteca.
- [ ] Crear pruebas de entradas con cola espía/Moq: postescaneo, evento y tarea
  periódica deben encolar cero con automático apagado, incluso con reglas activas.
  Analizar tampoco encola. Comprimir manualmente sí puede hacerlo con configuración
  completa y fichero elegible. Marcar cada trabajo con su origen persistente.
- [ ] Sustituir el interruptor heredado de nuevos elementos por uno global para
  todas las entradas automáticas. Al apagarlo, retener trabajos automáticos
  pendientes; no impedir trabajos manuales. Comprobar antes de reclamar y de
  iniciar FFmpeg. Desactivar no aborta una publicación de archivos ya comenzada.
- [ ] Añadir la misma política a entradas de archivo suelto y reintento: ninguna
  API heredada puede saltarse exclusiones. Limitar candidatos a películas locales
  de Jellyfin, no episodios, streams remotos, discos o carpetas DVD/Blu-ray.
- [ ] Ejecutar las dos clases nuevas y la suite existente; commit:
  `feat: require explicit folder selection and opt-in automation`.

## Tarea 3: Registro de contenido independiente del historial

**Archivos:** crear `Safety/ContentRegistry.cs`; modificar
`Media/MediaProbeInfo.cs`, `Media/MediaProber.cs`, `Jobs/TranscodeJob.cs`,
`Library/ItemEvaluator.cs`, `Jobs/TranscodeExecutor.cs`, `Api/PreTranscodeController.cs`;
crear `ContentRegistryTests.cs` en el proyecto de pruebas.

**Interfaces:** tipos bajo el namespace interno `.Safety`.

```csharp
internal sealed record ContentIdentity(string Sha256, long Length);
internal sealed record ContentRecord(
    string Sha256, long Length, string Kind, string ProfileKey,
    string? RelatedSha256, string LastKnownPath);
internal sealed class ContentRegistry
{
    public ContentRegistry(string directory);
    public static Task<ContentIdentity> IdentifyAsync(string path, CancellationToken token);
    public ContentRecord? Find(string sha256);
    public void Save(ContentRecord record);
    public void UpdateLocation(string sha256, string path);
}
```

`Kind` admite `compressed`, `original`, `restored` y `no-savings`. `ProfileKey` es
SHA-256 de la instantánea JSON determinista del perfil efectivo. Solo `no-savings`
puede reconsiderarse automáticamente al cambiar el perfil. Resultados propios y
originales ya procesados siempre se omiten. Las marcas del contenedor se exponen
como `MediaProbeInfo.IsCompressorOutput`.

- [ ] Crear archivos pequeños en un directorio temporal y probar persistencia
  después de renombrar y reinstanciar el registro:

```csharp
var identity = await ContentRegistry.IdentifyAsync(source, CancellationToken.None);
var registry = new ContentRegistry(state);
registry.Save(new ContentRecord(identity.Sha256, identity.Length,
    "compressed", "profile-a", null, source));
File.Move(source, moved);
var movedIdentity = await ContentRegistry.IdentifyAsync(moved, CancellationToken.None);
Assert.Equal(identity, movedIdentity);
Assert.Equal("compressed", new ContentRegistry(state).Find(movedIdentity.Sha256)!.Kind);
```

- [ ] Ejecutar `dotnet test --filter ContentRegistryTests` antes de implementar.
  Usar `SHA256.HashDataAsync` y comprobar tamaño/mtime antes y después de leer.
  Inicialmente sin caché: priorizar corrección, nunca cargar vídeos enteros en RAM.
- [ ] Guardar un JSON por huella con escritura a temporal exclusiva, flush y
  rename en el mismo directorio. Validar huellas de 64 caracteres hexadecimales.
  JSON corrupto o directorio inaccesible bloquean clasificación y publicación;
  no tratar un fallo de lectura como registro vacío.
- [ ] Repetir pruebas limpiando el historial con `JobQueue.ClearFinished`,
  cambiando perfil y purgando una transacción: el registro comprimido persiste.
  Probar mismo nombre con contenido distinto, copia de contenido idéntico y archivo
  que cambia durante el hash. Marcar `jellyfin_compressor=1` al codificar; reconocer
  la marca sin registro como Ya comprimida / revisar, sin ejecutar FFmpeg.
- [ ] Aplicar la comprobación antes de encolar, reintentar y ejecutar. No usar
  `SkipIfAlreadyCompliant` como sustituto del registro. Deshabilitar recompresión
  de resultados propios en todas las rutas de API.
- [ ] Ejecutar pruebas del registro/evaluador/cola y suite; commit:
  `feat: persist content identities independently of job history`.

## Tarea 4: Sustitución recuperable, con original verificado en cuarentena

**Archivos:** crear `Safety/ReplacementRecord.cs`, `Safety/ReplacementService.cs`;
modificar `Encoding/OutputApplier.cs`, `Jobs/TranscodeExecutor.cs`,
`Jobs/QueueProcessor.cs`, `PluginServiceRegistrator.cs`;
crear `ReplacementServiceTests.cs`, `ReplacementRecoveryTests.cs`.

**Interfaces:**

```csharp
internal enum ReplacementPhase
{
    Prepared, BackupVerified, SourceStaged, Published, Completed,
    RestorePrepared, Restored, PurgePrepared, Purged, Blocked
}
internal sealed record ReplacementRequest(
    string Id, string SourcePath, string VerifiedOutputPath, string LibraryRoot,
    string QuarantineRoot, int RetentionDays, string ProfileKey,
    ContentIdentity Source, ContentIdentity Output);
internal sealed record ReplacementResult(string Id, string FinalPath, string OriginalPath);
internal sealed class ReplacementService
{
    public ReplacementService(string transactionDirectory, ContentRegistry registry);
    public Task<ReplacementResult> PublishAsync(ReplacementRequest request, CancellationToken token);
    public Task RecoverAsync(CancellationToken token);
}
```

`ReplacementRecord` guarda todos los campos de la petición, fase, rutas exactas
de staging y destino, identidad del resultado, fecha de finalización/vencimiento,
motivo de bloqueo y revisión de esquema. Usar `Guid` válido para Id y rutas derivadas
por el servidor. El directorio de respaldo incorpora Id y ruta relativa, evitando
colisiones entre bibliotecas sin confiar en sus nombres visibles.

- [ ] Escribir la prueba de publicación con contenidos sintéticos, identidades
  reales y carpetas temporales. Tras publicar:

```csharp
Assert.Equal(compressedBytes, await File.ReadAllBytesAsync(result.FinalPath));
Assert.Equal(originalBytes, await File.ReadAllBytesAsync(result.OriginalPath));
Assert.NotNull(registry.Find(outputIdentity.Sha256));
Assert.NotNull(registry.Find(sourceIdentity.Sha256));
```

  Preparar petición mediante `ContentRegistry.IdentifyAsync`; el constructor del
  servicio recibe rutas temporales. Ejecutar `dotnet test --filter Replacement`.
- [ ] Implementar journal persistente antes de cada cambio irreversible. Copiar
  original a un `.partial` exclusivo en cuarentena, cerrar/flush, verificar SHA-256
  y renombrar. Usar la copia verificada incluso en el mismo volumen para compartir
  una sola ruta correcta con el caso de disco distinto.
- [ ] Preparar salida en un temporal oculto dentro del directorio de la fuente.
  Volver a comprobar identidad de fuente y salida, permisos y enlaces. Persistir
  rutas/fase; renombrar fuente a staging oculto del mismo volumen, publicar salida
  sin sobrescribir contenido ajeno, registrar identidades y finalizar. Retirar el
  staging solo tras persistencia completa y respaldo correcto.
- [ ] Preparar pruebas por estado persistido: interrupción antes/después de cada
  movimiento y antes/después del registro final. Recrear servicio y ejecutar
  `RecoverAsync`; debe finalizar cuando huellas lo permiten, restaurar cuando
  corresponde o bloquear sin borrar cuando exista ambigüedad. Un bloqueo impide
  nuevas sustituciones y aparece en el panel.
- [ ] Probar MP4→MKV con MKV preexistente, dos fuentes con igual nombre, fuente
  modificada, permiso denegado, copia parcial, respaldo alterado y cancelación.
  Probar la copia en volúmenes distintos en integración con montajes temporales;
  no basta simular dos directorios del mismo disco.
- [ ] Eliminar la ruta heredada que sustituye y borra inmediatamente. Una petición
  al modo antiguo debe rechazarse o pasar obligatoriamente por este servicio.
- [ ] Ejecutar recuperación antes de `CleanTempDirectory` y antes de reclamar cola;
  no eliminar temporales que pertenezcan a una transacción abierta.
- [ ] Suite y commit: `feat: replace movies through recoverable quarantine transactions`.

## Tarea 5: Vencimiento y restauración de originales

**Archivos:** crear `Safety/QuarantineService.cs`, `Library/QuarantineCleanupTask.cs`;
modificar `Safety/ReplacementRecord.cs`, `Safety/ReplacementService.cs`,
`PluginServiceRegistrator.cs`; crear `QuarantineServiceTests.cs`.

**Interfaces:**

```csharp
internal sealed record QuarantineItem(
    string Id, string SourcePath, string OriginalPath, DateTimeOffset? ExpiresUtc,
    ReplacementPhase Phase, string? BlockReason);
internal sealed class QuarantineService
{
    public QuarantineService(string transactionDirectory, ContentRegistry registry);
    public IReadOnlyList<QuarantineItem> List(int offset, int limit);
    public Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken token);
    public Task RestoreAsync(string id, CancellationToken token);
}
```

- [ ] Probar con reloj explícito: antes de vencer, cero borrados; al vencer, uno;
  segundo barrido, cero. El contenido ajeno permanece:

```csharp
Assert.Equal(0, await quarantine.PurgeExpiredAsync(expires.AddSeconds(-1), token));
Assert.True(File.Exists(originalPath));
Assert.Equal(1, await quarantine.PurgeExpiredAsync(expires, token));
Assert.False(File.Exists(originalPath));
Assert.True(File.Exists(unregisteredPath));
Assert.NotNull(registry.Find(outputHash));
```

  Crear el registro mediante una publicación real de la tarea 4; leer el vencimiento
  guardado. Ejecutar `dotnet test --filter QuarantineServiceTests` antes de implementar.
- [ ] Seleccionar únicamente `Completed` vencidos. Verificar raíz/ruta, ausencia
  de enlaces, hash original y presencia/identidad del comprimido. Si fue movido,
  usar la ubicación verificada del registro; si no se localiza, aplazar.
- [ ] Persistir `PurgePrepared` antes de borrar exclusivamente el archivo registrado.
  Persistir `Purged` después; si se interrumpe, reconciliar archivo presente/ausente.
  No vaciar directorios recursivamente ni eliminar registros de contenido.
- [ ] Restaurar mediante journal propio: comprobar que el destino contiene el
  resultado conocido; preservar el comprimido durante el intercambio y publicar
  una copia verificada del original. Registrar `restored` antes de limpiar staging.
  Ante cambios o reproducción activa, rechazar o diferir sin tocar archivos.
- [ ] Probar JSON corrupto, enlaces, respaldo alterado, salida movida, salida
  desaparecida, cambio posterior de retención y cortes durante purga/restauración.
  Una retención nueva no cambia vencimientos existentes.
- [ ] Añadir tarea diaria de limpieza separada; puede ejecutarse aunque nuevas
  compresiones automáticas estén desactivadas. Respetar la desactivación global
  del complemento y los bloqueos de recuperación. Mostrar su comportamiento en UI.
- [ ] Suite y commit: `feat: expire and restore registered quarantine originals`.

## Tarea 6: Pipeline de compresión y actualización de Jellyfin

**Archivos:** modificar `Jobs/TranscodeJob.cs`, `Jobs/TranscodeExecutor.cs`,
`Jobs/QueueProcessor.cs`, `Encoding/FfmpegCommandBuilder.cs`,
`Encoding/OutputVerifier.cs`, `Media/MediaProber.cs`, `Media/MediaProbeInfo.cs`,
`Library/ReplacedItemUpdater.cs`; crear `CompressionLifecycleTests.cs` y ampliar
`RealFfmpegIntegrationTests.cs`, `OutputVerifierTests.cs`.

**Interfaces:** extender `TranscodeJob` con `Origin`, `ProfileSnapshotJson`,
`ProfileKey`, `SourceSha256`, `SelectedRoot`, `QuarantineRoot`, `RetentionDays` y
`MinSavingsPercent`. La instantánea es completa y se valida al encolar; no resolver
el perfil vivo por Id al ejecutar. Sesiones activas se consultan mediante
`ISessionManager` de Jellyfin. Usar servicios de 2–5 en `ExecuteAsync` existente.

- [ ] Probar el cambio de perfil después de encolar: los argumentos mantienen el
  valor original, pero una nueva exclusión sí impide ejecutar el trabajo. Probar
  automático apagado con trabajos de origen automático pendientes y trabajos
  manuales permitidos. Ejecutar `dotnet test --filter CompressionLifecycleTests`.
- [ ] Revalidar política, identidad, estabilidad de archivo y espacio de las rutas
  implicadas. Omitir HDR/DV o vídeo sin metadatos suficientes. Ejecutar un solo
  FFmpeg; mantener parada/cancelación sin huérfanos y supervisión de errores.
- [ ] Usar el constructor de argumentos heredado, añadiendo la marca:

```csharp
arguments.Add("-metadata");
arguments.Add("jellyfin_compressor=1");
```

  Preservar todas las pistas de audio/subtítulos compatibles, adjuntos y capítulos.
  Añadir probe de idioma, disposición, capítulos y marcador. Validar pista por
  pista y dimensiones originales; no usar solo número de pistas o duración.
  Rechazar perfiles de API que contradigan el alcance de la v1.
- [ ] Medir ahorro con tamaños conocidos positivos:

```csharp
var savedPercent = 100d * (sourceBytes - outputBytes) / sourceBytes;
if (savedPercent < job.MinSavingsPercent)
{
    // Persistir no-savings con SourceSha256 y ProfileKey antes de terminar.
    // Eliminar únicamente el temporal de este trabajo; fuente intacta.
}
```

  Probar 14,99 %, 15 %, salida mayor, duración desconocida, pérdida de subtítulo,
  capítulos o idioma y vídeo de portada confundido con la película.
- [ ] Consultar reproducción antes de codificar y antes de publicar/restaurar.
  Si empieza durante FFmpeg, guardar estado de espera con salida validada;
  no volver a codificar al reanudar. El arranque recupera ese estado sin borrarlo.
- [ ] Pasar por `ReplacementService`, actualizar información y ruta de Jellyfin,
  y guardar un fallo de actualización como pendiente de reintento separado:
  jamás recomprimir una salida porque falle el refresco de biblioteca.
- [ ] Integración con FFmpeg real sobre vídeo sintético SDR con dos audios,
  subtítulos y capítulos. Validar salida, hash del respaldo, reinicio del registro,
  segunda ejecución omitida y vencimiento. Sin GPU, probar CPU; no declarar GPU
  validada por enumerar los codificadores.
- [ ] Suite y commit: `feat: connect validated compression to safe replacement`.

## Tarea 7: Panel, documentación y paquete privado

**Archivos:** modificar `Api/PreTranscodeController.cs`,
`Configuration/configPage.html`, `Configuration/queuePage.html`,
`Configuration/jobsPage.html`, `Plugin.cs`, `build-plugin.ps1`, `build.yaml`,
`README.md`; crear `.github/workflows/build.yaml`, `docs/testing.md`,
`ApiPolicyTests.cs` y `PackagingTests.cs` en el proyecto de pruebas.

**Interfaces:** todos los endpoints bajo `/JellyfinCompressor`, con
`[Authorize(Policy = "RequiresElevation")]`:

| Método y ruta | Entrada y resultado |
|---|---|
| GET `/Folders` | Ruta opcional; devuelve un nivel de raíces/subcarpetas permitidas |
| POST `/Analyze` | Carpetas seleccionadas; candidatos clasificados, sin encolar |
| POST `/Compress` | IDs de películas seleccionadas; revalidación y trabajos manuales |
| GET `/Quarantine` | offset/limit acotados; registros, vencimiento y bloqueos |
| POST `/Quarantine/{id}/Restore` | ID registrado; restauración verificada |

Reutilizar endpoints existentes de estado, pausa y cancelación, con la misma
política y nueva ruta. Las peticiones no pueden introducir una ruta arbitraria
por eludir IDs: toda ruta se valida contra bibliotecas reales. `Analyze` no cambia
medios; `Compress` vuelve a comprobar configuración e identidad, sin confiar en
resultados antiguos del navegador.

- [ ] Escribir pruebas de autorización y validación: sin sesión, no admin, ID
  ajeno, ruta fuera de biblioteca, retención cero, límite de consulta negativo,
  perfil HDR y recompresión de salida propia. Ejecutar
  `dotnet test --filter ApiPolicyTests` y corregir los fallos.
- [ ] Incorporar árbol de carpetas incluido/excluido con controles nativos,
  destino de originales y retención obligatoria. Sugerir siete días en la UI,
  pero exigir guardar configuración. Mostrar la próxima fecha de borrado.
  No añadir framework frontend ni página de administración externa.
- [ ] Separar Analizar de Comprimir seleccionadas. Mostrar estados traducidos,
  cola/progreso, ahorro, errores y bloqueo de recuperación. No representar un
  descarte por falta de ahorro como fallo de compresión.
- [ ] Comprobar el panel en 1280 px y 375 px: navegación de teclado, etiquetas,
  selección persistida, exclusiones claras, texto largo y cero errores de consola.
  Probar contra Jellyfin 10.11.6 aislado con biblioteca sintética, nunca el NAS real.
- [ ] Adaptar empaquetado con GUID/assembly propios, GPL y atribución. El ZIP
  contiene DLL, dependencias necesarias que Jellyfin no provea, `meta.json`,
  `LICENSE` y `UPSTREAM.md`; no configuración local, estado, credenciales ni medios.
  Verificar lista de entradas y metadatos antes de subir.
- [ ] Crear CI para .NET 9 con paquetes 10.11.6 y FFmpeg real; ejecutar:

```text
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
git diff --check
```

  La prueba FFmpeg debe fallar si faltan binarios en CI, no pasar por omisión.
  CI usa una matriz Windows/Linux para rutas y permisos; la prueba de volumen
  distinto se ejecuta en Linux con dos sistemas de archivos temporales.
- [ ] Documentar instalación manual privada, opciones, retención, restauración,
  no recompresión y copia del registro. Explicar que el espacio se libera al
  vencer originales, y que borrar el estado compromete la detección. Registrar
  exactamente las plataformas/codificadores realmente probados.
- [ ] Commit: `feat: expose compressor controls and package private alpha`.
- [ ] Revisar diff final y pruebas, subir `codex/v1` y abrir PR contra `main`.
  Adjuntar el PR a la tarea. Confirmar otra vez `isPrivate=true`. Publicar
  `v0.1.0-alpha.1` como prerelease privada solo sobre el commit verificado, con
  ZIP y SHA-256; no incluir tokens en manifest ni cambiar visibilidad del repo.

## Comprobación del plan antes de ejecución

- [x] Requisitos del diseño asignados a tareas 1–7.
- [x] Identidad propia y publicación sin historial del compresor anterior.
- [x] Entradas API y tareas periódicas incluidas, no solo botones de la UI.
- [x] Registro independiente del historial, cuarentena y perfil.
- [x] Copia entre volúmenes, recuperación y no sobrescritura contempladas.
- [x] Cinco condiciones de Review Focus asignadas a pruebas concretas.
- [x] Pruebas reales de FFmpeg y servidor aislado separadas de compilación.

## Ejecución propuesta

Implementación directa en esta tarea, en orden, con revisión independiente final.
Se recomienda por las dependencias entre selección, registro y transacciones:
repartir implementaciones simultáneas complicaría sus interfaces. También puede
ejecutarse con subagentes y revisión por tarea si el usuario prefiere ese método.
El plan requiere revisión del usuario antes de iniciar código de producto.
