using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace PromptVault
{
    static class Native
    {
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
        public const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Libera la memoria que la ventana dejó de usar (se llama al esconderla).</summary>
        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try
            {
                using (var p = Process.GetCurrentProcess())
                    SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch (Exception) { }
        }
    }

    /// <summary>Inicio con Windows vía HKCU\...\Run (no necesita admin).</summary>
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "PromptVault";

        static string Command()
        {
            return "\"" + Assembly.GetEntryAssembly().Location + "\" --tray";
        }

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(ValueName) != null;
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(ValueName, Command());
                else k.DeleteValue(ValueName, false);
            }
        }

        /// <summary>Si moviste el .exe, actualiza la ruta guardada.</summary>
        public static void FixPathIfEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                if (k == null) return;
                var v = k.GetValue(ValueName) as string;
                if (v == null || v == Command()) return;
            }
            Set(true);
        }
    }

    static class Clip
    {
        /// <summary>El portapapeles puede estar tomado por otra app un instante: reintenta 10 veces.</summary>
        public static bool TrySetText(string text, out string error)
        {
            error = null;
            try
            {
                WF.Clipboard.SetDataObject(text, true, 10, 100);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Write(ex);
                return false;
            }
        }
    }

    static class Log
    {
        public static string Dir;

        public static void Write(Exception ex)
        {
            if (ex == null || Dir == null) return;
            try
            {
                string path = Path.Combine(Dir, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }

    static class Res
    {
        public static object LoadXaml(string name)
        {
            using (var s = typeof(Res).Assembly.GetManifestResourceStream("PromptVault." + name))
            {
                if (s == null) throw new InvalidOperationException("Falta el recurso " + name);
                return XamlReader.Load(s);
            }
        }

        public static void LoadStyles(Application app)
        {
            app.Resources.MergedDictionaries.Add((ResourceDictionary)LoadXaml("Styles.xaml"));
        }
    }
}
