# PrintAgent HTTP API

## GET /printers
Returns information about default printer, preferred printer, and list of installed printers.

## POST /config
Updates server configuration (host, port, listen_all flag) and restarts the HTTP server.

## POST /print_text
Prints raw text using the preferred printer.

## POST /print_zpl
Prints ZPL commands received in the JSON payload under the "valores" key.

> Note: `/print` and `/print_text` are treated identically in the handler.