using System;
using System.Windows.Forms;

namespace PrintAgent
{
    /// <summary>
    /// Shell del proceso: icono de bandeja + menu (Configuracion/Salir).
    /// No muestra ninguna ventana al iniciar; HttpServer y PrintWorker
    /// ya corren en background tasks lanzados antes de Application.Run.
    /// </summary>
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly HttpServer _server;
        private readonly PrinterManager _manager;
        private readonly Logger _logger;
        private SettingsForm? _settingsForm;

        public TrayApplicationContext(HttpServer server, PrinterManager manager, Logger logger)
        {
            _server = server;
            _manager = manager;
            _logger = logger;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Configuración", null, (s, e) => OpenSettings());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Salir", null, (s, e) => ExitApplication());

            _trayIcon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "PrintAgent",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => OpenSettings();
        }

        private void OpenSettings()
        {
            if (_settingsForm == null || _settingsForm.IsDisposed)
                _settingsForm = new SettingsForm(_server, _manager, _logger);

            if (!_settingsForm.Visible)
                _settingsForm.Show();
            _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Activate();
        }

        private void ExitApplication()
        {
            _trayIcon.Visible = false;
            try { _server.Stop(); } catch (Exception ex) { _logger.Error("Error al detener server: " + ex.Message); }
            Application.Exit();
        }
    }
}
