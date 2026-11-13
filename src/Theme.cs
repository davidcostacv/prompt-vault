using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PromptVault
{
    /// <summary>Tokens de color. Claro/Oscuro cambian solo estos pinceles; todo el XAML usa DynamicResource.</summary>
    static class Theme
    {
        static readonly string[] Keys =
            { "Bg", "Sidebar", "Surface2", "CodeBg", "Hover", "Selected", "Border", "Text", "Muted", "Faint", "Accent", "AccentText", "Danger", "DangerText", "ScrollThumb" };
        static readonly string[] Dark =
            { "#111318", "#15181E", "#1C1F27", "#0D0F13", "#22262F", "#2A2519", "#262A33", "#ECEEF2", "#8A90A0", "#5D6372", "#F0B44C", "#1A1405", "#E5484D", "#FFFFFF", "#3A3F4B" };
        static readonly string[] Light =
            { "#F7F6F3", "#EFEDE8", "#FFFFFF", "#FFFFFF", "#E7E4DD", "#F5E6C6", "#DDD9D0", "#1B1C20", "#686C75", "#9A9DA5", "#A86B0A", "#FFFFFF", "#C6363B", "#FFFFFF", "#C9C5BC" };

        public static bool CurrentIsDark { get; private set; }

        public static bool SystemIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v == 0;
                }
            }
            catch (Exception) { }
            return false;
        }

        public static bool Resolve(string mode)
        {
            if (mode == "dark") return true;
            if (mode == "light") return false;
            return SystemIsDark();
        }

        public static void Apply(ResourceDictionary res, bool dark)
        {
            CurrentIsDark = dark;
            var vals = dark ? Dark : Light;
            for (int i = 0; i < Keys.Length; i++)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(vals[i]));
                b.Freeze();
                res[Keys[i]] = b;
            }
        }

        /// <summary>Barra de título del mismo color que la app (Windows 11).</summary>
        public static void ApplyChrome(Window w, bool dark)
        {
            try
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                var vals = dark ? Dark : Light;
                int on = dark ? 1 : 0;
                Native.DwmSetWindowAttribute(h, 20, ref on, 4);          // modo oscuro
                int caption = ColorRef(vals[0]);
                Native.DwmSetWindowAttribute(h, 35, ref caption, 4);     // color de la barra
                int text = ColorRef(vals[7]);
                Native.DwmSetWindowAttribute(h, 36, ref text, 4);        // color del texto
                int border = ColorRef(vals[6]);
                Native.DwmSetWindowAttribute(h, 34, ref border, 4);      // borde
            }
            catch (Exception) { }
        }

        static int ColorRef(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return c.R | (c.G << 8) | (c.B << 16);
        }
    }
}
