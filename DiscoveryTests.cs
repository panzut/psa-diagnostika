using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PsaReader
{
    internal static class DiscoveryTests
    {
        static int count;
        static void Check(bool condition,string name){if(!condition)throw new Exception("FAIL discovery: "+name);count++;}
        sealed class Fake : IDiscoveryVci
        {
            internal string Reply="*97 254 1 2 3";internal string Second="*127 34 49";
            internal bool Timeout, BadIdle, BadCleanup, BadFirmware;
            internal int Configured,Queries,Disconnected;
            public string Exchange(VciCommand c)
            {
                switch(c){
                    case VciCommand.Firmware:return BadFirmware?"*not-a-version":"*1423";
                    case VciCommand.Status:return BadIdle?"*1 1450":"*0 1450";
                    case VciCommand.Model:return "*CDP+";
                    case VciCommand.Serial:return "*123456";
                    case VciCommand.Pins:return "*114";
                    case VciCommand.Relays:return "*106";
                    case VciCommand.Timing:return "*0 10";
                    case VciCommand.ReceiveMode:return "*256";
                    case VciCommand.StartAbs:return "*193 208 143";
                    case VciCommand.Disconnect:Disconnected++;return BadCleanup?"*0":"*1";
                    default:throw new Exception("Discovery must not send session switching or DTC/other commands");
                }
            }
            public string Configure(CanRoute route){CanRoute.Validate(route);Configured++;return "*256";}
            internal bool BadCabinExit;internal int CabinEntries,CabinExits;
            public string CabinSession(CanRoute route,bool enter){if(enter){CabinEntries++;return "*80 192";}CabinExits++;return BadCabinExit?"*255":"*80 129";}
            public string Identify(IdentityQuery query){Queries++;if(Timeout)throw new TimeoutException("test timeout");return query==IdentityQuery.KwpSupplier?Reply:Second;}
            public void Dispose(){}
        }
        internal static int Run()
        {
            var catalog=Catalogue.Load();var car=catalog.Vehicles.First(v=>v.Id=="VCAN:all");var plan=CanRoute.Plan(catalog,car);
            Check(catalog.Vehicles.Length>=100&&catalog.Ecus.Length>=400,"real imported catalogue");
            Check(plan.Count>10,"multi-ECU plan");
            Check(plan.Select(x=>x.Id).Distinct().Count()==plan.Count,"shared addresses deduplicated");
            Check(plan[0].Id=="6AD:68D"&&plan[1].Id=="6A8:688","known ABS then engine");
            Check(plan.Any(route=>route.Nodes.Contains("BSI")&&route.Nodes.Contains("BSM")),"shared gateway retained as candidates");
            Check(!plan.Any(route=>route.Request==0x7B0),"foreign alternative routing not imported into scanner");
            Check(car.Nodes.Any(n=>n.Models.Any(m=>m.Key==null)),"missing definitions remain explicit");
            bool rejected=false;Check(CanRoute.Plan(catalog,catalog.Vehicles.First(v=>v.Architecture=="AEE2001.full")).Count==0,"unsupported K-line architecture has no CAN scan");
            rejected=false;try{CanRoute.Validate(new CanRoute{Request=0x7DF,Response=0x7E8});}catch(InvalidOperationException){rejected=true;}Check(rejected,"broadcast not permitted");
            rejected=false;try{catalog.Definition("../../Vci");}catch(IOException){rejected=true;}Check(rejected,"definition path traversal rejected");
            var good=new Fake();var r=Discovery.Probe(good,plan[0],delegate{});
            Check(r.Responded&&r.Disconnected&&good.Queries==1&&good.Disconnected==1,"positive identity and cleanup");
            var negative=new Fake{Reply="*127 33 17"};r=Discovery.Probe(negative,plan[0],delegate{});
            Check(r.Responded&&r.State.Contains("odmítla")&&negative.Queries==2,"negative ECU response establishes presence, not identity");
            var noStart=new CanRoute{Request=plan[0].Request,Response=plan[0].Response,Nodes=plan[0].Nodes,Candidates=plan[0].Candidates};
            var wrongService=new Fake{Reply="*127 46 49"};r=Discovery.Probe(wrongService,noStart,delegate{});
            Check(!r.Responded&&wrongService.Queries==1&&r.Disconnected,"unrelated negative reply cannot establish presence");
            var timeout=new Fake{Timeout=true};r=Discovery.Probe(timeout,noStart,delegate{});
            Check(!r.Responded&&r.State=="Neodpověděla"&&r.Disconnected&&timeout.Queries==1,"timeout never means absent and is not retried");
            var bridgeError=new Fake{Reply="*259"};r=Discovery.Probe(bridgeError,noStart,delegate{});
            Check(!r.Responded&&r.Disconnected,"bridge error not ECU presence");
            var badCleanup=new Fake{BadCleanup=true};r=Discovery.Probe(badCleanup,plan[0],delegate{});
            Check(r.Responded&&!r.Disconnected,"cleanup failure stays visible despite successful identity");
            var idle=new Fake{BadIdle=true};r=Discovery.Probe(idle,plan[0],delegate{});
            Check(!r.Disconnected&&idle.Configured==0&&idle.Queries==0,"unexpected active state prevents scan progression");
            var badAdapter=new Fake{BadFirmware=true};rejected=false;try{Discovery.CheckAdapter(badAdapter);}catch(IOException){rejected=true;}Check(rejected&&badAdapter.Configured==0,"firmware gate before routing");
            var abs=catalog.Definition("AEE2004.full/ABS/ESP81");
            Check(abs.Records.Any(x=>x.Service==0x21&&x.Identifier==0xC0),"real ABS read records imported");
            Check(abs.Records.All(x=>x.Service==0x21||x.Service==0x22),"catalogue exposes only read services");
            Check(abs.Records.First(x=>x.Identifier==0xC1&&x.Service==0x21).Layouts.Length>1,"variant alternatives preserved");
            var shortVin=new[]{0x62,0xF1,0x90,65};Check(!Discovery.IdentityMatches(IdentityQuery.UdsVin,shortVin),"truncated VIN response rejected");
            var cabin=plan.Single(x=>x.Id=="752:652");Check(cabin.CabinDiagnosticSession&&!plan[0].CabinDiagnosticSession,"cabin handshake requires documented candidate sessions and address class");
            var cf=new Fake();r=Discovery.Probe(cf,cabin,delegate{});Check(r.Responded&&r.Disconnected&&cf.CabinEntries==1&&cf.CabinExits==1,"cabin start and default-session return");
            cf=new Fake{BadCabinExit=true};r=Discovery.Probe(cf,cabin,delegate{});Check(!r.Disconnected&&cf.Disconnected==1,"failed ECU session exit still disconnects bridge and stops progression");
            Console.WriteLine("PASS: "+count+" catalogue/discovery checks; simulated adapter only.");return 0;
        }
    }
}

