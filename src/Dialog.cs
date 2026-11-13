using System;
using System.Windows;
using System.Windows.Controls;

namespace PromptVault
{
    /// <summary>Diálogos con el mismo estilo de la app (en vez del MessageBox gris de Windows).</summary>
    static class Dialog
    {
        /// <summary>Pide un texto. validate devuelve el error a mostrar o null. Devuelve null si cancelan.</summary>
        public static string AskText(Window owner, string heading, string message, string initial, string okText, Func<string, string> validate)
        {
            var w = Load(owner, heading, message, okText, false);
            var input = (TextBox)w.FindName("Input");
            var error = (TextBlock)w.FindName("Error");
            var ok = (Button)w.FindName("OkBtn");
            input.Text = initial ?? "";
            string result = null;
            ok.Click += delegate
            {
                string value = input.Text.Trim();
                string problem = validate == null ? null : validate(value);
                if (problem != null)
                {
                    error.Text = problem;
                    error.Visibility = Visibility.Visible;
                    input.Focus();
                    input.SelectAll();
                    return;
                }
                result = value;
                w.DialogResult = true;
            };
            w.Loaded += delegate { input.Focus(); input.SelectAll(); };
            w.ShowDialog();
            return result;
        }

        public static bool Confirm(Window owner, string heading, string message, string okText, bool danger)
        {
            var w = Load(owner, heading, message, okText, danger);
            ((UIElement)w.FindName("Input")).Visibility = Visibility.Collapsed;
            var ok = (Button)w.FindName("OkBtn");
            var cancel = (Button)w.FindName("CancelBtn");
            ok.Click += delegate { w.DialogResult = true; };
            w.Loaded += delegate { cancel.Focus(); };   // Enter no borra nada por accidente
            return w.ShowDialog() == true;
        }

        public static void Info(Window owner, string heading, string message)
        {
            var w = Load(owner, heading, message, "Entendido", false);
            ((UIElement)w.FindName("Input")).Visibility = Visibility.Collapsed;
            ((UIElement)w.FindName("CancelBtn")).Visibility = Visibility.Collapsed;
            var ok = (Button)w.FindName("OkBtn");
            ok.IsCancel = true;
            ok.Click += delegate { w.Close(); };
            w.Loaded += delegate { ok.Focus(); };
            w.ShowDialog();
        }

        static Window Load(Window owner, string heading, string message, string okText, bool danger)
        {
            var w = (Window)Res.LoadXaml("Dialog.xaml");
            if (owner != null && owner.IsVisible) w.Owner = owner;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ((TextBlock)w.FindName("Heading")).Text = heading;
            var msg = (TextBlock)w.FindName("Message");
            if (string.IsNullOrEmpty(message)) msg.Visibility = Visibility.Collapsed;
            else msg.Text = message;
            var ok = (Button)w.FindName("OkBtn");
            ok.Content = okText;
            if (danger)
            {
                ok.Style = (Style)Application.Current.FindResource("DangerBtn");
                ok.IsDefault = false;
            }
            w.SourceInitialized += delegate { Theme.ApplyChrome(w, Theme.CurrentIsDark); };
            return w;
        }
    }
}
