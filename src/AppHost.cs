using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace PromptVault
{
    /// <summary>Lo que vive 24/7: bandeja, atajo global, tema y datos. La ventana se crea solo cuando se abre.</summary>
    public class AppHost
    {
        const int HotkeyId = 0x5056;
        public readonly Application App;
        public readonly Store Store;
        readonly bool manageAutostart;
        readonly DispatcherTimer trimTimer, retryTimer;
        MainView view;
        WF.NotifyIcon tray;
        WF.ToolStripMenuItem autoItem;
        IntPtr trayIcon;
        HwndSource hotkeySource;
        bool exiting;
        string themeOverride, pendingWarning;

        public AppHost(Application app, Store store, bool manageAutostart)
        {
            App = app;
            Store = store;
            this.manageAutostart = manageAutostart;
            pendingWarning = store.LoadWarning;
            LastCategory = CatItem.All;
            LogoImage = ToImage(IconArt.Png(64));
            WindowIcon = LogoImage;
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            trimTimer.Tick += delegate
            {
                trimTimer.Stop();
                if (view == null) Native.TrimMemory();
            };
            retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            retryTimer.Tick += delegate
            {
                retryTimer.Stop();
                if (view == null && Store.Dirty) TrySave();
            };
        }

        public bool IsDark { get; private set; }
        public bool HotkeyOk { get; private set; }
        public ImageSource LogoImage { get; private set; }
        public ImageSource WindowIcon { get; private set; }
        public string LastCategory { get; set; }
        public string LastPromptId { get; set; }
        public bool CanManageAutostart { get { return manageAutostart; } }
        public bool AutostartEnabled { get { return manageAutostart && Autostart.IsEnabled(); } }

        static ImageSource ToImage(byte[] png)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new MemoryStream(png);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        // ================= Arranque =================

        public void Start(bool showWindow)
        {
            WF.Application.EnableVisualStyles();
            App.SessionEnding += delegate { EmergencySave(); };   // apagar / cerrar sesión
            RegisterHotkey();
            CreateTray();

            if (manageAutostart)
            {
                try
                {
                    if (!Store.Data.Settings.FirstRunDone)
                    {
                        Autostart.Set(true);   // Por defecto arranca con Windows; se desactiva desde el menú.
                        Store.Data.Settings.FirstRunDone = true;
                        Store.MarkDirty();
                        TrySave();
                        if (!showWindow) Balloon("PromptVault está en la bandeja. Ábrelo con Ctrl+Alt+P.");
                    }
                    else Autostart.FixPathIfEnabled();
                }
                catch (Exception ex) { Log.Write(ex); }
                autoItem.Checked = Autostart.IsEnabled();
            }

            if (showWindow) ShowMain();
            else
            {
                if (pendingWarning != null) Balloon("Se recuperaron tus prompts desde un respaldo. Abre PromptVault para ver el detalle.");
                trimTimer.Start();
            }
        }

        void RegisterHotkey()
        {
            var p = new HwndSourceParameters("PromptVaultHotkey");
            p.ParentWindow = new IntPtr(-3);   // HWND_MESSAGE: ventana invisible solo para recibir el atajo
            p.WindowStyle = 0;
            hotkeySource = new HwndSource(p);
            hotkeySource.AddHook(HotkeyHook);
            HotkeyOk = Native.RegisterHotKey(hotkeySource.Handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x50 /* P */);
        }

        IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                ToggleMain();
                handled = true;
            }
            return IntPtr.Zero;
        }

        void CreateTray()
        {
            int size = WF.SystemInformation.SmallIconSize.Width;
            using (var bmp = IconArt.Draw(size)) trayIcon = bmp.GetHicon();

            var menu = new WF.ContextMenuStrip();
            var open = new WF.ToolStripMenuItem("Abrir PromptVault", null, delegate { ShowMain(); });
            open.ShortcutKeyDisplayString = "Ctrl+Alt+P";
            open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
            autoItem = new WF.ToolStripMenuItem("Iniciar con Windows", null, delegate { SetAutostart(!autoItem.Checked); });
            autoItem.Enabled = manageAutostart;
            menu.Items.Add(open);
            menu.Items.Add(autoItem);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(new WF.ToolStripMenuItem("Salir", null, delegate { Exit(); }));

            tray = new WF.NotifyIcon
            {
                Icon = System.Drawing.Icon.FromHandle(trayIcon),
                Text = HotkeyOk ? "PromptVault (Ctrl+Alt+P)" : "PromptVault",
                ContextMenuStrip = menu,
                Visible = true
            };
            tray.MouseClick += delegate (object s, WF.MouseEventArgs e)
            {
                if (e.Button == WF.MouseButtons.Left) ShowMain();
            };
        }

        void Balloon(string text)
        {
            if (tray != null) tray.ShowBalloonTip(5000, "PromptVault", text, WF.ToolTipIcon.Info);
        }

        // ================= Ventana =================

        public void ShowMain()
        {
            if (exiting) return;
            if (view == null) view = new MainView(this);
            var v = view;
            var w = v.Win;
            if (!w.IsVisible) w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            // Windows a veces no deja que una app en segundo plano robe el foco; esto lo fuerza con el permiso del atajo.
            w.Topmost = true;
            w.Topmost = false;
            Native.SetForegroundWindow(new WindowInteropHelper(w).Handle);
            w.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(v.FocusSearch));

            if (pendingWarning != null)
            {
                string msg = pendingWarning;
                pendingWarning = null;
                w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(() => Dialog.Info(v.Win, "Se recuperaron tus datos", msg)));
            }
        }

        public void ToggleMain()
        {
            if (view != null && view.Win.IsActive) view.Win.Close();
            else ShowMain();
        }

        public void OnMainClosed(MainView v)
        {
            LastCategory = v.SelectedCategory;
            LastPromptId = v.CurrentId;
            if (view == v) view = null;
            if (Store.Dirty) TrySave();
            trimTimer.Stop();
            trimTimer.Start();
        }

        // ================= Ajustes =================

        string ThemeMode { get { return themeOverride ?? Store.Data.Settings.Theme; } }

        /// <summary>Solo para capturas: muestra la ventana como si el atajo estuviera registrado.</summary>
        public void PreviewAsRegistered() { HotkeyOk = true; }

        /// <summary>Solo para capturas de prueba: fuerza un tema sin guardarlo.</summary>
        public void OverrideTheme(string mode)
        {
            themeOverride = mode;
            ApplyTheme();
        }

        public void SetTheme(string mode)
        {
            Store.Data.Settings.Theme = mode;
            Store.MarkDirty();
            TrySave();
            ApplyTheme();
        }

        void ApplyTheme()
        {
            IsDark = Theme.Resolve(ThemeMode);
            Theme.Apply(App.Resources, IsDark);
            if (view != null) view.OnThemeChanged();
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            App.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ThemeMode == "auto" && Theme.Resolve("auto") != IsDark) ApplyTheme();
            }));
        }

        public void SetAutostart(bool on)
        {
            if (!manageAutostart) return;
            try { Autostart.Set(on); }
            catch (Exception ex)
            {
                Log.Write(ex);
                Notify("No se pudo cambiar el inicio con Windows: " + ex.Message);
            }
            if (autoItem != null) autoItem.Checked = Autostart.IsEnabled();
        }

        // ================= Guardado y salida =================

        public bool TrySave()
        {
            try
            {
                Store.Save();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                retryTimer.Stop();
                retryTimer.Start();
                return false;
            }
        }

        /// <summary>Devuelve true si todo quedó en disco.</summary>
        public bool EmergencySave()
        {
            try
            {
                if (view != null) view.FlushEditor();
                if (Store.Dirty) Store.Save();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                return false;
            }
        }

        public void Notify(string text)
        {
            if (view != null && view.Win.IsVisible) view.ShowToast(text);
            else Balloon(text);
        }

        public void Exit()
        {
            if (exiting) return;
            exiting = true;
            if (view != null) view.Win.Close();
            EmergencySave();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            if (hotkeySource != null)
            {
                Native.UnregisterHotKey(hotkeySource.Handle, HotkeyId);
                hotkeySource.Dispose();
            }
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            if (trayIcon != IntPtr.Zero) Native.DestroyIcon(trayIcon);
            App.Shutdown();
        }
    }
}
