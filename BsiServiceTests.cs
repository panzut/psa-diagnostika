using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace PsaReader
{
    internal static class BsiServiceTests
    {
        static int count;
        static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL BSI service: "+name);count++;}
        static void Reject(Action action,string name){bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}catch(IOException){failed=true;}Check(failed,name);}
        static int[] Bytes(string s){return s.Split(' ').Select(x=>Convert.ToInt32(x,16)).ToArray();}
        static string Frame(int[] b){return "*"+String.Join(" ",b);}
        sealed class Fake : IBsiServiceVci
        {
            internal readonly CommandSafety Gate=new CommandSafety();
            internal readonly UnitSnapshot Fixture=new JavaScriptSerializer{MaxJsonLength=4000000}.Deserialize<UnitSnapshot>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tests","fixtures","bsi-service-20261122.json")));
            internal int[] ServiceConfig=Bytes("61 B4 18 96 00 06 01"),ServiceStatus=Bytes("61 C9 00 23 91 E9 17 A9 17 BC 17 0A 01 25 00 87 03 55 FF FE 64 FF 50 6E 73 08 40");internal bool BadServiceReadback,ResetNegative,ResetNoEffect,ResetBusy,ServiceReopenMismatch,ServiceWriteInConnection;internal int[] Lighting,Power;internal int Writes,Closes,Opens;
            internal bool BadIdentity,BadMap,Negative,Timeout,BadReadback,BadReopen,BadClose,BadUnlock,BadSeed,ZeroSeed;
            internal List<string> Sent=new List<string>();
            internal Fake(){Lighting=Bytes(Fixture.Reads.Single(r=>r.Service==0x21&&r.Identifier==0xB9).Raw);Power=Bytes(Fixture.Reads.Single(r=>r.Service==0x21&&r.Identifier==0xC0).Raw);Power[30]&=~2;Power[14]=126;}
            internal string Send(string command,string reply){Gate.Send(command,s=>Sent.Add(s));Gate.AcceptResponse(command,reply);return reply;}
            public string Exchange(VciCommand command){string r;switch(command){case VciCommand.Firmware:r="*1423";break;case VciCommand.Model:r="*CDP+";break;case VciCommand.Serial:r="*123456";break;case VciCommand.Status:r="*0 1260";break;case VciCommand.Pins:r="*114";break;case VciCommand.Relays:r="*106";break;case VciCommand.Timing:r="*0 10";break;case VciCommand.ReceiveMode:r="*256";break;case VciCommand.Disconnect:Closes++;ServiceWriteInConnection=false;r=BadClose?"*0":"*1";break;default:throw new Exception("unexpected "+command);}return Send(VciProtocol.Encode(command),r);}
            public string Configure(CanRoute r){return Send("*66A_0_500_"+r.Request.ToString("X3")+"_"+r.Response.ToString("X3")+"_000_"+r.Request.ToString("X3")+"_01_3E_00_00_00_00_00d02_200"+(r.UseCabinPins?"H03L08":"#"),"*256");}
            public string CabinSession(CanRoute r,bool enter){if(enter)Opens++;return Send(enter?"*608_10_C0":"*608_10_81",enter?"*80 192":"*80 129");}
            public string Identify(IdentityQuery q){throw new Exception("unused");}
            public string ReadRecord(ReadRecord r){int[] b;if(r.Identifier==0x80)b=Bytes(BsiServiceProfile.Identity);else if(r.Identifier==0xFE)b=Bytes(BsiServiceProfile.Software);else if(r.Identifier==0xB9)b=(int[])Lighting.Clone();else if(r.Identifier==0xC0)b=(int[])Power.Clone();else if(r.Identifier==0xB4){b=(int[])ServiceConfig.Clone();if(BadServiceReadback&&Writes>0||ServiceReopenMismatch&&Closes>0)b[3]^=1;}else if(r.Identifier==0xC9)b=(int[])ServiceStatus.Clone();else throw new Exception("unknown record");if(BadIdentity&&r.Identifier==0x80)b[2]^=1;if((BadReadback&&Writes>0||BadReopen&&Opens>2)&&r.Identifier==0xB9)b[3]^=1;return Send(SerialVci.EncodeReadRecord(r),Frame(b));}
            public string ReadMap(CanRoute r,int root,int page){var b=Bytes(Fixture.Reads.Single(x=>x.Service==0x35&&x.Identifier==root&&x.Page==page).Raw);if(BadMap&&page==1)b[8]^=1;return Send(ParameterMap.Command(r,root,page),Frame(b));}
            public string ExecuteBsi(BsiAction action,bool off){Gate.ArmBsi(action,off);string command=Gate.ArmedBsiCommand();Gate.Send(command,s=>{Sent.Add(s);Writes++;});if(Timeout)throw new TimeoutException("simulated timeout");string reply;if(Negative)reply=action==BsiAction.EconomyOff?"*127 49 18":"*127 59 51";else if(action==BsiAction.EconomyOff){Power[30]&=~4;reply="*113 173";}else if(action==BsiAction.ServiceReset){if(ResetBusy||ServiceWriteInConnection)reply="*127 49 33";else if(ResetNegative)reply="*127 49 34";else{if(!ResetNoEffect){int units=ServiceConfig[3]*10;ServiceStatus[12]=units>>8;ServiceStatus[13]=units&255;ServiceStatus[25]++;ServiceStatus[26]&=~64;}reply="*113 170";}}else{Lighting=BsiServiceProfile.Daylight(Lighting,action==BsiAction.DaylightOn);reply="*123 185";}Gate.AcceptResponse(command,reply);return reply;}
            public string SetServiceInterval(int km,bool off){Gate.ArmServiceInterval(km,off);string cmd=Gate.ArmedBsiCommand();Gate.Send(cmd,s=>{Sent.Add(s);Writes++;});if(Timeout)throw new TimeoutException("simulated timeout");string reply=Negative?"*127 59 51":"*123 180";if(!Negative){ServiceConfig=ServiceMaintenance.Configuration(ServiceConfig,km);ServiceWriteInConnection=true;}Gate.AcceptResponse(cmd,reply);return reply;} public string EconomyResult(){return Send(BsiServiceProfile.EconomyPoll,"*113 173 0");}
            public void UnlockBsiConfiguration(bool engineOff){Gate.ArmBsiConfiguration(engineOff);Send(Gate.ArmedBsiCommand(),BadSeed?"*127 39 34":ZeroSeed?"*103 131 0 0 0 0":"*103 131 18 52 86 120");if(!Gate.ConfigurationUnlocked)Send(Gate.ConfigurationKeyCommand(),BadUnlock?"*127 39 53":"*103 132");if(!Gate.ConfigurationUnlocked)throw new IOException("simulated security denial");}
            public void Dispose(){}
        }
        internal static int Run()
        {
            // Golden vectors generated by the separately compiled upstream reference.
            foreach(var vector in new[]{"11111111 56F37A8A","12345678 57F71DFB","89ABCDEF 1F773BFD","FFFFFFFF 77D377F9","00000001 16FB1BCC"}){
                var pair=vector.Split(' ');var seed=Enumerable.Range(0,4).Select(i=>Convert.ToInt32(pair[0].Substring(i*2,2),16)).ToArray();
                Check(String.Join("",BsiConfigurationKey.FromSeed(seed).Select(x=>x.ToString("X2")))==pair[1],"JCAE key matches independent upstream vector "+pair[0]);
            }
            var c=Catalogue.Load();var car=c.Vehicles.Single(v=>v.Id=="VCAN:all");var route=BsiServices.Route(c,car);
            var fake=new Fake();var expected=Accessories.Preview(c,car,"BSI",new[]{fake.Fixture},false);
            Check(BsiServiceProfile.Hex(BsiServiceProfile.Daylight(fake.Lighting,false))==expected.Proposed,"compiled one-bit location matches independent dynamic-map decoder");
            BsiServiceProfile.CheckMap(fake.Fixture.Reads,0xB0,0xB9,BsiServiceProfile.LightingMap);BsiServiceProfile.CheckMap(fake.Fixture.Reads,0xC0,0xC0,BsiServiceProfile.PowerMap);Check(true,"compiled maps match actual recorded ECU maps");
            var r=BsiServices.Session(fake,route,BsiAction.DaylightOff,true,delegate{});Check(r.Success&&r.Changed&&r.Disconnected&&fake.Writes==1&&fake.Opens==3&&fake.Closes==2,"OFF acknowledged and verified after reopen, one write: "+r.Report);
            // The complete packet must contain the independently calculated record, not
            // a duplicated 61 B9 header, a guessed byte offset or stale default values.
            Check(fake.Sent.Single(s=>s.StartsWith("*608_3B_"))=="*608_3B_B9_FF_"+expected.Proposed.Substring(6).Replace(' ','_'),"full-record packet equals independent expected data");
            Reject(()=>fake.Gate.Send(BsiServiceProfile.DaylightCommand(fake.Lighting,true),delegate{}),"no replay after disconnect");
            r=BsiServices.Session(fake,route,BsiAction.DaylightOn,true,delegate{});Check(r.Success&&fake.Writes==2,"ON verified using new current record");
            fake=new Fake();r=BsiServices.Session(fake,route,BsiAction.DaylightOn,true,delegate{});Check(r.Success&&!r.Changed&&fake.Writes==0,"already-on does not write");
            fake=new Fake();fake.Lighting[19]&=~1;r=BsiServices.Session(fake,route,BsiAction.DaylightOff,true,delegate{});Check(r.Success&&r.Changed&&fake.Writes==1&&fake.Lighting[18]==1,"default bit off alone is not a completed DRL-off configuration");
            fake=new Fake{BadSeed=true};r=BsiServices.Session(fake,route,BsiAction.DaylightOff,true,delegate{});Check(!r.Success&&fake.Writes==0&&!fake.Sent.Any(s=>s.StartsWith("*608_27_84_")),"rejected seed never sends a key or configuration");
            fake=new Fake{ZeroSeed=true};r=BsiServices.Session(fake,route,BsiAction.DaylightOff,true,delegate{});Check(r.Success&&fake.Writes==1&&!fake.Sent.Any(s=>s.StartsWith("*608_27_84_")),"zero seed means already unlocked; no invented key");
            fake=new Fake();r=BsiServices.Session(fake,route,BsiAction.DaylightOff,false,delegate{});Check(!r.Success&&fake.Sent.Count==0,"no confirmation opens no connection");
            foreach(string failure in new[]{"identity","map","low","ignition","unlock","negative","timeout","readback","reopen","close"}){
                fake=new Fake();switch(failure){case "identity":fake.BadIdentity=true;break;case "map":fake.BadMap=true;break;case "low":fake.Power[14]=121;break;case "ignition":fake.Power[30]&=~8;break;case "unlock":fake.BadUnlock=true;break;case "negative":fake.Negative=true;break;case "timeout":fake.Timeout=true;break;case "readback":fake.BadReadback=true;break;case "reopen":fake.BadReopen=true;break;case "close":fake.BadClose=true;break;}
                r=BsiServices.Session(fake,route,BsiAction.DaylightOff,true,delegate{});Check(!r.Success&&fake.Writes<=1&&fake.Closes>=1,"failure closes without retry: "+failure);
                if(new[]{"identity","map","low","ignition","unlock"}.Contains(failure))Check(fake.Writes==0,"preflight failure never writes: "+failure);
            }
            fake=new Fake();r=BsiServices.Session(fake,route,BsiAction.EconomyOff,true,delegate{});Check(r.Success&&!r.Changed&&fake.Writes==0,"inactive economy is a no-op");
            fake=new Fake();fake.Power[30]|=4;r=BsiServices.Session(fake,route,BsiAction.EconomyOff,true,delegate{});Check(r.Success&&r.Changed&&fake.Writes==1&&fake.Sent.Contains(BsiServiceProfile.EconomyPoll),"economy exact start/status then verified state");
            foreach(int target in new[]{0x744,0x6AD,0x7FF}){
                fake=new Fake();var config="*66A_0_500_"+target.ToString("X3")+"_"+(target==0x6AD?0x68D:target-0x100).ToString("X3")+"_000_"+target.ToString("X3")+"_01_3E_00_00_00_00_00d02_200"+(target==0x6AD?"#":"H03L08");
                try{fake.Send(config,"*256");}catch(InvalidOperationException){}
                Reject(()=>fake.Gate.ArmBsi(BsiAction.DaylightOff,true),"cannot arm SRS/ABS/unknown "+target);
                Reject(()=>fake.Gate.ArmBsiConfiguration(true),"cannot unlock SRS/ABS/unknown "+target);
                foreach(var cmd in new[]{BsiServiceProfile.DaylightCommand(fake.Lighting,false),BsiServiceProfile.EconomyStart,BsiServiceProfile.EconomyPoll,ServiceMaintenance.ResetCommand,ServiceMaintenance.ConfigurationCommand(fake.ServiceConfig,10000),"*608_27_83","*608_30_C5_00_00_06_64"})Reject(()=>fake.Gate.Send(cmd,delegate{}),"active command blocked at other target "+target);
            }
            fake=new Fake();Reject(()=>fake.Gate.ArmBsi(BsiAction.DaylightOff,true),"no route/session cannot arm");
            BsiServiceProfile.CheckMap(fake.Fixture.Reads,0xB0,0xB4,ServiceMaintenance.ConfigMap);BsiServiceProfile.CheckMap(fake.Fixture.Reads,0xC0,0xC9,ServiceMaintenance.StatusMap);
            Check(ServiceMaintenance.Distance(fake.ServiceStatus)==-5860,"actual C9 magnitude and negative sign decoded");
            var def=c.Definition("AEE2004.full/BSI/BSI");
            var b4record=def.Records.Single(x=>x.Service==0x21&&x.Identifier==0xB4);
            var bound=MappedDecoder.Bind(IdentityResolver.Layout(b4record,fake.Fixture.Matches.Single(x=>x.Key=="AEE2004.full/BSI/BSI").Variant),ServiceMaintenance.ConfigMap);
            var field=bound.Fields.Single(x=>x.Name=="DURATION_BEFORE_FIRST_SERVICE");
            Check(field.Bits=="2.7-2.0"&&field.Factor==200,"interval location and scale independently bound from database");
            fake=new Fake();var originalLights=(int[])fake.Lighting.Clone();r=BsiServices.Session(fake,route,BsiAction.ServiceReset,true,delegate{},10000);
            Check(r.Success&&r.Changed&&r.Disconnected&&fake.Writes==2&&fake.Closes==3,"10k interval then reset verified: "+r.Report);
            Check(fake.Sent.Single(x=>x.StartsWith("*608_3B_B4_"))=="*608_3B_B4_FF_18_32_00_06_01","10k changes only interval byte, preserves time/type");
            Check(fake.Sent.Count(x=>x==ServiceMaintenance.ResetCommand)==1&&fake.ServiceStatus[25]==9&&fake.Lighting.SequenceEqual(originalLights),"one reset, count advanced, lights preserved");
            int writeIndex=fake.Sent.FindIndex(x=>x.StartsWith("*608_3B_B4_")),resetIndex=fake.Sent.IndexOf(ServiceMaintenance.ResetCommand);
            var between=fake.Sent.Skip(writeIndex+1).Take(resetIndex-writeIndex-1).ToArray();
            Check(between.Contains("*607")&&between.Any(x=>x.StartsWith("*66A_"))&&between.Contains("*608_21_B4")&&between.Contains("*608_35_B0_00_00_01_11"),"coding and reset separated by verified disconnect, fresh connection and maps");
            Check(!between.Any(x=>x.StartsWith("*608_27_")),"reset connection does not unlock or recode");
            Reject(()=>fake.Gate.Send(ServiceMaintenance.ResetCommand,delegate{}),"reset cannot replay after disconnect");
            fake=new Fake();fake.ServiceConfig[3]=50;r=BsiServices.Session(fake,route,BsiAction.ServiceReset,true,delegate{},10000);
            Check(r.Success&&fake.Writes==1&&!fake.Sent.Any(x=>x.StartsWith("*608_3B_")),"already 10k skips config write but resets once");
            foreach(int km in new[]{0,999,10001,50200,Int32.MaxValue}){
                fake=new Fake();r=BsiServices.Session(fake,route,BsiAction.ServiceReset,true,delegate{},km);Check(!r.Success&&fake.Sent.Count==0,"invalid km before any connection "+km);
            }
            foreach(int km in new[]{1000,10200,50000}){
                fake=new Fake();r=BsiServices.Session(fake,route,BsiAction.ServiceReset,true,delegate{},km);Check(r.Success&&ServiceMaintenance.Distance(fake.ServiceStatus)==km,"selectable exact interval "+km);
            }
            foreach(string failure in new[]{"identity","map","low","ignition","unlock","negative","timeout","readback","reset-denied","reset-no-effect","reset-busy","reopen-config"}){
                fake=new Fake();switch(failure){case "identity":fake.BadIdentity=true;break;case "map":fake.BadMap=true;break;case "low":fake.Power[14]=121;break;case "ignition":fake.Power[30]&=~8;break;case "unlock":fake.BadUnlock=true;break;case "negative":fake.Negative=true;break;case "timeout":fake.Timeout=true;break;case "readback":fake.BadServiceReadback=true;break;case "reset-denied":fake.ResetNegative=true;break;case "reset-no-effect":fake.ResetNoEffect=true;break;case "reset-busy":fake.ResetBusy=true;break;case "reopen-config":fake.ServiceReopenMismatch=true;break;}
                r=BsiServices.Session(fake,route,BsiAction.ServiceReset,true,delegate{},10000);
                Check(!r.Success&&fake.Closes>=1&&fake.Writes<=2,"service failure stops and disconnects: "+failure);
                if(failure.StartsWith("reset-"))Check(r.Changed&&r.Report.Contains("Interval již byl změněn"),"partial config success reported: "+failure);
                else Check(!fake.Sent.Contains(ServiceMaintenance.ResetCommand),"no reset after failed preparation: "+failure);
            }
            fake=new Fake();r=BsiServices.Session(fake,route,BsiAction.ServiceReset,false,delegate{},10000);Check(!r.Success&&fake.Sent.Count==0,"service engine-off confirmation required");
            foreach(int target in new[]{0x744,0x6AD,0x7FF}){
                fake=new Fake();try{fake.Send("*66A_0_500_"+target.ToString("X3")+"_"+(target==0x6AD?0x68D:target-256).ToString("X3")+"_000_"+target.ToString("X3")+"_01_3E_00_00_00_00_00d02_200"+(target==0x6AD?"#":"H03L08"),"*256");}catch(InvalidOperationException){}
                Reject(()=>fake.Gate.ArmServiceInterval(10000,true),"cannot arm interval on SRS/ABS/unknown "+target);Reject(()=>fake.Gate.ArmBsi(BsiAction.ServiceReset,true),"cannot arm reset on SRS/ABS/unknown "+target);
            }
            Console.WriteLine("PASS: "+count+" BSI service transaction and common-transport checks; simulated IO only.");return 0;
        }
    }
}


