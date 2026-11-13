using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Declara el framework de destino para activar el comportamiento moderno de WPF (DPI por monitor, etc.).
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("PromptVault")]
[assembly: AssemblyProduct("PromptVault")]
[assembly: AssemblyDescription("Gestor de prompts para Windows")]
[assembly: AssemblyCopyright("Copyright © 2026 davidcostacv. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace PromptVault
{
    public class Args
    {
        public bool Tray, Exit, CustomData;
        public string DataDir, Screenshot, Theme, Search;

        public Args(string[] argv)
        {
            DataDir = Store.DefaultDir();
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i].ToLowerInvariant();
                string next = i + 1 < argv.Length ? argv[i + 1] : null;
                if (a == "--tray") Tray = true;
                else if (a == "--exit") Exit = true;
                else if (a == "--data" && next != null) { DataDir = Path.GetFullPath(next); CustomData = true; i++; }
                else if (a == "--screenshot" && next != null) { Screenshot = Path.GetFullPath(next); i++; }
                else if (a == "--theme" && next != null) { Theme = next; i++; }
                else if (a == "--search" && next != null) { Search = next; i++; }
            }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] argv)
        {
            var a = new Args(argv);
            if (a.Screenshot != null) return Shot.Run(a);

            // Una sola copia por carpeta de datos. Si ya hay una, se le pide que se muestre (o que salga) y esta termina.
            string key = @"Local\PromptVault_" + Fnv(a.DataDir.ToLowerInvariant()).ToString("x8");
            bool created;
            var mutex = new Mutex(true, key, out created);
            if (!created)
            {
                Signal(key + (a.Exit ? "_exit" : "_show"), !a.Exit);
                return 0;
            }
            if (a.Exit) return 0;

            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, key + "_show");
            var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, key + "_exit");

            Store store;
            try { store = Store.Open(a.DataDir); }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo abrir la carpeta de datos:\n" + a.DataDir + "\n\n" + ex.Message, "PromptVault",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            Log.Dir = store.Dir;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Res.LoadStyles(app);
            var host = new AppHost(app, store, !a.CustomData);

            app.DispatcherUnhandledException += delegate (object s, DispatcherUnhandledExceptionEventArgs e)
            {
                Log.Write(e.Exception);
                bool saved = host.EmergencySave();
                e.Handled = true;   // la app sigue viva; el error queda en error.log
                host.Notify(saved
                    ? "Algo falló, pero tus prompts están guardados. Detalle en error.log."
                    : "Algo falló y no se pudo guardar todavía; se reintentará. Detalle en error.log.");
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Log.Write(e.ExceptionObject as Exception);
            };

            var listener = new Thread(() =>
            {
                var handles = new WaitHandle[] { showEvent, exitEvent };
                while (true)
                {
                    int i = WaitHandle.WaitAny(handles);
                    if (i == 0) app.Dispatcher.BeginInvoke(new Action(host.ShowMain));
                    else
                    {
                        app.Dispatcher.BeginInvoke(new Action(host.Exit));
                        return;
                    }
                }
            });
            listener.IsBackground = true;
            listener.Start();

            app.Dispatcher.BeginInvoke(new Action(() => host.Start(!a.Tray)));
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        static void Signal(string name, bool allowForeground)
        {
            if (allowForeground) Native.AllowSetForegroundWindow(-1);   // deja que la copia que ya corre tome el foco
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    using (var ev = EventWaitHandle.OpenExisting(name))
                    {
                        ev.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(100); }
            }
        }

        static uint Fnv(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s)
                {
                    h ^= c;
                    h *= 16777619;
                }
                return h;
            }
        }
    }

    /// <summary>Modo de prueba: dibuja la ventana a un PNG sin mostrarla en pantalla.</summary>
    static class Shot
    {
        public static int Run(Args a)
        {
            var store = Store.Open(a.DataDir);
            Log.Dir = store.Dir;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Res.LoadStyles(app);
            var host = new AppHost(app, store, false);
            if (a.Theme != null) host.OverrideTheme(a.Theme);
            host.PreviewAsRegistered();
            int code = 0;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                var v = new MainView(host);
                var w = v.Win;
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -20000;
                w.Top = -20000;
                w.Width = 1080;
                w.Height = 680;
                w.ShowInTaskbar = false;
                w.ShowActivated = false;
                w.Show();
                if (a.Search != null) v.SetSearch(a.Search);
                app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try { Save(w, a.Screenshot); }
                    catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
                    app.Shutdown();
                }));
            }));
            app.Run();
            return code;
        }

        static void Save(Window w, string path)
        {
            var root = (FrameworkElement)w.Content;
            const double scale = 1.5;
            var rtb = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }
    }
}
