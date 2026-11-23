using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PsaReader
{
    internal static class FaultClearTests
    {
        static int count;
        static void Check(bool ok,string label){if(!ok)throw new Exception("FAIL clear: "+label);count++;}
        static void Reject(Action action,string label){bool failed=false;try{action();}catch(IOException){failed=true;}catch(InvalidOperationException){failed=true;}Check(failed,label);}
        static int[] Bytes(string text){return text.Split(' ').Select(s=>Convert.ToInt32(s,16)).ToArray();}
        static string Frame(int[] b){return "*"+String.Join(" ",b);}
        sealed class Fake:IFaultClearVci
        {
            internal readonly CommandSafety Gate=new CommandSafety();internal readonly List<string> Sent=new List<string>();
            internal bool BadIdentity,Negative,Timeout,BadAfter,BadBefore,Empty,Retained;
            internal int Clears,Closes;internal bool Uds;internal string Identity=ModuleTests.EngineIdentity;
            string Send(string command,string reply){Gate.Send(command,s=>Sent.Add(s));Gate.AcceptResponse(command,reply);return reply;}
            public string Exchange(VciCommand command){string reply;switch(command){
                case VciCommand.Firmware:reply="*1513";break;case VciCommand.Status:reply="*0 1260";break;case VciCommand.Model:reply="*CDP+";break;case VciCommand.Serial:reply="*654321";break;
                case VciCommand.Pins:reply="*114";break;case VciCommand.Relays:reply="*106";break;case VciCommand.Timing:reply="*0 10";break;case VciCommand.ReceiveMode:reply="*256";break;case VciCommand.StartAbs:reply="*193 208 143";break;
                case VciCommand.Disconnect:Closes++;reply="*1";break;
                case VciCommand.ReadUdsDtc:reply=Clears>0?"*89 2 255":"*89 2 255 18 52 86 8";break;case VciCommand.ReadAbsDtc:reply=(BadBefore&&Clears==0||BadAfter&&Clears>0)?"*127 23 34":Empty||Clears>0&&!Retained?"*87 0":"*87 1 18 52 8";break;
                default:throw new Exception("Unexpected clear command "+command);
            }return Send(VciProtocol.Encode(command),reply);}
            public string Configure(CanRoute r){return Send("*66A_0_500_"+r.Request.ToString("X3")+"_"+r.Response.ToString("X3")+"_000_"+r.Request.ToString("X3")+"_01_3E_00_00_00_00_00d02_200"+(r.UseCabinPins?"H03L08":"#"),"*256");}
            public string CabinSession(CanRoute r,bool enter){return Send(enter?"*608_10_C0":"*608_10_81",enter?"*80 192":"*80 129");}
            public string Identify(IdentityQuery query){throw new Exception("Unused");}
            public string ReadRecord(ReadRecord r){var b=Bytes(Identity);if(BadIdentity)b[b.Length-1]^=1;return Send(SerialVci.EncodeReadRecord(r),Frame(b));}
            public string ClearFaults(FaultClearPlan plan,bool off){string command=Gate.ArmFaultClear(plan,off);Gate.Send(command,s=>{Clears++;Sent.Add(s);});if(Timeout)throw new TimeoutException("Clear response timeout");string reply=Negative?"*127 20 34":Uds?"*84":"*84 255 0";Gate.AcceptResponse(command,reply);return reply;}
            public void Dispose(){}
        }
        internal static void Run()
        {
            var c=Catalogue.Load();var car=c.Vehicles.Single(v=>v.Id=="VCAN:all");var route=CanRoute.Plan(c,car).Single(r=>r.Nodes.Contains("CMM"));
            var unit=new DiscoveredEcu{Address=route.Id,Nodes=route.Nodes,Candidates=route.Candidates,Responded=true,Disconnected=true,Identity=ModuleTests.EngineIdentity};
            var snapshot=new UnitSnapshot{Unit=unit,Matches=IdentityResolver.Resolve(c,car.Code,unit),Reads=new List<DataRead>()};
            var plan=FaultClearPlan.Create(c,car,"CMM",new[]{snapshot});Check(!plan.Uds&&plan.ClearCommand=="*608_14_FF_00","KWP clear selected from exact protocol");
            var fake=new Fake();var log=new List<string>();var result=FaultClear.Session(fake,plan,true,s=>log.Add(s));
            Check(result.Success&&result.Changed&&result.Disconnected&&fake.Clears==1&&fake.Closes==1,"single clear plus verification and disconnect: "+result.Report);
            Check(log.Any(s=>s.StartsWith("BACKUP ID"))&&log.Any(s=>s.StartsWith("BACKUP DTC"))&&log.Any(s=>s.StartsWith("AFTER DTC")),"before and after persisted through log callback");
            Reject(()=>fake.Gate.ArmFaultClear(plan,true),"disconnected clear proof cannot replay");
            fake=new Fake{Empty=true};result=FaultClear.Session(fake,plan,true,delegate{});Check(result.Success&&!result.Changed&&fake.Clears==0,"empty DTC memory no-op");
            fake=new Fake{Retained=true};result=FaultClear.Session(fake,plan,true,delegate{});Check(result.Success&&result.Report.Contains("Počet záznamů závad: 1"),"returning fault reported, not falsely claimed empty");
            foreach(string failure in new[]{"identity","negative","timeout","before","after","backup"}){
                fake=new Fake{BadIdentity=failure=="identity",Negative=failure=="negative",Timeout=failure=="timeout",BadBefore=failure=="before",BadAfter=failure=="after"};
                result=FaultClear.Session(fake,plan,true,s=>{if(failure=="backup"&&s.StartsWith("BACKUP DTC"))throw new IOException("Cannot persist backup");});
                Check(!result.Success&&fake.Closes==1&&fake.Clears<=1,"failure stops without retry "+failure);
                if(failure=="identity"||failure=="before"||failure=="backup")Check(fake.Clears==0,"preflight must not clear "+failure);
            }
            fake=new Fake();result=FaultClear.Session(fake,plan,false,delegate{});Check(!result.Success&&fake.Sent.Count==0,"physical condition confirmation required before IO");
            Reject(()=>FaultClearPlan.Create(c,car,"RBG",new[]{snapshot}),"SRS has no clear plan");
            foreach(int request in new[]{0x744,0x7DF,0x7FF}){
                fake=new Fake();var forged=new FaultClearPlan{Catalogue=c,Vehicle=car,Route=new CanRoute{Request=request,Response=request-256,Nodes=new[]{"RBG"},Candidates=route.Candidates},Match=plan.Match,Definition=plan.Definition};
                Reject(()=>fake.Gate.ArmFaultClear(forged,true),"SRS and unknown target cannot arm "+request);
                Reject(()=>fake.Gate.Send("*608_14_FF_00",delegate{}),"raw KWP clear blocked "+request);Reject(()=>fake.Gate.Send("*608_14_FF_FF_FF",delegate{}),"raw UDS clear blocked "+request);
            }
            fake=new Fake();fake.Configure(route);Reject(()=>fake.Gate.ArmFaultClear(plan,true),"route alone cannot grant clear");
            fake.ReadRecord(new ReadRecord{Service=plan.Definition.IdentityService,Identifier=plan.Definition.IdentityIdentifier});Reject(()=>fake.Gate.ArmFaultClear(plan,true),"identity alone cannot grant clear");
            fake.Exchange(VciCommand.ReadAbsDtc);fake.BadBefore=true;fake.Exchange(VciCommand.ReadAbsDtc);Reject(()=>fake.Gate.ArmFaultClear(plan,true),"negative reread invalidates old DTC proof");
            var c4=c.Vehicles.Single(v=>v.Id=="B7:all");var c4route=CanRoute.Plan(c,c4).Single(r=>r.Id=="752:652");
            var c4snapshot=new UnitSnapshot{Unit=new DiscoveredEcu{Address=c4route.Id,Nodes=c4route.Nodes,Candidates=c4route.Candidates,Responded=true,Disconnected=true},Matches=new List<IdentityMatch>{new IdentityMatch{Key="AEE2010.full/BSI/BSI2010",Model="BSI2010",Variant="V1",Reference="9664992380"}},Reads=new List<DataRead>()};
            var udsPlan=FaultClearPlan.Create(c,c4,"BSI",new[]{c4snapshot});
            fake=new Fake{Uds=true,Identity="62 F0 80 96 64 99 23 80"};result=FaultClear.Session(fake,udsPlan,true,delegate{});
            Check(result.Success&&result.Changed&&fake.Clears==1&&fake.Sent.Contains("*608_14_FF_FF_FF")&&!fake.Sent.Contains("*608_14_FF_00"),"C4 UDS clear uses 24-bit group and verifies fresh ID: "+result.Report);
            Console.WriteLine("PASS: "+count+" fault-clear transaction, backup and SRS denial checks; simulated IO only.");
        }
    }
}

