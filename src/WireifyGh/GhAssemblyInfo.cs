// SPDX-License-Identifier: Apache-2.0
using System;
using Grasshopper.Kernel;

namespace WireifyGh
{
    public sealed class GhAssemblyInfo : GH_AssemblyInfo
    {
        public override string Name => "Wireify";
        public override string Description => "Your own Claude Code, live in Grasshopper.";
        public override Guid Id => new Guid("b1e7c0de-0000-4000-8000-00000000a001");
        public override string AuthorName => "Hossein Zargar";
        public override string AuthorContact => "ify@ifylab.dev";
    }
}
