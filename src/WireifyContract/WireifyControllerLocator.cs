// SPDX-License-Identifier: Apache-2.0
using System;

namespace WireifyContract
{
    /// <summary>
    /// Process-wide holder for the one <see cref="IWireifyController"/>. This class lives in the
    /// contract assembly, which loads exactly once in the Default load context — so the
    /// <c>.rhp</c> and the <c>.gha</c> (each with its own bootstrap code) share a single
    /// controller and a single isolated core, whichever of them runs first.
    /// </summary>
    public static class WireifyControllerLocator
    {
        static readonly object Gate = new object();
        static IWireifyController? _current;

        public static IWireifyController? Current
        {
            get { lock (Gate) return _current; }
        }

        public static IWireifyController GetOrCreate(Func<IWireifyController> factory)
        {
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            lock (Gate)
            {
                return _current ??= factory();
            }
        }
    }
}
