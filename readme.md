# PrintAgent

Agente local de impresión para Windows. Corre en la PC del punto de venta/depósito y expone una API HTTP simple para que un sistema externo (backend/ERP) consulte qué impresoras hay instaladas y les envíe contenido ya renderizado (ZPL para etiquetas térmicas, texto plano, tickets ESC/POS) para imprimir directo, sin diálogos de Windows.

## Para qué sirve

El sistema externo (ej: un backend de ventas) no sabe ni le importa qué impresoras tiene cada PC. Flujo:

1. Backend hace `GET /printers` → PrintAgent le devuelve la lista de impresoras instaladas en esa PC + cuál está preferida.
2. Backend (o el operador vía `POST /config`) elige/fija la impresora preferida para esa PC.
3. Backend pre-renderiza el contenido (ZPL de una etiqueta, texto de un ticket) y lo manda a `POST /print_zpl` o `POST /print_text`.
4. PrintAgent encola el trabajo y lo imprime en la impresora preferida, en RAW, sin pasar por el spooler visual de Windows.

Cada PC corre su propia instancia de PrintAgent, escuchando en localhost (o en su IP de LAN si se configura así), y el backend le pega a la IP:puerto correspondiente de cada PC.

## Stack / herramientas

- **.NET 8** (`net8.0-windows`), proyecto `Microsoft.NET.Sdk.WindowsDesktop`.
- **WinForms habilitado** (`UseWindowsForms=true`) — no hay ventanas de UI hoy, se usa solo por las APIs de `System.Drawing.Printing`/GDI+ (`PrintDocument`, `Image`, `Bitmap`, `Font`) para renderizar e imprimir.
- **`System.Net.HttpListener`** — servidor HTTP nativo, sin ASP.NET Core ni Kestrel.
- **Win32 `winspool.drv` vía P/Invoke** (`RawPrinterHelper.cs`) — impresión RAW real (`OpenPrinter`/`StartDocPrinter`/`WritePrinter`/...), manda bytes ZPL/ESC-POS directo a la cola de impresión de Windows con `DataType="RAW"`, sin que Windows intente interpretarlos.
- **`System.Text.Json`** — único paquete NuGet explícito, para (de)serializar los bodies JSON.
- Config propia hecha a mano: parser `.env`-like (`Config.cs`) + un JSON de config en `%ProgramData%\PrintAgent\config.json` (`PrinterManager.cs`). No usa `Microsoft.Extensions.Configuration` ni `appsettings.json`.

## Arquitectura / flujo interno

```
Program.cs (Main)
  ├─ PrinterManager   (enumera impresoras, guarda "impresora preferida")
  ├─ BlockingCollection<PrintJob>  (cola en memoria)
  ├─ PrintWorker (Task.Run)  → consume la cola, imprime Text/Zpl vía PrinterManager
  └─ HttpServer.StartAsync() → HttpListener, loop while(true) aceptando requests

HttpServer.Handle(ctx) despacha por ruta:
  GET  /printers      → PrinterManager.GetInstalledPrinters() / GetDefaultPrinter() / GetPreferredPrinter()
  POST /config         → PrinterManager.SetPreferredPrinter(nombre)
  POST /print          } alias de /print_text
  POST /print_text      → SendRawText(texto) → RawPrinterHelper.SendBytesToPrinter (impresión inmediata, no pasa por la cola)
  POST /print_zpl      → arma ZPL (barcode EAN-8, layout fijo 203dpi) → encola PrintJob(Zpl) → lo imprime PrintWorker
```

La impresión efectiva baja siempre a `RawPrinterHelper.SendBytesToPrinter(printerName, bytes)`, que abre la impresora de Windows por nombre y manda los bytes crudos (RAW) directo al spooler.

## Config del servidor (host/puerto)

`ServerConfig.Load()` (`Config.cs`) resuelve el prefix HTTP con esta prioridad:

1. Env var `PRINTAGENT_PREFIX` (prefix completo, ej `http://+:7000/`)
2. Env vars `PRINTAGENT_HOST` / `PRINTAGENT_PORT` / `PRINTAGENT_LISTEN_ALL`
3. Archivo `printagent.env` (al lado del exe, o ruta via `PRINTAGENT_ENV_PATH`)
4. Default: `host=localhost`, `port=5000`, `listenAll=false`

Si `printagent.env` no existe, se autogenera un template al arrancar. Si `LISTEN_ALL=true`, escucha en toda la LAN (`http://+:{port}/`) — en Windows puede requerir `netsh http add urlacl url=http://+:{port}/ user=Everyone`.

## Config de impresora preferida

`PrinterManager` persiste `preferredPrinter` en `%ProgramData%\PrintAgent\config.json`. Se setea con `POST /config {"printer": "NombreImpresora"}`, validando que el nombre exista en `PrinterSettings.InstalledPrinters`.

## Endpoints

### `GET /printers`
```
curl http://localhost:5000/printers
```
Devuelve `{ defaultPrinter, preferredPrinter, printers: [...] }`.

### `POST /config`
Fija la impresora preferida.
```bash
curl -X POST http://localhost:5000/config \
     -H "Content-Type: application/json" \
     -d "{\"printer\":\"POS-80\"}"
```
```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://localhost:5000/config" `
  -Body '{"printer":"POS-80"}' `
  -ContentType "application/json"
```

### `POST /print` o `POST /print_text`
Imprime texto crudo + corte ESC/POS.
```bash
curl -X POST http://localhost:5000/print_text \
     -H "Content-Type: application/json" \
     -d "{\"text\":\"TEST OK\"}"
```

### `POST /print_zpl`
Imprime etiquetas ZPL (barcode EAN-8, 203dpi, layout fijo pensado para banderas de 15x25mm).
```bash
curl -X POST http://localhost:5000/print_zpl \
     -H "Content-Type: application/json" \
     -d "{\"valores\":[{\"codigo_barra\":\"12345678\"}]}"
```

## Código presente pero no conectado (hallazgo, sin resolver todavía)

En `HttpServer.cs` existen tres métodos completos que **no están en el dispatcher de `Handle`** y hoy no son alcanzables por ninguna ruta HTTP:

- `HandlePrintWithQR` — imprime texto + imagen QR (ESC/POS bitmap).
- `HandlePrintTicket` — arma un ticket completo en ESC/POS (header, items, totales, QR, footer) a puro texto/bytes.
- `HandlePrintTicketGdi` — el más completo: renderiza el mismo tipo de ticket pero vía GDI+ (`PrintDocument`), con logo, QR, alineación de columnas, fuente mono, y dispara el corte ESC/POS al terminar (`SendCutCommand`).

Son candidatos naturales para un futuro `POST /print_ticket`, pero quedan documentados como "código muerto" hasta que se decida conectarlos o eliminarlos — no se tocan en este cambio.

## Roadmap (en progreso)

- GUI de configuración (tray icon, sin ventana de consola) para elegir host/IP/puerto e impresora preferida sin editar `printagent.env` a mano.
- Auto-arranque al iniciar Windows (shortcut en carpeta Startup del usuario).

## Instalación / build

```bash
dotnet build
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
El `.csproj` ya está configurado para publicar single-file self-contained (`PrintAgent.csproj`).
