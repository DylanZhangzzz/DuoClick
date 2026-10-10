using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DuoClick
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            SetPerMonitorDpiAwareness();

            bool ownsMutex;
            using (Mutex instanceMutex = new Mutex(true, @"Local\DuoClick.SingleInstance", out ownsMutex))
            {
                if (!ownsMutex)
                    return;

                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    using (DuoClickApplicationContext context = new DuoClickApplicationContext())
                    {
                        Application.Run(context);
                    }
                }
                finally
                {
                    try { instanceMutex.ReleaseMutex(); }
                    catch (ApplicationException) { }
                }
            }
        }

        private static void SetPerMonitorDpiAwareness()
        {
            // Keep WinForms from creating any windows before choosing awareness.
            // The manifest also requests PerMonitorV2; these calls cover launches
            // where the manifest is not present (for example, a direct csc build).
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    return;

                // ERROR_ACCESS_DENIED means awareness was already fixed, usually
                // by the executable manifest, so do not attempt to change it.
                if (Marshal.GetLastWin32Error() == 5)
                    return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try
            {
                int result = SetProcessDpiAwareness(2);
                if (result == 0 || result == unchecked((int)0x80070005))
                    return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try { SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetProcessDPIAware();
    }
}
