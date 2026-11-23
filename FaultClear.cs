using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PsaReader
{
    internal interface IFaultClearVci : IParameterVci { string ClearFaults(FaultClearPlan plan,bool engineOff); }
    internal sealed class FaultClearPlan
    {
        internal Catalogue Catalogue;internal VehicleDefinition Vehicle;internal CanRoute Route;internal IdentityMatch Match;internal EcuDefinition Definition;internal bool Uds;
        internal string IdentityCommand {get{return SerialVci.EncodeReadRecord(new ReadRecord{Service=Definition.IdentityService,Identifier=Definition.IdentityIdentifier});}}
        internal string ReadCommand {get{return VciProtocol.Encode(Uds?VciCommand.ReadUdsDtc:VciCommand.ReadAbsDtc);}}
        internal string ClearCommand {get{return Uds?"*608_14_FF_FF_FF":"*608_14_FF_00";}}
        internal static FaultClearPlan Create(Catalogue c,VehicleDefinition v,string node,IEnumerable<UnitSnapshot> snapshots)
        {
            if(node=="RBG"||node=="SRS"||node=="ROLLOVER")throw new IOException("SRS a pyrotechnické prvky jsou trvale pouze ke čtení, včetně paměti závad.");
            var routes=CanRoute.Plan(c,v).Where(r=>r.Nodes.Contains(node)).ToArray();if(routes.Length!=1)throw new IOException("Nejdřív je potřeba jednoznačně rozpoznat jednotku a její spojení.");
            var route=routes[0];var matches=snapshots.Where(s=>s.Unit.Address==route.Id&&s.Unit.Responded&&s.Unit.Disconnected).SelectMany(s=>s.Matches).Where(m=>m.Key.Split('/')[1]==node).GroupBy(m=>m.Key+"/"+m.Variant).Select(g=>g.First()).ToArray();
            if(matches.Length!=1)throw new IOException("Nejdřív načti jednotku. Její konkrétní model a verze musí být rozpoznané.");
            var def=c.Definition(matches[0].Key);var protocols=c.Find(matches[0].Key).Protocols;
            bool uds=protocols.Contains("UDS")&&!protocols.Any(p=>p.StartsWith("KWP",StringComparison.Ordinal));
            var plan=new FaultClearPlan{Catalogue=c,Vehicle=v,Route=route,Match=matches[0],Definition=def,Uds=uds};plan.Validate();return plan;
        }
        internal void Validate()
        {
            CanRoute.Validate(Route);
            if(Route.Request==0x744||Route.Nodes.Any(n=>n=="RBG"||n=="SRS"||n=="ROLLOVER")||Route.Candidates.Any(k=>k.Contains("/RBG/")||k.Contains("/SRS/")||k.Contains("/ROLLOVER/")))throw new IOException("SRS: mazání, zápisy a aktivace jsou trvale zakázané.");
            var canonical=CanRoute.Plan(Catalogue,Vehicle).SingleOrDefault(r=>r.Id==Route.Id);
            if(canonical==null||canonical.UseCabinPins!=Route.UseCabinPins||!canonical.Candidates.Contains(Match.Key)||Definition.Key!=Match.Key||!Definition.ClearDtcSupported||!(Definition.ReadDtcServices??new int[0]).Contains(Uds?0x19:0x17)||String.IsNullOrEmpty(Definition.ReferenceBits))throw new IOException("Chybí úplný profil identifikace a mazání závad této jednotky.");
            var protocols=Catalogue.Find(Match.Key).Protocols;
            if(Uds?!protocols.Contains("UDS"):!protocols.Contains("KWP-PSA2000"))throw new IOException("Nejednoznačný protokol mazání závad.");
            if(Definition.IdentityService!=0x21&&Definition.IdentityService!=0x22)throw new IOException("Chybí doložený identifikační dotaz.");
        }
        internal void VerifyIdentity(int[] bytes)
        {
            var read=BulkReader.Classify(Definition.IdentityService,Definition.IdentityIdentifier,"Identifikace",bytes);
            var snapshot=new UnitSnapshot{Unit=new DiscoveredEcu{Candidates=new[]{Match.Key},Nodes=Route.Nodes},Matches=new List<IdentityMatch>(),Reads=new List<DataRead>{read}};
            BulkReader.ResolveReferences(Catalogue,Vehicle,snapshot);
            if(snapshot.Matches.Count!=1||snapshot.Matches[0].Variant!=Match.Variant||snapshot.Matches[0].Reference!=Match.Reference)throw new IOException("Čerstvá identifikace neodpovídá vybrané jednotce. Mazání zastaveno.");
        }
        internal bool ValidFaultReply(int[] b){return Uds?b.Length>=3&&b[0]==0x59&&b[1]==2:b.Length>=2&&b[0]==0x57;}
    }
    internal static class FaultClear
    {
        internal static BsiServiceResult Session(IFaultClearVci wire,FaultClearPlan plan,bool engineOff,Action<string> log)
        {
            var result=new BsiServiceResult();bool started=false,cabin=false,attempted=false;string report=null;
            try{
                plan.Validate();if(!engineOff)throw new IOException("Potvrď stojící auto, vypnutý motor a zapnuté zapalování.");
                Discovery.CheckAdapter(wire);started=true;
                VciProtocol.Expect(wire.Exchange(VciCommand.Pins),"*114");VciProtocol.Expect(wire.Exchange(VciCommand.Relays),"*106");VciProtocol.Expect(wire.Configure(plan.Route),"*256");VciProtocol.Expect(wire.Exchange(VciCommand.Timing),"*0 10");VciProtocol.Expect(wire.Exchange(VciCommand.ReceiveMode),"*256");
                if(plan.Route.CabinDiagnosticSession){cabin=true;if(!Discovery.EcuBytes(wire.CabinSession(plan.Route,true)).SequenceEqual(new[]{0x50,0xC0}))throw new IOException("Jednotka nepřijala diagnostickou relaci.");}
                else if(plan.Route.HasKwp&&plan.Route.Request<0x740){var start=Discovery.EcuBytes(wire.Exchange(VciCommand.StartAbs));if(start.Length<1||start[0]!=0xC1)throw new IOException("Jednotka nepotvrdila navázání komunikace.");}
                var identity=Discovery.EcuBytes(wire.ReadRecord(new ReadRecord{Service=plan.Definition.IdentityService,Identifier=plan.Definition.IdentityIdentifier}));plan.VerifyIdentity(identity);
                var before=Discovery.EcuBytes(wire.Exchange(plan.Uds?VciCommand.ReadUdsDtc:VciCommand.ReadAbsDtc));if(!plan.ValidFaultReply(before))throw new IOException("Původní paměť závad se nepodařilo přečíst. Mazání zastaveno.");
                // Auto-flushed log is the durable backup, before the sole clear attempt.
                log("BACKUP ID "+BsiServiceProfile.Hex(identity));log("BACKUP DTC "+BsiServiceProfile.Hex(before));
                if((!plan.Uds&&before.SequenceEqual(new[]{0x57,0}))||(plan.Uds&&before.Length==3&&before[2]!=0))report="Paměť neobsahuje závady odpovídající dotazu. Mazání nebylo potřeba.";
                else{
                    attempted=true;var ack=Discovery.EcuBytes(wire.ClearFaults(plan,engineOff));
                    if(!(plan.Uds?ack.SequenceEqual(new[]{0x54}):ack.SequenceEqual(new[]{0x54,0xFF,0})))throw new IOException("Jednotka nepotvrdila smazání: "+BsiServiceProfile.Hex(ack)+". Povel se neopakuje.");
                    result.Changed=true;var after=Discovery.EcuBytes(wire.Exchange(plan.Uds?VciCommand.ReadUdsDtc:VciCommand.ReadAbsDtc));log("AFTER DTC "+BsiServiceProfile.Hex(after));
                    if(!plan.ValidFaultReply(after))throw new IOException("Mazání bylo potvrzeno, ale kontrolní čtení selhalo.");
                    report="Jednotka potvrdila mazání. Kontrolní odečet:\r\n"+ModuleReader.DecodeFaults(plan.Definition,after)+"\r\nPřetrvávající závada se může ihned vrátit. Smazání neopravuje její příčinu.";
                }
                result.Success=true;
            }catch(Exception e){report=e.Message+(attempted?"\r\nMazací povel se odesílal; kontroluj potvrzení a odečet. Bez automatického opakování.":"\r\nMazací povel nebyl odeslán.");}
            finally{if(started)try{Discovery.Close(wire,plan.Route,cabin);result.Disconnected=true;}catch(Exception e){result.Success=false;report+="\r\nUkončení nepotvrzeno: "+e.Message;}}
            result.Report=report;log(report);return result;
        }
        internal static BsiServiceResult Run(FaultClearPlan plan,bool engineOff,Action<string> progress)
        {
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");Directory.CreateDirectory(folder);string path=Path.Combine(folder,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-clear-dtc.txt");BsiServiceResult result;
            using(var log=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),Encoding.UTF8)){log.AutoFlush=true;Action<string> record=s=>{log.WriteLine(DateTimeOffset.Now.ToString("o")+" "+s);progress(s);};record("CLEAR DTC "+plan.Vehicle.Id+" / "+plan.Match.Key+" / "+plan.Match.Variant+" / engine-off confirmed by operator");try{using(var wire=new SerialVci(record))result=Session(wire,plan,engineOff,record);}catch(Exception e){result=new BsiServiceResult{Report=e.Message};record(result.Report);}}
            result.LogPath=path;return result;
        }
    }
}
