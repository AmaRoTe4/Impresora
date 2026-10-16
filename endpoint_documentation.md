# PrintAgent — API HTTP

Referencia de los endpoints **actuales**, tomada del código (`HttpServer.cs`, método `HandleCore` y `HandleAdmin`).
Para el cruce con el frontend de TiendaWeb ver [`INTEGRACION_TIENDAWEB.md`](INTEGRACION_TIENDAWEB.md).

## Generalidades

- **Servidor**: Kestrel (`WebApplication`), no `HttpListener`. Escucha en el prefix de `printagent.env` (default `http://localhost:5000/`).
- **CORS**: abierto. Responde `Access-Control-Allow-Origin: *`, `Allow-Methods: GET, POST, OPTIONS`, `Allow-Headers: Content-Type`. Cualquier `OPTIONS` devuelve `204`.
- **Cuerpos**: JSON UTF-8. Las respuestas son JSON salvo `GET /admin` (HTML).
- **Impresora destino** de los POST de impresión: campo opcional `printer` en el body. Si falta o está vacío, se usa la impresora preferida (`POST /config`) y, si no hay, la predeterminada de Windows. Si `printer` no está instalada → `400 {"error":"Impresora '...' no encontrada."}`.
- **Errores**: ruta desconocida → `404 {"error":"not-found"}`. Excepción no controlada → `500 {"error":"<mensaje>"}` (y queda en el log).
- No hay autenticación.

## Resumen

| Método | Ruta | Síncrono | Uso |
|---|---|---|---|
| GET | `/` | sí | Health check |
| GET | `/printers` | sí | Impresoras instaladas |
| POST | `/config` | sí | Fijar impresora preferida |
| POST | `/print`, `/print_text` | **sí** (imprime y recién responde) | Texto crudo + corte |
| POST | `/print_zpl` | no (encola) | Etiqueta fija EAN-8 "bandera" |
| POST | `/print_zpl_generic` | no (encola) | ZPL crudo, uno o varios |
| GET / POST | `/admin` | sí | Host y puerto del servidor |

"Encola" significa que la respuesta `{"status":"queued"}` confirma que el trabajo entró a la cola en memoria, **no** que se imprimió. Si después falla la impresión, el error solo queda en el log (`PrintWorker` en `Program.cs`).

## GET `/`

```json
{ "status": "ok" }
```

## GET `/printers`

```json
{
  "defaultPrinter": "Microsoft Print to PDF",
  "preferredPrinter": "POS-80",
  "printers": ["POS-80", "Zebra ZPL", "Microsoft Print to PDF"],
  "installed": ["POS-80", "Zebra ZPL", "Microsoft Print to PDF"]
}
```

- `defaultPrinter`: impresora predeterminada **de Windows**.
- `preferredPrinter`: la guardada con `POST /config`; si no hay, es igual a `defaultPrinter`.
- `printers` e `installed` tienen el mismo contenido (`installed` es un alias).

## POST `/config`

Fija la impresora preferida y la persiste en `%ProgramData%\PrintAgent\config.json`.

```json
{ "printer": "POS-80" }
```

Respuesta `200 {"status":"ok"}`. Si el nombre no está instalado, el agente lanza una excepción y responde `500 {"error":"Printer '...' no encontrada."}`. Si el body no trae `printer`, responde `ok` sin cambiar nada.

> No cambia host ni puerto; eso es `/admin`.

## POST `/print` y POST `/print_text`

Alias idénticos. Manda el texto en bytes UTF-8 por RAW, seguido de `\n\n` y el corte ESC/POS `1D 56 41 00`.

```json
{ "text": "TEST OK", "printer": "POS-80" }
```

- `200 {"status":"printed_with_cut"}`
- `400 {"error":"Falta campo 'text'"}`
- `500 {"error":"Error al imprimir: ..."}` — este endpoint sí informa el fallo, porque imprime en la misma request.

## POST `/print_zpl`

Genera una etiqueta **fija** y la encola. Es la plantilla de joyería "bandera": EAN-8 vertical, 203 dpi, `^PW240` `^LL560`, `^CI28`, `^BY2,2,34`, barras en `(66,376)`.

```json
{
  "valores": [
    { "codigo_barra": "123456" },
    { "codigo_barra": "7790001234567" }
  ],
  "printer": "Zebra ZPL"
}
```

- Solo se lee `codigo_barra`. **Cualquier otro campo (`nombre`, `precio`, `use_precio`) se ignora.**
- El valor se convierte a EAN-8: se quedan solo los dígitos, se toman los **últimos 6** (si hay menos, se rellena con ceros a la izquierda), se antepone `0` y se calcula el dígito verificador. Dos códigos que terminen en los mismos 6 dígitos producen la misma etiqueta.
- Se genera un bloque `^XA…^XZ` por elemento, y todo va en un único trabajo.
- `200 {"status":"queued","count":N}`
- `400 {"error":"Falta arreglo 'valores' con codigo_barra"}` (falta, no es arreglo o está vacío)

## POST `/print_zpl_generic`

Encola ZPL ya armado por el cliente. No interpreta ni modifica el ZPL.

```json
{ "zpl": "^XA...^XZ", "printer": "Zebra ZPL" }
```

o varias etiquetas:

```json
{ "zpl": "", "zpl_list": ["^XA...^XZ", "^XA...^XZ"], "printer": "Zebra ZPL" }
```

- Si viene `zpl_list` (arreglo no vacío), se **concatenan** sus strings no vacíos (sin separador) y ese resultado **reemplaza** a `zpl`. Si todos los elementos están vacíos o no son string, se conserva `zpl`.
- Antes de enviar se agrega `\r\n` final si falta, y se codifica en **UTF-8**. El agente no agrega `^CI28`: si el ZPL incluye texto con acentos o `ñ`, la plantilla debe traer `^CI28` o saldrán mal.
- `200 {"status":"queued","printer":<printer|null>}`
- `400 {"error":"Falta campo 'zpl' (o 'zpl_list' vacío)"}`

## GET `/admin` y POST `/admin`

Configuración de host y puerto.

- **GET**: con `Accept: application/json` o `?format=json` devuelve `{"host","port","prefix"}`; si no, un formulario HTML.
- **POST**: guarda la config (`ServerConfig.Save`, con `listenAll=false`) y reinicia el servidor 200 ms después de responder, con rollback al prefix anterior si el nuevo no levanta.
  - `Content-Type: application/json` con `{"host":"...","port":5000}` → `200 {"status":"ok","message":"Config saved. Restarting...","prefix":"..."}` (si pide JSON) o `302` a `/admin?saved=1` (si no).
  - Formulario `application/x-www-form-urlencoded` con `host` y `port` (un puerto inválido pasa a 5000).
  - Cualquier otro método → `405 {"error":"method-not-allowed"}`.

Al ser un POST sin autenticación y con CORS abierto, cualquier origen que alcance el agente puede cambiarle el host y el puerto.

## Endpoints que existieron y ya no están

Reconstruido desde los zips versionados en el repo `Tools` (la carpeta `Impresora-Windows` no tiene historia propia en git).

| Ruta | Estaba en | Estado |
|---|---|---|
| `/print_zpl_raw`, `/print_qr`, `/print/ticket`, `/print_ticket_gdi` | `Impresora.zip` (`9d55ef8`, 2026-01-26) | Eliminados del dispatcher. Los handlers `HandlePrintWithQR`, `HandlePrintTicket` y `HandlePrintTicketGdi` siguen en `HttpServer.cs` sin ninguna ruta que los llame |
| `/info`, `/health`, `/print-test-1` | versión "ImpresoraInstalable" (`f54500d` y `087de8d`) | No existen en el agente actual. `/` cubre solo la parte de "está vivo" |

Esa versión "instalable" respondía `/printers` con `systemDefault`, `defaultPrinter` y `exists`; el agente actual devuelve otro formato (ver arriba).
