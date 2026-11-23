using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PsaReader
{
    internal enum IdentityQuery { KwpSupplier, UdsVin, Bsi, Bsm }
    internal interface IDiscoveryVci : IVci { string Configure(CanRoute route); string Identify(IdentityQuery query); string CabinSession(CanRoute route,bool enter); }
    internal sealed class CanRoute
    {
        public int Request; public int Response; public string[] Nodes; public string[] Candidates; public bool HasKwp; public bool CabinDiagnosticSession; public bool UseCabinPins;
        // Reviewed PSA diagnostic socket addresses. Not a CAN broadcast or all-ID sweep.
        static readonly string[] reviewed = {"6A2:682","6A6:686","6A7:687","6A8:688","6A9:689","6AA:68A","6AD:68D","6AF:68F","6B0:690","6B3:693","6B4:694","6B5:695","6B6:696","6B7:697","6B8:698","6BB:69B","6BC:69C","6BD:69D","6C1:601","6C2:602","6C3:603","6C4:604","6C8:628","6E5:625","6E6:626","741:641","742:642","743:643","744:644","745:645","746:646","747:647","748:648","749:649","74A:64A","74B:64B","74C:64C","74F:64F","750:650","751:651","752:652","753:653","754:654","755:655","756:656","757:657","759:659","75B:65B","75C:65C","75D:65D","75E:65E","75F:65F","760:660","761:661","762:662","763:663","764:664","765:665","766:666","767:667","768:668","769:669","76A:66A","76B:66B","76C:66C","76D:66D","76E:66E","76F:66F","770:670","771:671","772:672","773:673","774:674","775:675","777:677","778:678","779:679","77B:67B","77C:67C","77D:67D"};
        public string Id { get { return Request.ToString("X3")+":"+Response.ToString("X3"); } }
        internal static bool IsReviewed(int tx,int rx) { return Array.IndexOf(reviewed,tx.ToString("X3")+":"+rx.ToString("X3"))>=0; }
        internal static bool Supports(VehicleDefinition vehicle)
        {return vehicle!=null&&(vehicle.Architecture??"").StartsWith("AEE2004.",StringComparison.Ordinal)||vehicle!=null&&(vehicle.Architecture??"").StartsWith("AEE2010.",StringComparison.Ordinal);}
        internal static void Validate(CanRoute route) { if(route==null || !IsReviewed(route.Request,route.Response)) throw new InvalidOperationException("Tato adresa není v ověřeném seznamu diagnostických adres PSA."); }
        internal static List<CanRoute> Plan(Catalogue catalogue,VehicleDefinition vehicle)
        {
            // AEE2004/AEE2010 use the supported CAN diagnostic wiring. K-line,
            // partner architectures and NEA2020 require separate transport profiles.
            if(!Supports(vehicle))return new List<CanRoute>();
            var routes=new Dictionary<string,CanRoute>();
            foreach(var node in vehicle.Nodes) foreach(var model in node.Models??new EcuReference[0])
            {
                var ecu=catalogue.Find(model.Key); if(ecu==null)continue;
                foreach(var a in ecu.Addresses.Where(a=>a.Transport=="can-tp" && String.IsNullOrEmpty(a.Bus) && IsReviewed(a.Request,a.Response)))
                {
                    string id=a.Request.ToString("X3")+":"+a.Response.ToString("X3");CanRoute route;
                    if(!routes.TryGetValue(id,out route)){route=new CanRoute{Request=a.Request,Response=a.Response,Nodes=new string[0],Candidates=new string[0]};routes.Add(id,route);}
                    route.Nodes=route.Nodes.Concat(new[]{node.Code}).Distinct().ToArray();
                    route.Candidates=route.Candidates.Concat(new[]{ecu.Key}).Distinct().ToArray();
                    route.HasKwp|=ecu.Protocols.Any(p=>p.StartsWith("KWP",StringComparison.Ordinal));
                }
            }
            foreach(var route in routes.Values){route.CabinDiagnosticSession=route.Request>=0x740&&route.HasKwp&&route.Candidates.Any(key=>catalogue.Definition(key).CabinDiagnosticSession);route.UseCabinPins=route.Request>=0x740;}
            // ABS is our known control; engine follows. Shared gateway addresses are queried once.
            return routes.Values.OrderBy(r=>r.Request==0x6AD?0:r.Request==0x6A8?1:2).ThenBy(r=>r.Request).ToList();
        }
    }
    public sealed class DiscoveredEcu
    {
        public string Address {get;set;} public string[] Nodes {get;set;} public string State {get;set;} public string Identity {get;set;}
        public string[] Candidates {get;set;} public string Error {get;set;} public bool Responded {get;set;} public bool Disconnected {get;set;}
        public Dictionary<string,string> Identifications {get;set;} public string Wiring {get;set;}
    }
    public sealed class DiscoveryResult
    {
        public string VehicleId {get;set;} public string Started {get;set;} public string Finished {get;set;} public string CatalogRevision {get;set;}
        public bool Completed {get;set;} public string Error {get;set;} public List<DiscoveredEcu> Units {get;set;} public string LogPath {get;set;}
        public string Summary(VehicleDefinition vehicle)
        {
            var b=new StringBuilder("VYHLEDÁNÍ JEDNOTEK — "+vehicle.Title+"\r\n"+Started+"\r\n");
            b.AppendLine(Completed?"Naplánované adresy byly zpracovány.":"Průchod nebyl dokončen: "+Error);
            b.AppendLine("Odpovědělo: "+Units.Count(x=>x.Responded)+" / zpracované adresy: "+Units.Count);
            foreach(var u in Units){b.AppendLine("\r\n"+u.Address+" · "+String.Join(", ",u.Nodes)+" · "+u.State);if(!String.IsNullOrEmpty(u.Identity))b.AppendLine("Identifikace (hex): "+u.Identity);if(!String.IsNullOrEmpty(u.Error))b.AppendLine(u.Error);b.AppendLine("Ukončení: "+(u.Disconnected?"potvrzeno":"NEPOTVRZENO"));}
            var queried=Units.SelectMany(u=>u.Nodes).ToArray();
            b.AppendLine("\r\nNeprozkoumané položky profilu: "+String.Join(", ",vehicle.Nodes.Where(n=>!queried.Contains(n.Code)).Select(n=>n.Code)));
            b.AppendLine("\r\nNeodpověděla neznamená, že jednotka není osazená. Kandidátní modely nejsou potvrzenou identifikací.");
            b.AppendLine("Sdílená adresa (např. BSI/BSM/BSR) nepotvrzuje každou uvedenou jednotku jednotlivě.");
            b.AppendLine("Žádné mazání, kódování, aktivace nebo aktualizace.");return b.ToString();
        }
    }
    internal static class Discovery
    {
        internal static int[] EcuBytes(string text)
        {
            int[] b=VciProtocol.Numbers(text);if(b.Any(x=>x>255))throw new IOException("Stavové hlášení adaptéru: "+text);return b;
        }
        internal static bool IdentityMatches(IdentityQuery query,int[] bytes)
        {
            if(query==IdentityQuery.Bsi||query==IdentityQuery.Bsm)return bytes.Length>=3&&bytes[0]==0x61&&bytes[1]==(query==IdentityQuery.Bsi?0x80:0x8B);
            if(query==IdentityQuery.KwpSupplier)return bytes.Length>=3&&bytes[0]==0x61&&bytes[1]==0xFE;
            return bytes.Length==20&&bytes[0]==0x62&&bytes[1]==0xF1&&bytes[2]==0x90;
        }
        internal static bool NegativeMatches(IdentityQuery query,int[] b) { return b.Length==3&&b[0]==0x7F&&b[1]==(query==IdentityQuery.UdsVin?0x22:0x21); }
        internal static void CheckAdapter(IVci wire)
        {
            AdapterIdentity.Number(wire.Exchange(VciCommand.Firmware),"firmware");
            VciProtocol.Status(wire.Exchange(VciCommand.Status),0);
            VciProtocol.Expect(wire.Exchange(VciCommand.Model),"*CDP+");
            AdapterIdentity.Number(wire.Exchange(VciCommand.Serial),"sériové číslo");
        }
        internal static DiscoveredEcu Probe(IDiscoveryVci wire,CanRoute route,Action<string> progress)
        {
            CanRoute.Validate(route);
            var unit=new DiscoveredEcu{Address=route.Id,Nodes=route.Nodes,Candidates=route.Candidates,State="Neodpověděla",Identity="",Identifications=new Dictionary<string,string>(),Wiring=route.UseCabinPins?"H03L08":"default"};
            bool started=false,cabinAttempted=false;
            try
            {
                progress("Hledám "+String.Join(" / ",route.Nodes)+" ("+route.Id+")…");
                VciProtocol.Status(wire.Exchange(VciCommand.Status),0); started=true;
                VciProtocol.Expect(wire.Exchange(VciCommand.Pins),"*114");VciProtocol.Expect(wire.Exchange(VciCommand.Relays),"*106");
                VciProtocol.Expect(wire.Configure(route),"*256");
                VciProtocol.Expect(wire.Exchange(VciCommand.Timing),"*0 10");VciProtocol.Expect(wire.Exchange(VciCommand.ReceiveMode),"*256");
                // KWP StartCommunication (81) is transport/session establishment, not
                // extended/programming session selection. Exact envelope proven on ABS.
                if(route.CabinDiagnosticSession)
                {
                    cabinAttempted=true;int[] start=EcuBytes(wire.CabinSession(route,true));
                    if(start.Length>=2&&start[0]==0x50&&start[1]==0xC0){unit.Responded=true;unit.State="Odpověděla — relace karoserie navázána";}
                    else if(start.Length==1&&start[0]==0xFF)cabinAttempted=false;
                    else if(start.Length==3&&start[0]==0x7F&&start[1]==0x10){cabinAttempted=false;unit.Responded=true;unit.State="Odpověděla — relaci odmítla";}
                    else throw new IOException("Neznámá odpověď na relaci karoserie.");
                }
                else if(route.HasKwp&&route.Request<0x740)
                {
                    int[] start=EcuBytes(wire.Exchange(VciCommand.StartAbs));
                    if(start.Length>=1&&start[0]==0xC1){unit.Responded=true;unit.State="Odpověděla — komunikace navázána";}
                    else if(!(start.Length==1&&start[0]==0xFF) && !(start.Length==3&&start[0]==0x7F&&start[1]==0x81)) throw new IOException("Neznámá odpověď na zahájení komunikace.");
                }
                // Bounded KWP / UDS identity probes; never reset, unlock or select a
                // programming session. A one-byte FF is not an ECU identity.
                foreach(var query in route.Request==0x752?new[]{IdentityQuery.Bsi,IdentityQuery.Bsm,IdentityQuery.KwpSupplier}:new[]{IdentityQuery.KwpSupplier,IdentityQuery.UdsVin})
                {
                    int[] b=EcuBytes(wire.Identify(query));
                    if(b.Length==1&&b[0]==0xFF)continue;
                    if(IdentityMatches(query,b)){unit.Responded=true;unit.State="Odpověděla — identifikace přečtena";string raw=String.Join(" ",b.Select(x=>x.ToString("X2")));unit.Identifications[query.ToString()]=raw;if(String.IsNullOrEmpty(unit.Identity))unit.Identity=raw;if(route.Request!=0x752)break;continue;}
                    if(NegativeMatches(query,b)){unit.Responded=true;if(unit.Identifications.Count==0)unit.State="Odpověděla — identifikaci odmítla";unit.Error="Odpověď ECU: "+String.Join(" ",b.Select(x=>x.ToString("X2")));continue;}
                    throw new IOException("Neznámá odpověď; bez přiřazení modelu: "+String.Join(" ",b.Select(x=>x.ToString("X2"))));
                }
            }
            catch(Exception e){unit.Error=e.Message;}
            finally
            {
                if(started)try {Close(wire,route,cabinAttempted);unit.Disconnected=true;}
                catch(Exception e){unit.Error+="\r\nUkončení nebylo potvrzeno: "+e.Message;}
                // If idle status failed before initialization, adapter state is unknown.
                // Do not proceed to another address just because we sent no bus commands.
            }
            return unit;
        }
        internal static void Close(IDiscoveryVci wire,CanRoute route,bool cabinAttempted)
        {
            try{if(cabinAttempted){var reply=EcuBytes(wire.CabinSession(route,false));if(reply.Length<2||reply[0]!=0x50||reply[1]!=0x81)throw new IOException("Návrat jednotky do výchozí relace nebyl potvrzen.");}}
            finally{VciProtocol.Expect(wire.Exchange(VciCommand.Disconnect),"*1");if(wire is SerialVci)Thread.Sleep(2100);VciProtocol.Status(wire.Exchange(VciCommand.Status),0);}
        }
        internal static DiscoveryResult Run(Catalogue catalogue,VehicleDefinition vehicle,Func<bool> cancelled,Action<string> progress,Action<DiscoveredEcu> found,string node=null)
        {
            var plan=CanRoute.Plan(catalogue,vehicle); // validate BEFORE opening anything
            if(node!=null)plan=plan.Where(r=>r.Nodes.Contains(node)).ToList();if(plan.Count==0)throw new IOException("Pro vybranou jednotku není připravena diagnostická adresa.");
            var result=new DiscoveryResult{VehicleId=vehicle.Id,CatalogRevision=catalogue.Revision,Started=DateTimeOffset.Now.ToString("o"),Units=new List<DiscoveredEcu>()};
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");Directory.CreateDirectory(folder);
            result.LogPath=Path.Combine(folder,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+(node==null?"-discovery-":"-probe-")+Guid.NewGuid().ToString("N").Substring(0,6)+".txt");
            using(var log=new StreamWriter(new FileStream(result.LogPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read),Encoding.UTF8))
            {
                log.AutoFlush=true;Action<string> record=s=>log.WriteLine(DateTimeOffset.Now.ToString("o")+" "+s);
                record("READ-ONLY DISCOVERY "+vehicle.Id+" / source "+catalogue.Revision+" / "+plan.Count+" addresses");
                try
                {
                    using(var wire=new SerialVci(record))
                    {
                        CheckAdapter(wire);
                        foreach(var route in plan)
                        {
                            if(cancelled()){result.Error="Zastaveno uživatelem po ukončení předchozí relace.";break;}
                            var unit=Probe(wire,route,progress);result.Units.Add(unit);found(unit);
                            record("RESULT "+unit.Address+" "+unit.State+" "+unit.Error);
                            if(!unit.Disconnected){result.Error="Ukončení nebylo potvrzeno. Průchod zastaven; před dalším použitím odpojte krabičku od auta i USB.";break;}
                        }
                        result.Completed=result.Units.Count==plan.Count&&result.Error==null;
                    }
                }
                catch(Exception e){result.Error=e.Message;}
                result.Finished=DateTimeOffset.Now.ToString("o");record(result.Summary(vehicle));
            }
            File.WriteAllText(result.LogPath+".json",new JavaScriptSerializer().Serialize(result),Encoding.UTF8);
            File.WriteAllText(result.LogPath+".report.txt",result.Summary(vehicle),Encoding.UTF8);
            return result;
        }
    }
}

