using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PsaReader
{
    internal static class BulkTests
    {
        static int count;
        static void Check(bool value,string label){if(!value)throw new Exception("FAIL bulk: "+label);count++;}
        static void Reject(Action action,string label){bool rejected=false;try{action();}catch(IOException){rejected=true;}catch(InvalidOperationException){rejected=true;}Check(rejected,label);}
        sealed class Fake : IBulkVci
        {
            internal int Reads,Exits,Disconnects,MapReads;
            internal bool Cabin,BadCleanup,Timeout,ShortDtc;
            public string Exchange(VciCommand command){switch(command){
                case VciCommand.Status:return "*0 1470";case VciCommand.Pins:return "*114";case VciCommand.Relays:return "*106";
                case VciCommand.Timing:return "*0 10";case VciCommand.ReceiveMode:return "*256";case VciCommand.StartAbs:return "*193 208 143";
                case VciCommand.ReadUdsDtc:return "*89 2 255";case VciCommand.ReadAbsDtc:return ShortDtc?"*87 2 245 255 241 89":"*87 0";case VciCommand.Disconnect:Disconnects++;return BadCleanup?"*0":"*1";
                default:throw new Exception("Forbidden/unexpected command "+command);
            }}
            public string Configure(CanRoute route){return "*256";}
            public string CabinSession(CanRoute route,bool enter){if(!enter)Exits++;return enter?"*80 192":"*80 129";}
            public string Identify(IdentityQuery query){
                if(Cabin){if(query==IdentityQuery.Bsi)return "*97 128 150 100 5 135 128 0 44 150 100 5 135 128 5 1 255 255 10 64 82 17 71 153";if(query==IdentityQuery.Bsm)return "*97 139 150 96 55 100 128 0 19 150 100 112 99 128 8 13 255 255 255 255 255 255 255 255";return "*127 33 18";}
                if(query==IdentityQuery.UdsVin)return "*127 34 49";
                return "*"+String.Join(" ",ModuleTests.EngineIdentity.Split(' ').Select(x=>Convert.ToInt32(x,16)));
            }
            public string ReadRecord(ReadRecord record){Reads++;if(Timeout)throw new TimeoutException("test timeout");return "*127 "+record.Service+" 18";}
            public string ReadMap(CanRoute route,int root,int page){ParameterMap.Command(route,root,page);MapReads++;return "*117 "+root+" 255";}
            public void Dispose(){}
        }
        internal static int Run()
        {
            var c=Catalogue.Load();var car=c.Vehicles.First(v=>v.Id=="VCAN:all");var routes=CanRoute.Plan(c,car);var cabin=routes.Single(item=>item.Id=="752:652");var engine=routes.Single(item=>item.Id=="6A8:688");
            Check(cabin.UseCabinPins&&!engine.UseCabinPins,"separate electrical routing retained");
            var f=new Fake{Cabin=true};var r=BulkReader.ReadUnit(f,c,car,cabin,()=>false,delegate{});
            Check(r.Completed&&r.Unit.Disconnected&&f.Exits==1&&f.Disconnects==1,"complete cabin read exits ECU session and adapter");
            Check(r.Matches.Any(m=>m.Model=="BSI"&&m.Reference=="9664058780")&&r.Matches.Any(m=>m.Model=="BSM"&&m.Reference=="9664706380"),"actual extended identities independently resolve known reference prefixes");
            Check(r.Matches.Count==2,"shared gateway does not invent BSR identity");
            var shortDtcFake=new Fake{Cabin=true,ShortDtc=true};var shortDtc=BulkReader.ReadUnit(shortDtcFake,c,car,cabin,()=>false,delegate{});Check(shortDtc.Completed&&shortDtcFake.Reads>10&&shortDtc.Reads.Single(x=>x.Service==0x17).Raw=="57 02 F5 FF F1 59","unrecognized actual BSI DTC layout retained without blocking other read-only records");
            Check(f.MapReads==3,"only three registered parameter-map roots probed");
            Check(r.Reads.Count(x=>x.Service==0x17)==1,"fault memory read once on shared address");
            Check(r.Reads.Count>25&&r.Reads.Any(x=>x.Service==0x21&&x.Identifier==0xB0),"bulk includes read-only access to VIN/configuration records");
            var plan=BulkReader.Plan(c,cabin,r.Matches);Check(plan.Select(x=>x.Service+":"+x.Identifier).Distinct().Count()==plan.Count,"duplicate data queries deduplicated");
            Check(plan.All(x=>x.Service==0x21||x.Service==0x22),"no write or actuator service in bulk plan");
            f=new Fake();r=BulkReader.ReadUnit(f,c,car,engine,()=>false,delegate{});Check(r.Completed&&r.Unit.Disconnected&&f.Reads>5&&f.MapReads==0,"engine collects rejected records and never requests BSI maps");
            f=new Fake{Timeout=true};r=BulkReader.ReadUnit(f,c,car,engine,()=>false,delegate{});Check(!r.Completed&&f.Reads==1&&f.Disconnects==1,"timeout aborts unit without retries and closes adapter");
            f=new Fake{BadCleanup=true};r=BulkReader.ReadUnit(f,c,car,engine,()=>false,delegate{});Check(!r.Unit.Disconnected,"failed cleanup remains visible");
            f=new Fake();r=BulkReader.ReadUnit(f,c,car,engine,()=>true,delegate{});Check(!r.Completed&&f.Reads==0&&f.Disconnects==1,"cancellation skips all record reads and disconnects");
            Reject(()=>ParameterMap.Command(engine,0xC0,1),"map read cannot access engine memory");
            Reject(()=>ParameterMap.Command(cabin,0,1),"arbitrary memory root rejected");
            Reject(()=>ParameterMap.Command(cabin,0xC0,33),"parameter-map pages bounded");
            Reject(()=>BulkReader.Classify(0x21,0x80,"x",new[]{0x61,0x8B,1}),"mismatched reply rejected");
            Check(BulkReader.Classify(0x21,0x80,"x",new[]{0x7F,0x21,0x22}).State.StartsWith("Odmítnuto"),"negative response not a value");
            Check(RecordDecoder.Value(new FieldDefinition{Type="str",Bits="1.7-17.0"},Encoding.ASCII.GetBytes("123456789ABCDEFGH"))=="123456789ABCDEFGH","17-byte VIN supported without integer truncation");
            Check(c.Definition("AEE2004.full/BSI/BSI").Records.Any(rec=>rec.Layouts.Any(l=>l.Mapped!=null&&l.Mapped.Length>0)),"real parameter map definitions imported");
            var mapped=new RecordLayout{Patterns=new string[0],Mapped=new[]{new MappedField{Index=8,Length=2,Field=new FieldDefinition{Name="state",Label="state",Type="uint",Factor=1}},new MappedField{Index=1,Length=16,Field=new FieldDefinition{Name="value",Label="value",Type="uint",Factor=0.5}}}};
            var bound=MappedDecoder.Bind(mapped,new[]{8,1});Check(bound.Length==3&&RecordDecoder.Value(bound.Fields[0],new byte[]{3,1,0})=="3"&&RecordDecoder.Value(bound.Fields[1],new byte[]{3,1,0})=="128","ECU map controls order, LSB bit packing, byte alignment and scaling");
            Reject(()=>MappedDecoder.Bind(mapped,new[]{8,99}),"unknown index cannot shift subsequent measurements");
            Reject(()=>MappedDecoder.Bind(mapped,new[]{8,8}),"duplicate indexes rejected");
            var mapPage=new DataRead{Service=0x35,Identifier=0xC0,Page=1,State="Přečteno",Raw="75 C0 FF 00 00 01 04 00 08 01 FF 00 00"};
            var table=MappedDecoder.Table(new[]{mapPage},0xC0);Check(table[0xC0].SequenceEqual(new[]{8,1}),"final map page contributes payload and excludes envelope/footer");
            Reject(()=>MappedDecoder.Table(new[]{mapPage,mapPage},0xC0),"duplicate page invalidates parameter map");
            Reject(()=>MappedDecoder.Table(new[]{new DataRead{Service=0x35,Identifier=0xC0,Page=1,State="Přečteno",Raw=mapPage.Raw.Replace("C0 FF","C0 80")}},0xC0),"missing end marker invalidates map");
            var fixture=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<UnitSnapshot>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tests","fixtures","bsi-20261122.json")));
            MappedDecoder.Reprocess(c,fixture);
            Check(fixture.Reads.Single(x=>x.Service==0x21&&x.Identifier==0xC0).Values.Contains("Battery voltage: 14.5 V"),"recorded BSI multi-page map and voltage conversion");
            Check(fixture.Reads.Single(x=>x.Service==0x21&&x.Identifier==0xCD).Values.Contains("Windscreen washing control: Inactive"),"recorded wiper/washer map across final partial page");
            Check(fixture.Reads.Single(x=>x.Service==0x21&&x.Identifier==0xCA).Values.Contains("Mezní surová hodnota 0xFFFF")&&!fixture.Reads.Single(x=>x.Service==0x21&&x.Identifier==0xCA).Values.Contains("6553.5"),"unqualified boundary value not presented as physical fuel consumption");
            var presentation=UnitPresentation.Rows(new[]{fixture});
            Check(presentation.Any(x=>x.Group=="Stěrače a ostřikovače"&&x.Parameter=="Povel předních ostřikovačů"&&x.Value=="Neaktivní"&&x.Owner=="BSI"),"native unit table preserves decoded value, grouping and owner");
            Check(UnitPresentation.Normalize("STĚRAČE")=="sterace","native search supports Czech without accents");
            var refused=new UnitSnapshot{Unit=fixture.Unit,Matches=fixture.Matches,Reads=new List<DataRead>{new DataRead{Service=0x21,Identifier=0xC4,Name="Vstřikování",State="Odmítnuto — NRC 12",Raw="7F 21 12"}}};
            var refusedRows=UnitPresentation.Rows(new[]{refused});Check(refusedRows.Count==1&&refusedRows[0].Value=="—"&&refusedRows[0].State.StartsWith("Odmítnuto"),"rejected request cannot appear as a numeric measurement in UI");
            Console.WriteLine("PASS: "+count+" bulk read checks; simulated transport only.");return 0;
        }
    }
}


