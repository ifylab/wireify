// SPDX-License-Identifier: Apache-2.0
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino.UI;
using Wireify.Entry;
using WireifyContract;

namespace Wireify
{
    /// <summary>
    /// The docked Connect/Status panel (Eto, SampleCsEto pattern): a state header, the connect
    /// checklist (one row per step kind), the Wireify components on the active canvas, and the
    /// scope-tagged log. All controller events arrive on background threads and are marshalled
    /// through <see cref="Application.Instance"/>.
    /// </summary>
    [Guid("b1e7c0de-0000-4000-8000-00000000a003")]
    public sealed class WireifyPanel : Eto.Forms.Panel, IPanel
    {
        static readonly Color DotIdle = Colors.Gray;
        static readonly Color DotListening = Color.FromArgb(100, 116, 139);
        static readonly Color DotLaunched = Color.FromArgb(191, 144, 0);
        static readonly Color DotConnected = Color.FromArgb(46, 125, 50);
        static readonly Color TextError = Color.FromArgb(178, 34, 34);

        readonly IWireifyController _controller;

        readonly Eto.Forms.Panel _dot = new Eto.Forms.Panel { Size = new Size(12, 12), BackgroundColor = DotIdle };
        readonly Label _stateLabel = new Label { Font = SystemFonts.Bold() };
        readonly Label _docLabel = new Label { TextAlignment = TextAlignment.Right };
        readonly Dictionary<string, Label> _rows = new Dictionary<string, Label>();
        readonly ListBox _canvasList = new ListBox { Height = 84 };
        readonly Button _connect = new Button { Text = "Build" };
        readonly Button _openHome = new Button { Text = "Open home", Enabled = false };
        readonly Button _openApp = new Button { Text = "Open app", Enabled = false };
        readonly Button _openLog = new Button { Text = "Open log" };
        readonly TextArea _log = new TextArea { ReadOnly = true, Wrap = false, Font = Eto.Drawing.Fonts.Monospace(9) };

        readonly Action<WireifyConnectionState> _onState;
        readonly Action<WireifyConnectStep> _onStep;
        readonly Action<WireifyLogLine> _onLog;
        readonly Action<Guid, bool> _onActivity;

        string _homeDir = "";

        public WireifyPanel(uint documentSerialNumber)
        {
            _controller = WireifyBootstrap.EnsureController();

            BuildUi();

            _onState = state => Ui(() => RenderState(state));
            _onStep = step => Ui(() => RenderStep(step));
            _onLog = line => Ui(() => AppendLog(line));
            _onActivity = (_, _) => Ui(RefreshCanvasList);
            _controller.StateChanged += _onState;
            _controller.ConnectStepCompleted += _onStep;
            _controller.LogEmitted += _onLog;
            _controller.ComponentActivityChanged += _onActivity;

            _connect.Click += (_, _) => StartConnect();
            _openHome.Click += (_, _) => OpenFolder(_homeDir);
            _openLog.Click += (_, _) => OpenFolder(_controller.LogsDirectory);
            // The use gesture, without a socket on the canvas: the live link is minted on click.
            _openApp.Click += (_, _) => Task.Run(() => _controller.OpenApp(_controller.ActiveDefinitionPath()));

            foreach (var line in _controller.RecentLog) AppendLog(line);
            RenderState(_controller.State);
        }

        void BuildUi()
        {
            Padding = new Padding(8);
            MinimumSize = new Size(240, 0);

            var header = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { _dot, _stateLabel, new StackLayoutItem(_docLabel, expand: true) },
            };

            var steps = new TableLayout { Spacing = new Size(8, 3) };
            steps.Rows.Add(StepRow("server", "Server"));
            steps.Rows.Add(StepRow("home", "Home"));
            steps.Rows.Add(StepRow("config", "Config"));
            steps.Rows.Add(StepRow("preflight", "Preflight"));
            steps.Rows.Add(StepRow("terminal", "Terminal"));
            steps.Rows.Add(StepRow("claude", "Claude"));

            var buttons = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Items = { _connect, _openApp, _openHome, _openLog },
            };

            var layout = new DynamicLayout { Spacing = new Size(6, 6) };
            layout.AddRow(header);
            layout.AddRow(steps);
            layout.AddRow(new Label { Text = "Canvas", Font = SystemFonts.Bold() });
            layout.AddRow(_canvasList);
            layout.AddRow(buttons);
            layout.Add(_log, yscale: true);
            Content = layout;
        }

        TableRow StepRow(string kind, string title)
        {
            var value = new Label { Text = "-" };
            _rows[kind] = value;
            return new TableRow(
                new TableCell(new Label { Text = title, Width = 64 }),
                new TableCell(value, scaleWidth: true));
        }

        void StartConnect()
        {
            _connect.Enabled = false;
            foreach (var row in _rows.Values)
            {
                row.Text = "-";
                row.TextColor = SystemColors.ControlText;
            }

            Task.Run(() =>
            {
                try
                {
                    var report = _controller.Connect(null);
                    Ui(() =>
                    {
                        if (!string.IsNullOrEmpty(report.HomeDir))
                        {
                            _homeDir = report.HomeDir;
                            _openHome.Enabled = true;
                        }
                        var claude = _rows["claude"];
                        if (_controller.State == WireifyConnectionState.Connected)
                            claude.Text = "connected";
                        else if (report.Success)
                            claude.Text = "waiting for first request (first run: approve the wireify server in the terminal)";
                        else if (report.Hint is { Length: > 0 } hint)
                        {
                            claude.Text = hint;
                            claude.TextColor = TextError;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        var claude = _rows["claude"];
                        claude.Text = $"connect failed: {ex.Message}";
                        claude.TextColor = TextError;
                    });
                }
                finally
                {
                    Ui(() =>
                    {
                        _connect.Enabled = true;
                        _connect.Text = "New session";
                        RefreshCanvasList();
                    });
                }
            });
        }

        void RenderState(WireifyConnectionState state)
        {
            // Everything follows the ACTIVE definition's own session — header included: a file
            // whose Build failed must not read "connected" off another file's terminal
            // (round-11 S11.32), and a green header over a body of dashes contradicted itself
            // in one screenshot (round-12 S12.8). The server level is the floor.
            var mine = state;
            try
            {
                mine = _controller.StateFor(_controller.ActiveDefinitionPath());
                if (mine < WireifyConnectionState.ServerListening && state >= WireifyConnectionState.ServerListening)
                    mine = WireifyConnectionState.ServerListening;
            }
            catch { /* no canvas yet: fall back to the global state */ }
            // New session always spawns a fresh terminal — the way back after closing the window.
            _connect.Text = mine >= WireifyConnectionState.TerminalLaunched ? "New session" : "Build";
            switch (mine)
            {
                case WireifyConnectionState.Connected:
                    _dot.BackgroundColor = DotConnected;
                    _stateLabel.Text = "Claude connected";
                    break;
                case WireifyConnectionState.TerminalLaunched:
                    _dot.BackgroundColor = DotLaunched;
                    _stateLabel.Text = "Launched - waiting for Claude";
                    break;
                case WireifyConnectionState.ServerListening:
                    _dot.BackgroundColor = DotListening;
                    _stateLabel.Text = "Server listening";
                    break;
                default:
                    _dot.BackgroundColor = DotIdle;
                    _stateLabel.Text = "Idle";
                    break;
            }
            var claude = _rows["claude"];
            switch (mine)
            {
                case WireifyConnectionState.Connected:
                    claude.Text = "connected";
                    claude.TextColor = SystemColors.ControlText;
                    break;
                case WireifyConnectionState.TerminalLaunched:
                    claude.Text = "waiting for first request (first run: approve the wireify server in the terminal)";
                    claude.TextColor = SystemColors.ControlText;
                    break;
                default:
                    if (claude.TextColor != TextError) claude.Text = "-";
                    break;
            }
            RefreshDocLabel();
            RefreshCanvasList();
        }

        void RenderStep(WireifyConnectStep step)
        {
            if (!_rows.TryGetValue(step.Kind, out var row))
            {
                // Steps without a fixed row (trust, refused, error): failures surface in the
                // header, successes just go to the log.
                if (!step.Ok) _stateLabel.Text = step.Message;
                return;
            }
            row.Text = step.Ok ? StripPrefix(step.Message) : $"{step.Scope} {step.Message}";
            row.TextColor = step.Ok ? SystemColors.ControlText : TextError;
        }

        void AppendLog(WireifyLogLine line)
        {
            _log.Append($"{line.StampLocal:HH:mm:ss} {line.Scope} {(line.Ok ? "ok " : "ERR")} {line.Message}\n", scrollToCursor: true);
        }

        void RefreshDocLabel()
        {
            // Touch the canvas only once Grasshopper is up (the server starts when GH loads).
            if (_controller.State == WireifyConnectionState.ServerStopped)
            {
                _docLabel.Text = "";
                _openApp.Enabled = false;
                _openHome.Enabled = false;
                return;
            }
            try
            {
                var path = _controller.ActiveDefinitionPath();
                _docLabel.Text = string.IsNullOrEmpty(path) ? "no saved definition" : Path.GetFileName(path);
                // Every button follows the ACTIVE definition, whoever pressed Build: the home
                // folder exists or it does not, and the step rows show that definition's last
                // Build — a panel opened afterwards used to hold dashes above a log that listed
                // every completed step, with Open home dead for good (round-10 S10.5).
                var status = _controller.AppStatusFor(path);
                _openApp.Enabled = status.PageExists;
                _homeDir = status.HomeExists ? (Path.GetDirectoryName(status.AppFolder) ?? "") : "";
                _openHome.Enabled = _homeDir.Length > 0;
                // Rows read the ACTIVE file's last Build — cleared first, so a file with no Build
                // shows dashes instead of the previous definition's walk (round-11 S11.27).
                foreach (var kind in new[] { "server", "home", "config", "preflight", "terminal" })
                {
                    _rows[kind].Text = "-";
                    _rows[kind].TextColor = SystemColors.ControlText;
                }
                foreach (var step in _controller.RecentStepsFor(path)) RenderStep(step);
            }
            catch
            {
                _docLabel.Text = "";
                _openApp.Enabled = false;
                _openHome.Enabled = false;
            }
        }

        void RefreshCanvasList()
        {
            if (_controller.State == WireifyConnectionState.ServerStopped) return;
            try
            {
                var items = _controller.DescribeCanvas();
                _canvasList.Items.Clear();
                foreach (var item in items)
                {
                    var state = item.Converted
                        ? "converted"
                        : $"staged ({item.InputNames.Length} input{(item.InputNames.Length == 1 ? "" : "s")})";
                    _canvasList.Items.Add(new ListItem { Text = $"#{item.Number}  {item.NickName}  -  {state}" });
                }
            }
            catch { /* canvas reads are best-effort UI sugar */ }
        }

        static string StripPrefix(string message)
            => message.StartsWith("[", StringComparison.Ordinal) ? message : message;

        static void OpenFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch { /* opening a folder is never worth an error dialog */ }
        }

        void Ui(Action action) => Application.Instance.AsyncInvoke(action);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _controller.StateChanged -= _onState;
                _controller.ConnectStepCompleted -= _onStep;
                _controller.LogEmitted -= _onLog;
                _controller.ComponentActivityChanged -= _onActivity;
            }
            base.Dispose(disposing);
        }

        // --- IPanel --------------------------------------------------------------------------

        public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
        {
            RenderState(_controller.State);
        }

        public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { }

        public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) { }
    }
}
