using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PsaReader
{
    internal static class DiagnosticsExport
    {
        internal const string Version="0.3.0-test";
        internal static string BuildId
        {
            get{using(var sha=SHA256.Create())using(var input=File.OpenRead(typeof(DiagnosticsExport).Assembly.Location))return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
        }
        internal static void RecordError(Exception error)
        {
            try{string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-application-error.txt"),"PSA Diagnostika "+Version+"\r\nBuild SHA256: "+BuildId+"\r\n"+DateTimeOffset.Now.ToString("o")+"\r\n"+error,Encoding.UTF8);}catch{ /* Never hide the original error behind a logging failure. */ }
        }
        internal static void Create(string destination,Catalogue catalogue,VehicleDefinition vehicle,string logDirectory=null)
        {
            string folder=logDirectory??Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");
            var files=Directory.Exists(folder)?Directory.GetFiles(folder).Where(p=>new[]{".txt",".json",".jsonl"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)).OrderBy(p=>p).ToArray():new string[0];
            if(files.Sum(p=>new FileInfo(p).Length)>256L*1024*1024)throw new IOException("Logy přesahují 256 MB. Starší odečty nejprve přesuňte do archivu.");
            using(var output=new FileStream(destination,FileMode.Create,FileAccess.Write,FileShare.None))using(var zip=new ZipArchive(output,ZipArchiveMode.Create)){
                var manifest=zip.CreateEntry("INFO.txt");using(var text=new StreamWriter(manifest.Open(),Encoding.UTF8)){
                    text.WriteLine("PSA Diagnostika "+Version);text.WriteLine("Build SHA256: "+BuildId);text.WriteLine("Export: "+DateTimeOffset.Now.ToString("o"));text.WriteLine("Windows: "+Environment.OSVersion+" / "+(Environment.Is64BitOperatingSystem?"64":"32")+" bit");text.WriteLine(".NET: "+Environment.Version);
                    text.WriteLine("Databáze: "+catalogue.Revision+" / "+catalogue.Vehicles.Length+" profilů / "+catalogue.Ecus.Length+" ECU");
                    text.WriteLine("Vybraný profil při exportu: "+(vehicle==null?"žádný":vehicle.Id+" / "+vehicle.Title));
                    text.WriteLine("Archiv obsahuje všechny místní logy, i z jiných profilů. Profil jednotlivého odečtu je v jeho JSON/textu. Obsah může zahrnovat VIN a identifikace jednotek.");
                    text.WriteLine("TX/RX záznamy obsahují odpovědi modelu, firmware a sériového čísla adaptéru. Číslo FW není samo o sobě ověřením kompatibility. Firmware se neaktualizuje.");
                    text.WriteLine("Soubory: "+files.Length);foreach(string file in files)text.WriteLine(Path.GetFileName(file)+" / "+new FileInfo(file).Length+" B");
                }
                foreach(string file in files){var entry=zip.CreateEntry("logs/"+Path.GetFileName(file),CompressionLevel.Optimal);using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var target=entry.Open())input.CopyTo(target);}
            }
        }
    }
}
