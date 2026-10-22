using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassDesk.Acceptance
{
    /// <summary>Confirms full-host input tests are attached to their own active desktop.</summary>
    public static class DesktopIsolationGuard
    {
        public const string DesktopNamePrefix = "LGDsk-Acceptance-";
        private const uint DesktopReadObjects = 0x0001;
        private const int UserObjectName = 2;

        public static bool IsActiveAcceptanceDesktop()
        {
            string currentDesktop;
            string inputDesktop;
            return TryReadActiveDesktopNames(out currentDesktop, out inputDesktop)
                && IsNamedActiveAcceptanceDesktop(currentDesktop, inputDesktop);
        }

        public static bool IsNamedActiveAcceptanceDesktop(string currentDesktop, string inputDesktop)
        {
            return !string.IsNullOrWhiteSpace(currentDesktop)
                && !string.IsNullOrWhiteSpace(inputDesktop)
                && currentDesktop.StartsWith(DesktopNamePrefix, StringComparison.OrdinalIgnoreCase)
                && currentDesktop.Equals(inputDesktop, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryReadActiveDesktopNames(out string currentDesktop, out string inputDesktop)
        {
            currentDesktop = string.Empty;
            inputDesktop = string.Empty;
            var current = GetThreadDesktop(GetCurrentThreadId());
            if (current == IntPtr.Zero) return false;

            var input = OpenInputDesktop(0, false, DesktopReadObjects);
            if (input == IntPtr.Zero) return false;
            try
            {
                return TryGetUserObjectName(current, out currentDesktop)
                    && TryGetUserObjectName(input, out inputDesktop);
            }
            finally { CloseDesktop(input); }
        }

        private static bool TryGetUserObjectName(IntPtr handle, out string name)
        {
            var buffer = new StringBuilder(128);
            uint needed;
            if (!GetUserObjectInformation(handle, UserObjectName, buffer,
                    (uint)(buffer.Capacity * sizeof(char)), out needed))
            {
                name = string.Empty;
                return false;
            }

            name = buffer.ToString().TrimEnd('\0');
            return name.Length > 0;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index,
            StringBuilder information, uint length, out uint needed);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);
    }
}
