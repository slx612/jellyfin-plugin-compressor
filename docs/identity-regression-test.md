# Prueba de recién añadidas, visto y progreso

Esta prueba ejecuta el complemento en un **Jellyfin 10.11.6 desechable**. No debe
apuntarse a un servidor existente ni a películas reales. Los scripts solo aceptan
una carpeta nueva dentro de `artifacts/identity-check/`, ignorada por Git, y el
servidor escucha exclusivamente en `127.0.0.1:18096`. Se rechaza un puerto ocupado.

Se crean tres vídeos sintéticos de diez minutos (MKV, MP4 y M4V), fechas de alta
distintas y dos usuarios. Cada usuario tiene una película vista, una sin ver y una
a medias; las asignaciones y posiciones son diferentes entre usuarios.

Se guardan instantáneas y se exige igualdad exacta de:

- `ItemId`, ruta completa y `DateCreated`.
- Clave de datos del usuario, visto, posición en ticks, contador de reproducciones,
  última reproducción y favorito.
- Listas y orden de recién añadidas, recién añadidas sin ver y continuar viendo,
  obtenidos de los endpoints de Jellyfin que utilizan los clientes.

Se compara tras comprimir, tras escanear, tras reiniciar, tras escanear nuevamente,
tras restaurar los originales y tras otro escaneo. El monitor en tiempo real queda
activado. La prueba exige tres trabajos completados, reducción de tamaño, cambio
de SHA-256 y códec HEVC actualizado en Jellyfin. Restaurar debe recuperar los bytes
originales. También comprueba automatización apagada y selección de carpetas vacía
en la primera instalación.

## Ejecución en Windows

Requiere Python 3.11 o posterior (`py -3`), FFmpeg con libx264/libx265, el servidor
portable oficial Jellyfin 10.11.6 y la DLL compilada del complemento. Ejemplo desde
la raíz del repositorio, sustituyendo las rutas de las herramientas:

```powershell
dotnet build -c Release
py -3 scripts/verify-library-state.py prepare `
  --fixture artifacts/identity-check/mi-prueba `
  --ffmpeg C:/herramientas/ffmpeg.exe `
  --plugin Jellyfin.Plugin.PreTranscode/bin/Release/net9.0/Jellyfin.Plugin.Compressor.dll

./scripts/verify-library-state.ps1 `
  -Fixture artifacts/identity-check/mi-prueba `
  -Jellyfin C:/herramientas/jellyfin/jellyfin.exe `
  -Ffmpeg C:/herramientas/ffmpeg.exe
```

Preparar genera solo los vídeos y archivos de configuración. El segundo comando
arranca, reinicia y detiene el servidor oculto; puede tardar varios minutos.
Conserva la base de datos, medios, registros e instantáneas como evidencia incluso
si falla. `fixture.json` contiene una contraseña aleatoria exclusiva de estos
usuarios ficticios: no publicar la carpeta de ejecución.

Solo `result.json` con `Passed: true` y una salida correcta del script completo
acreditan una ejecución satisfactoria. La mera existencia de estos scripts o de
pruebas unitarias correctas **no acredita esta validación**. El resultado vigente
se documenta en [validation.md](validation.md).

Esto cubre las listas y datos servidos por Jellyfin. No sustituye las pruebas
visuales de cada cliente, las del sistema de archivos del NAS ni las de cortes
durante la publicación. No se simulan reproducciones activas durante el reemplazo.
