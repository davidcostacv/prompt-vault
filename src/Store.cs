using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PromptVault
{
    public class Category
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class Prompt
    {
        public string Id { get; set; }
        public string CategoryId { get; set; }   // null = "Sin categoría"
        public string Title { get; set; }
        public string Body { get; set; }
        public string CreatedAt { get; set; }    // ISO 8601 UTC
        public string UpdatedAt { get; set; }
    }

    public class Settings
    {
        public Settings() { Theme = "auto"; Wrap = true; }
        public string Theme { get; set; }        // auto | light | dark
        public bool Wrap { get; set; }
        public double WinLeft { get; set; }
        public double WinTop { get; set; }
        public double WinWidth { get; set; }
        public double WinHeight { get; set; }
        public bool FirstRunDone { get; set; }
    }

    public class DataFile
    {
        public DataFile()
        {
            Version = 1;
            Categories = new List<Category>();
            Prompts = new List<Prompt>();
            Settings = new Settings();
        }
        public int Version { get; set; }
        public List<Category> Categories { get; set; }
        public List<Prompt> Prompts { get; set; }
        public Settings Settings { get; set; }
    }

    public class ImportResult
    {
        public int Prompts;
        public int Categories;
        public int Skipped;
    }

    /// <summary>
    /// Todo el estado vive en un solo JSON. Cada guardado escribe un .tmp, lo fuerza
    /// al disco y lo cambia por el archivo real con File.Replace (atómico en NTFS),
    /// dejando la versión anterior en prompts.json.bak.
    /// </summary>
    public class Store
    {
        public const string FileName = "prompts.json";
        public const int MaxCategoryName = 60;
        public static int DailyBackupsToKeep = 7;
        readonly object sync = new object();

        Store() { }

        public string Dir { get; private set; }
        public string FilePath { get { return Path.Combine(Dir, FileName); } }
        public string BakPath { get { return FilePath + ".bak"; } }
        public string BackupDir { get { return Path.Combine(Dir, "backups"); } }
        public DataFile Data { get; private set; }
        public bool Dirty { get; private set; }
        public string LoadWarning { get; private set; }

        public static string DefaultDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PromptVault");
        }

        public static Store Open(string dir)
        {
            var s = new Store();
            s.Dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(s.Dir);
            s.Load();
            return s;
        }

        // ---------- JSON ----------

        static JavaScriptSerializer Ser()
        {
            var j = new JavaScriptSerializer();
            j.MaxJsonLength = int.MaxValue;   // el default (2 MB) rompería prompts largos
            j.RecursionLimit = 64;
            return j;
        }

        public static string Serialize(DataFile d)
        {
            return Ser().Serialize(d);
        }

        public static DataFile Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("El archivo está vacío.");
            var d = Ser().Deserialize<DataFile>(json);
            if (d == null) throw new InvalidDataException("El archivo no tiene datos.");
            Normalize(d);
            return d;
        }

        static void Normalize(DataFile d)
        {
            if (d.Settings == null) d.Settings = new Settings();
            if (d.Settings.Theme != "light" && d.Settings.Theme != "dark") d.Settings.Theme = "auto";

            d.Categories = (d.Categories ?? new List<Category>()).Where(c => c != null).ToList();
            var catIds = new HashSet<string>();
            foreach (var c in d.Categories)
            {
                if (string.IsNullOrEmpty(c.Id) || catIds.Contains(c.Id)) c.Id = NewId();
                catIds.Add(c.Id);
                c.Name = string.IsNullOrWhiteSpace(c.Name) ? "Sin nombre" : c.Name.Trim();
            }

            d.Prompts = (d.Prompts ?? new List<Prompt>()).Where(p => p != null).ToList();
            var promptIds = new HashSet<string>();
            foreach (var p in d.Prompts)
            {
                if (string.IsNullOrEmpty(p.Id) || promptIds.Contains(p.Id)) p.Id = NewId();
                promptIds.Add(p.Id);
                if (p.Title == null) p.Title = "";
                if (p.Body == null) p.Body = "";
                if (p.CategoryId != null && !catIds.Contains(p.CategoryId)) p.CategoryId = null;
                if (string.IsNullOrEmpty(p.CreatedAt)) p.CreatedAt = Now();
                if (string.IsNullOrEmpty(p.UpdatedAt)) p.UpdatedAt = p.CreatedAt;
            }
        }

        // ---------- Cargar ----------

        void Load()
        {
            var candidates = new List<string> { FilePath, BakPath };
            if (Directory.Exists(BackupDir))
                candidates.AddRange(Directory.GetFiles(BackupDir, "prompts-*.json").OrderByDescending(f => f, StringComparer.Ordinal));

            if (!candidates.Any(File.Exists))
            {
                Data = new DataFile();   // primera vez
                return;
            }

            var errors = new List<string>();
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                // Un archivo bloqueado (antivirus al arrancar Windows) NO está dañado: ReadWithRetry lanza IOException
                // y la app no arranca, en vez de "recuperar" una versión vieja y pisar la nueva.
                string json = ReadWithRetry(path);
                try
                {
                    Data = Parse(json);
                }
                catch (Exception ex)
                {
                    errors.Add(Path.GetFileName(path) + ": " + ex.Message);
                    continue;
                }
                if (path != FilePath)
                {
                    // Sacar el dañado del camino para que no termine como .bak ni como backup diario.
                    if (!File.Exists(FilePath) || PreserveCorrupt(FilePath)) TryDelete(FilePath);
                    LoadWarning = "El archivo principal estaba dañado o no existía. Se recuperaron tus prompts desde "
                        + Path.GetFileName(path) + ". Si existía una copia dañada, quedó guardada en la carpeta de datos.";
                    Dirty = true;
                    try { Save(); } catch (Exception) { /* queda Dirty: se reintenta en el próximo guardado */ }
                }
                return;
            }

            // Nada se pudo leer: guardar copias de lo dañado antes de empezar vacío.
            PreserveCorrupt(FilePath);
            PreserveCorrupt(BakPath);
            Data = new DataFile();
            LoadWarning = "No se pudo leer ningún archivo de datos (" + string.Join("; ", errors)
                + "). Se empezó con una lista vacía; las copias dañadas quedaron en la carpeta de datos.";
        }

        static string ReadWithRetry(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return File.ReadAllText(path, Encoding.UTF8); }
                catch (IOException) { if (attempt >= 9) throw; }
                catch (UnauthorizedAccessException) { if (attempt >= 9) throw; }
                Thread.Sleep(300);   // hasta ~3 s esperando a que el antivirus suelte el archivo
            }
        }

        /// <summary>Guarda una copia del archivo dañado. Devuelve true si quedó copiado.</summary>
        bool PreserveCorrupt(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Copy(path, Path.Combine(Dir, "corrupt-" + stamp + "-" + Path.GetFileName(path)), true);
                return true;
            }
            catch (Exception) { return false; }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        // ---------- Guardar ----------

        public void MarkDirty() { Dirty = true; }

        public void Save()
        {
            lock (sync)
            {
                SnapshotDailyBackup();
                WriteAtomic(FilePath, Serialize(Data), BakPath);
                Dirty = false;
            }
        }

        /// <summary>Escribe a .tmp, hace flush al disco y reemplaza el destino de forma atómica.</summary>
        public static void WriteAtomic(string path, string content, string backupPath)
        {
            string tmp = path + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(content);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            Exception last = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, backupPath, true);
                    else File.Move(tmp, path);
                    return;
                }
                catch (IOException ex) { last = ex; }
                catch (UnauthorizedAccessException ex) { last = ex; }
                Thread.Sleep(50 * (attempt + 1));   // antivirus o indexador con el archivo abierto
            }
            throw new IOException("No se pudo reemplazar " + Path.GetFileName(path) + ": " + last.Message, last);
        }

        /// <summary>Antes del primer guardado del día, copia el archivo actual (estado del día anterior).</summary>
        void SnapshotDailyBackup()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                Directory.CreateDirectory(BackupDir);
                string today = Path.Combine(BackupDir, "prompts-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");
                if (!File.Exists(today)) File.Copy(FilePath, today);
                var old = Directory.GetFiles(BackupDir, "prompts-*.json")
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .Skip(DailyBackupsToKeep).ToList();
                foreach (var f in old) File.Delete(f);
            }
            catch (Exception) { /* el backup diario es un extra; el guardado principal sigue */ }
        }

        // ---------- Categorías ----------

        public Category FindCategory(string id)
        {
            if (id == null) return null;
            return Data.Categories.FirstOrDefault(c => c.Id == id);
        }

        public Category FindCategoryByName(string name)
        {
            name = (name ?? "").Trim();
            return Data.Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase));
        }

        /// <summary>Devuelve el problema con el nombre, o null si sirve.</summary>
        public string ValidateCategoryName(string name, string exceptId)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "Escribe un nombre.";
            if (name.Length > MaxCategoryName) return "Máximo " + MaxCategoryName + " caracteres.";
            var other = FindCategoryByName(name);
            if (other != null && other.Id != exceptId) return "Ya existe una categoría con ese nombre.";
            return null;
        }

        public Category AddCategory(string name)
        {
            string problem = ValidateCategoryName(name, null);
            if (problem != null) throw new ArgumentException(problem);
            var c = new Category { Id = NewId(), Name = name.Trim() };
            Data.Categories.Add(c);
            Dirty = true;
            return c;
        }

        public void RenameCategory(string id, string name)
        {
            var c = FindCategory(id);
            if (c == null) throw new ArgumentException("La categoría no existe.");
            string problem = ValidateCategoryName(name, id);
            if (problem != null) throw new ArgumentException(problem);
            c.Name = name.Trim();
            Dirty = true;
        }

        /// <summary>Borra la categoría; sus prompts pasan a "Sin categoría". Devuelve cuántos se movieron.</summary>
        public int DeleteCategory(string id)
        {
            int moved = 0;
            foreach (var p in Data.Prompts)
            {
                if (p.CategoryId == id) { p.CategoryId = null; moved++; }
            }
            Data.Categories.RemoveAll(c => c.Id == id);
            Dirty = true;
            return moved;
        }

        public int CountIn(string categoryId)
        {
            return Data.Prompts.Count(p => p.CategoryId == categoryId);
        }

        // ---------- Prompts ----------

        public Prompt FindPrompt(string id)
        {
            if (id == null) return null;
            return Data.Prompts.FirstOrDefault(p => p.Id == id);
        }

        public Prompt AddPrompt(string categoryId, string title, string body)
        {
            string now = Now();
            var p = new Prompt
            {
                Id = NewId(),
                CategoryId = FindCategory(categoryId) == null ? null : categoryId,
                Title = title ?? "",
                Body = body ?? "",
                CreatedAt = now,
                UpdatedAt = now
            };
            Data.Prompts.Add(p);
            Dirty = true;
            return p;
        }

        public Prompt Duplicate(Prompt src)
        {
            string title = string.IsNullOrWhiteSpace(src.Title) ? "Sin título (copia)" : src.Title + " (copia)";
            return AddPrompt(src.CategoryId, title, src.Body);
        }

        public void DeletePrompt(Prompt p)
        {
            Data.Prompts.Remove(p);
            Dirty = true;
        }

        public void Touch(Prompt p)
        {
            p.UpdatedAt = Now();
            Dirty = true;
        }

        // ---------- Búsqueda (solo título) ----------

        /// <summary>Todas las palabras de la búsqueda deben aparecer en el título, sin importar mayúsculas ni tildes.</summary>
        public static bool TitleMatches(string title, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            title = title ?? "";
            var ci = CultureInfo.InvariantCulture.CompareInfo;
            foreach (var token in query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (ci.IndexOf(title, token, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) < 0) return false;
            }
            return true;
        }

        public List<Prompt> Search(string query)
        {
            return Data.Prompts.Where(p => TitleMatches(p.Title, query)).ToList();
        }

        public static string DisplayTitle(Prompt p)
        {
            return string.IsNullOrWhiteSpace(p.Title) ? "Sin título" : p.Title;
        }

        // ---------- Exportar / Importar ----------

        public void Export(string path)
        {
            lock (sync)
            {
                WriteAtomic(path, Serialize(Data), null);
            }
        }

        /// <summary>Agrega (no reemplaza). Categorías con el mismo nombre se unen; prompts idénticos se saltan.</summary>
        public ImportResult Import(string path)
        {
            var src = Parse(File.ReadAllText(path, Encoding.UTF8));
            var r = new ImportResult();
            var map = new Dictionary<string, string>();
            foreach (var c in src.Categories)
            {
                var existing = FindCategoryByName(c.Name);
                if (existing == null)
                {
                    existing = new Category { Id = NewId(), Name = c.Name };
                    Data.Categories.Add(existing);
                    r.Categories++;
                }
                map[c.Id] = existing.Id;
            }
            foreach (var p in src.Prompts)
            {
                string cat = (p.CategoryId != null && map.ContainsKey(p.CategoryId)) ? map[p.CategoryId] : null;
                bool duplicate = Data.Prompts.Any(x => x.CategoryId == cat && x.Title == p.Title && x.Body == p.Body);
                if (duplicate) { r.Skipped++; continue; }
                Data.Prompts.Add(new Prompt
                {
                    Id = NewId(),
                    CategoryId = cat,
                    Title = p.Title,
                    Body = p.Body,
                    CreatedAt = p.CreatedAt,
                    UpdatedAt = p.UpdatedAt
                });
                r.Prompts++;
            }
            if (r.Prompts + r.Categories > 0) Dirty = true;
            return r;
        }

        // ---------- util ----------

        public static string NewId() { return Guid.NewGuid().ToString("N"); }

        public static string Now() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    }
}
