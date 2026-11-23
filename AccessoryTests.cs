using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace PsaReader
{
    internal static class AccessoryTests
    {
        static int count;
        static void Check(bool value,string label){if(!value)throw new Exception("FAIL accessories: "+label);count++;}
        static void Reject(Action action,string label){bool rejected=false;try{action();}catch(IOException){rejected=true;}catch(InvalidOperationException){rejected=true;}Check(rejected,label);}
        static string Setup(int tx,int rx,bool cabin){return "*66A_0_500_"+tx.ToString("X3")+"_"+rx.ToString("X3")+"_000_"+tx.ToString("X3")+"_01_3E_00_00_00_00_00d02_200"+(cabin?"H03L08":"#");}
        static CommandSafety Gate(int tx,int rx,bool cabin){var gate=new CommandSafety();var setup=Setup(tx,rx,cabin);gate.Send(setup,delegate{});gate.AcceptResponse(setup,"*256");return gate;}
        static UnitSnapshot Fixture(){return new JavaScriptSerializer{MaxJsonLength=1024*1024}.Deserialize<UnitSnapshot>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tests","fixtures","bsi-daylight-20261122.json"),Encoding.UTF8));}
        internal static int Run()
        {
            int writes=0;Action<string> write=s=>writes++;
            var gate=new CommandSafety();Reject(()=>gate.Send("*608_21_80",write),"no ECU traffic before confirmed routing");Check(writes==0,"rejection occurs before serial sink");
            foreach(var command in new[]{"*201","*203","*20A","*200","*60b","*60bc","*607"})gate.Send(command,write);
            Check(writes==7,"known adapter checks and cleanup preserved");
            var setup=Setup(0x752,0x652,true);gate.Send(setup,write);gate.AcceptResponse(setup,"*0");Reject(()=>gate.Send("*608_21_80",write),"failed setup never enables ECU traffic");
            gate=Gate(0x752,0x652,true);
            foreach(var command in new[]{"*608f00_0A","*608pN","*608_10_C0","*608_21_80","*608_21_B9","*608_22_F1_90","*608_17_FF_00","*608_35_B0_00_00_01_11","*608_35_C0_00_00_20_11","*608_10_81"})gate.Send(command,write);
            Check(true,"existing BSI read/session/map flow accepted by common gate");
            foreach(var command in new[]{"*608_30_C5_03_01_03","*608_3B_B9_00","*608_27_83","*608_31_A8_00","*608_11_01","*608_14_FF_00","*608_35_B0_00_00_00_11","*608_35_B0_00_00_21_11","*608_21_80_00","*608_21_80\r*608_30_C5_03_01_03","*608_10_03","*608_2F_C5_03","*999"}){
                int before=writes;Reject(()=>gate.Send(command,write),"unverified/unknown/injected operation rejected: "+command.Replace('\r',' '));Check(writes==before,"rejected request never reaches serial sink");
            }
            gate=Gate(0x744,0x644,true);gate.Send("*608_10_C0",write);gate.Send("*608_21_FE",write);gate.Send("*608_17_FF_00",write);
            foreach(int service in new[]{0x11,0x14,0x27,0x2E,0x2F,0x30,0x31,0x32,0x34,0x35,0x36,0x37,0x3B}){
                int before=writes;Reject(()=>gate.Send("*608_"+service.ToString("X2")+"_00_00",write),"SRS rejects active service "+service.ToString("X2"));Check(writes==before,"SRS blocked before serial sink");
            }
            gate.Send("*608_10_81",write);gate.Send("*607",write);Reject(()=>gate.Send("*608_21_FE",write),"disconnect invalidates target");
            gate=Gate(0x752,0x652,false);Reject(()=>gate.Send("*608_35_B0_00_00_01_11",write),"map forbidden on unverified wiring");
            gate=Gate(0x6AD,0x68D,false);gate.Send("*608_81",write);gate.Send("*608_17_FF_00",write);Reject(()=>gate.Send("*608_30_C5_03_01_03",write),"BSI-shaped activation cannot be routed to ABS");
            Reject(()=>gate.Send(Setup(0x7FF,0x7FE,true),write),"unknown/broadcast target rejected");Reject(()=>gate.Send("*608_21_FE",write),"invalid setup invalidates previous route");
            Check(Accessories.Tests("RBG").Count==0&&Accessories.Tests("SRS").Count==0&&Accessories.Tests("ABS").Count==0&&Accessories.Tests("BSM").Count==0,"no imported or shared-address actuator capability leaks into other nodes");
            Check(Accessories.Tests("BSI").Count==6&&Accessories.Tests("BSI").All(item=>item.Availability.StartsWith("Nedostupné")),"incomplete source definitions never imply executable tests");
            var c=Catalogue.Load();var car=c.Vehicles.Single(v=>v.Id=="VCAN:all");var fixture=Fixture();
            string original=fixture.Reads.Single(r=>r.Service==0x21).Raw;
            var off=Accessories.Preview(c,car,"BSI",new[]{fixture},false);var on=Accessories.Preview(c,car,"BSI",new[]{fixture},true);
            Check(off.Current&&!off.Requested&&off.Original==original,"actual captured DRL state is on and OFF proposal retains source");
            var a=off.Original.Split(' ').Select(x=>Convert.ToInt32(x,16)).ToArray();var b=off.Proposed.Split(' ').Select(x=>Convert.ToInt32(x,16)).ToArray();
            Check(a.Length==b.Length&&b[off.TypeDataByte+1]==1&&Enumerable.Range(0,a.Length).All(i=>i==off.TypeDataByte+1||i==off.DataByte+1?a[i]>=0:a[i]==b[i])&&(a[off.DataByte+1]^b[off.DataByte+1])==off.Mask,"OFF proposal changes only mapped DRL type and default bit");
            Check(off.OriginalHash!=off.ProposedHash&&on.OriginalHash==on.ProposedHash&&on.Original==on.Proposed,"hashes distinguish change from already-on no-op");
            Check(fixture.Reads.Single(r=>r.Service==0x21).Raw==original,"preview never mutates captured data");
            fixture.Reads.Single(r=>r.Service==0x21).Raw=off.Proposed;
            var restore=Accessories.Preview(c,car,"BSI",new[]{fixture},true);Check(!restore.Current&&restore.Proposed==original,"ON proposal restores just DRL bit");
            Reject(()=>Accessories.Preview(c,car,"RBG",new[]{Fixture()},false),"SRS cannot use configuration preview");
            Reject(()=>Accessories.Preview(c,car,"BSM",new[]{Fixture()},false),"shared address does not grant BSI configuration to BSM");
            Reject(()=>Accessories.Preview(c,car,"BSI",new UnitSnapshot[0],false),"no data is not a default configuration");
            Reject(()=>Accessories.Preview(c,car,"BSI",new[]{Fixture(),Fixture()},false),"ambiguous snapshots rejected");
            fixture=Fixture();fixture.Completed=false;Reject(()=>Accessories.Preview(c,car,"BSI",new[]{fixture},false),"incomplete capture rejected");
            fixture=Fixture();fixture.Unit.Disconnected=false;Reject(()=>Accessories.Preview(c,car,"BSI",new[]{fixture},false),"unconfirmed cleanup rejected");
            fixture=Fixture();fixture.Reads.Remove(fixture.Reads.First(r=>r.Service==0x35));Reject(()=>Accessories.Preview(c,car,"BSI",new[]{fixture},false),"partial parameter map rejected");
            fixture=Fixture();fixture.Reads.Single(r=>r.Service==0x21).Raw="61 B9 00";Reject(()=>Accessories.Preview(c,car,"BSI",new[]{fixture},false),"truncated settings rejected");
            fixture=Fixture();fixture.Matches.Single(m=>m.Key==Accessories.BsiKey).Reference="0000000000";Reject(()=>Accessories.Preview(c,car,"BSI",new[]{fixture},false),"different BSI reference rejected");
            Console.WriteLine("PASS: "+count+" accessory safety and configuration-preview checks; no device IO.");return 0;
        }
    }
}
