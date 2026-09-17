// SPDX-License-Identifier: Apache-2.0
using System;
using Grasshopper.Kernel;

namespace WireifyGh
{
    public sealed class GhAssemblyInfo : GH_AssemblyInfo
    {
        static System.Drawing.Bitmap? _icon;

        public override string Name => "Wireify";
        public override string Description => "Your own Claude Code, live in Grasshopper.";
        public override Guid Id => new Guid("b1e7c0de-0000-4000-8000-00000000a001");
        public override string AuthorName => "Hossein Zargar";
        public override string AuthorContact => "ify@ifylab.dev";
        /// <summary>The version the missing-plugin dialog and the plug-in list show — the shipped
        /// product version (Directory.Build.props), never the SDK's 1.0.0 default (round-9 S9.64).</summary>
        public override string Version
            => typeof(GhAssemblyInfo).Assembly.GetName().Version?.ToString(3) ?? "";
        public override System.Drawing.Bitmap? Icon => _icon ??= LoadIcon();

        static System.Drawing.Bitmap? LoadIcon()
        {
            var stream = typeof(GhAssemblyInfo).Assembly
                .GetManifestResourceStream("WireifyGh.Resources.wireify-24.png");
            return stream is null ? null : new System.Drawing.Bitmap(stream);
        }
    }
}
