using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace PsaReader
{
    // Fixed profile from two local captures; no arbitrary-command or replay API.
    internal enum VciCommand { Firmware, Status, Model, Serial, Pins, Relays, ConfigureAbs, Timing, ReceiveMode, StartAbs, ReadAbsDtc, Disconnect, ReadUdsDtc }
    internal static class VciProtocol
    {
        internal static string Encode(VciCommand command)
        {
            switch (command)
            {
                case VciCommand.Firmware: return "*201";
                case VciCommand.Status: return "*203";
                case VciCommand.Model: return "*20A";
                case VciCommand.Serial: return "*200";
                case VciCommand.Pins: return "*60b";
                case VciCommand.Relays: return "*60bc";
                case VciCommand.ConfigureAbs: return "*66A_0_500_6AD_68D_000_6AD_01_3E_00_00_00_00_00d02_200#";
                case VciCommand.Timing: return "*608f00_0A";
                case VciCommand.ReceiveMode: return "*608pN";
                case VciCommand.StartAbs: return "*608_81";
                case VciCommand.ReadAbsDtc: return "*608_17_FF_00";
                case VciCommand.ReadUdsDtc: return "*608_19_02_FF";
                case VciCommand.Disconnect: return "*607";
                default: throw new InvalidOperationException("Nepovolený příkaz.");
            }
        }

        internal static void Expect(string actual, string expected)
        {
            if (actual != expected) throw new IOException("Nečekaná odpověď: " + actual + " (očekáváno " + expected + ").");
        }

        internal static int[] Numbers(string frame)
        {
            if (frame == null || frame.Length < 2 || frame[0] != '*') throw new IOException("Chybí hlavička odpovědi VCI.");
            string[] tokens = frame.Substring(1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length > 1024) throw new IOException("Neplatná délka odpovědi VCI.");
            int[] result = new int[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
                if (!Int32.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i]))
                    throw new IOException("Neplatné desítkové číslo VCI.");
            return result;
        }

        internal static string Status(string frame, int expectedState)
        {
            int[] numbers = Numbers(frame);
            if (numbers.Length != 2 || numbers[0] != expectedState || numbers[1] < 0 || numbers[1] > 65535)
                throw new IOException("Nečekaný stav krabičky: " + frame + ".");
            // Voltage scaling has not been independently calibrated, so preserve raw data.
            return frame;
        }

        internal static string DecodeAbs(string frame)
        {
            int[] b = Numbers(frame);
            foreach (int value in b) if (value > 255) throw new IOException("Odpověď neobsahuje bajty ECU.");
            if (b[0] == 0x7F) throw new IOException("ECU vrátila negativní odpověď: " + frame);
            if (b.Length < 2 || b[0] != 0x57 || b.Length != 2 + b[1] * 3)
                throw new IOException("Neúplná nebo neznámá struktura odpovědi ABS. Data nejsou vydávána za seznam závad.");
            var result = new StringBuilder();
            result.AppendLine("Počet záznamů DTC: " + b[1]);
            for (int i = 2; i < b.Length; i += 3)
            {
                string code = b[i].ToString("X2") + b[i + 1].ToString("X2");
                string status = b[i + 2].ToString("X2");
                result.AppendLine(); result.AppendLine("Kód " + code + " · stav 0x" + status);
                if (code == "5301")
                {
                    result.AppendLine("Snímače tlaku brzdového pedálu");
                    if (status == "13") result.AppendLine("Přerušovaný — označení porovnané s Delphi pro tento profil a stav.");
                    else result.AppendLine("Význam tohoto stavu zatím není ověřen.");
                    result.AppendLine("Popis převzat z místního kontrolního čtení Delphi; sám neurčuje vadný díl.");
                }
                else result.AppendLine("Popis tohoto kódu není v našem ověřeném profilu.");
            }
            if (b[1] == 0) result.AppendLine("Tato odpověď neobsahuje DTC. Ostatní jednotky nebyly kontrolovány.");
            result.AppendLine(); result.Append("Surový obsah ECU (hex):");
            foreach (int value in b) result.Append(" " + value.ToString("X2"));
            return result.ToString();
        }
    }

    internal sealed class CrFrame
    {
        readonly StringBuilder buffer = new StringBuilder();
        internal string Feed(int value)
        {
            if (value == 13)
            {
                if (buffer.Length < 2 || buffer[0] != '*') throw new IOException("Neplatný rámec VCI.");
                string frame = buffer.ToString().TrimEnd(' '); buffer.Clear(); return frame;
            }
            if (value < 32 || value > 126 || buffer.Length >= 4096) throw new IOException("Poškozený nebo příliš dlouhý rámec VCI.");
            buffer.Append((char)value); return null;
        }
    }

    internal interface IVci : IDisposable { string Exchange(VciCommand command); }

    internal sealed class SerialVci : IVci, IParameterVci, ICabinIdentityVci, IBulkVci, IBsiServiceVci, IFaultClearVci
    {
        readonly SerialPort port;
        readonly Action<string> log;
        readonly CommandSafety commandSafety = new CommandSafety();
        volatile bool serialError;
        internal static string KnownPort(){return AdapterIdentity.FindPort();}
        internal SerialVci(Action<string> logger)
        {
            log = logger;
            log("PSA Diagnostika "+DiagnosticsExport.Version+" / build SHA256 "+DiagnosticsExport.BuildId);
            foreach (var process in Process.GetProcessesByName("Main"))
            {
                process.Dispose();
                throw new IOException("Nejprve ukončete Delphi, aby krabičku používal jen jeden program.");
            }
            string name = KnownPort();
            // USB setup wValue 0x0208 = 8 bits, EVEN parity, ONE stop bit.
            // wIndex 0x0100 = RTS/CTS, baud divisor 0x001A = nominal 115200.
            port = new SerialPort(name, 115200, Parity.Even, 8, StopBits.One);
            port.Handshake = Handshake.RequestToSend; port.DtrEnable = false;
            port.ReadTimeout = 100; port.WriteTimeout = 1500; port.Encoding = Encoding.ASCII;
            port.ErrorReceived += delegate { serialError = true; };
            try { port.Open(); Thread.Sleep(100); log("PORT " + name + " / 115200 8E1 RTS-CTS / DTR off"); }
            catch { port.Dispose(); throw; }
        }
        public string Exchange(VciCommand command)
        {
            return ExchangeText(VciProtocol.Encode(command),command == VciCommand.Disconnect);
        }
        public string Configure(CanRoute route)
        {
            // H03L08 is the locally captured and now responding cabin diagnostic bus.
            CanRoute.Validate(route);
            string tx=route.Request.ToString("X3"), rx=route.Response.ToString("X3");
            if(route.UseCabinPins&&route.Request<0x740)throw new InvalidOperationException("Cesta karoserie není pro tuto adresu připravena.");
            return ExchangeText("*66A_0_500_"+tx+"_"+rx+"_000_"+tx+"_01_3E_00_00_00_00_00d02_200"+(route.UseCabinPins?"H03L08":"#"),false);
        }
        public string Identify(IdentityQuery query)
        {
            if(query==IdentityQuery.KwpSupplier) return ExchangeText("*608_21_FE",false);
            if(query==IdentityQuery.UdsVin) return ExchangeText("*608_22_F1_90",false);
            if(query==IdentityQuery.Bsi)return ExchangeText("*608_21_80",false);
            if(query==IdentityQuery.Bsm)return ExchangeText("*608_21_8B",false);
            throw new InvalidOperationException("Nepovolený identifikační dotaz.");
        }
        public string ConfigureCabin(CabinWiring wiring)
        {
            return ExchangeText(CabinProbe.ConfigureCommand(wiring),false);
        }
        public string ReadCabin(CabinRecord record)
        {
            return ExchangeText(CabinProbe.ReadCommand(record),false);
        }
        public string ReadMap(CanRoute route,int root,int page)
        {
            return ExchangeText(ParameterMap.Command(route,root,page),false);
        }
        public string CabinSession(CanRoute route,bool enter)
        {
            CanRoute.Validate(route);if(route.Request<0x740||!route.CabinDiagnosticSession)throw new InvalidOperationException("Relace karoserie není pro tuto adresu doložená.");
            return ExchangeText(enter?"*608_10_C0":"*608_10_81",!enter);
        }
        public string ReadRecord(ReadRecord record)
        {
            return ExchangeText(EncodeReadRecord(record),false);
        }
        public string ExecuteBsi(BsiAction action,bool engineOff)
        {
            commandSafety.ArmBsi(action,engineOff);
            return ExchangeText(commandSafety.ArmedBsiCommand(),false);
        }
        public string SetServiceInterval(int km,bool engineOff){commandSafety.ArmServiceInterval(km,engineOff);return ExchangeText(commandSafety.ArmedBsiCommand(),false);}
        public string ClearFaults(FaultClearPlan plan,bool engineOff){return ExchangeText(commandSafety.ArmFaultClear(plan,engineOff),false);}
        public string EconomyResult(){return ExchangeText(BsiServiceProfile.EconomyPoll,false);}
        public void UnlockBsiConfiguration(bool engineOff)
        {
            commandSafety.ArmBsiConfiguration(engineOff);
            var seed=Discovery.EcuBytes(ExchangeText(commandSafety.ArmedBsiCommand(),false));
            if(seed.Length!=6||seed[0]!=0x67||seed[1]!=0x83)throw new IOException("BSI odmítla výzvu pro konfiguraci: "+BsiServiceProfile.Hex(seed));
            if(!commandSafety.ConfigurationUnlocked){
                var reply=Discovery.EcuBytes(ExchangeText(commandSafety.ConfigurationKeyCommand(),false));
                if(reply.Length!=2||reply[0]!=0x67||reply[1]!=0x84)throw new IOException("BSI nepovolila konfiguraci: "+BsiServiceProfile.Hex(reply)+". Žádný další klíč se nezkouší.");
            }
        }
        internal static string EncodeReadRecord(ReadRecord record)
        {
            if(record==null || (record.Service!=0x21&&record.Service!=0x22) || record.Identifier<0 || record.Identifier>(record.Service==0x21?255:65535))throw new InvalidOperationException("Nepovolený čtecí dotaz.");
            return "*608_"+record.Service.ToString("X2")+"_"+(record.Service==0x21?record.Identifier.ToString("X2"):(record.Identifier>>8).ToString("X2")+"_"+(record.Identifier&255).ToString("X2"));
        }
        string ExchangeText(string request, bool recovering)
        {
            // Disconnect is the only permitted recovery message, observed in Delphi.
            if (!recovering && (serialError || port.BytesToRead != 0)) throw new IOException("Neočekávaná data nebo chyba sériového spojení; další čtení zastaveno.");
            commandSafety.Send(request, command => { log("TX " + command); port.Write(command + "\r"); });
            var frame = new CrFrame(); var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 5000)
            {
                if (serialError && !recovering) throw new IOException("Ovladač oznámil chybu sériového přenosu.");
                int value;
                try { value = port.ReadByte(); } catch (TimeoutException) { continue; }
                string response = frame.Feed(value);
                if (response != null) { log("RX " + response); commandSafety.AcceptResponse(request,response); return response; }
            }
            throw new TimeoutException("Krabička nevrátila úplnou odpověď do 5 sekund. Příkaz nebyl opakován.");
        }
        public void Dispose() { port.Dispose(); }
    }

    internal sealed class ReadResult
    {
        internal bool Success;
        internal string Report;
        internal string LogPath;
    }

    internal static class AbsReader
    {
        // Only this fixed sequence can reach the transport. Fail closed on every unexpected reply.
        internal static ReadResult Session(IVci wire, bool abs, Action<string> progress)
        {
            var result = new ReadResult(); var report = new StringBuilder(); bool touchedBus = false;
            report.AppendLine(abs ? "VLASTNÍ ČTENÍ ABS — Lancia Phedra 2007" : "KONTROLA DIAGNOSTICKÉ KRABIČKY");
            report.AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            report.AppendLine("Profil: Bosch ABS 8.1 ESP PSA · pouze toto vozidlo; USB DS150E/CDP+\r\n");
            try
            {
                progress("Ověřuji krabičku…");
                string firmware = AdapterIdentity.Number(wire.Exchange(VciCommand.Firmware), "firmware");
                string initialStatus = VciProtocol.Status(wire.Exchange(VciCommand.Status), 0);
                VciProtocol.Expect(wire.Exchange(VciCommand.Model), "*CDP+");
                string serial = AdapterIdentity.Number(wire.Exchange(VciCommand.Serial), "sériové číslo");
                if(AdapterIdentity.Number(wire.Exchange(VciCommand.Firmware), "firmware")!=firmware)throw new IOException("Identifikace firmware není stálá.");
                report.AppendLine("Krabička: CDP+ · "+serial+" · firmware "+firmware);
                report.AppendLine("Stav před čtením (surový): " + initialStatus);
                if (abs)
                {
                    progress("Navazuji ověřené spojení s ABS…");
                    touchedBus = true;
                    VciProtocol.Expect(wire.Exchange(VciCommand.Pins), "*114");
                    VciProtocol.Expect(wire.Exchange(VciCommand.Relays), "*106");
                    VciProtocol.Expect(wire.Exchange(VciCommand.ConfigureAbs), "*256");
                    VciProtocol.Expect(wire.Exchange(VciCommand.Timing), "*0 10");
                    VciProtocol.Expect(wire.Exchange(VciCommand.ReceiveMode), "*256");
                    VciProtocol.Expect(wire.Exchange(VciCommand.StartAbs), "*193 208 143");
                    VciProtocol.Status(wire.Exchange(VciCommand.Status), 1);
                    progress("Čtu paměť závad ABS…");
                    string raw = wire.Exchange(VciCommand.ReadAbsDtc);
                    report.AppendLine("\r\n" + VciProtocol.DecodeAbs(raw));
                }
                else report.AppendLine("\r\nIdentifikace krabičky ověřena. Spojení s ECU nebylo navazováno.");
                result.Success = true;
            }
            catch (Exception e) { report.AppendLine("\r\nČTENÍ NEDOKONČENO: " + e.Message); progress("Čtení se zastavilo."); }
            finally
            {
                if (touchedBus)
                {
                    progress("Ukončuji spojení…");
                    try
                    {
                        VciProtocol.Expect(wire.Exchange(VciCommand.Disconnect), "*1");
                        // Delphi waits about 2 seconds before querying idle status.
                        if (wire is SerialVci) Thread.Sleep(2100);
                        string status = VciProtocol.Status(wire.Exchange(VciCommand.Status), 0);
                        report.AppendLine("\r\nUkončení spojení potvrzeno krabičkou: " + status);
                    }
                    catch (Exception e)
                    {
                        result.Success = false;
                        report.AppendLine("\r\nUKONČENÍ NEPOTVRZENO: " + e.Message + "\r\nPřed dalším použitím odpojte krabičku od auta i USB. Neopakujte čtení automaticky.");
                    }
                }
            }
            report.AppendLine("\r\nJednorázový odečet, nikoli průběžné sledování. Žádné chyby nebyly smazány.");
            result.Report = report.ToString(); return result;
        }

        internal static ReadResult Run(bool abs, Action<string> progress)
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + (abs ? "abs" : "vci") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
            ReadResult result;
            using (var log = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), Encoding.UTF8))
            {
                log.AutoFlush = true;
                Action<string> record = text => log.WriteLine(DateTimeOffset.Now.ToString("o") + " " + text);
                record("PROFILE phedra-2007-bosch81-local-v1; " + (abs ? "READ ABS" : "IDENTITY ONLY"));
                try { using (var wire = new SerialVci(record)) result = Session(wire, abs, progress); }
                catch (Exception e) { result = new ReadResult { Success = false, Report = "Čtení nebylo dokončeno: " + e.Message }; }
                record(result.Report);
            }
            result.LogPath = path;
            File.WriteAllText(path + ".report.txt", result.Report, Encoding.UTF8);
            return result;
        }
    }
}


