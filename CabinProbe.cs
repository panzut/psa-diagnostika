using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PsaReader
{
    internal enum CabinWiring { CapturedDefault, CapturedH03L08 }
    internal enum CabinRecord { Bsi80, Bsm8B }
    internal interface ICabinIdentityVci : IVci
    {
        string ConfigureCabin(CabinWiring wiring);
        string ReadCabin(CabinRecord record);
        string CabinSession(CanRoute route,bool enter);
    }
    public sealed class CabinObservation
    {
        public string Record {get;set;}
        public string Raw {get;set;}
        public string State {get;set;}
        public string Reference {get;set;}
        public string HardwareReference {get;set;}
        public bool Responded {get;set;}
    }
    public sealed class CabinProbeResult
    {
        public string Wiring {get;set;}
        public List<CabinObservation> Records {get;set;}
        public string Error {get;set;}
        public bool Disconnected {get;set;}
    }
    internal static class CabinProbe
    {
        // These two exact setup strings were captured from Delphi on this car in
        // captures/phedra-bsi-02.vci.txt. No invented pin map or bit-rate search.
        internal static string ConfigureCommand(CabinWiring wiring)
        {
            const string prefix="*66A_0_500_752_652_000_752_01_3E_00_00_00_00_00d02_200";
            switch(wiring){
                case CabinWiring.CapturedDefault:return prefix+"#";
                case CabinWiring.CapturedH03L08:return prefix+"H03L08";
                default:throw new InvalidOperationException("Neověřené zapojení.");
            }
        }
        internal static string ReadCommand(CabinRecord record)
        {
            switch(record){
                case CabinRecord.Bsi80:return "*608_21_80";
                case CabinRecord.Bsm8B:return "*608_21_8B";
                default:throw new InvalidOperationException("Nepovolený dotaz.");
            }
        }
        static string Bcd(int[] bytes,int start,int count)
        {
            var s=new StringBuilder();
            for(int i=start;i<start+count;i++){
                int hi=bytes[i]>>4,lo=bytes[i]&15;
                if(hi>9||lo>9)throw new IOException("Neplatná BCD reference; model nebyl přiřazen.");
                s.Append(hi);s.Append(lo);
            }
            return s.ToString();
        }
        internal static CabinObservation Decode(CabinRecord record,string raw)
        {
            ReadCommand(record); // Reject unknown enum values, including in offline decoding.
            var result=new CabinObservation{Record=record==CabinRecord.Bsi80?"BSI: 21 80":"BSM: 21 8B",Raw=raw};
            int[] b=Discovery.EcuBytes(raw);
            if(b.Length==1&&b[0]==0xFF){result.State="Bez odpovědi ECU";return result;}
            if(b.Length==3&&b[0]==0x7F&&b[1]==0x21){
                result.Responded=true;result.State="ECU odpověděla záporně, NRC "+b[2].ToString("X2")+"; identifikace nezískána";return result;
            }
            int lid=record==CabinRecord.Bsi80?0x80:0x8B;
            if(b.Length<2||b[0]!=0x61||b[1]!=lid)throw new IOException("Odpověď nepatří k odeslanému identifikačnímu dotazu: "+raw);
            result.Responded=true;
            int expected=record==CabinRecord.Bsi80?19:14; // Headers + documented VCAN data layout.
            if(b.Length!=expected){result.State="ECU odpověděla, délka identifikace nemá ověřený formát";return result;}
            // Keep BSI and BSM records separate; never resolve all gateway nodes from one reply.
            result.Reference=Bcd(b,record==CabinRecord.Bsi80?2:9,5);
            if(record==CabinRecord.Bsm8B)result.HardwareReference=Bcd(b,2,5);
            result.State="Identifikační záznam přečten; nezpřístupňuje další funkce jednotky";
            return result;
        }
        internal static CabinProbeResult Probe(ICabinIdentityVci wire,CabinWiring wiring,bool diagnosticSession=false)
        {
            ConfigureCommand(wiring);
            if(diagnosticSession&&wiring!=CabinWiring.CapturedH03L08)throw new InvalidOperationException("Relace je v tomto testu připravena jen pro zachycenou cestu H03L08.");
            var result=new CabinProbeResult{Wiring=wiring.ToString(),Records=new List<CabinObservation>()};
            bool configured=false,sessionAttempted=false;
            var route=new CanRoute{Request=0x752,Response=0x652,CabinDiagnosticSession=true};
            try{
                VciProtocol.Status(wire.Exchange(VciCommand.Status),0);
                configured=true;
                VciProtocol.Expect(wire.Exchange(VciCommand.Pins),"*114");
                VciProtocol.Expect(wire.Exchange(VciCommand.Relays),"*106");
                VciProtocol.Expect(wire.ConfigureCabin(wiring),"*256");
                VciProtocol.Expect(wire.Exchange(VciCommand.Timing),"*0 10");
                VciProtocol.Expect(wire.Exchange(VciCommand.ReceiveMode),"*256");
                if(diagnosticSession){
                    sessionAttempted=true;
                    int[] reply=Discovery.EcuBytes(wire.CabinSession(route,true));
                    if(reply.Length==1&&reply[0]==0xFF){sessionAttempted=false;throw new IOException("Zahájení diagnostické relace bez odpovědi.");}
                    if(reply.Length==3&&reply[0]==0x7F&&reply[1]==0x10){sessionAttempted=false;throw new IOException("ECU odmítla relaci, NRC "+reply[2].ToString("X2"));}
                    if(reply.Length<2||reply[0]!=0x50||reply[1]!=0xC0)throw new IOException("Neznámá odpověď na zahájení relace.");
                }
                // Only readDataByLocalIdentifier after optional captured 10 C0.
                // No security access, reset, fault erasure, actuators, or timeout retries.
                foreach(var record in new[]{CabinRecord.Bsi80,CabinRecord.Bsm8B}){
                    string raw=wire.ReadCabin(record);
                    result.Records.Add(Decode(record,raw));
                }
            }catch(Exception e){result.Error=e.Message;}
            finally{
                if(configured)try{
                    try{
                        if(sessionAttempted){
                            int[] reply=Discovery.EcuBytes(wire.CabinSession(route,false));
                            if(reply.Length<2||reply[0]!=0x50||reply[1]!=0x81)throw new IOException("Návrat do výchozí relace nebyl potvrzen.");
                        }
                    }finally{
                        VciProtocol.Expect(wire.Exchange(VciCommand.Disconnect),"*1");
                        if(wire is SerialVci)Thread.Sleep(2100);
                        VciProtocol.Status(wire.Exchange(VciCommand.Status),0);
                    }
                    result.Disconnected=true;
                }catch(Exception e){result.Error=(result.Error??"")+" Ukončení nebylo potvrzeno: "+e.Message;}
            }
            return result;
        }
        internal static List<CabinProbeResult> RunRoutes(ICabinIdentityVci wire,Action<string> report,bool diagnosticSession=false)
        {
            Discovery.CheckAdapter(wire);
            var results=new List<CabinProbeResult>();
            foreach(var wiring in diagnosticSession?new[]{CabinWiring.CapturedH03L08}:new[]{CabinWiring.CapturedDefault,CabinWiring.CapturedH03L08}){
                report("Pouze identifikace "+(diagnosticSession?"v diagnostické relaci: ":"bez změny relace: ")+wiring);
                var r=Probe(wire,wiring,diagnosticSession);results.Add(r);
                foreach(var item in r.Records)report(item.Record+": "+item.State+" / "+item.Raw+" / reference "+item.Reference);
                report("Ukončení: "+(r.Disconnected?"potvrzeno":"NEPOTVRZENO")+" "+r.Error);
                // A positive or negative ECU response already proves a transport path.
                // Stop instead of switching wiring underneath a responding ECU.
                if(!r.Disconnected||r.Error!=null||r.Records.Any(x=>x.Responded))break;
            }
            return results;
        }
        internal static int Run(bool diagnosticSession=false)
        {
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-cabin-ident-"+Guid.NewGuid().ToString("N").Substring(0,6)+".txt");
            using(var log=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),Encoding.UTF8)){
                log.AutoFlush=true;Action<string> record=s=>{log.WriteLine(DateTimeOffset.Now.ToString("o")+" "+s);Console.WriteLine(s);};
                record("READ ONLY: 752/652, 500 kbit/s; captured wiring; 21 80 and 21 8B; "+(diagnosticSession?"captured 10 C0 entry, 10 81 exit":"no session change"));
                try{
                    List<CabinProbeResult> results;
                    using(var wire=new SerialVci(record))results=RunRoutes(wire,record,diagnosticSession);
                    File.WriteAllText(path+".json",new JavaScriptSerializer().Serialize(results),Encoding.UTF8);
                    record("Záznam: "+path);
                    return results.All(x=>x.Disconnected&&x.Error==null)&&results.Any(x=>x.Records.Any(r=>r.Responded))?0:1;
                }catch(Exception e){record("CHYBA: "+e.Message);return 1;}
            }
        }
    }
}
