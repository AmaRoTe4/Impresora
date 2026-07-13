# PrintAgent HTTP API

## GET /
Health check. Returns `{"status":"ok"}` with status code 200.

## GET /admin
Admin configuration page.

- **Accept: application/json** or **?format=json** → returns JSON with current `host`, `port`, `prefix`.
- Otherwise → returns an HTML form to edit host/port.

## POST /admin
Save new host/port config and restart the server.

- **Content-Type: application/json** → body: `{"host":"...","port":...}` returns JSON `{"status":"ok","message":"...","prefix":"..."}`.
- **Content-Type: application/x-www-form-urlencoded** → form fields `host`, `port`, redirects to `/admin?saved=1`.

The server restarts asynchronously after the response is sent.

## GET /printers
Returns printer info:

```json
{
  "defaultPrinter": "...",
  "preferredPrinter": "...",
  "printers": ["...", "..."],
  "installed": ["...", "..."]
}
```

## POST /config
Updates server configuration (host, port, listen_all flag) and restarts the HTTP server.

## POST /print_text
Prints raw text using the preferred printer.

## POST /print_zpl
Prints ZPL commands received in the JSON payload under the `"valores"` key.

> Note: `/print` and `/print_text` are treated identically in the handler.
