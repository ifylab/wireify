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
    /// GH-side hub over the shared controller: bootstraps it once, tracks which components a
    /// Wireify tool is currently touching (for the socket's "Working" state), and repaints the
    /// sockets when the session state changes. Everything here runs in the Default load context.
    /// </summary>
    internal static class WireifyGhRuntime
    {
        static readonly object Gate = new object();
        static readonly HashSet<Guid> Active = new HashSet<Guid>();
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
                    }
                    return _controller;
                }
            }
        }

        public static WireifyConnectionState State => Controller.State;

        public static bool IsActive(Guid componentId)
        {
            lock (Gate) return Active.Contains(componentId);
        }

        static void OnActivity(Guid componentId, bool active)
        {
            lock (Gate)
            {
                if (active) Active.Add(componentId);
                else Active.Remove(componentId);
            }
            RepaintSockets();
        }

        static void RepaintSockets()
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
