using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;

namespace FrostyCLI.Core
{
    public static unsafe class FrostyMessageBoxPatch
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;

            Type fmbType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                fmbType = asm.GetType("Frosty.Core.Controls.FrostyMessageBox");
                if (fmbType != null) break;
            }

            if (fmbType == null)
            {
                _installed = true;
                return;
            }

            var showInternal = fmbType.GetMethod(
                "ShowInternal",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (showInternal == null)
            {
                _installed = true;
                return;
            }

            var replacement = typeof(FrostyMessageBoxPatch).GetMethod(
                "ShowInternalStub",
                BindingFlags.NonPublic | BindingFlags.Static);

            RuntimeHelpers.PrepareMethod(showInternal.MethodHandle);
            RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

            long* target = (long*)showInternal.MethodHandle.GetFunctionPointer().ToPointer();
            long* src    = (long*)replacement.MethodHandle.GetFunctionPointer().ToPointer();
            *target = *src;

            _installed = true;
        }

        private static MessageBoxResult ShowInternalStub(
            string message, string title,
            MessageBoxButton button, MessageBoxImage image)
        {
            return MessageBoxResult.OK;
        }
    }
}
