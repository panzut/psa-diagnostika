using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace PsaReader
{
    public sealed class ParameterValue {public string Name {get;set;} public string Label {get;set;} public string Value {get;set;} public string State {get;set;} public string Owner {get;set;}}
    internal interface IBulkVci : IParameterVci { string ReadMap(CanRoute route,int root,int page); }
    public sealed class DataRead
    {
        public string Time {get;set;} public int Service {get;set;} public int Identifier {get;set;}
        public string Name {get;set;} public string State {get;set;} public string Raw {get;set;}
        public string Values {get;set;} public string[] Definitions {get;set;}
        public int Page {get;set;}
        public List<ParameterValue> Parameters {get;set;}
    }
    public sealed class UnitSnapshot
    {
        public DiscoveredEcu Unit {get;set;} public List<IdentityMatch> Matches {get;set;}
        public List<DataRead> Reads {get;set;} public bool Completed {get;set;} public string Error {get;set;}
    }
    public sealed class VehicleSnapshot
    {
        public string VehicleId {get;set;} public string Started {get;set;} public string Finished {get;set;}
        public string Revision {get;set;} public string LogPath {get;set;} public bool Completed {get;set;}
        public string Error {get;set;} public List<UnitSnapshot> Units {get;set;} public string[] UnplannedNodes {get;set;}
        public string Report()
        {
            var b=new StringBuilder("ODEČET DOSTUPNÝCH DAT — "+Started+"\r\n");
            b.AppendLine("Jednorázové hodnoty, pořízené postupně; nejsou současným stavem celého auta.");
            b.AppendLine("Adresy: "+Units.Count+", odpovědělo: "+Units.Count(u=>u.Unit.Responded)+". "+(Completed?"Naplánovaný průchod dokončen.":"Průchod nedokončen: "+Error));
            b.AppendLine("Záznamy přečtené: "+Units.Sum(u=>u.Reads.Count(r=>r.State=="Přečteno"))+", odmítnuté: "+Units.Sum(u=>u.Reads.Count(r=>r.State.StartsWith("Odmítnuto")))+", bez odpovědi: "+Units.Sum(u=>u.Reads.Count(r=>r.State=="Bez odpovědi")));
            b.AppendLine("Nepokryté položky tohoto odečtu: "+String.Join(", ",UnplannedNodes??new string[0]));
            foreach(var u in Units){
                b.AppendLine("\r\n=== "+u.Unit.Address+" · "+String.Join(" / ",u.Unit.Nodes)+" · "+u.Unit.Wiring+" ===");
                b.AppendLine(u.Unit.State+"; ukončení "+(u.Unit.Disconnected?"potvrzeno":"NEPOTVRZENO"));
                foreach(var m in u.Matches)b.AppendLine("Shoda reference: "+m.Model+" / "+m.Variant+" / "+m.Reference);
                if(!String.IsNullOrEmpty(u.Error))b.AppendLine("Omezení: "+u.Error);
                foreach(var r in u.Reads){b.AppendLine("\r\n"+r.Time+" · "+r.Service.ToString("X2")+" "+r.Identifier.ToString("X2")+" · "+r.Name+" · "+r.State);b.AppendLine(r.Values);b.AppendLine("Surová odpověď: "+r.Raw);}
            }
            b.AppendLine("\r\nSdílená adresa nepotvrzuje osazení všech kandidátů. Neodpověď neznamená nepřítomnost jednotky.");
            b.AppendLine("Žádné mazání, kódování, spínání akčních členů nebo aktualizace.");return b.ToString();
        }
    }
    internal static class ParameterMap
    {
        internal static string Command(CanRoute route,int root,int page)
        {
            CanRoute.Validate(route);
            if(route.Id!="752:652"||!route.UseCabinPins||!route.CabinDiagnosticSession||!(root==0xB0||root==0xC0||root==0xD0)||page<1||page>32)throw new InvalidOperationException("Nepovolený dotaz na tabulku parametrů.");
            // PSA-RE BSI ecu.yml: narrowly scoped parameter catalogue read, not arbitrary upload.
            return "*608_35_"+root.ToString("X2")+"_00_00_"+page.ToString("X2")+"_11";
        }
    }
    internal static class BulkReader
    {
        internal static void ResolveReferences(Catalogue c,VehicleDefinition vehicle,UnitSnapshot unit)
        {
            var matches=new List<IdentityMatch>(unit.Matches);
            foreach(var key in unit.Unit.Candidates??new string[0]){
                var def=c.Definition(key);if(String.IsNullOrEmpty(def.ReferenceBits))continue;
                foreach(var read in unit.Reads.Where(r=>r.Service==def.IdentityService&&r.Identifier==def.IdentityIdentifier&&r.State=="Přečteno")){
                    try{
                        int header=read.Service==0x21?2:3;
                        var bytes=read.Raw.Split(' ').Select(s=>Convert.ToByte(s,16)).Skip(header).ToArray();
                        string reference=RecordDecoder.Value(new FieldDefinition{Bits=def.ReferenceBits,Type="bcd"},bytes);
                        foreach(var variant in def.Variants.Where(v=>v.Cars.Contains(vehicle.Code)&&v.Parts.Contains(reference)))matches.Add(new IdentityMatch{Key=key,Model=def.Model,Variant=variant.Name,Reference=reference});
                    }catch(IOException){}catch(FormatException){}catch(OverflowException){}
                }
            }
            unit.Matches=matches.GroupBy(m=>m.Key+"/"+m.Variant).Select(g=>g.First()).ToList();
            var unique=unit.Matches.GroupBy(m=>m.Key.Split('/')[1]).Where(g=>g.Count()==1).Select(g=>g.First()).ToList();
            foreach(var read in unit.Reads.Where(r=>r.State=="Přečteno"&&(r.Service==0x21||r.Service==0x22||((r.Service==0x17||r.Service==0x19)&&unit.Unit.Nodes.Length==1))))Describe(c,unique,read,read.Raw.Split(' ').Select(x=>Convert.ToInt32(x,16)).ToArray());
        }
        internal static List<ReadRecord> Plan(Catalogue c,CanRoute route,List<IdentityMatch> matches)
        {
            // If identified, use that model's complete read list. With an ambiguous model,
            // only registered 21/22 reads are probed and the response stays undecoded.
            var keys=matches.Count>0?matches.Select(x=>x.Key).Distinct():route.Candidates;
            return keys.SelectMany(k=>c.Definition(k).Records).Where(r=>r.Service==0x21||r.Service==0x22)
                .GroupBy(r=>r.Service+":"+r.Identifier).Select(g=>g.First()).OrderBy(r=>r.Service).ThenBy(r=>r.Identifier).ToList();
        }
        internal static DataRead Classify(int service,int id,string name,int[] b)
        {
            var r=new DataRead{Time=DateTimeOffset.Now.ToString("o"),Service=service,Identifier=id,Name=name,Raw=String.Join(" ",b.Select(x=>x.ToString("X2")))};
            if(b.Length==1&&b[0]==0xFF){r.State="Bez odpovědi";return r;}
            if(b.Length==3&&b[0]==0x7F&&b[1]==service){r.State="Odmítnuto — NRC "+b[2].ToString("X2");return r;}
            int header=service==0x22?3:2;
            if(service==0x17){if(b.Length<2||b[0]!=0x57)throw new IOException("Neznámá odpověď na čtení závad.");}
            else if(service==0x19){if(b.Length<3||b[0]!=0x59||b[1]!=0x02)throw new IOException("Neznámá odpověď na čtení UDS závad.");}
            else if(service==0x35){if(b.Length<3||b[0]!=0x75)throw new IOException("Neznámá odpověď na čtení mapy.");}
            else if(b.Length<header||b[0]!=service+0x40||(service==0x21?b[1]:(b[1]<<8)|b[2])!=id)throw new IOException("Odpověď nepatří k čtecímu dotazu.");
            r.State="Přečteno";r.Values="Surová data; formát zatím nepřiřazen.";return r;
        }
        internal static void Describe(Catalogue c,List<IdentityMatch> matches,DataRead read,int[] b)
        {
            if(read.State!="Přečteno")return;
            read.Parameters=new List<ParameterValue>();
            var texts=new List<string>();var sources=new List<string>();
            foreach(var match in matches){
                var def=c.Definition(match.Key);
                try{
                    if(read.Service==0x17||read.Service==0x19){texts.Add(match.Model+":\r\n"+ModuleReader.DecodeFaults(def,b));sources.Add(match.Key);continue;}
                    var records=def.Records.Where(r=>r.Service==read.Service&&r.Identifier==read.Identifier).ToArray();if(records.Length!=1)continue;
                    var layout=IdentityResolver.Layout(records[0],match.Variant);
                    texts.Add(match.Model+":\r\n"+RecordDecoder.Decode(records[0],layout,b));sources.Add(records[0].Source);
                    read.Parameters.AddRange(RecordDecoder.Fields(records[0],layout,b,match.Model));
                }catch(IOException e){texts.Add(match.Model+": "+e.Message);}
            }
            if(texts.Count>0)read.Values=String.Join("\r\n",texts);read.Definitions=sources.ToArray();
        }
        internal static UnitSnapshot ReadUnit(IBulkVci wire,Catalogue c,VehicleDefinition vehicle,CanRoute route,Func<bool> cancel,Action<string> progress)
        {
            var unit=new DiscoveredEcu{Address=route.Id,Nodes=route.Nodes,Candidates=route.Candidates,State="Neodpověděla",Identity="",Identifications=new Dictionary<string,string>(),Wiring=route.UseCabinPins?"H03L08":"default"};
            var output=new UnitSnapshot{Unit=unit,Matches=new List<IdentityMatch>(),Reads=new List<DataRead>()};
            bool started=false,cabinAttempted=false;
            try{
                CanRoute.Validate(route);VciProtocol.Status(wire.Exchange(VciCommand.Status),0);started=true;
                progress("Připojuji "+String.Join(" / ",route.Nodes)+" ("+route.Id+")");
                VciProtocol.Expect(wire.Exchange(VciCommand.Pins),"*114");VciProtocol.Expect(wire.Exchange(VciCommand.Relays),"*106");
                VciProtocol.Expect(wire.Configure(route),"*256");VciProtocol.Expect(wire.Exchange(VciCommand.Timing),"*0 10");VciProtocol.Expect(wire.Exchange(VciCommand.ReceiveMode),"*256");
                if(route.CabinDiagnosticSession){
                    cabinAttempted=true;int[] reply=Discovery.EcuBytes(wire.CabinSession(route,true));
                    if(reply.Length==1&&reply[0]==0xFF){cabinAttempted=false;output.Completed=true;return output;}
                    if(reply.Length==3&&reply[0]==0x7F&&reply[1]==0x10){cabinAttempted=false;unit.Responded=true;unit.State="ECU odmítla relaci; zkouším pouze čtení";}
                    else if(reply.Length<2||reply[0]!=0x50||reply[1]!=0xC0)throw new IOException("Neznámá odpověď na relaci karoserie.");
                    unit.Responded=true;
                }else if(route.HasKwp&&route.Request<0x740){
                    int[] reply=Discovery.EcuBytes(wire.Exchange(VciCommand.StartAbs));
                    if(reply.Length>0&&reply[0]==0xC1)unit.Responded=true;
                    else if(!(reply.Length==1&&reply[0]==0xFF)&&!(reply.Length==3&&reply[0]==0x7F&&reply[1]==0x81))throw new IOException("Neznámá odpověď na zahájení komunikace.");
                }
                foreach(var q in route.Request==0x752?new[]{IdentityQuery.Bsi,IdentityQuery.Bsm,IdentityQuery.KwpSupplier}:new[]{IdentityQuery.KwpSupplier,IdentityQuery.UdsVin}){
                    if(cancel()){output.Error="Zastaveno uživatelem.";return output;}
                    int[] b=Discovery.EcuBytes(wire.Identify(q));int service=q==IdentityQuery.UdsVin?0x22:0x21;int id=q==IdentityQuery.Bsi?0x80:q==IdentityQuery.Bsm?0x8B:q==IdentityQuery.UdsVin?0xF190:0xFE;
                    var read=Classify(service,id,"Identifikace",b);output.Reads.Add(read);
                    if(read.State=="Přečteno"){unit.Responded=true;unit.Identifications[q.ToString()]=read.Raw;if(String.IsNullOrEmpty(unit.Identity))unit.Identity=read.Raw;}
                    if(read.State.StartsWith("Odmítnuto"))unit.Responded=true;
                }
                if(!unit.Responded){output.Completed=true;return output;}
                unit.State="Odpověděla — odečet dat";
                output.Matches=IdentityResolver.Resolve(c,vehicle.Code,unit);
                foreach(var read in output.Reads.Where(r=>r.State=="Přečteno"))Describe(c,output.Matches,read,read.Raw.Split(' ').Select(x=>Convert.ToInt32(x,16)).ToArray());
                // Read fault memory once per physical diagnostic address. Do not label a
                // gateway's fault list as separate confirmed faults of every virtual node.
                if(route.Candidates.Any(k=>(c.Definition(k).ReadDtcServices??new int[0]).Contains(0x17))&&!cancel()){
                    int[] b=Discovery.EcuBytes(wire.Exchange(VciCommand.ReadAbsDtc));var read=Classify(0x17,0,"Závady na diagnostické adrese",b);output.Reads.Add(read);
                    if(route.Nodes.Length==1)Describe(c,output.Matches,read,b);
                    else if(read.State=="Přečteno")try{read.Values=ModuleReader.DecodeFaults(new EcuDefinition{Dtcs=new FaultDefinition[0]},b)+"\r\nSdílená adresa: původ závad nepřiřazen jednotlivým modulům.";}catch(IOException e){read.Values=e.Message+" Surový záznam je uložen; ostatní čtení pokračuje.";}
                }
                if(route.Candidates.Any(k=>(c.Definition(k).ReadDtcServices??new int[0]).Contains(0x19))&&!cancel()){
                    int[] b=Discovery.EcuBytes(wire.Exchange(VciCommand.ReadUdsDtc));var read=Classify(0x19,2,"Závady UDS — všechny stavové příznaky",b);output.Reads.Add(read);
                    if(read.State=="Přečteno")try{read.Values=ModuleReader.DecodeFaults(new EcuDefinition(),b);if(route.Nodes.Length==1)Describe(c,output.Matches,read,b);}catch(IOException e){read.Values=e.Message+" Surový záznam uložen.";}
                }
                // Preserve parameter table replies first; their envelope is checked against
                // observed ECU data before a mapping is ever used for numeric decoding.
                if(route.Id=="752:652"&&route.CabinDiagnosticSession&&cabinAttempted&&output.Matches.Any(m=>m.Key=="AEE2004.full/BSI/BSI"))foreach(int root in new[]{0xB0,0xC0,0xD0}){
                    for(int page=1;page<=32&&!cancel();page++){
                        int[] b=Discovery.EcuBytes(wire.ReadMap(route,root,page));var read=Classify(0x35,root,"Tabulka parametrů — stránka "+page,b);read.Page=page;output.Reads.Add(read);
                        if(read.State!="Přečteno")break;
                        if(b.Length<3||b[1]!=root)throw new IOException("Odpověď na mapu nepatří k požadovanému bloku.");
                        if(b[2]==0xFF){read.Values="Konec tabulky parametrů.";break;}
                        if(b.Length!=26||b[2]!=0x80||b[3]!=0||b[4]!=0||b[5]!=page||b[6]!=0x11){read.Values="Neověřený formát stránky; další stránky nebudou čteny.";break;}
                        if(page==32)read.Values="Dosažen limit stránek; úplnost mapy nepotvrzena.";
                    }
                }
                var plan=Plan(c,route,output.Matches);int done=0;
                foreach(var rec in plan){
                    if(cancel()){output.Error="Zastaveno uživatelem.";return output;}
                    if(output.Reads.Any(r=>r.Service==rec.Service&&r.Identifier==rec.Identifier))continue;
                    SerialVci.EncodeReadRecord(rec);
                    int[] b=Discovery.EcuBytes(wire.ReadRecord(rec));var read=Classify(rec.Service,rec.Identifier,rec.Name,b);output.Reads.Add(read);Describe(c,output.Matches,read,b);
                    if(++done%8==0)progress(String.Join(" / ",route.Nodes)+": "+done+" / "+plan.Count+" čtecích záznamů");
                }
                ResolveReferences(c,vehicle,output);MappedDecoder.Reprocess(c,output);output.Completed=true;
            }catch(Exception e){output.Error=e.Message;}
            finally{
                if(started)try{Discovery.Close(wire,route,cabinAttempted);unit.Disconnected=true;}catch(Exception e){output.Error=(output.Error??"")+" UKONČENÍ NEPOTVRZENO: "+e.Message;}
                unit.Error=output.Error;
            }
            return output;
        }
        internal static VehicleSnapshot Run(Catalogue c,VehicleDefinition vehicle,Func<bool> cancel,Action<string> progress,Action<UnitSnapshot> found,string node=null)
        {
            var plan=CanRoute.Plan(c,vehicle);if(node!=null)plan=plan.Where(r=>r.Nodes.Contains(node)).ToList();if(plan.Count==0)throw new IOException("Žádná připravená cesta.");
            var result=new VehicleSnapshot{VehicleId=vehicle.Id,Started=DateTimeOffset.Now.ToString("o"),Revision=c.Revision,Units=new List<UnitSnapshot>(),UnplannedNodes=vehicle.Nodes.Where(n=>!plan.Any(r=>r.Nodes.Contains(n.Code))).Select(n=>n.Code).ToArray()};
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");Directory.CreateDirectory(folder);
            result.LogPath=Path.Combine(folder,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-all-data-"+Guid.NewGuid().ToString("N").Substring(0,6)+".txt");
            using(var log=new StreamWriter(new FileStream(result.LogPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read),Encoding.UTF8)){
                log.AutoFlush=true;Action<string> write=s=>log.WriteLine(DateTimeOffset.Now.ToString("o")+" "+s);
                write("READ ONLY ALL REGISTERED DATA "+vehicle.Id+" / "+c.Revision);
                try{using(var wire=new SerialVci(write)){
                    Discovery.CheckAdapter(wire);
                    foreach(var route in plan){
                        if(cancel()){result.Error="Zastaveno uživatelem.";break;}
                        var item=ReadUnit(wire,c,vehicle,route,cancel,progress);result.Units.Add(item);found(item);write("RESULT "+route.Id+" "+item.Unit.State+" "+item.Error);
                        Save(result);
                        if(!item.Unit.Disconnected){result.Error="Ukončení spojení nebylo potvrzeno; průchod zastaven.";break;}
                    }
                    result.Completed=result.Units.Count==plan.Count&&result.Units.All(u=>u.Completed)&&!cancel();
                    if(!result.Completed&&result.Error==null)result.Error="Některé jednotky nebyly dočteny; viz jednotlivé výsledky.";
                }}catch(Exception e){result.Error=e.Message;}
                result.Finished=DateTimeOffset.Now.ToString("o");Save(result);write(result.Report());
            }
            return result;
        }
        static void Save(VehicleSnapshot result)
        {
            File.WriteAllText(result.LogPath+".json",new JavaScriptSerializer{MaxJsonLength=32*1024*1024}.Serialize(result),Encoding.UTF8);
            File.WriteAllText(result.LogPath+".report.txt",result.Report(),Encoding.UTF8);
        }
        internal static int Reprocess(string path)
        {
            var result=new JavaScriptSerializer{MaxJsonLength=32*1024*1024}.Deserialize<VehicleSnapshot>(File.ReadAllText(path,Encoding.UTF8));
            var c=Catalogue.Load();var vehicle=c.Vehicles.Single(v=>v.Id==result.VehicleId);foreach(var unit in result.Units){ResolveReferences(c,vehicle,unit);MappedDecoder.Reprocess(c,unit);}
            // Preserve the original acquisition and its timestamp; write a separate report.
            string target=path+".decoded";File.WriteAllText(target+".json",new JavaScriptSerializer{MaxJsonLength=32*1024*1024}.Serialize(result),Encoding.UTF8);
            File.WriteAllText(target+".txt",result.Report(),Encoding.UTF8);Console.WriteLine(target+".txt");return 0;
        }
    }
}
