using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.IO;
using My.JDownloader.Api;
using System.Linq;
using My.JDownloader.Api.Models.Devices;
using My.JDownloader.Api.Models.LinkgrabberV2.Request;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClickNLoad2MyJD
{
    static class Program
    {
        private static HttpListener Listener;
        private static DeviceHandler Jdownloader;
        private static TrayApplicationContext AppContext;
        private static LogForm LogWindow;
        private static bool shuttingDown;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LogWindow = new LogForm();
            AppContext = new TrayApplicationContext();

            Log("========================================");
            Log("Click'N'Load 2 MyJDownloader");
            Log("========================================");

            if (!InitializeJdownloaderApi())
            {
                Log("JDownloader initialization failed. Links will only be printed in the log.");
                AppContext.ShowBalloon("Warning", "JDownloader initialization failed. Links will only be printed in the log.", ToolTipIcon.Warning);
            }

            StartListener();

            Application.Run(AppContext);
        }

        private static void StartListener()
        {
            Listener = new HttpListener();
            Listener.Prefixes.Add("http://127.0.0.1:9666/");

            try
            {
                Listener.Start();
                Log("Listening for Click'N'Load requests on http://127.0.0.1:9666/");

                Task.Run(async () =>
                {
                    while (!shuttingDown && Listener != null && Listener.IsListening)
                    {
                        try
                        {
                            HttpListenerContext context = await Listener.GetContextAsync();
                            _ = Task.Run(() => ProcessRequestSafe(context));
                        }
                        catch (HttpListenerException)
                        {
                            if (!shuttingDown)
                                Log("HttpListener stopped unexpectedly.");
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log("Listener error: " + ex.Message);
                        }
                    }
                });
            }
            catch (HttpListenerException)
            {
                Log("Seems like another application is already using port 9666. Please close it first.");
                MessageBox.Show(
                    "Port 9666 is already in use.\n\nPlease close the other application using this port.",
                    "Click'N'Load 2 MyJD",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                Log("Could not start HTTP listener: " + ex.Message);
                MessageBox.Show(ex.Message, "Click'N'Load 2 MyJD", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static bool InitializeJdownloaderApi()
        {
            try
            {
                var credentials = GetOrPromptCredentials();

                if (!credentials.HasValue)
                {
                    Log("No credentials provided.");
                    return false;
                }

                Log("Try connecting to your MyJDownloader account...");
                var jDownloaderHandler = new JDownloaderHandler(credentials.Value.Mail, credentials.Value.Password, "ClickNListen");

                if (!jDownloaderHandler.IsConnected)
                {
                    Log("Connection to MyJDownloader API failed.");

                    DialogResult result = MessageBox.Show(
                        "Connection to MyJDownloader API failed.\n\nDo you want to reenter your MyJDownloader account credentials?",
                        "MyJDownloader",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                        AppContext.ShowBalloon("Error", "Connection to MyJDownloader API failed.", ToolTipIcon.Error);

                    if (result == DialogResult.Yes)
                    {
                        Config.DeleteConfiguration();
                        return InitializeJdownloaderApi();
                    }

                    return false;
                }

                var devices = jDownloaderHandler.GetDevices().ToArray();

                if (devices.Length == 0)
                {
                    Log("Found 0 devices connected to your MyJDownloader account.");
                    return false;
                }
                else if (devices.Length > 1)
                {
                    Log("Found more than one device connected to your MyJDownloader account.");

                    for (int i = 0; i < devices.Length; i++)
                        Log($"{i + 1} - {devices[i]}");

                    int deviceNumber = SelectDevice(devices);

                    if (deviceNumber < 0)
                    {
                        Log("Device selection cancelled.");
                        return false;
                    }

                    Jdownloader = jDownloaderHandler.GetDeviceHandler(devices[deviceNumber]);
                }
                else
                {
                    var device = devices.First();
                    Log("Found one device connected to your MyJDownloader account.");
                    Jdownloader = jDownloaderHandler.GetDeviceHandler(device);
                }

                Log($"Successfully connected to {Jdownloader.Jd.Device.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Log("JDownloader initialization error: " + ex.Message);
                return false;
            }
        }

        private static (string Mail, string Password)? GetOrPromptCredentials()
        {
            // 1. Versuche vorhandene Zugangsdaten zu laden
            var creds = Config.GetCredentials();
            if (creds.HasValue)
            {
                Log("Loaded MyJDownloader credentials from configuration.");
                return creds.Value;
            }

            // 2. Falls keine Daten existieren -> WinForms Dialog öffnen
            Log("No configuration found. Requesting user input...");
            using (var loginForm = new LoginForm())
            {
                if (loginForm.ShowDialog() == DialogResult.OK)
                {
                    if (!string.IsNullOrWhiteSpace(loginForm.Email) && !string.IsNullOrWhiteSpace(loginForm.Password))
                    {
                        Config.SaveCredentials(loginForm.Email, loginForm.Password);
                        Log("Credentials successfully saved.");
                        return (loginForm.Email, loginForm.Password);
                    }
                }
            }

            return null;
        }

        private static int SelectDevice(dynamic[] devices)
        {
            using (var form = new DeviceSelectionForm(devices))
            {
                DialogResult result = form.ShowDialog();
                return result == DialogResult.OK ? form.SelectedIndex : -1;
            }
        }

        private static void ProcessRequestSafe(HttpListenerContext context)
        {
            try
            {
                ProcessRequest(context);
            }
            catch (Exception ex)
            {
                Log("Request processing error: " + ex);

                try
                {
                    context.Response.StatusCode = 500;
                    byte[] buffer = Encoding.UTF8.GetBytes("ERROR");
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    context.Response.Close();
                }
                catch
                {
                    // Ignore errors while sending an error response.
                }
            }
        }

        private static void ProcessRequest(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            string responseString = "";

            // CORS
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS, PATCH, HEAD";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Referer, X-Requested-With, Accept, Origin";

            if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 200;
                response.StatusDescription = "OK";
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";

            Log("");
            Log("----------------------------------------");
            Log($"HTTP {request.HttpMethod} {request.RawUrl}");

            if (request.RawUrl == "/crossdomain.xml")
            {
                responseString = "<?xml version=\"1.0\"?>"
                    + "<!DOCTYPE cross-domain-policy SYSTEM \"http://www.macromedia.com/xml/dtds/cross-domain-policy.dtd\">"
                    + "<cross-domain-policy>"
                    + "<allow-access-from domain=\"*\" />"
                    + "</cross-domain-policy>";
            }
            else if (request.RawUrl == "/jdcheck.js")
            {
                responseString = "jdownloader=true; var version='18507';";
            }
            else if (request.RawUrl.StartsWith("/flash", StringComparison.OrdinalIgnoreCase))
            {
                if (request.RawUrl.IndexOf("add", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string requestBody;

                    using (StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    {
                        requestBody = System.Web.HttpUtility.UrlDecode(reader.ReadToEnd());
                    }

                    Log("Request body:");
                    Log(requestBody);

                    string queryString = new Uri(request.Url.AbsoluteUri + "?" + requestBody).Query;
                    var queryDictionary = System.Web.HttpUtility.ParseQueryString(queryString);

                    string links = string.Empty;

                    if (request.RawUrl.IndexOf("addcrypted2", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Regex rgxData = new Regex("crypted=(.*?)(&|$)");
                        string data = rgxData.Match(requestBody).Groups[1].ToString();

                        Regex rgxPass = new Regex("jk=(.*?){(.*?)}(&|$)");
                        string pass = rgxPass.Match(requestBody).Groups[2].ToString();

                        var jsEngine = new Jurassic.ScriptEngine();
                        pass = jsEngine.Evaluate("(function (){" + pass + "})()").ToString();

                        links = DecryptLinks(pass, data);
                    }
                    else
                    {
                        links = queryDictionary.Get("urls");
                    }

                    string source = queryDictionary.Get("source");

                    Log($"Source: {source}");

                    if (Jdownloader != null && !string.IsNullOrEmpty(links))
                    {
                        var addLinkRequest = new AddLinkRequest()
                        {
                            Links = links.Replace(Environment.NewLine, ";")
                        };

                        if (Jdownloader.LinkgrabberV2.AddLinks(addLinkRequest))
                        {
                            int count = links.Split(new[] { "\r\n", "\r", "\n" },StringSplitOptions.None).Length;

                            AppContext.ShowBalloon("Linkgrabber", $"{count} Links from successfully sent to {Jdownloader.Jd.Device.Name}", ToolTipIcon.Info);
                            Log($"Links from {source} successfully sent to {Jdownloader.Jd.Device.Name}");
                        }
                        else
                        {
                            Log("JDownloader did not accept the links.");
                        }
                    }

                    if (!string.IsNullOrEmpty(links))
                    {
                        Log($"Extracted links from {source}:");
                        Log(links);
                    }

                    responseString = "success\r\n";
                }
                else
                {
                    responseString = "JDownloader";
                }
            }
            else
            {
                response.StatusCode = 400;
                responseString = "Bad Request";
            }

            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;

            using (Stream output = response.OutputStream)
            {
                output.Write(buffer, 0, buffer.Length);
            }
        }

        private static string DecryptLinks(string key, string data)
        {
            key = key.ToUpper();

            byte[] decKey = Convert.FromHexString(key);
            byte[] dataByte = Convert.FromBase64String(data);

            using Aes aes = Aes.Create();
            aes.Key = decKey;
            aes.IV = decKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            using ICryptoTransform cTransform = aes.CreateDecryptor();
            byte[] resultArray = cTransform.TransformFinalBlock(dataByte, 0, dataByte.Length);

            string rawLinks = Encoding.ASCII.GetString(resultArray);

            string cleanLinks = rawLinks.TrimEnd('\0');
            cleanLinks = Regex.Replace(cleanLinks, @"\n+", "\r\n");

            return cleanLinks;
        }

        private static void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            try
            {
                if (LogWindow != null && !LogWindow.IsDisposed)
                    LogWindow.AppendLog(line);
                else
                    System.Diagnostics.Debug.WriteLine(line);
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine(line);
            }
        }

        public static void ShowLogWindow()
        {
            if (LogWindow == null || LogWindow.IsDisposed)
                LogWindow = new LogForm();

            if (LogWindow.InvokeRequired)
            {
                LogWindow.BeginInvoke(new Action(ShowLogWindow));
                return;
            }

            LogWindow.Show();
            LogWindow.WindowState = FormWindowState.Normal;
            LogWindow.BringToFront();
            LogWindow.Activate();
        }

        public static void ResetAndRestart()
        {
            if (MessageBox.Show("Are you sure you want to delete the login details and restart the application?",
                "Delete Credentials & Restart", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Config.DeleteConfiguration();

                // Anwendungsneustart ausführen
                Application.Restart();
                Environment.Exit(0);
            }
        }

        public static void Shutdown()
        {
            if (shuttingDown)
                return;

            shuttingDown = true;
            Log("Stopping listener...");

            try
            {
                if (Listener != null && Listener.IsListening)
                    Listener.Stop();

                Listener?.Close();
            }
            catch { }

            try
            {
                AppContext?.DisposeTrayIcon();
            }
            catch { }

            Application.ExitThread();
        }

        // Hilfsmethode zum Laden des App-Icons
        private static Icon GetApplicationIcon()
        {
            try
            {
                // Versuche das Symbol aus der ausführbaren EXE zu extrahieren
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // Fallback auf Standard-System-Symbol
                return SystemIcons.Application;
            }
        }

        private sealed class TrayApplicationContext : ApplicationContext
        {
            private readonly NotifyIcon trayIcon;
            private readonly ContextMenuStrip menu;
            private readonly ToolStripMenuItem autostartItem;
            public TrayApplicationContext()
            {
                menu = new ContextMenuStrip();

                var showLogItem = new ToolStripMenuItem("Show Log");
                showLogItem.Click += (s, e) => ShowLogWindow();

                autostartItem = new ToolStripMenuItem("Start With Windows")
                {
                    CheckOnClick = true,
                    Checked = AutostartHelper.IsInAutostart()
                };
                autostartItem.CheckedChanged += (s, e) =>
                {
                    AutostartHelper.SetAutostart(autostartItem.Checked);
                    Log(autostartItem.Checked ? "Enabled Autostart" : "Disabled Autostart");
                };

                var resetItem = new ToolStripMenuItem("Delete Credentials && Restart");
                resetItem.Click += (s, e) => ResetAndRestart();

                var exitItem = new ToolStripMenuItem("Exit");
                exitItem.Click += (s, e) => Shutdown();

                menu.Items.Add(showLogItem);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(autostartItem);
                menu.Items.Add(resetItem);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(exitItem);

                trayIcon = new NotifyIcon
                {
                    Icon = GetApplicationIcon(),
                    Text = "Click'N'Load 2 MyJD",
                    Visible = true,
                    ContextMenuStrip = menu
                };

                trayIcon.DoubleClick += (s, e) => ShowLogWindow();
                trayIcon.BalloonTipTitle = "Click'N'Load 2 MyJD";
                trayIcon.BalloonTipText = "Listener läuft auf Port 9666.";
            }

            public void DisposeTrayIcon()
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                menu.Dispose();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    DisposeTrayIcon();
                base.Dispose(disposing);
            }
            public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
            {
                if (trayIcon != null && trayIcon.Visible)
                {
                    trayIcon.ShowBalloonTip(3000, title, message, icon);
                }
            }
        }

        private sealed class LoginForm : Form
        {
            private readonly TextBox txtEmail;
            private readonly TextBox txtPassword;

            public string Email => txtEmail.Text;
            public string Password => txtPassword.Text;

            public LoginForm()
            {
                Text = "MyJDownloader Login";
                Width = 380;
                Height = 220;
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                Icon = GetApplicationIcon();

                var lblEmail = new Label { Text = "E-Mail:", Left = 20, Top = 20, AutoSize = true };
                txtEmail = new TextBox { Left = 20, Top = 40, Width = 320 };

                var lblPassword = new Label { Text = "Password:", Left = 20, Top = 75, AutoSize = true };
                txtPassword = new TextBox { Left = 20, Top = 95, Width = 320, UseSystemPasswordChar = true };

                var btnOk = new Button { Text = "Save", Left = 180, Top = 135, Width = 75, DialogResult = DialogResult.OK };
                var btnCancel = new Button { Text = "Cancel & Continue", Left = 265, Top = 135, Width = 75, DialogResult = DialogResult.Cancel };

                Controls.Add(lblEmail);
                Controls.Add(txtEmail);
                Controls.Add(lblPassword);
                Controls.Add(txtPassword);
                Controls.Add(btnOk);
                Controls.Add(btnCancel);

                AcceptButton = btnOk;
                CancelButton = btnCancel;
            }
        }

        private sealed class LogForm : Form
        {
            private readonly RichTextBox logBox;
            private readonly Button clearButton;

            public LogForm()
            {
                Text = "Click'N'Load 2 MyJD - Log";
                Width = 900;
                Height = 600;
                StartPosition = FormStartPosition.CenterScreen;
                MinimizeBox = true;
                MaximizeBox = true;
                Icon = GetApplicationIcon();

                logBox = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    BackColor = Color.White,
                    Font = new Font("Consolas", 10),
                    WordWrap = false,
                    DetectUrls = true
                };

                clearButton = new Button
                {
                    Text = "Clear Log",
                    Dock = DockStyle.Bottom,
                    Height = 32
                };
                clearButton.Click += (s, e) => logBox.Clear();

                Controls.Add(logBox);
                Controls.Add(clearButton);
            }

            public void AppendLog(string message)
            {
                if (IsDisposed)
                    return;

                if (InvokeRequired)
                {
                    BeginInvoke(new Action<string>(AppendLog), message);
                    return;
                }

                logBox.AppendText(message + Environment.NewLine);
                logBox.SelectionStart = logBox.TextLength;
                logBox.ScrollToCaret();
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    return;
                }

                base.OnFormClosing(e);
            }
        }

        private sealed class DeviceSelectionForm : Form
        {
            private readonly ComboBox comboBox;
            public int SelectedIndex => comboBox.SelectedIndex;

            public DeviceSelectionForm(dynamic[] devices)
            {
                Text = "MyJDownloader Device Selection auswählen";
                Width = 500;
                Height = 180;
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                Icon = GetApplicationIcon();

                var label = new Label
                {
                    Text = "Please select the MyJDownloader device:",
                    AutoSize = true,
                    Left = 15,
                    Top = 15
                };

                comboBox = new ComboBox
                {
                    Left = 15,
                    Top = 45,
                    Width = 450,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };

                for (int i = 0; i < devices.Length; i++)
                    comboBox.Items.Add($"{i + 1} - {devices[i]}");

                if (comboBox.Items.Count > 0)
                    comboBox.SelectedIndex = 0;

                var okButton = new Button
                {
                    Text = "OK",
                    Left = 300,
                    Top = 85,
                    Width = 80,
                    DialogResult = DialogResult.OK
                };

                var cancelButton = new Button
                {
                    Text = "Cancel",
                    Left = 390,
                    Top = 85,
                    Width = 80,
                    DialogResult = DialogResult.Cancel
                };

                Controls.Add(label);
                Controls.Add(comboBox);
                Controls.Add(okButton);
                Controls.Add(cancelButton);

                AcceptButton = okButton;
                CancelButton = cancelButton;
            }
        }
    }
    public static class AutostartHelper
    {
        private const string REG_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "ClickNLoad2MyJD";

        public static bool IsInAutostart()
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_KEY, false);
            return key?.GetValue(APP_NAME) != null;
        }

        public static void SetAutostart(bool enable)
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_KEY, true);
            if (key == null) return;

            if (enable)
            {
                string exePath = $"\"{Application.ExecutablePath}\"";
                key.SetValue(APP_NAME, exePath);
            }
            else
            {
                if (key.GetValue(APP_NAME) != null)
                {
                    key.DeleteValue(APP_NAME, false);
                }
            }
        }
    }
}