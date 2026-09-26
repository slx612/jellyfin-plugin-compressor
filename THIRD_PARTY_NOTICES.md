# Bundled HDR10+ tools (experimental Linux x86-64 package)

The plugin package includes the unmodified `hdr10plus_tool` 1.7.2 Linux musl executable
from [quietvoid/hdr10plus_tool](https://github.com/quietvoid/hdr10plus_tool/releases/tag/1.7.2).
It is distributed under the MIT license; see `licenses/hdr10plus_tool-MIT.txt`.

The package also includes the unmodified MKVToolNix 102.0 Linux AppImage from
[the MKVToolNix downloads](https://mkvtoolnix.download/appimage/). It is used as
separate `mkvmerge` and `mkvextract` processes. MKVToolNix's corresponding source
release and build instructions are available from [its source page](https://mkvtoolnix.download/source.html).
Its GPL version 2 license text is included as `licenses/MKVToolNix-GPL-2.0.txt`.
The AppImage contains additional bundled components and their notices; see the
upstream source distribution for the complete component list.

Jellyfin FFmpeg remains supplied by the Jellyfin server, not this package.
