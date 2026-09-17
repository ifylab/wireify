// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using Grasshopper;
using Rhino;
using Wireify.Entry;
using WireifyContract;

namespace WireifyGh
{
    /// <summary>
    /// GH-side hub over the shared controller: bootstraps it once, forwards the canvas's
    /// document changes to it, and repaints the sockets when the session state changes or a
    /// tool touches a component. Everything here runs in the Default load context.
    /// </summary>
    internal static class WireifyGhRuntime
    {
        static readonly object Gate = new object();
        static IWireifyController? _controller;

        public static IWireifyController Controller
        {
            get
            {
                lock (Gate)
                {
                    if (_controller is null)
                    {
                        _controller = WireifyBootstrap.EnsureController();
                        _controller.StateChanged += _ => RepaintSockets();
                        _controller.ComponentActivityChanged += OnActivity;
                        _controller.ConnectStepCompleted += OnStep;
                        HookActiveDocument();
                    }
                    return _controller;
                }
            }
        }

        /// <summary>A tab switch, open, or close changes which definition is "the" definition
        /// for every per-file surface; nothing told the panel until it was reopened or a Build
        /// ran (round-11 S11.27). The canvas raises DocumentChanged for all three; a canvas
        /// created later (the editor opened after the .gha loaded) is hooked when it appears.</summary>
        static void HookActiveDocument()
        {
            try
            {
                var canvas = Instances.ActiveCanvas;
                if (canvas is not null) canvas.DocumentChanged += OnCanvasDocumentChanged;
                Instances.CanvasCreated += created =>
                {
                    if (created is null) return;
                    created.DocumentChanged -= OnCanvasDocumentChanged;
                    created.DocumentChanged += OnCanvasDocumentChanged;
                };
            }
            catch { /* the hook is a courtesy for the panel; the sockets repaint on their own */ }
        }

        static void OnCanvasDocumentChanged(Grasshopper.GUI.Canvas.GH_Canvas sender, Grasshopper.GUI.Canvas.GH_CanvasDocumentChangedEventArgs e)
        {
            try { _controller?.ActiveDefinitionChanged(); }
            catch { /* best-effort */ }
        }

        public static WireifyConnectionState State => Controller.State;

        /// <summary>The state of THIS definition's session — sockets render per-document, so a
        /// second open file never shows "do #n" off another file's terminal.</summary>
        public static WireifyConnectionState StateFor(string? ghFilePath) => Controller.StateFor(ghFilePath);

        public static WireifyAppStatus AppStatusFor(string? ghFilePath) => Controller.AppStatusFor(ghFilePath);
        public static string? AppLinkFor(string? ghFilePath) => Controller.AppLinkFor(ghFilePath);
        public static WireifyConnectStep OpenApp(string? ghFilePath) => Controller.OpenApp(ghFilePath);
        public static void DefinitionRenamed(string? oldPath, string? newPath) => Controller.DefinitionRenamed(oldPath, newPath);

        static WireifyConnectStep? _lastStep;

        /// <summary>The build flow's latest step (any definition) — a socket shows it on its
        /// plate while its own flow runs.</summary>
        public static WireifyConnectStep? LastStep
        {
            get { lock (Gate) return _lastStep; }
        }

        static void OnStep(WireifyConnectStep step)
        {
            lock (Gate) _lastStep = step;
            RepaintSockets();
        }

        /// <summary>One repaint later — how a transient plate line ("link copied") clears itself
        /// without the user having to move the mouse.</summary>
        public static void RepaintAfter(int milliseconds)
        {
            System.Threading.Timer? timer = null;
            timer = new System.Threading.Timer(_ =>
            {
                RepaintSockets();
                timer?.Dispose();
            }, null, milliseconds, System.Threading.Timeout.Infinite);
        }

        /// <summary>A tool started or stopped touching a component: repaint, so the plate and
        /// the registry catch up (a converted socket is gone, a renamed control has a new name).
        /// There is no "Working" state any more — a conversion replaces the socket before any
        /// repaint could show one, so it was never observable in three rounds of trying
        /// (round-12 S12.14); the converted component appearing is the signal.</summary>
        static void OnActivity(Guid componentId, bool active) => RepaintSockets();

        public static void RepaintSockets()
        {
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                var canvas = Instances.ActiveCanvas;
                var doc = canvas?.Document;
                if (doc is null) return;
                foreach (var obj in doc.Objects)
                    if (obj is WireifySocketComponent socket)
                        socket.Attributes?.ExpireLayout();
                canvas?.Refresh();
            }));
        }
    }
}
