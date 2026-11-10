# Third-party software in RetroLabel

| Component | Used for | License |
|---|---|---|
| [PDFium](https://pdfium.googlesource.com/pdfium/) (binaries from [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) via [pypdfium2](https://pypi.org/project/pypdfium2/) 4.30.0) | Rendering PDF pages | BSD-3-Clause / Apache-2.0, plus the notices in `native/PDFIUM-THIRD-PARTY-LICENSES.txt` |
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) 0.16.9 | Barcode check ("will it scan?") | Apache-2.0 |
| [qrcode-generator](https://github.com/kazuhikoarase/qrcode-generator) 1.4.4 (Kazuhiko Arase) | Phone pairing QR code | MIT |
| [.NET 8 runtime](https://github.com/dotnet/runtime) / ASP.NET Core | Application runtime (bundled) | MIT |
| [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | Desktop window | Microsoft Software License |
| [NSIS](https://nsis.sourceforge.io) | Installer | zlib/libpng |

"QR Code" is a registered trademark of DENSO WAVE INCORPORATED.

Zebra, Eltron and the names of Zebra products are trademarks of Zebra Technologies Corporation. RetroLabel is an independent
project that talks to these printers using Zebra's publicly documented EPL2 command language. It is **not affiliated with,
sponsored by or endorsed by Zebra Technologies**, and it contains no Zebra software or drivers.
