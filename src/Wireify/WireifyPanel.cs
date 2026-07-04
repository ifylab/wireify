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
        readonly Button _connect = new Button { Text = "Connect" };
        readonly Button _openHome = new Button { Text = "Open home", Enabled = false };
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
                Items = { _connect, _openHome, _openLog },
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
                        _connect.Text = "Reconnect";
                        RefreshCanvasList();
                    });
                }
            });
        }

        void RenderState(WireifyConnectionState state)
        {
            // Reconnect always spawns a fresh terminal — the way back after closing the window.
            _connect.Text = state >= WireifyConnectionState.TerminalLaunched ? "Reconnect" : "Connect";
            switch (state)
            {
                case WireifyConnectionState.Connected:
                    _dot.BackgroundColor = DotConnected;
                    _stateLabel.Text = "Claude connected";
                    _rows["claude"].Text = "connected";
                    _rows["claude"].TextColor = SystemColors.ControlText;
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
            if (_controller.State == WireifyConnectionState.ServerStopped) { _docLabel.Text = ""; return; }
            try
            {
                var path = _controller.ActiveDefinitionPath();
                _docLabel.Text = string.IsNullOrEmpty(path) ? "no saved definition" : Path.GetFileName(path);
            }
            catch { _docLabel.Text = ""; }
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
