using System;
using System.Drawing;
using System.Windows.Forms;

namespace PrintAgent
{
    /// <summary>
    /// Shell del proceso: icono de bandeja con estado dinamico (verde=activo,
    /// rojo=detenido, amarillo=error) + menu (estado, iniciar/detener/reiniciar,
    /// ver logs, iniciar con Windows, configuracion, salir).
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

        private readonly Icon _iconRunning;
        private readonly Icon _iconStopped;
        private readonly Icon _iconError;
        private bool _lastActionFailed;

        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _startStopItem;
        private readonly ToolStripMenuItem _restartItem;
        private readonly ToolStripMenuItem _startupItem;

        public TrayApplicationContext(HttpServer server, PrinterManager manager, Logger logger)
        {
            _server = server;
            _manager = manager;
            _logger = logger;

            _iconRunning = TrayIconFactory.Build(Color.LimeGreen);
            _iconStopped = TrayIconFactory.Build(Color.Red);
            _iconError = TrayIconFactory.Build(Color.Gold);

            _statusItem = new ToolStripMenuItem("Estado") { Enabled = false };
            _startStopItem = new ToolStripMenuItem("Detener servidor", null, (s, e) => ToggleServer());
            _restartItem = new ToolStripMenuItem("Reiniciar servidor", null, (s, e) => RestartServer());
            _startupItem = new ToolStripMenuItem("Iniciar con Windows", null, (s, e) => ToggleStartup());

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Configuración...", null, (s, e) => OpenSettings());
            menu.Items.Add(_startStopItem);
            menu.Items.Add(_restartItem);
            menu.Items.Add("Ver logs", null, (s, e) => OpenLogs());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Salir", null, (s, e) => ExitApplication());
            menu.Opening += (s, e) => RefreshMenuItems();

            _trayIcon = new NotifyIcon
            {
                Icon = _iconStopped,
                Text = "PrintAgent",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => OpenSettings();

            RefreshTrayVisual();
        }

        private void RefreshMenuItems()
        {
            bool running = _server.IsRunning;
            _statusItem.Text = running
                ? $"● Activo — {_server.Prefix}"
                : "● Detenido";
            _startStopItem.Text = running ? "Detener servidor" : "Iniciar servidor";
            _startupItem.Checked = StartupHelper.IsEnabled();
            RefreshTrayVisual();
        }

        private void RefreshTrayVisual()
        {
            bool running = _server.IsRunning;
            _trayIcon.Icon = _lastActionFailed ? _iconError : (running ? _iconRunning : _iconStopped);

            string status = running ? $"Activo ({_server.Prefix})" : "Detenido";
            string text = $"PrintAgent — {status}";
            _trayIcon.Text = text.Length > 63 ? text.Substring(0, 60) + "..." : text;
        }

        private async void ToggleServer()
        {
            try
            {
                if (_server.IsRunning)
                {
                    _server.Stop();
                }
                else
                {
                    await _server.RestartAsync(_server.Prefix);
                }
                _lastActionFailed = false;
            }
            catch (Exception ex)
            {
                _lastActionFailed = true;
                _logger.Error("Error al cambiar estado del server desde bandeja: " + ex.Message);
                _trayIcon.ShowBalloonTip(4000, "PrintAgent", "Error: " + ex.Message, ToolTipIcon.Error);
            }
            RefreshTrayVisual();
        }

        private async void RestartServer()
        {
            try
            {
                await _server.RestartAsync(_server.Prefix);
                _lastActionFailed = false;
                _trayIcon.ShowBalloonTip(3000, "PrintAgent", "Servidor reiniciado.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _lastActionFailed = true;
                _logger.Error("Error al reiniciar server desde bandeja: " + ex.Message);
                _trayIcon.ShowBalloonTip(4000, "PrintAgent", "Error al reiniciar: " + ex.Message, ToolTipIcon.Error);
            }
            RefreshTrayVisual();
        }

        private void ToggleStartup()
        {
            if (StartupHelper.IsEnabled())
                StartupHelper.Disable();
            else
                StartupHelper.Enable();
        }

        private void OpenLogs() => _logger.OpenLogFile();

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
            _iconRunning.Dispose();
            _iconStopped.Dispose();
            _iconError.Dispose();
            Application.Exit();
        }
    }
}
