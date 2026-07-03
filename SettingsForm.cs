using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrintAgent
{
    /// <summary>
    /// Ventana de configuracion abierta desde el tray icon. Permite elegir
    /// host (localhost / IP de LAN detectada / IP personalizada), puerto,
    /// impresora preferida y si arranca con Windows. Al guardar reescribe
    /// printagent.env (ServerConfig.Save) y reinicia el HttpListener en caliente
    /// (HttpServer.RestartAsync), sin reiniciar el proceso.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const string CustomOption = "Personalizada...";

        private readonly HttpServer _server;
        private readonly PrinterManager _manager;
        private readonly Logger _logger;

        private readonly ComboBox _hostCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _customHostBox = new();
        private readonly NumericUpDown _portInput = new() { Minimum = 1, Maximum = 65535, Width = 100 };
        private readonly ComboBox _printerCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _startWithWindowsCheck = new() { Text = "Iniciar con Windows" };
        private readonly Label _statusLabel = new() { AutoSize = true, ForeColor = System.Drawing.Color.DarkGreen };

        public SettingsForm(HttpServer server, PrinterManager manager, Logger logger)
        {
            _server = server;
            _manager = manager;
            _logger = logger;

            Text = "PrintAgent - Configuración";
            Width = 420;
            Height = 320;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            // No destruir el form al cerrar la ventana (X): solo ocultar,
            // asi el proceso sigue en bandeja sin la ventana molestando.
            FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };

            BuildLayout();
            LoadCurrentValues();
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 2,
                AutoSize = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));

            layout.Controls.Add(new Label { Text = "Host:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(_hostCombo, 1, 0);

            layout.Controls.Add(new Label { Text = "IP personalizada:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            layout.Controls.Add(_customHostBox, 1, 1);

            layout.Controls.Add(new Label { Text = "Puerto:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            layout.Controls.Add(_portInput, 1, 2);

            layout.Controls.Add(new Label { Text = "Impresora preferida:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
            layout.Controls.Add(_printerCombo, 1, 3);

            layout.Controls.Add(new Label(), 0, 4);
            layout.Controls.Add(_startWithWindowsCheck, 1, 4);

            var saveButton = new Button { Text = "Guardar", Width = 100 };
            saveButton.Click += async (s, e) => await Save();
            layout.Controls.Add(new Label(), 0, 5);
            layout.Controls.Add(saveButton, 1, 5);

            layout.Controls.Add(new Label(), 0, 6);
            layout.Controls.Add(_statusLabel, 1, 6);

            Controls.Add(layout);

            _hostCombo.SelectedIndexChanged += (s, e) =>
                _customHostBox.Enabled = (string)_hostCombo.SelectedItem! == CustomOption;
        }

        private void LoadCurrentValues()
        {
            var lanIps = GetLanIps().ToList();

            _hostCombo.Items.Clear();
            _hostCombo.Items.Add("localhost");
            foreach (var ip in lanIps)
                _hostCombo.Items.Add(ip);
            _hostCombo.Items.Add(CustomOption);

            var currentHost = _server.Prefix
                .Replace("http://", "").Replace("https://", "")
                .Split(':')[0];

            if (currentHost == "+" ) currentHost = lanIps.FirstOrDefault() ?? "localhost";

            if (_hostCombo.Items.Contains(currentHost))
            {
                _hostCombo.SelectedItem = currentHost;
                _customHostBox.Enabled = false;
            }
            else
            {
                _hostCombo.SelectedItem = CustomOption;
                _customHostBox.Text = currentHost;
                _customHostBox.Enabled = true;
            }

            var portPart = _server.Prefix.TrimEnd('/').Split(':').LastOrDefault();
            _portInput.Value = int.TryParse(portPart, out var p) && p > 0 && p < 65536 ? p : 5000;

            _printerCombo.Items.Clear();
            foreach (var printer in _manager.GetInstalledPrinters())
                _printerCombo.Items.Add(printer);

            var preferred = _manager.GetPreferredPrinter();
            if (_printerCombo.Items.Contains(preferred))
                _printerCombo.SelectedItem = preferred;
            else if (_printerCombo.Items.Count > 0)
                _printerCombo.SelectedIndex = 0;

            _startWithWindowsCheck.Checked = StartupHelper.IsEnabled();
        }

        private static IEnumerable<string> GetLanIps()
        {
            try
            {
                return Dns.GetHostEntry(Dns.GetHostName())
                    .AddressList
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                    .Select(ip => ip.ToString())
                    .ToList();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private async Task Save()
        {
            string host = (string)_hostCombo.SelectedItem! == CustomOption
                ? _customHostBox.Text.Trim()
                : (string)_hostCombo.SelectedItem!;

            if (string.IsNullOrWhiteSpace(host))
            {
                _statusLabel.ForeColor = System.Drawing.Color.DarkRed;
                _statusLabel.Text = "Ingresá una IP válida.";
                return;
            }

            int port = (int)_portInput.Value;

            try
            {
                if (_printerCombo.SelectedItem is string printerName)
                    _manager.SetPreferredPrinter(printerName);

                var newConfig = ServerConfig.Save(host, port, listenAll: false, _logger);
                await _server.RestartAsync(newConfig.Prefix!);

                if (_startWithWindowsCheck.Checked)
                    StartupHelper.Enable();
                else
                    StartupHelper.Disable();

                _statusLabel.ForeColor = System.Drawing.Color.DarkGreen;
                _statusLabel.Text = $"Guardado. Escuchando en {newConfig.Prefix}";
            }
            catch (Exception ex)
            {
                _logger.Error("Error guardando config desde GUI: " + ex.Message);
                _statusLabel.ForeColor = System.Drawing.Color.DarkRed;
                _statusLabel.Text = "Error: " + ex.Message;
            }
        }
    }
}
