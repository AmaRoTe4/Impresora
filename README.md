
# PrintAgent (versión refinada)

Microservicio de impresión local **sin dependencias externas**.

## Novedades vs versión inicial
* `[STAThread]` para drivers GDI+.
* **Cola interna** (`BlockingCollection`) que serializa impresiones ⇒ seguro bajo carga.
* **Persistencia** de impresora preferida en `%PROGRAMDATA%\PrintAgent\config.json`.
* **Logging rotativo** en `%PROGRAMDATA%\PrintAgent\agent.log`.
* **UI web** en `http://localhost:5000/` (static `wwwroot/index.html`).
* *README* y *Inno Setup* de instalación 1‑clic.

## Compilación (Windows o GitHub Actions)

```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## Instalación rápida

1. Ejecutar `Setup.exe` (generado con Inno Setup) — ocupa 20 s.
2. Instalador:
   * Copia `PrintAgent.exe` a `C:\Program Files\PrintAgent\`
   * Registra URL ACL (`netsh http add urlacl ...`)
   * Crea clave **Run** para arranque al login
   * Abre UI web para prueba inicial

## API

Igual que antes (`/printers`, `/config`, `/print`) + UI.

---
