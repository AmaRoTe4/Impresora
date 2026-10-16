# Integración PrintAgent ↔ TiendaWeb (FrontEnd)

Cruce entre este agente y el frontend `~/TiendaWeb/App/FrontEnd` (Vite + React). Sirve para saber si lo que el frontend manda es lo que el agente espera, y qué cosas hay que vigilar.

- Fecha del cruce: 2026-10-15.
- Método: lectura de código de ambos lados. **No se imprimió nada ni se ejecutó el agente**; los puntos marcados como *inferido* no están probados en una impresora real.
- Referencia de la API del agente: [`endpoint_documentation.md`](endpoint_documentation.md).

## Veredicto

**Los endpoints están coordinados**: todas las rutas que usa el frontend existen en el agente actual, con los campos que el agente lee. Las plantillas ZPL de "bandera" son idénticas en ambos lados. Hay cinco diferencias a conocer (sección *Desajustes*); ninguna rompe el flujo hoy, pero dos pueden dar resultados engañosos.

## Dónde vive cada cosa en el frontend

Rutas relativas a `FrontEnd/src/`.

| Archivo | Rol |
|---|---|
| `api/impresora_local.ts` | Cliente HTTP del agente: `getPrinters`, `imprimir_local`, `printZplGeneric`, `imprimir_etiqueta_local` |
| `const/zpl.ts` | Plantillas ZPL (`ZPL_TEMPLATES`) y `toEan8`; comentario: *convertidas de C# `ZplTemplates`* |
| `components/Productos/Etiquetas/CodeBarra.tsx` | Pantalla de etiquetas: arma el ZPL y llama al agente (`onSubmit`, líneas ~504-640) |
| `components/Config/General/ModalImpresionCompleta.tsx` | Lista impresoras con `getPrinters` y guarda `impresora_name_ticket` / `impresora_name_etiquetas` |
| `components/Config/General/sections/ImpresionesSection.tsx` | Configura `tipo_impresion_modo`, plantilla por defecto y `url_impresora` |
| `const/cc/bases.ts` | Default `url_impresora: "http://localhost:5000/"` |
| `functions/normalizaciones/etiqueta_termicas.ts` | `generarZPLDesdeValores`: **no se importa en ningún lado** (código sin uso) |

Tickets y recibos (`Totales.tsx`, `ResumenPayments.tsx`, `useViewVenta.tsx`, `useVentaNow.tsx`, `useViewDevolucion.tsx`, `AddPago.tsx`, `PerfilCliente/Cards`, `imprimirTicketTrabajo.ts`) usan solo `imprimir_local` → `POST /print_text`.

## Mapa de llamadas

| Frontend | Agente | Body que manda el frontend | Lo que lee el agente |
|---|---|---|---|
| `getPrinters(baseUrl)` | `GET /printers` | — | Responde `installed`; el front usa solo `installed` |
| `imprimir_local({text, printer})` | `POST /print_text` | `{text, printer?}` | `text`, `printer` ✔ |
| `printZplGeneric({zpl, zpl_list, printer})` | `POST /print_zpl_generic` | `{zpl, zpl_list?, printer?}` | `zpl`, `zpl_list`, `printer` ✔ |
| `imprimir_etiqueta_local({valores})` | `POST /print_zpl` | `{valores:[{codigo_barra, nombre, precio?, use_precio?}], printer?}` | Solo `valores[].codigo_barra` y `printer` |

El frontend **no** usa `/`, `/config`, `/print`, `/admin` ni ninguno de los endpoints eliminados (`/print_zpl_raw`, `/print_qr`, `/print/ticket`, `/print_ticket_gdi`, `/info`, `/health`, `/print-test-1`). Sacar o no reconectar esos handlers no afecta a TiendaWeb.

### Cómo decide qué endpoint usar (`CodeBarra.tsx`)

Depende de `getConfig.tipo_impresion_modo`:

- **`"completa"` + modo "impresora"** → arma ZPL en el navegador con la plantilla elegida (o ZPL personalizado) y llama a `printZplGeneric`. Impresora: `impresora_name_etiquetas`. Con plantilla manda `zpl: "ignored but required"` más `zpl_list`; con ZPL personalizado manda solo `zpl`.
- **`"original"`** (default de `bases.ts`) → `imprimir_etiqueta_local({ valores })` → `/print_zpl`, **sin** `printer`: el agente usa su impresora preferida.
- **Modo "pdf"** → no toca el agente.

Para tickets, `printer` se envía solo si `tipo_impresion_modo === "completa"` (`impresora_name_ticket`); en `"original"` se omite y el agente usa su preferida.

## Plantillas ZPL: cuáles coinciden

| Plantilla del front (`ZPL_TEMPLATES`) | Agente | Coordinada |
|---|---|---|
| `label_joyeria_bandera` (`zplJewelryLabelFlag`) | `/print_zpl` genera la misma etiqueta | **Sí**: mismos valores (`PW240 LL560`, `^CI28`, `^BY2,2,34`, barras `(66,376)`, texto `(46,376)` `^A0B,22,18`) y mismo `toEan8` (últimos 6 dígitos + `0` + verificador). Cambia solo el formato del string (el front lo escribe multilínea; el agente compacto), equivalente para la impresora |
| `label_joyeria_bandera_precio`, `label_38x20_*`, `label_50x25`, `barcode_vertical` | No existen en el agente | Solo viajan por `/print_zpl_generic`, que no interpreta nada. El agente no valida su contenido |

Consecuencia: dos caminos distintos pueden producir la **misma** etiqueta de bandera (modo original vía `/print_zpl`, o modo completa con la plantilla `label_joyeria_bandera`). Si se cambia el layout de uno hay que cambiar el otro, porque no comparten código (C# vs TypeScript).

## Desajustes

### 1. Modo "original": el agente ignora `nombre` y `precio` *(engañoso)*
El front arma `valores` con nombre, precio y `use_precio`, pero `/print_zpl` solo usa `codigo_barra` y siempre imprime la etiqueta bandera EAN-8. Un usuario que active `use_precio` en modo original no verá el precio. Además se descartan todos los dígitos salvo los últimos 6, así que códigos distintos con igual terminación dan etiquetas iguales.

### 2. `{status:"queued"}` no significa impreso *(engañoso)*
`/print_zpl` y `/print_zpl_generic` responden apenas encolan. El front muestra "Impresion realizada con exito!" si `res.status` es truthy. Si la impresora está apagada, sin papel o el nombre está mal en Windows, el error queda solo en el log del agente. `/print_text`, en cambio, imprime en la misma request y devuelve `500` si falla, aunque la mayoría de los llamadores del front no leen esa respuesta.

### 3. Acentos y `ñ` en las plantillas 38x20 y 50x25 *(inferido, sin probar en impresora)*
El agente envía el ZPL en UTF-8 y no agrega `^CI28`. Las plantillas bandera sí llevan `^CI28`, pero `zplLabel38x20*` y `zplLabel50x25` no. Si el nombre del producto tiene `ñ` o tildes, probablemente salgan caracteres mal impresos. Revisar con un producto real antes de dar por buena la plantilla.

### 4. Forma de `GET /printers`
`PrintersResponse` (`types.d.ts`) define `installed`, `systemDefault`, `defaultPrinter`, `exists`, que coincide con la versión histórica "ImpresoraInstalable". El agente actual no manda `systemDefault` ni `exists`, y su `defaultPrinter` significa "predeterminada de Windows" (antes era "la configurada en el agente"). Hoy no rompe nada porque el front solo lee `installed`, pero quien use `defaultPrinter` o `systemDefault` obtendría datos distintos de los esperados. El agente además manda `preferredPrinter` y `printers`, que el front no usa.

### 5. Placeholder `"ignored but required"`
Con plantilla, el front manda `zpl: "ignored but required"`. El agente lo descarta solo si `zpl_list` trae al menos un string no vacío. Si todos fueran vacíos, ese texto literal se enviaría a la impresora como ZPL. En la práctica no ocurre porque el front corta antes si `zplList` está vacío, pero conviene saberlo si se reutiliza `printZplGeneric`.

## Cosas del entorno a vigilar *(comportamiento general de navegadores, no probado acá)*

- **Mixed content**: el front se publica con Vercel (`vercel.json`), o sea HTTPS. Llamar a `http://localhost:5000/` suele estar permitido por los navegadores, pero apuntar `url_impresora` a una IP de LAN en `http://` (agente con `LISTEN_ALL=true`) lo bloquean. Para ese caso hace falta servir el front por HTTP o poner el agente detrás de HTTPS.
- **Acceso a red local**: los navegadores recientes pueden pedir permiso o un preflight específico para que un sitio público hable con `localhost`/LAN. El agente solo responde los encabezados CORS básicos (ver `endpoint_documentation.md`). Si una instalación nueva no imprime desde el navegador y `curl` sí funciona, empezar por acá.
- **Sin autenticación + CORS `*`**: cualquier página abierta en esa PC puede llamar al agente, incluido `POST /admin`.

## Cómo verificar un cambio en cualquiera de los dos lados

1. `curl http://localhost:5000/printers` → debe incluir el nombre exacto que se guardó en `impresora_name_etiquetas` / `impresora_name_ticket`.
2. Etiqueta de bandera por el agente:
   ```bash
   curl -X POST http://localhost:5000/print_zpl -H "Content-Type: application/json" \
        -d '{"valores":[{"codigo_barra":"123456"}],"printer":"<impresora>"}'
   ```
3. La misma etiqueta desde el front (modo completa, plantilla *Joyería / bandera*) y comparar a ojo ambas impresiones: deben coincidir.
4. Para el punto 3 de *Desajustes*: imprimir un producto con `ñ` y tilde en `38x20` y `50x25`.
5. Mirar el log del agente (menú de la bandeja → *Ver logs*) después de cada prueba: ahí aparecen los errores que la respuesta HTTP no informa.
