using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PromptVault
{
    public class CatItem : INotifyPropertyChanged
    {
        public const string All = "__all";
        public const string None = "__none";
        public string Id { get; set; }
        public string Name { get; set; }
        public int Count { get; set; }
        public bool IsReal { get { return Id != All && Id != None; } }
        // Se reconstruye en cada refresco; implementa la interfaz para que WPF no use el binding lento/con fuga.
        public event PropertyChangedEventHandler PropertyChanged { add { } remove { } }
    }

    public class PromptItem : INotifyPropertyChanged
    {
        public PromptItem(Prompt p, string subtitle) { P = p; Subtitle = subtitle; }
        public Prompt P { get; private set; }
        public string Title { get { return Store.DisplayTitle(P); } }
        public string Subtitle { get; private set; }
        public void RaiseTitle()
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs("Title"));
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>La ventana principal. Se crea al abrirla y se destruye al cerrarla (así la RAM baja en la bandeja).</summary>
    public class MainView
    {
        static readonly CultureInfo Es = new CultureInfo("es-ES");
        static readonly StringComparer TitleOrder = StringComparer.Create(Es, true);

        readonly AppHost host;
        readonly Store store;
        readonly ObservableCollection<CatItem> cats = new ObservableCollection<CatItem>();
        readonly ObservableCollection<PromptItem> items = new ObservableCollection<PromptItem>();
        readonly DispatcherTimer saveTimer, toastTimer;

        readonly TextBox searchBox, titleBox, bodyBox;
        readonly TextBlock searchHint, titleHint, bodyHint, listTitle, listEmpty, statusText, countText, catPickText, toastText, hotkeyHint;
        readonly ListBox catList, promptList;
        readonly Button menuBtn, addCatBtn, newBtn, copyBtn, dupBtn, delBtn, catPickBtn;
        readonly ToggleButton wrapToggle;
        readonly FrameworkElement editorPane, editorEmpty;
        readonly Border toast;
        readonly Image logo;

        string selectedCat;
        Prompt current;
        bool loadingEditor, suppressSelection, bodyPending;

        public Window Win { get; private set; }
        public string SelectedCategory { get { return selectedCat; } }
        public string CurrentId { get { return current == null ? null : current.Id; } }

        public MainView(AppHost host)
        {
            this.host = host;
            store = host.Store;
            Win = (Window)Res.LoadXaml("MainWindow.xaml");

            searchBox = F<TextBox>("SearchBox");
            titleBox = F<TextBox>("TitleBox");
            bodyBox = F<TextBox>("BodyBox");
            searchHint = F<TextBlock>("SearchHint");
            titleHint = F<TextBlock>("TitleHint");
            bodyHint = F<TextBlock>("BodyHint");
            listTitle = F<TextBlock>("ListTitle");
            listEmpty = F<TextBlock>("ListEmpty");
            statusText = F<TextBlock>("StatusText");
            countText = F<TextBlock>("CountText");
            catPickText = F<TextBlock>("CatPickText");
            toastText = F<TextBlock>("ToastText");
            hotkeyHint = F<TextBlock>("HotkeyHint");
            catList = F<ListBox>("CatList");
            promptList = F<ListBox>("PromptList");
            menuBtn = F<Button>("MenuBtn");
            addCatBtn = F<Button>("AddCatBtn");
            newBtn = F<Button>("NewBtn");
            copyBtn = F<Button>("CopyBtn");
            dupBtn = F<Button>("DupBtn");
            delBtn = F<Button>("DelBtn");
            catPickBtn = F<Button>("CatPickBtn");
            wrapToggle = F<ToggleButton>("WrapToggle");
            editorPane = F<FrameworkElement>("EditorPane");
            editorEmpty = F<FrameworkElement>("EditorEmpty");
            toast = F<Border>("Toast");
            logo = F<Image>("Logo");

            Win.Icon = host.WindowIcon;
            logo.Source = host.LogoImage;
            catList.ItemsSource = cats;
            promptList.ItemsSource = items;
            bodyBox.UndoLimit = 200;     // sin límite, la historia de deshacer crece sin parar en una app 24/7
            titleBox.UndoLimit = 50;
            wrapToggle.IsChecked = store.Data.Settings.Wrap;
            ApplyWrap();
            hotkeyHint.Text = host.HotkeyOk ? "Ctrl+Alt+P abre PromptVault desde cualquier lugar" : "Ctrl+Alt+P lo está usando otra app";
            statusText.Text = "Se guarda solo";

            saveTimer = new DispatcherTimer(DispatcherPriority.Background, Win.Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveNow(); };
            toastTimer = new DispatcherTimer(DispatcherPriority.Normal, Win.Dispatcher) { Interval = TimeSpan.FromMilliseconds(1500) };
            toastTimer.Tick += delegate { HideToast(); };

            Wire();
            RestoreBounds();
            selectedCat = host.LastCategory ?? CatItem.All;
            RefreshCategories();
            RefreshPrompts(store.FindPrompt(host.LastPromptId));
            UpdateHints();
        }

        T F<T>(string name) where T : class
        {
            var o = Win.FindName(name) as T;
            if (o == null) throw new InvalidOperationException("Falta el elemento " + name);
            return o;
        }

        void Wire()
        {
            Win.SourceInitialized += delegate { Theme.ApplyChrome(Win, host.IsDark); };
            Win.PreviewKeyDown += OnWindowKey;
            Win.Deactivated += delegate { SaveNow(); };
            Win.Closing += delegate { saveTimer.Stop(); SaveBounds(); SaveNow(); };
            Win.Closed += delegate { saveTimer.Stop(); toastTimer.Stop(); host.OnMainClosed(this); };

            searchBox.TextChanged += delegate { UpdateHints(); RefreshPrompts(current); };
            searchBox.PreviewKeyDown += OnSearchKey;
            titleBox.TextChanged += OnTitleChanged;
            bodyBox.TextChanged += OnBodyChanged;

            catList.SelectionChanged += OnCategorySelected;
            catList.MouseRightButtonUp += OnCategoryRightClick;
            catList.MouseDoubleClick += delegate (object s, MouseButtonEventArgs e)
            {
                var c = ItemUnder<CatItem>(catList, e.OriginalSource);
                if (c != null && c.IsReal) RenameCategory(c);
            };
            promptList.SelectionChanged += OnPromptSelected;
            promptList.MouseRightButtonUp += OnPromptRightClick;
            promptList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnRowButton));

            menuBtn.Click += delegate { ShowAppMenu(); };
            addCatBtn.Click += delegate { AddCategory(true); };
            newBtn.Click += delegate { NewPrompt(); };
            copyBtn.Click += delegate { Copy(current); };
            dupBtn.Click += delegate { Duplicate(); };
            delBtn.Click += delegate { DeletePrompt(current); };
            catPickBtn.Click += delegate { ShowCategoryPicker(); };
            wrapToggle.Click += delegate
            {
                store.Data.Settings.Wrap = wrapToggle.IsChecked == true;
                ApplyWrap();
                ScheduleSave();
            };
        }

        // ================= Públicos (los usa AppHost) =================

        public void FocusSearch()
        {
            searchBox.Focus();
            Keyboard.Focus(searchBox);
            searchBox.SelectAll();
        }

        public void SetSearch(string text) { searchBox.Text = text; }

        public void OnThemeChanged() { Theme.ApplyChrome(Win, host.IsDark); }

        /// <summary>Pasa el texto del editor al modelo. Leer bodyBox.Text arma un string nuevo, por eso no se hace en cada tecla.</summary>
        public void FlushEditor()
        {
            if (current == null) return;
            if (bodyPending)
            {
                current.Body = bodyBox.Text;
                bodyPending = false;
            }
            UpdateCount(current.Body);
            bodyHint.Visibility = current.Body.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public bool SaveNow()
        {
            FlushEditor();
            if (!store.Dirty) return true;
            try
            {
                store.Save();
                statusText.Text = "Guardado " + DateTime.Now.ToString("HH:mm");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                statusText.Text = "⚠ No se pudo guardar, reintentando… (" + ex.Message + ")";
                saveTimer.Stop();
                saveTimer.Start();
                return false;
            }
        }

        public void ShowToast(string text)
        {
            toastText.Text = text;
            toastTimer.Stop();
            var tt = toast.RenderTransform as TranslateTransform;
            if (tt == null)
            {
                tt = new TranslateTransform();
                toast.RenderTransform = tt;
            }
            if (SystemParameters.ClientAreaAnimation)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            }
            else
            {
                toast.BeginAnimation(UIElement.OpacityProperty, null);
                toast.Opacity = 1;
            }
            toastTimer.Start();
        }

        void HideToast()
        {
            toastTimer.Stop();
            if (SystemParameters.ClientAreaAnimation)
                toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
            else
            {
                toast.BeginAnimation(UIElement.OpacityProperty, null);
                toast.Opacity = 0;
            }
        }

        // ================= Listas =================

        void RefreshCategories()
        {
            suppressSelection = true;
            try
            {
                cats.Clear();
                cats.Add(new CatItem { Id = CatItem.All, Name = "Todos", Count = store.Data.Prompts.Count });
                foreach (var c in store.Data.Categories.OrderBy(c => c.Name, TitleOrder))
                    cats.Add(new CatItem { Id = c.Id, Name = c.Name, Count = store.CountIn(c.Id) });
                int none = store.CountIn(null);
                if (none > 0 || selectedCat == CatItem.None)
                    cats.Add(new CatItem { Id = CatItem.None, Name = "Sin categoría", Count = none });

                var sel = cats.FirstOrDefault(c => c.Id == selectedCat);
                if (sel == null)
                {
                    sel = cats[0];
                    selectedCat = CatItem.All;
                }
                catList.SelectedItem = sel;
            }
            finally { suppressSelection = false; }
        }

        bool Searching { get { return searchBox.Text.Trim().Length > 0; } }

        void RefreshPrompts(Prompt keep)
        {
            string q = searchBox.Text.Trim();
            bool searching = q.Length > 0;
            IEnumerable<Prompt> source = searching
                ? store.Search(q)
                : store.Data.Prompts.Where(p => InCategory(p, selectedCat));
            var list = source.OrderBy(p => Store.DisplayTitle(p), TitleOrder).ToList();
            bool showCategory = searching || selectedCat == CatItem.All;

            PromptItem sel = null;
            suppressSelection = true;
            try
            {
                items.Clear();
                foreach (var p in list)
                {
                    var it = new PromptItem(p, Subtitle(p, showCategory));
                    items.Add(it);
                    if (p == keep) sel = it;
                }
                if (sel == null && items.Count > 0) sel = items[0];
                promptList.SelectedItem = sel;
            }
            finally { suppressSelection = false; }

            Prompt selP = sel == null ? null : sel.P;
            if (selP != current) LoadEditor(selP);
            if (sel != null) promptList.ScrollIntoView(sel);

            listTitle.Text = searching ? "Resultados" : CategoryTitle(selectedCat);
            if (items.Count > 0) listEmpty.Visibility = Visibility.Collapsed;
            else
            {
                listEmpty.Visibility = Visibility.Visible;
                listEmpty.Text = searching
                    ? "Ningún título coincide con “" + q + "”."
                    : store.Data.Prompts.Count == 0
                        ? "Todavía no tienes prompts.\nCrea el primero con “Nuevo”."
                        : "No hay prompts en esta categoría.";
            }
        }

        bool InCategory(Prompt p, string catId)
        {
            if (catId == CatItem.All) return true;
            if (catId == CatItem.None) return p.CategoryId == null;
            return p.CategoryId == catId;
        }

        string CategoryName(string id)
        {
            var c = store.FindCategory(id);
            return c == null ? "Sin categoría" : c.Name;
        }

        string CategoryTitle(string id)
        {
            if (id == CatItem.All) return "Todos los prompts";
            return CategoryName(id == CatItem.None ? null : id);
        }

        string Subtitle(Prompt p, bool withCategory)
        {
            string when = FriendlyDate(p.UpdatedAt);
            return withCategory ? CategoryName(p.CategoryId) + "  ·  " + when : when;
        }

        static string FriendlyDate(string iso)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) return "";
            t = t.ToLocalTime();
            var today = DateTime.Today;
            if (t.Date == today) return "hoy, " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return "ayer";
            if (t.Year == today.Year) return t.ToString("d 'de' MMM", Es);
            return t.ToString("d MMM yyyy", Es);
        }

        PromptItem FindItem(Prompt p)
        {
            return items.FirstOrDefault(i => i.P == p);
        }

        static T ItemUnder<T>(ItemsControl list, object source) where T : class
        {
            var d = source as DependencyObject;
            if (d == null) return null;
            var container = ItemsControl.ContainerFromElement(list, d) as FrameworkElement;
            return container == null ? null : container.DataContext as T;
        }

        // ================= Editor =================

        void LoadEditor(Prompt p)
        {
            FlushEditor();   // primero guarda lo que se escribió en el prompt anterior
            current = p;
            loadingEditor = true;
            try
            {
                if (p == null)
                {
                    editorPane.Visibility = Visibility.Collapsed;
                    editorEmpty.Visibility = Visibility.Visible;
                    SetText(titleBox, "");
                    SetText(bodyBox, "");
                }
                else
                {
                    editorPane.Visibility = Visibility.Visible;
                    editorEmpty.Visibility = Visibility.Collapsed;
                    SetText(titleBox, p.Title);
                    SetText(bodyBox, p.Body);
                    bodyBox.CaretIndex = 0;
                    bodyBox.ScrollToHome();
                    catPickText.Text = CategoryName(p.CategoryId);
                    UpdateCount(p.Body);
                    bodyHint.Visibility = p.Body.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            finally { loadingEditor = false; }
            UpdateHints();
        }

        /// <summary>Cambia el texto sin dejarlo en el historial: Ctrl+Z no debe traer el texto de otro prompt.</summary>
        static void SetText(TextBox tb, string text)
        {
            tb.IsUndoEnabled = false;
            tb.Text = text ?? "";
            tb.IsUndoEnabled = true;
        }

        void OnTitleChanged(object sender, TextChangedEventArgs e)
        {
            UpdateHints();
            if (loadingEditor || current == null) return;
            current.Title = titleBox.Text;
            store.Touch(current);
            var it = FindItem(current);
            if (it != null) it.RaiseTitle();
            ScheduleSave();
        }

        void OnBodyChanged(object sender, TextChangedEventArgs e)
        {
            if (loadingEditor || current == null) return;
            bodyPending = true;
            if (bodyHint.Visibility == Visibility.Visible) bodyHint.Visibility = Visibility.Collapsed;
            store.Touch(current);
            ScheduleSave();
        }

        void ScheduleSave()
        {
            store.MarkDirty();
            statusText.Text = "Guardando…";
            saveTimer.Stop();
            saveTimer.Start();
        }

        void UpdateHints()
        {
            searchHint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            titleHint.Visibility = titleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateCount(string body)
        {
            int lines = 0;
            if (body.Length > 0)
            {
                lines = 1;
                foreach (char ch in body) if (ch == '\n') lines++;
            }
            countText.Text = string.Format(Es, "{0:N0} caracteres  ·  {1:N0} {2}", body.Length, lines, lines == 1 ? "línea" : "líneas");
        }

        void ApplyWrap()
        {
            bool wrap = wrapToggle.IsChecked == true;
            bodyBox.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            bodyBox.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        }

        // ================= Acciones de prompts =================

        void Copy(Prompt p)
        {
            if (p == null) return;
            if (p == current) FlushEditor();
            if (string.IsNullOrEmpty(p.Body))
            {
                ShowToast("Este prompt está vacío");
                return;
            }
            string error;
            if (Clip.TrySetText(p.Body, out error)) ShowToast("Copiado  ✓");
            else ShowToast("No se pudo copiar: otra app está usando el portapapeles. Intenta de nuevo.");
        }

        void NewPrompt()
        {
            FlushEditor();
            string cat = (selectedCat == CatItem.All || selectedCat == CatItem.None) ? null : selectedCat;
            var p = store.AddPrompt(cat, "", "");
            if (searchBox.Text.Length > 0) searchBox.Text = "";
            SaveNow();
            RefreshCategories();
            RefreshPrompts(p);
            titleBox.Focus();
        }

        void Duplicate()
        {
            if (current == null) return;
            FlushEditor();
            var p = store.Duplicate(current);
            SaveNow();
            RefreshCategories();
            RefreshPrompts(p);
            titleBox.Focus();
            titleBox.SelectAll();
        }

        void DeletePrompt(Prompt p)
        {
            if (p == null) return;
            if (!Dialog.Confirm(Win, "¿Eliminar «" + Store.DisplayTitle(p) + "»?",
                "Se borra el prompt y su texto. No se puede deshacer.", "Eliminar", true))
                return;
            // current no se pone en null aquí: RefreshPrompts debe detectar el cambio y vaciar el editor;
            // si no, el editor seguiría mostrando el prompt borrado y lo escrito se perdería.
            if (p == current) bodyPending = false;
            store.DeletePrompt(p);
            SaveNow();
            RefreshCategories();
            RefreshPrompts(null);
        }

        void OnPromptSelected(object sender, SelectionChangedEventArgs e)
        {
            if (suppressSelection) return;
            var it = promptList.SelectedItem as PromptItem;
            LoadEditor(it == null ? null : it.P);
        }

        void OnRowButton(object sender, RoutedEventArgs e)
        {
            var b = e.OriginalSource as Button;
            if (b == null) return;
            var it = b.Tag as PromptItem;
            if (it == null) return;
            Copy(it.P);
            e.Handled = true;
        }

        void OnPromptRightClick(object sender, MouseButtonEventArgs e)
        {
            var it = ItemUnder<PromptItem>(promptList, e.OriginalSource);
            if (it == null) return;
            promptList.SelectedItem = it;
            var m = NewMenu(promptList, PlacementMode.MousePoint);
            m.Items.Add(Item("Copiar texto", "Ctrl+Enter", delegate { Copy(it.P); }));
            m.Items.Add(Item("Duplicar", "Ctrl+D", delegate { Duplicate(); }));
            m.Items.Add(Sep());
            m.Items.Add(Item("Eliminar…", null, delegate { DeletePrompt(it.P); }));
            m.IsOpen = true;
            e.Handled = true;
        }

        void ShowCategoryPicker()
        {
            if (current == null) return;
            var m = NewMenu(catPickBtn, PlacementMode.Bottom);
            foreach (var c in store.Data.Categories.OrderBy(c => c.Name, TitleOrder))
            {
                string id = c.Id;
                m.Items.Add(Check(Item(c.Name, null, delegate { MoveCurrent(id); }), current.CategoryId == id));
            }
            if (store.Data.Categories.Count > 0) m.Items.Add(Sep());
            m.Items.Add(Check(Item("Sin categoría", null, delegate { MoveCurrent(null); }), current.CategoryId == null));
            m.Items.Add(Item("Nueva categoría…", null, delegate
            {
                var c = AddCategory(false);
                if (c != null) MoveCurrent(c.Id);
            }));
            m.IsOpen = true;
        }

        void MoveCurrent(string catId)
        {
            if (current == null || current.CategoryId == catId) return;
            FlushEditor();
            current.CategoryId = catId;
            store.Touch(current);
            catPickText.Text = CategoryName(catId);
            // Sigue la nota a su nueva categoría para que no desaparezca de la lista.
            if (!Searching && selectedCat != CatItem.All) selectedCat = catId ?? CatItem.None;
            SaveNow();
            RefreshCategories();
            RefreshPrompts(current);
        }

        // ================= Categorías =================

        void OnCategorySelected(object sender, SelectionChangedEventArgs e)
        {
            if (suppressSelection) return;
            var c = catList.SelectedItem as CatItem;
            if (c == null) return;
            selectedCat = c.Id;
            if (searchBox.Text.Length > 0) searchBox.Text = "";   // TextChanged refresca
            else RefreshPrompts(current);
        }

        void OnCategoryRightClick(object sender, MouseButtonEventArgs e)
        {
            var c = ItemUnder<CatItem>(catList, e.OriginalSource);
            if (c == null || !c.IsReal) return;
            var m = NewMenu(catList, PlacementMode.MousePoint);
            m.Items.Add(Item("Renombrar…", null, delegate { RenameCategory(c); }));
            m.Items.Add(Item("Eliminar categoría…", null, delegate { DeleteCategory(c); }));
            m.IsOpen = true;
            e.Handled = true;
        }

        Category AddCategory(bool select)
        {
            string name = Dialog.AskText(Win, "Nueva categoría", "Ponle un nombre corto, por ejemplo «Código» o «Emails».", "", "Crear",
                n => store.ValidateCategoryName(n, null));
            if (name == null) return null;
            var c = store.AddCategory(name);
            SaveNow();
            if (select)
            {
                selectedCat = c.Id;
                if (searchBox.Text.Length > 0) searchBox.Text = "";
            }
            RefreshCategories();
            RefreshPrompts(current);
            return c;
        }

        void RenameCategory(CatItem item)
        {
            string id = item.Id;
            string name = Dialog.AskText(Win, "Renombrar categoría", null, item.Name, "Guardar",
                n => store.ValidateCategoryName(n, id));
            if (name == null) return;
            store.RenameCategory(id, name);
            SaveNow();
            RefreshCategories();
            RefreshPrompts(current);
            if (current != null) catPickText.Text = CategoryName(current.CategoryId);
        }

        void DeleteCategory(CatItem item)
        {
            int count = store.CountIn(item.Id);
            string msg = count == 0
                ? "La categoría está vacía."
                : (count == 1 ? "Su prompt pasa" : "Sus " + count + " prompts pasan") + " a «Sin categoría». No se borra ningún prompt.";
            if (!Dialog.Confirm(Win, "¿Eliminar la categoría «" + item.Name + "»?", msg, "Eliminar categoría", true)) return;
            FlushEditor();
            store.DeleteCategory(item.Id);
            if (selectedCat == item.Id) selectedCat = CatItem.All;
            SaveNow();
            RefreshCategories();
            RefreshPrompts(current);
            if (current != null) catPickText.Text = CategoryName(current.CategoryId);
        }

        // ================= Menú de opciones =================

        void ShowAppMenu()
        {
            var m = NewMenu(menuBtn, PlacementMode.Bottom);
            var auto = Item("Iniciar con Windows", null, delegate { host.SetAutostart(!host.AutostartEnabled); });
            auto.IsChecked = host.AutostartEnabled;
            auto.IsEnabled = host.CanManageAutostart;
            m.Items.Add(auto);
            m.Items.Add(Sep());
            string mode = store.Data.Settings.Theme;
            m.Items.Add(Check(Item("Tema automático (Windows)", null, delegate { host.SetTheme("auto"); }), mode == "auto"));
            m.Items.Add(Check(Item("Tema claro", null, delegate { host.SetTheme("light"); }), mode == "light"));
            m.Items.Add(Check(Item("Tema oscuro", null, delegate { host.SetTheme("dark"); }), mode == "dark"));
            m.Items.Add(Sep());
            m.Items.Add(Item("Exportar backup…", null, delegate { Export(); }));
            m.Items.Add(Item("Importar prompts…", null, delegate { Import(); }));
            m.Items.Add(Item("Abrir carpeta de datos", null, delegate { Process.Start("explorer.exe", "\"" + store.Dir + "\""); }));
            m.Items.Add(Sep());
            m.Items.Add(Item("Ocultar ventana", "Esc", delegate { Win.Close(); }));
            m.Items.Add(Item("Salir de PromptVault", null, delegate { host.Exit(); }));
            m.IsOpen = true;
        }

        void Export()
        {
            SaveNow();
            var dlg = new SaveFileDialog
            {
                Title = "Exportar backup de PromptVault",
                Filter = "Backup JSON (*.json)|*.json",
                FileName = "PromptVault-backup-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(Win) != true) return;
            try
            {
                store.Export(dlg.FileName);
                ShowToast("Backup exportado  ✓");
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                Dialog.Info(Win, "No se pudo exportar", ex.Message);
            }
        }

        void Import()
        {
            SaveNow();
            var dlg = new OpenFileDialog
            {
                Title = "Importar prompts",
                Filter = "Backup JSON (*.json)|*.json|Todos los archivos (*.*)|*.*"
            };
            if (dlg.ShowDialog(Win) != true) return;
            try
            {
                var r = store.Import(dlg.FileName);
                SaveNow();
                RefreshCategories();
                RefreshPrompts(current);
                string extra = r.Skipped > 0 ? " Se saltaron " + r.Skipped + " que ya tenías." : "";
                Dialog.Info(Win, "Importación lista",
                    "Se agregaron " + r.Prompts + " prompts y " + r.Categories + " categorías nuevas." + extra + " No se borró nada.");
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                Dialog.Info(Win, "No se pudo importar", "El archivo no es un backup válido de PromptVault.\n\n" + ex.Message);
            }
        }

        static ContextMenu NewMenu(UIElement target, PlacementMode placement)
        {
            return new ContextMenu { PlacementTarget = target, Placement = placement };
        }

        static MenuItem Item(string header, string gesture, RoutedEventHandler click)
        {
            var mi = new MenuItem { Header = header };
            if (gesture != null) mi.InputGestureText = gesture;
            mi.Click += click;
            return mi;
        }

        static MenuItem Check(MenuItem mi, bool on)
        {
            mi.IsChecked = on;
            return mi;
        }

        static Separator Sep()
        {
            return new Separator { Style = (Style)Application.Current.FindResource("MenuSep") };
        }

        // ================= Teclado =================

        void OnWindowKey(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (mods == ModifierKeys.Control && key == Key.F) { FocusSearch(); e.Handled = true; }
            else if (mods == ModifierKeys.Control && key == Key.N) { NewPrompt(); e.Handled = true; }
            else if (mods == ModifierKeys.Control && key == Key.Enter) { Copy(current); e.Handled = true; }
            else if (mods == ModifierKeys.Control && key == Key.D) { Duplicate(); e.Handled = true; }
            else if (mods == ModifierKeys.None && key == Key.Escape)
            {
                if (searchBox.IsKeyboardFocusWithin && searchBox.Text.Length > 0) searchBox.Text = "";
                else Win.Close();
                e.Handled = true;
            }
        }

        void OnSearchKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (items.Count == 0) return;
                int i = promptList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                promptList.SelectedIndex = Math.Max(0, Math.Min(items.Count - 1, i));
                promptList.ScrollIntoView(promptList.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                Copy(current);
                e.Handled = true;
            }
        }

        // ================= Tamaño y posición =================

        void RestoreBounds()
        {
            var st = store.Data.Settings;
            if (st.WinWidth < 400 || st.WinHeight < 300) return;
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            // La barra de título tiene que quedar dentro de alguna pantalla.
            if (!screen.Contains(new Point(st.WinLeft + 60, st.WinTop + 10))) return;
            Win.WindowStartupLocation = WindowStartupLocation.Manual;
            Win.Left = st.WinLeft;
            Win.Top = st.WinTop;
            Win.Width = st.WinWidth;
            Win.Height = st.WinHeight;
        }

        void SaveBounds()
        {
            Rect r = Win.WindowState == WindowState.Normal ? new Rect(Win.Left, Win.Top, Win.Width, Win.Height) : Win.RestoreBounds;
            if (r.IsEmpty || double.IsNaN(r.Left) || double.IsNaN(r.Width)) return;
            var st = store.Data.Settings;
            if (st.WinLeft == r.Left && st.WinTop == r.Top && st.WinWidth == r.Width && st.WinHeight == r.Height) return;
            st.WinLeft = r.Left;
            st.WinTop = r.Top;
            st.WinWidth = r.Width;
            st.WinHeight = r.Height;
            store.MarkDirty();
        }
    }
}
