using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PsaReader
{
    internal static class ModuleTests
    {
        internal const string EngineIdentity="61 FE 00 00 00 00 03 3B 04 03 09 01 39 0D 01 00 00 00 FE 02 00 00 01 66 13 75";
        static int count;
        static void Check(bool value,string label){if(!value)throw new Exception("FAIL module: "+label);count++;}
        static void Reject(Action action,string label){bool rejected=false;try{action();}catch(IOException){rejected=true;}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}Check(rejected,label);}
        sealed class Fake : IParameterVci
        {
            internal string Identity=EngineIdentity;
            internal bool BadCleanup, Timeout;
            internal int Reads,Disconnected;
            public string Exchange(VciCommand c)
            {
                switch(c){
                    case VciCommand.Firmware:return "*1423";case VciCommand.Status:return "*0 1450";
                    case VciCommand.Model:return "*CDP+";case VciCommand.Serial:return "*123456";
                    case VciCommand.Pins:return "*114";case VciCommand.Relays:return "*106";
                    case VciCommand.Timing:return "*0 10";case VciCommand.ReceiveMode:return "*256";
                    case VciCommand.StartAbs:return "*193 208 143";
                    case VciCommand.ReadAbsDtc:Reads++;if(Timeout)throw new TimeoutException("test");return "*87 0";
                    case VciCommand.Disconnect:Disconnected++;return BadCleanup?"*0":"*1";
                    default:throw new Exception("Unexpected command");
                }
            }
            public string Configure(CanRoute route){return "*256";}
            public string CabinSession(CanRoute route,bool enter){return enter?"*80 192":"*80 129";}
            public string Identify(IdentityQuery query){return "*"+String.Join(" ",Identity.Split(' ').Select(s=>Convert.ToInt32(s,16).ToString()));}
            public string ReadRecord(ReadRecord record){if(record.Service==0x21&&record.Identifier==0xFE)return Identify(IdentityQuery.KwpSupplier);Reads++;return "*97 193 0";}
            public void Dispose(){}
        }
        internal static int Run()
        {
            var c=Catalogue.Load();var car=c.Vehicles.First(v=>v.Id=="VCAN:all");var route=CanRoute.Plan(c,car).Single(item=>item.Nodes.Contains("CMM"));
            var unit=new DiscoveredEcu{Identity=EngineIdentity,Candidates=route.Candidates};var matches=IdentityResolver.Resolve(c,car.Code,unit);
            Check(matches.Count==1&&matches[0].Model=="EDC16CP39"&&matches[0].Variant=="VCAN_V1_10","recorded motor identity matches exact reference and vehicle");
            Check(IdentityResolver.Resolve(c,"WRONG",unit).Count==0,"reference cannot bypass vehicle membership");
            Check(IdentityResolver.Resolve(c,car.Code,new DiscoveredEcu{Identity=EngineIdentity.Replace("66 13 75","66 13 FF"),Candidates=route.Candidates}).Count==0,"invalid BCD identity cannot resolve");
            var f=new FieldDefinition{Bits="1.7-2.0",Type="uint",Factor=0.25,Offset=-40,Unit="C"};
            Check(RecordDecoder.Value(f,new byte[]{1,0})=="24 C","multibyte big endian scale and offset");
            f.Bits="1.7-1.0";f.Type="int";f.Factor=1;f.Offset=0;f.Unit="";Check(RecordDecoder.Value(f,new byte[]{254})=="-2","signed field");
            f.Type="bcd";Check(RecordDecoder.Value(f,new byte[]{1})=="01","leading zeros in identity preserved");
            Reject(()=>RecordDecoder.Value(f,new byte[]{0xAF}),"nondecimal nibble rejected");
            f.Bits="0.7-1.0";Reject(()=>RecordDecoder.Value(f,new byte[]{1}),"negative data offset rejected");
            f.Bits="1.7-2.0";Reject(()=>RecordDecoder.Value(f,new byte[]{1}),"truncated data rejected");
            var d=c.Definition(matches[0].Key);var rec=d.Records.First(item=>item.Identifier==0xC0);var layout=IdentityResolver.Layout(rec,matches[0].Variant);
            Reject(()=>RecordDecoder.Decode(rec,layout,new[]{0x61,0xC2,0}),"wrong identifier rejected");
            Reject(()=>RecordDecoder.Decode(rec,layout,new[]{0x61,0xC0,0}),"wrong length rejected");
            Reject(()=>RecordDecoder.Decode(rec,layout,new[]{0x7F,0x21,0x12}),"recorded motor rejection never decoded as measurements");
            Reject(()=>IdentityResolver.Layout(d.Records.First(item=>item.Identifier==0xC1),matches[0].Variant),"record from another vehicle variant unavailable");
            Reject(()=>ModuleReader.CanonicalRecord(d,new ReadRecord{Service=0x21,Identifier=0x87},matches[0].Variant),"duplicate imported record identifier cannot choose a format arbitrarily");
            Reject(()=>SerialVci.EncodeReadRecord(new ReadRecord{Service=0x2E,Identifier=1}),"write service rejected by transport");
            Reject(()=>SerialVci.EncodeReadRecord(new ReadRecord{Service=0x21,Identifier=256}),"out of range LID rejected");
            Check(SerialVci.EncodeReadRecord(new ReadRecord{Service=0x22,Identifier=0xF190})=="*608_22_F1_90","UDS read encoding");
            Check(ModuleReader.DecodeFaults(d,new[]{0x57,0}).Contains("Počet záznamů závad: 0"),"empty fault list only from valid response");
            Reject(()=>ModuleReader.DecodeFaults(d,new[]{0x57,1,0}),"truncated faults are not reported empty");
            var good=new Fake();var r=ModuleReader.Session(good,c,car,route,matches[0],null,delegate{});
            Check(r.Success&&good.Reads==1&&good.Disconnected==1,"valid identity reads once and disconnects");
            var changed=new Fake{Identity=EngineIdentity.Replace("66 13 75","66 13 76")};r=ModuleReader.Session(changed,c,car,route,matches[0],null,delegate{});
            Check(!r.Success&&changed.Reads==0&&changed.Disconnected==1,"changed ECU blocks reading and still disconnects");
            var timeout=new Fake{Timeout=true};r=ModuleReader.Session(timeout,c,car,route,matches[0],null,delegate{});
            Check(!r.Success&&timeout.Reads==1&&timeout.Disconnected==1,"timeout no retry and cleanup");
            var cleanup=new Fake{BadCleanup=true};r=ModuleReader.Session(cleanup,c,car,route,matches[0],null,delegate{});
            Check(!r.Success&&r.Report.Contains("UKONČENÍ NEPOTVRZENO"),"successful read never masks failed disconnect");
            Console.WriteLine("PASS: "+count+" identity, decoding and module-session checks; simulated adapter only.");return 0;
        }
    }
}
