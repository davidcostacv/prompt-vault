using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PromptVault.Tests
{
    static class Program
    {
        static int passes, fails;

        static int Main()
        {
            Run("roundtrip", Roundtrip);
            Run("big body > 2MB", BigBody);
            Run("bak y sin tmp", BakAndNoTmp);
            Run("main danado -> recupera de .bak", CorruptMainRecovers);
            Run("todo danado -> vacio y copias", AllCorrupt);
            Run("archivo bloqueado no es danado", LockedFileIsNotCorrupt);
            Run("tmp sobrante no molesta", LeftoverTmp);
            Run("borrar categoria mueve prompts", DeleteCategoryMoves);
            Run("validacion de nombres", CategoryValidation);
            Run("busqueda por titulo", SearchTitle);
            Run("importar une y salta repetidos", ImportMerge);
            Run("normaliza JSON raro", Normalize);
            Run("backup diario y poda", DailyBackup);
            Run("duplicar", Duplicate);

            Console.WriteLine(passes + " ok, " + fails + " fallas");
            return fails == 0 ? 0 : 1;
        }

        static void Run(string name, Action test)
        {
            try { test(); }
            catch (Exception ex) { fails++; Console.WriteLine("EXCEPCION en '" + name + "': " + ex); }
        }

        static void Check(bool ok, string what)
        {
            if (ok) passes++;
            else { fails++; Console.WriteLine("FALLA: " + what); }
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "pv-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Roundtrip()
        {
            string d = TempDir();
            var s = Store.Open(d);
            var c = s.AddCategory("Código");
            string body = "línea 1\r\n\tindentada {\"json\": \"x\"}\n\\ barra <tag> & 😀 ñ\n\n";
            s.AddPrompt(c.Id, "Revisar PR", body);
            s.Save();

            var s2 = Store.Open(d);
            Check(s2.LoadWarning == null, "sin advertencia al cargar");
            Check(s2.Data.Prompts.Count == 1, "1 prompt");
            var p = s2.Data.Prompts[0];
            Check(p.Body == body, "texto identico (saltos, tabs, emoji)");
            Check(p.Title == "Revisar PR" && p.CategoryId == c.Id, "titulo y categoria");
            Check(s2.Data.Categories[0].Name == "Código", "nombre con tilde");
        }

        static void BigBody()
        {
            string d = TempDir();
            var s = Store.Open(d);
            string body = new string('x', 3000000) + "\nfin";
            s.AddPrompt(null, "Enorme", body);
            s.Save();
            var s2 = Store.Open(d);
            Check(s2.LoadWarning == null && s2.Data.Prompts[0].Body == body, "3 MB ida y vuelta");
        }

        static void BakAndNoTmp()
        {
            string d = TempDir();
            var s = Store.Open(d);
            s.AddPrompt(null, "A", "a");
            s.Save();
            s.AddPrompt(null, "B", "b");
            s.Save();
            Check(File.Exists(s.BakPath), "existe .bak");
            Check(Store.Parse(File.ReadAllText(s.BakPath)).Prompts.Count == 1, ".bak = version anterior");
            Check(!File.Exists(s.FilePath + ".tmp"), "no queda .tmp");
        }

        static void CorruptMainRecovers()
        {
            string d = TempDir();
            var s = Store.Open(d);
            s.AddPrompt(null, "A", "a");
            s.Save();
            s.AddPrompt(null, "B", "b");
            s.Save();
            File.WriteAllText(s.FilePath, "{\"Prompts\": [ {\"Title\": \"a mitad", Encoding.UTF8);

            var s2 = Store.Open(d);
            Check(s2.LoadWarning != null, "avisa que recupero");
            Check(s2.Data.Prompts.Count == 1 && s2.Data.Prompts[0].Title == "A", "datos desde .bak");
            Check(Directory.GetFiles(d, "corrupt-*").Length >= 1, "guardo copia danada");
            Check(Store.Parse(File.ReadAllText(s2.FilePath)).Prompts.Count == 1, "principal reparado");
            Check(Store.Parse(File.ReadAllText(s2.BakPath)).Prompts.Count == 1, ".bak bueno no se pisa con el danado");
            string today = Path.Combine(s2.BackupDir, "prompts-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json");
            Check(!File.Exists(today) || Store.Parse(File.ReadAllText(today)) != null, "backup diario no queda con el archivo danado");
        }

        static void LockedFileIsNotCorrupt()
        {
            string d = TempDir();
            var s = Store.Open(d);
            s.AddPrompt(null, "A", "a");
            s.Save();
            s.AddPrompt(null, "B", "b");
            s.Save();   // principal = A,B ; .bak = A
            bool threw = false;
            using (new FileStream(s.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { Store.Open(d); }
                catch (IOException) { threw = true; }
            }
            Check(threw, "archivo bloqueado -> error, no 'recuperar' version vieja");
            var s2 = Store.Open(d);
            Check(s2.LoadWarning == null && s2.Data.Prompts.Count == 2, "al desbloquear carga los datos nuevos (A,B)");
        }

        static void AllCorrupt()
        {
            string d = TempDir();
            File.WriteAllText(Path.Combine(d, "prompts.json"), "basura", Encoding.UTF8);
            File.WriteAllText(Path.Combine(d, "prompts.json.bak"), "mas basura", Encoding.UTF8);
            var s = Store.Open(d);
            Check(s.LoadWarning != null, "avisa");
            Check(s.Data.Prompts.Count == 0, "empieza vacio");
            Check(Directory.GetFiles(d, "corrupt-*").Length == 2, "preserva las 2 copias danadas");
        }

        static void LeftoverTmp()
        {
            string d = TempDir();
            var s = Store.Open(d);
            s.AddPrompt(null, "A", "a");
            s.Save();
            File.WriteAllText(s.FilePath + ".tmp", "basura de un guardado cortado", Encoding.UTF8);
            var s2 = Store.Open(d);
            Check(s2.LoadWarning == null && s2.Data.Prompts.Count == 1, "carga el principal");
            s2.AddPrompt(null, "B", "b");
            s2.Save();
            Check(Store.Open(d).Data.Prompts.Count == 2, "guarda encima del tmp");
        }

        static void DeleteCategoryMoves()
        {
            var s = Store.Open(TempDir());
            var c = s.AddCategory("Emails");
            s.AddPrompt(c.Id, "1", "");
            s.AddPrompt(c.Id, "2", "");
            s.AddPrompt(null, "3", "");
            int moved = s.DeleteCategory(c.Id);
            Check(moved == 2, "movio 2");
            Check(s.Data.Prompts.Count == 3, "no borro prompts");
            Check(s.Data.Prompts.All(p => p.CategoryId == null), "todos sin categoria");
            Check(s.Data.Categories.Count == 0, "categoria borrada");
        }

        static void CategoryValidation()
        {
            var s = Store.Open(TempDir());
            var c = s.AddCategory("Código");
            var e = s.AddCategory("Emails");
            Check(Throws(() => s.AddCategory("código")), "duplicado sin importar mayusculas");
            Check(Throws(() => s.AddCategory("   ")), "vacio");
            Check(Throws(() => s.AddCategory(new string('a', 61))), "muy largo");
            Check(Throws(() => s.RenameCategory(e.Id, "CÓDIGO")), "renombrar a uno existente");
            s.RenameCategory(c.Id, "CÓDIGO");
            Check(s.FindCategory(c.Id).Name == "CÓDIGO", "renombrar a si mismo con otras mayusculas");
            s.RenameCategory(c.Id, "  Dev  ");
            Check(s.FindCategory(c.Id).Name == "Dev", "recorta espacios");
        }

        static void SearchTitle()
        {
            Check(Store.TitleMatches("Correo de seguimiento a cliente", "SEGUIMIENTO"), "mayusculas");
            Check(Store.TitleMatches("Revisión de código", "revision codigo"), "tildes");
            Check(Store.TitleMatches("Plan de marketing", "marketing plan"), "palabras en otro orden");
            Check(!Store.TitleMatches("Plan", "plano"), "no coincide");
            Check(Store.TitleMatches("Lo que sea", "   "), "busqueda vacia = todo");
            var s = Store.Open(TempDir());
            s.AddPrompt(null, "Título A", "contenido secreto");
            Check(s.Search("secreto").Count == 0, "no busca en el texto");
            Check(s.Search("titulo").Count == 1, "busca en el titulo");
        }

        static void ImportMerge()
        {
            string da = TempDir();
            var a = Store.Open(da);
            var ac = a.AddCategory("código");
            var ae = a.AddCategory("Emails");
            a.AddPrompt(ac.Id, "X", "cuerpo x");
            a.AddPrompt(ae.Id, "Y", "cuerpo y");
            string export = Path.Combine(da, "export.json");
            a.Export(export);

            var b = Store.Open(TempDir());
            var bc = b.AddCategory("Código");
            b.AddPrompt(bc.Id, "X", "cuerpo x");
            var r = b.Import(export);
            Check(r.Categories == 1, "1 categoria nueva (Emails)");
            Check(r.Prompts == 1 && r.Skipped == 1, "1 nuevo, 1 repetido");
            var y = b.Data.Prompts.First(p => p.Title == "Y");
            Check(b.FindCategory(y.CategoryId).Name == "Emails", "Y quedo en Emails");
            Check(b.Data.Categories.Count == 2, "no duplico Codigo");
        }

        static void Normalize()
        {
            var d = Store.Parse("{\"Version\":1,\"Categories\":null,\"Prompts\":[{\"Id\":\"a\",\"CategoryId\":\"nope\",\"Title\":null,\"Body\":\"x\"},{\"Id\":\"a\",\"Title\":\"dup\"}],\"Settings\":{\"Theme\":\"rosa\"}}");
            Check(d.Categories != null && d.Categories.Count == 0, "lista nula -> vacia");
            Check(d.Prompts[0].CategoryId == null, "categoria inexistente -> sin categoria");
            Check(d.Prompts[0].Title == "" && d.Prompts[1].Body == "", "nulos -> texto vacio");
            Check(d.Prompts[0].Id != d.Prompts[1].Id, "ids repetidos se arreglan");
            Check(d.Settings.Theme == "auto", "tema invalido -> auto");
        }

        static void DailyBackup()
        {
            string d = TempDir();
            string bdir = Path.Combine(d, "backups");
            var s = Store.Open(d);
            s.AddPrompt(null, "A", "a");
            s.Save();   // crea el principal
            Check(!Directory.Exists(bdir), "el primer guardado no crea backup (no habia archivo previo)");
            Directory.CreateDirectory(bdir);
            for (int i = 1; i <= 9; i++)
                File.WriteAllText(Path.Combine(bdir, "prompts-2020-01-0" + i + ".json"), "{}");
            s.AddPrompt(null, "B", "b");
            s.Save();   // primer guardado "del dia" con principal existente -> backup
            string today = Path.Combine(bdir, "prompts-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json");
            Check(File.Exists(today), "backup de hoy");
            Check(Store.Parse(File.ReadAllText(today)).Prompts.Count == 1, "backup = estado antes del cambio");
            Check(Directory.GetFiles(bdir, "prompts-*.json").Length == Store.DailyBackupsToKeep, "quedan 7");
        }

        static void Duplicate()
        {
            var s = Store.Open(TempDir());
            var c = s.AddCategory("Dev");
            var p = s.AddPrompt(c.Id, "Original", "texto");
            var copy = s.Duplicate(p);
            Check(copy.Id != p.Id && copy.Title == "Original (copia)" && copy.Body == "texto" && copy.CategoryId == c.Id, "copia igual con id nuevo");
        }

        static bool Throws(Action a)
        {
            try { a(); return false; }
            catch (ArgumentException) { return true; }
        }
    }
}
