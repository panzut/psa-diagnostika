using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PsaReader
{
    // Inventory is read-only registry inspection; live transport lives in Vci.cs.
    public static class Inspector
    {
        public static string Inventory()
        {
            var text = new StringBuilder();
            text.AppendLine("PSA Diagnostika — místní kontrola počítače");
            text.AppendLine("Čas kontroly: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            text.AppendLine("Tato kontrola neotevírá COM port a nekomunikuje s autem.\r\n");
            text.AppendLine("AKTUÁLNÍ SÉRIOVÉ PORTY (evidence Windows)");
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
                {
                    if (key == null || key.GetValueNames().Length == 0)
                        text.AppendLine("Žádný port nebyl nalezen.");
                    else
                        foreach (string name in key.GetValueNames())
                            text.AppendLine(Convert.ToString(key.GetValue(name)) + " — " + name);
                }
            }
            catch (Exception e) { text.AppendLine("Nelze přečíst porty: " + e.Message); }

            text.AppendLine("\r\nEVIDENCE OVLADAČE AUTOCOM USB 0403:D6DA");
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_0403&PID_D6DA"))
                {
                    if (key == null) text.AppendLine("Ovladač není v této větvi registru evidován.");
                    else foreach (string instance in key.GetSubKeyNames())
                        using (var device = key.OpenSubKey(instance))
                        {
                            if (device == null) continue;
                            text.AppendLine("Popis: " + device.GetValue("DeviceDesc", "neuvedeno"));
                            text.AppendLine("Služba: " + device.GetValue("Service", "neuvedeno"));
                            text.AppendLine("Záznam může přetrvávat i po odpojení zařízení.");
                        }
                }
            }
            catch (Exception e) { text.AppendLine("Nelze přečíst evidenci USB: " + e.Message); }

            text.AppendLine("\r\nREGISTROVANÁ ROZHRANÍ J2534");
            bool any = false, complete = true;
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                if (view == RegistryView.Registry64 && !Environment.Is64BitOperatingSystem) continue;
                foreach (string version in new[] { "04.04", "05.00" })
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = root.OpenSubKey(@"SOFTWARE\PassThruSupport." + version))
                    {
                        if (key == null) continue;
                        foreach (string name in key.GetSubKeyNames())
                        using (var device = key.OpenSubKey(name))
                        {
                            if (device == null) continue;
                            any = true;
                            text.AppendLine(view + " / " + version + ": " + device.GetValue("Name", name));
                            text.AppendLine("Knihovna: " + device.GetValue("FunctionLibrary", "neuvedena"));
                        }
                    }
                }
                catch (Exception e) { complete = false; text.AppendLine(view + ": kontrola selhala — " + e.Message); }
            }
            if (!any) text.AppendLine(complete ? "Žádné nalezeno. Knihovny nebyly načteny." : "Žádné nalezeno v dostupných větvích; kontrola není úplná.");
            text.AppendLine("\r\nŽIVÁ DIAGNOSTIKA: samostatná karta ABS — jednorázové čtení ověřeného profilu.");
            text.AppendLine("Nalezený port ani ovladač nepotvrzuje spojení s vozidlem.");
            return text.ToString();
        }
    }

    public static class Transcript
    {
        // Input must already contain complete ECU application payloads, without
        // CAN IDs, ISO-TP framing, FTDI status bytes, or DS150E envelopes.
        public static string DecodeLine(string line)
        {
            if (String.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) return "";
            var match = Regex.Match(line.Trim(), @"^(TX|RX)\s+([0-9a-fA-F]{2}(?:\s+[0-9a-fA-F]{2})*)$", RegexOptions.IgnoreCase);
            if (!match.Success) return "NEZPRACOVÁNO — očekává se TX/RX a úplný hexadecimální obsah zprávy ECU.";
            string[] parts = Regex.Split(match.Groups[2].Value, @"\s+");
            var bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++) bytes[i] = Convert.ToByte(parts[i], 16);
            bool request = match.Groups[1].Value.Equals("TX", StringComparison.OrdinalIgnoreCase);
            int service = bytes[0];
            if (request)
            {
                switch (service)
                {
                    case 0x01: case 0x02: case 0x03: case 0x07: case 0x09: case 0x0A:
                        return "Záznam požadavku EOBD ke čtení (služba " + service.ToString("X2") + "). Nic se neodesílá.";
                    case 0x19: return "Záznam požadavku UDS na čtení DTC. Nic se neodesílá.";
                    case 0x21: return "Možný požadavek KWP na čtení lokálních dat; význam vyžaduje profil ECU.";
                    case 0x22: return "Záznam požadavku na čtení identifikátoru dat; význam vyžaduje profil ECU.";
                    case 0x04: case 0x14: return "MAZÁNÍ CHYB v importovaném záznamu. Program tento příkaz neumí odeslat.";
                    case 0x2E: case 0x3B: case 0x2F: case 0x30: case 0x31: case 0x34: case 0x36: case 0x37:
                        return "ZMĚNA / ŘÍZENÍ / PROGRAMOVÁNÍ v importovaném záznamu. Program nic neodesílá.";
                    default: return "Neklasifikovaný požadavek " + service.ToString("X2") + "; nelze označit za pouhé čtení.";
                }
            }
            if (service == 0x7F)
                return bytes.Length == 3 ? "Negativní odpověď: služba " + bytes[1].ToString("X2") + ", kód " + bytes[2].ToString("X2") + ". Význam závisí na protokolu." : "Neplatná délka negativní odpovědi.";
            if (service == 0x43 || service == 0x47 || service == 0x4A)
            {
                if ((bytes.Length - 1) % 2 != 0) return "Neúplná odpověď EOBD DTC — lichý počet datových bajtů.";
                var codes = new List<string>();
                for (int i = 1; i < bytes.Length; i += 2)
                {
                    if (bytes[i] == 0 && bytes[i + 1] == 0) continue;
                    codes.Add("PCBU"[bytes[i] >> 6] + ((bytes[i] >> 4) & 3).ToString() + (bytes[i] & 15).ToString("X") + bytes[i + 1].ToString("X2"));
                }
                return codes.Count == 0 ? "V tomto úplném EOBD payloadu nejsou kódy DTC. Nevypovídá to o ostatních jednotkách." : "Kódy EOBD: " + String.Join(", ", codes.ToArray()) + ". Bez odhadování PSA popisů.";
            }
            if (service == 0x62) return "Odpověď na čtení dat. Pro převod hodnot chybí ověřená definice dané ECU.";
            if (service == 0x59) return "Odpověď UDS DTC; tento prototyp ji ponechává bez dekódování.";
            return "Přijatý obsah ponechán bez interpretace (služba " + service.ToString("X2") + ").";
        }

        public static string Analyze(string source)
        {
            if (source.Length > 1024 * 1024) throw new ArgumentException("Maximální velikost textu je 1 MB.");
            var result = new StringBuilder("OFFLINE ROZBOR — importované zprávy, nikoli aktuální stav auta\r\n\r\n");
            int number = 0;
            foreach (string line in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                number++;
                string decoded = DecodeLine(line);
                if (decoded.Length == 0) continue;
                result.AppendLine(number + ": " + line);
                result.AppendLine("    " + decoded);
            }
            return result.ToString();
        }
    }

    public class ReaderForm : Form
    {
        readonly TextBox inventory = Box(true);
        readonly TextBox input = Box(false);
        readonly TextBox output = Box(true);
        readonly TextBox liveOutput = Box(true);
        bool busy;
        static TextBox Box(bool readOnly)
        {
            return new TextBox { Multiline = true, ReadOnly = readOnly, ScrollBars = ScrollBars.Both,
                WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 10), MaxLength = 1024 * 1024 };
        }
        static Button Button(string caption, EventHandler action)
        {
            var b = new Button { Text = caption, AutoSize = true, Height = 32, Margin = new Padding(6) };
            b.Click += action; return b;
        }
        public ReaderForm()
        {
            Text = "PSA Diagnostika — čtečka ABS pro Phedru";
            Size = new Size(1050, 740); MinimumSize = new Size(780, 540); StartPosition = FormStartPosition.CenterScreen;
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var hardware = new TabPage("Připojení a ovladače");
            var logs = new TabPage("Rozbor zpráv offline");
            var profile = new TabPage("Phedra — ověřené poznatky");
            var live = new TabPage("ABS — čtení z auta");
            tabs.TabPages.AddRange(new[] { live, hardware, logs, profile });
            var heading = new Label { Dock = DockStyle.Top, Height = 62, Padding = new Padding(12),
                Text = "Lancia Phedra 2007 · Bosch ABS 8.1 ESP PSA · vlastní čtečka přes DS150E\r\nJednorázové čtení závad. Bez mazání, kódování a aktualizací.", Font = new Font("Segoe UI", 10) };
            Controls.Add(tabs); Controls.Add(heading);
            var liveBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48 };
            var parked = new CheckBox { Text = "Auto stojí, je připojené a zapalování je zapnuté. Delphi je zavřené.", Dock = DockStyle.Top, Height = 38, Padding = new Padding(8) };
            var status = new Label { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(8), Text = "Připraveno. Připojení se spustí pouze tlačítkem." };
            Action<bool> read = abs => {
                if (busy) return;
                if (abs && !parked.Checked) { status.Text = "Nejprve potvrďte, že auto stojí a je připravené ke čtení."; return; }
                busy = true; liveBar.Enabled = false; parked.Enabled = false;
                liveOutput.Text = "Probíhá " + (abs ? "čtení ABS" : "kontrola krabičky") + "…";
                var worker = new BackgroundWorker { WorkerReportsProgress = true };
                worker.DoWork += delegate(object sender, DoWorkEventArgs e) { e.Result = AbsReader.Run(abs, message => worker.ReportProgress(0, message)); };
                worker.ProgressChanged += delegate(object sender, ProgressChangedEventArgs e) { status.Text = Convert.ToString(e.UserState); };
                worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e) {
                    busy = false; liveBar.Enabled = true; parked.Enabled = true; parked.Checked = false;
                    if (e.Error != null) { liveOutput.Text = "Čtení se nepodařilo dokončit: " + e.Error.Message; status.Text = "Chyba — viz podrobnosti."; }
                    else {
                        var result = (ReadResult)e.Result;
                        liveOutput.Text = result.Report + "\r\nZáznam: " + result.LogPath;
                        status.Text = result.Success ? "Dokončeno. Port je zavřený; zobrazen je poslední odečet." : "Čtení nebo ukončení nebylo potvrzeno — viz podrobnosti.";
                    }
                    worker.Dispose();
                };
                worker.RunWorkerAsync();
            };
            liveBar.Controls.Add(Button("Ověřit krabičku", delegate { read(false); }));
            liveBar.Controls.Add(Button("Přečíst závady ABS", delegate { read(true); }));
            liveBar.Controls.Add(Button("Poslední uložený odečet", delegate {
                try {
                    string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                    string[] files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*-abs-*.txt.report.txt") : new string[0];
                    if (files.Length == 0) { status.Text = "Dosud není uložený žádný odečet ABS."; return; }
                    Array.Sort(files, StringComparer.Ordinal);
                    string file = files[files.Length - 1];
                    liveOutput.Text = "ULOŽENÝ ODEČET — nejde o nové připojení\r\n\r\n" + File.ReadAllText(file, Encoding.UTF8) + "\r\nSoubor: " + file;
                    status.Text = "Zobrazen uložený výsledek. Program není připojený k autu.";
                } catch (Exception e) { status.Text = "Soubor nelze přečíst: " + e.Message; }
            }));
            liveBar.Controls.Add(Button("Uložit přehled…", delegate { Save(liveOutput.Text); }));            liveOutput.Text = "Pomocný profil ABS pro Phedru. Pro ostatní jednotky použijte hlavní výběr vozidla. Výsledky se ukládají do složky logs.";
            live.Controls.Add(liveOutput); live.Controls.Add(parked); live.Controls.Add(liveBar); live.Controls.Add(status);
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "Počkejte na ukončení spojení; čtení má omezené časové limity."; } };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48 };
            bar.Controls.Add(Button("Znovu přečíst evidenci Windows", delegate { inventory.Text = Inspector.Inventory(); }));
            bar.Controls.Add(Button("Uložit místní přehled…", delegate { Save(inventory.Text); }));
            hardware.Controls.Add(inventory); hardware.Controls.Add(bar);
            inventory.Text = "Zvolte „Znovu přečíst evidenci Windows“. Neprobíhá žádné automatické připojování.";

            var logBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48 };
            logBar.Controls.Add(Button("Otevřít textový záznam…", delegate {
                using (var dialog = new OpenFileDialog { Filter = "Textové záznamy|*.txt;*.log|Všechny soubory|*.*" })
                    if (dialog.ShowDialog() == DialogResult.OK)
                        try {
                            if (new FileInfo(dialog.FileName).Length > 1024 * 1024) throw new IOException("Maximální velikost souboru je 1 MB.");
                            input.Text = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                        } catch (Exception e) { MessageBox.Show(e.Message, "Soubor nelze načíst"); }
            }));
            logBar.Controls.Add(Button("Rozebrat text", delegate {
                try { output.Text = Transcript.Analyze(input.Text); }
                catch (Exception e) { MessageBox.Show(e.Message, "Rozbor nelze provést"); }
            }));
            logBar.Controls.Add(Button("Uložit rozbor…", delegate { Save(output.Text); }));
            var help = new Label { Dock = DockStyle.Top, Height = 58, Padding = new Padding(8),
                Text = "Vstup: jeden úplný aplikační payload ECU na řádek, např. TX 03 nebo RX 43 01 33.\r\nBez CAN adres, ISO-TP hlaviček a obalu DS150E. Surové USB/PCAP záznamy zatím nepodporujeme." };
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(input); split.Panel2.Controls.Add(output);
            logs.Controls.Add(split); logs.Controls.Add(help); logs.Controls.Add(logBar);
            input.Text = "# Sem vložte vlastní záznam; ukázková data nejsou údaje z auta.";
            var details = Box(true);            details.Text = "PSA Diagnostika — testovací verze. Podporu a omezení popisuje CTI-ME.txt. Ověření krabičky samo o sobě nepotvrzuje spojení s vozidlem.";
            profile.Controls.Add(details);
        }
        static void Save(string contents)
        {
            using (var dialog = new SaveFileDialog { Filter = "Textový přehled|*.txt", FileName = "psa-prehled.txt", OverwritePrompt = true })
                if (dialog.ShowDialog() == DialogResult.OK)
                    try { File.WriteAllText(dialog.FileName, contents, Encoding.UTF8); }
                    catch (Exception e) { MessageBox.Show(e.Message, "Přehled nelze uložit"); }
        }
    }
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try{return Run(args);}catch(Exception e){DiagnosticsExport.RecordError(e);Console.Error.WriteLine("CHYBA: "+e.Message);return 1;}
        }
        static int Run(string[] args)
        {
            if(args.Length==4&&args[0]=="--service-reset"&&args[2]=="--parked"&&args[3]=="--engine-off-ignition-on"){
                int km;if(!Int32.TryParse(args[1],out km))throw new ArgumentException("Zadejte interval v kilometrech.");ServiceMaintenance.ValidateKm(km);
                var c=Catalogue.Load();var v=Array.Find(c.Vehicles,x=>x.Id=="VCAN:all");var r=BsiServices.Run(c,v,BsiAction.ServiceReset,true,Console.WriteLine,km);Console.WriteLine("Záznam: "+r.LogPath);return r.Success?0:1;
            }
            if (args.Length == 1 && args[0] == "--self-test") { SelfTest(); VciTests.Run(); DiscoveryTests.Run(); ModuleTests.Run(); CabinProbeTests.Run(); BulkTests.Run(); AccessoryTests.Run(); AccessoryRunnerTests.Run(); UniversalTests.Run(); FaultClearTests.Run(); return BsiServiceTests.Run(); }
            if(args.Length==3&&args[1]=="--parked"&&args[2]=="--engine-off-ignition-on"&&(args[0]=="--daylight-off"||args[0]=="--daylight-on"||args[0]=="--economy-off")){
                var c=Catalogue.Load();var v=Array.Find(c.Vehicles,x=>x.Id=="VCAN:all");var a=args[0]=="--economy-off"?BsiAction.EconomyOff:args[0]=="--daylight-on"?BsiAction.DaylightOn:BsiAction.DaylightOff;
                var r=BsiServices.Run(c,v,a,true,Console.WriteLine);Console.WriteLine("Záznam: "+r.LogPath);return r.Success?0:1;
            }
            if ((args.Length==2&&args[0]=="--read-all"&&args[1]=="--parked")||(args.Length==3&&args[0]=="--read-node"&&args[2]=="--parked")) {var c=Catalogue.Load();var v=Array.Find(c.Vehicles,x=>x.Id=="VCAN:all");var r=BulkReader.Run(c,v,()=>false,Console.WriteLine,u=>Console.WriteLine(u.Unit.Address+" "+u.Unit.State+" / "+u.Reads.Count+" records / "+u.Error),args[0]=="--read-node"?args[1]:null);Console.WriteLine("Záznam: "+r.LogPath);return r.Completed?0:1;}
            if (args.Length == 2 && args[0] == "--bsi-coding-readiness" && args[1] == "--parked") return BsiCodingReadiness.Run();
            if (args.Length == 2 && args[0] == "--cabin-ident" && args[1] == "--parked") return CabinProbe.Run();
            if (args.Length == 2 && args[0] == "--decode-all") return BulkReader.Reprocess(args[1]);
            if (args.Length == 2 && args[0] == "--cabin-session-ident" && args[1] == "--parked") return CabinProbe.Run(true);
            if (args.Length == 1 && args[0] == "--catalog") { var c=Catalogue.Load(); Console.WriteLine(c.Vehicles.Length+" vehicle versions / "+c.Ecus.Length+" ECU definitions"); foreach(var route in CanRoute.Plan(c,Array.Find(c.Vehicles,v=>v.Id=="VCAN:all"))) Console.WriteLine(route.Id+" "+String.Join(", ",route.Nodes)); return 0; }
            if (args.Length == 2 && args[0] == "--discover" && args[1] == "--parked") {var c=Catalogue.Load();var v=Array.Find(c.Vehicles,x=>x.Id=="VCAN:all");var r=Discovery.Run(c,v,()=>false,Console.WriteLine,u=>Console.WriteLine(u.Address+" "+u.State+" "+u.Error));Console.WriteLine(r.Summary(v));return r.Completed?0:1;}
            if (args.Length == 3 && args[0] == "--probe" && args[2] == "--parked") {var c=Catalogue.Load();var v=Array.Find(c.Vehicles,x=>x.Id=="VCAN:all");var r=Discovery.Run(c,v,()=>false,Console.WriteLine,u=>Console.WriteLine(u.Address+" "+u.State+" "+u.Error),args[1]);Console.WriteLine(r.Summary(v));return r.Completed?0:1;}
            if (args.Length == 1 && args[0] == "--inventory") { Console.WriteLine(Inspector.Inventory()); return 0; }
            if (args.Length == 1 && args[0] == "--identify") return Live(false);
            if (args.Length == 2 && args[0] == "--read-abs" && args[1] == "--parked") return Live(true);
            if (args.Length != 0) { Console.Error.WriteLine("Použití: --self-test | --inventory | --identify | --read-abs --parked | --cabin-ident --parked | --cabin-session-ident --parked"); return 2; }
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException+=delegate(object sender,System.Threading.ThreadExceptionEventArgs e){DiagnosticsExport.RecordError(e.Exception);MessageBox.Show("Chyba byla uložena do logu:\r\n"+e.Exception.Message+"\r\nPo ukončení operace použij Exportovat logy.","PSA Diagnostika");};
            AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e){DiagnosticsExport.RecordError(e.ExceptionObject as Exception??new Exception(Convert.ToString(e.ExceptionObject)));};
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new VehicleForm()); return 0; }
            catch(Exception e) { MessageBox.Show("Aplikaci nelze spustit: "+e.Message,"PSA Diagnostika"); return 1; }
        }
        static int Live(bool abs)
        {
            try {
                var result = AbsReader.Run(abs, Console.WriteLine);
                Console.WriteLine(result.Report); Console.WriteLine("Záznam: " + result.LogPath);
                return result.Success ? 0 : 1;
            } catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
        static int SelfTest()
        {
            int count = 0;
            var cases = new[] {
                new[] { "RX 43 01 33 C1 23 00 00", "P0133, U0123" },
                new[] { "RX 43 00 00", "nejsou kódy" },
                new[] { "RX 43 01", "Neúplná" },
                new[] { "TX 04", "MAZÁNÍ" },
                new[] { "TX 14 FF FF FF", "MAZÁNÍ" },
                new[] { "TX 2E F1 90 00", "ZMĚNA" },
                new[] { "TX 31 01 00 00", "ZMĚNA" },
                new[] { "TX 27 01", "Neklasifikovaný" },
                new[] { "RX 7F 22 31", "kód 31" },
                new[] { "RX 7F 22", "Neplatná délka" },
                new[] { "RX 62 F1 90 41", "chybí ověřená definice" },
                new[] { "RX 7E8 43 01 33", "NEZPRACOVÁNO" },
                new[] { "RX 43 01 GG", "NEZPRACOVÁNO" },
                new[] { "*608_21_01", "NEZPRACOVÁNO" },
                new[] { "RX 59 02 FF", "bez dekódování" }
            };
            foreach (var test in cases)
            {
                if (!Transcript.DecodeLine(test[0]).Contains(test[1])) throw new Exception("Test failed: " + test[0]);
                count++;
            }
            if (Transcript.DecodeLine("# test") != "" || Transcript.DecodeLine("") != "") throw new Exception("Comment handling failed");
            if (!Transcript.Analyze("TX 03\rRX 43 01 33").Contains("P0133")) throw new Exception("Newline handling failed");
            bool rejected = false;
            try { Transcript.Analyze(new string('A', 1024 * 1024 + 1)); } catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("Size limit failed");
            Console.WriteLine("PASS: " + (count + 3) + " checks; no device IO."); return 0;
        }
    }
}



