using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PsaReader
{
    public sealed class IdentityMatch { public string Key {get;set;} public string Model {get;set;} public string Variant {get;set;} public string Reference {get;set;} }
    internal static class IdentityResolver
    {
        internal static List<IdentityMatch> Resolve(Catalogue c,string car,DiscoveredEcu unit)
        {
            var result=new List<IdentityMatch>();
            if(unit.Identifications!=null){
                foreach(var pair in unit.Identifications){
                    if(pair.Key=="Bsi"||pair.Key=="Bsm"){
                        string key=pair.Key=="Bsi"?"AEE2004.full/BSI/BSI":"AEE2004.full/BSM/BSM";
                        if(!(unit.Candidates??new string[0]).Contains(key))continue;
                        try{
                            int[] bytes=pair.Value.Split(' ').Select(x=>Convert.ToInt32(x,16)).ToArray();
                            int lid=pair.Key=="Bsi"?0x80:0x8B;
                            // Only observed 22-byte extension or source's exact base format.
                            if(bytes.Length!=(pair.Key=="Bsi"?19:14)&&bytes.Length!=24)continue;
                            if(bytes[0]!=0x61||bytes[1]!=lid)continue;
                            string reference=RecordDecoder.Value(new FieldDefinition{Bits=pair.Key=="Bsi"?"1.7-5.0":"8.7-12.0",Type="bcd"},bytes.Skip(2).Select(x=>checked((byte)x)).ToArray());
                            var definition=c.Definition(key);
                            foreach(var variant in definition.Variants.Where(v=>v.Cars.Contains(car)&&v.Parts.Contains(reference)))result.Add(new IdentityMatch{Key=key,Model=definition.Model,Variant=variant.Name,Reference=reference});
                        }catch(IOException){}catch(FormatException){}catch(OverflowException){}
                    }else{
                        result.AddRange(Resolve(c,car,new DiscoveredEcu{Identity=pair.Value,Candidates=unit.Candidates}));
                    }
                }
                return result.GroupBy(x=>x.Key+"/"+x.Variant).Select(g=>g.First()).ToList();
            }
            if(String.IsNullOrEmpty(unit.Identity))return result;
            byte[] b;try{b=unit.Identity.Split(' ').Select(s=>Convert.ToByte(s,16)).ToArray();}catch{return result;}
            if(b.Length!=26||b[0]!=0x61||b[1]!=0xFE)return result;
            foreach(string key in unit.Candidates??new string[0])
            {
                var d=c.Definition(key);var record=d.Records.FirstOrDefault(r=>r.Service==0x21&&r.Identifier==0xFE);if(record==null)continue;
                foreach(var variant in d.Variants.Where(v=>v.Cars.Contains(car)))
                {
                    var layouts=record.Layouts.Where(l=>Matches(l,variant.Name)&&l.Length==24).ToArray();
                    if(layouts.Length!=1)continue;
                    var f=layouts[0].Fields.FirstOrDefault(x=>x.Name=="SOFTWARE_REFERENCE"&&x.Type=="bcd");if(f==null)continue;
                    string reference;try{reference=RecordDecoder.Value(f,b.Skip(2).ToArray());}catch(IOException){continue;}
                    if(variant.Parts.Contains(reference))result.Add(new IdentityMatch{Key=key,Model=d.Model,Variant=variant.Name,Reference=reference});
                }
            }
            return result;
        }
        internal static bool Matches(RecordLayout layout,string variant) { return layout.Patterns.Length==0||layout.Patterns.Any(p=>Regex.IsMatch(variant,"^"+Regex.Escape(p).Replace("\\*",".*")+"$")); }
        internal static RecordLayout Layout(ReadRecord record,string variant)
        {
            var matches=record.Layouts.Where(l=>Matches(l,variant)).ToArray();if(matches.Length!=1)throw new IOException("Formát tohoto záznamu není jednoznačný pro zjištěnou verzi ECU.");return matches[0];
        }
    }
    internal static class RecordDecoder
    {
        internal static List<ParameterValue> Fields(ReadRecord record,RecordLayout layout,int[] response,string owner)
        {
            Decode(record,layout,response); // Same strict header/length validation as the text report.
            var bytes=response.Skip(record.Service==0x21?2:3).Select(x=>checked((byte)x)).ToArray();var values=new List<ParameterValue>();
            foreach(var field in layout.Fields){var item=new ParameterValue{Name=field.Name,Label=field.Label,Owner=owner,State="Přečteno"};try{item.Value=Value(field,bytes);if(item.Value.StartsWith("Neznámý")||item.Value.StartsWith("Mezní"))item.State="Význam neověřen";}catch(IOException e){item.Value=e.Message;item.State="Nerozpoznáno";}values.Add(item);}return values;
        }
        internal static string Value(FieldDefinition f,byte[] data)
        {
            var m=Regex.Match(f.Bits,@"^(\d+)\.([0-7])(?:-(\d+)\.([0-7]))?$");if(!m.Success)throw new IOException("Nepodporovaný formát bitového pole.");
            int start=(Int32.Parse(m.Groups[1].Value)-1)*8+7-Int32.Parse(m.Groups[2].Value);
            int end=m.Groups[3].Success?(Int32.Parse(m.Groups[3].Value)-1)*8+7-Int32.Parse(m.Groups[4].Value):start;
            int width=end-start+1;if(start<0||end>=data.Length*8||width<1)throw new IOException("Pole přesahuje ověřenou délku dat nebo podporovanou šířku.");
            if((f.Type=="str"||f.Type=="bytes")&&start%8==0&&width%8==0){var part=data.Skip(start/8).Take(width/8).ToArray();if(f.Type=="bytes")return BitConverter.ToString(part).Replace('-',' ');if(part.Any(x=>x<32||x>126))throw new IOException("Text obsahuje neznámé znaky.");return Encoding.ASCII.GetString(part);}
            if(width>63)throw new IOException("Číselné pole je příliš široké.");
            ulong raw=0;for(int i=start;i<=end;i++)raw=(raw<<1)|(uint)((data[i/8]>>(7-i%8))&1);
            if(f.FlagBoundary&&f.Type=="uint"&&width>=8&&raw>=(1UL<<width)-2)return "Mezní surová hodnota 0x"+raw.ToString("X")+" — může označovat nedostupný údaj; nepřevádím na měření";
            if(f.Type=="bcd")
            {
                if(width%4!=0)throw new IOException("Neplatná šířka BCD.");var b=new StringBuilder();for(int i=width-4;i>=0;i-=4){int digit=(int)((raw>>i)&15);if(digit>9)throw new IOException("Neplatné BCD číslo.");b.Append(digit);}return b.ToString();
            }
            if(f.Type=="enum"||f.Type=="bool") {string label;if(f.Values!=null&&f.Values.TryGetValue(raw.ToString(CultureInfo.InvariantCulture),out label))return label;if(f.Type=="enum")return "Neznámý stav 0x"+raw.ToString("X");}
            if(f.Type!="uint"&&f.Type!="int"&&f.Type!="bool")throw new IOException("Nepodporovaný datový typ "+f.Type+"; bez odhadování hodnoty.");
            double value=f.Type=="int"&&(raw&(1UL<<(width-1)))!=0?(long)raw-(double)(1UL<<width):raw;
            value=value*f.Factor+f.Offset;
            if(Double.IsNaN(value)||Double.IsInfinity(value))throw new IOException("Neplatný převod hodnoty.");
            return value.ToString("0.####",CultureInfo.InvariantCulture)+(String.IsNullOrEmpty(f.Unit)?"":" "+f.Unit);
        }
        internal static string Decode(ReadRecord record,RecordLayout layout,int[] response)
        {
            if(response.Length==3&&response[0]==0x7F&&response[1]==record.Service)throw new IOException("Jednotka odmítla čtení: "+String.Join(" ",response.Select(x=>x.ToString("X2")))+". Žádné hodnoty nebyly dekódovány.");
            int header=record.Service==0x21?2:3;
            if(response.Length<header||response[0]!=record.Service+0x40 || (record.Service==0x21?response[1]:(response[1]<<8)|response[2])!=record.Identifier)throw new IOException("Odpověď neodpovídá zvolenému datovému záznamu.");
            if(!layout.Length.HasValue||response.Length-header!=layout.Length.Value)throw new IOException("Délka odpovědi nesouhlasí s definicí; hodnoty nebudou dekódovány.");
            byte[] bytes=response.Skip(header).Select(x=>checked((byte)x)).ToArray();var b=new StringBuilder(record.Name+"\r\n");
            foreach(var field in layout.Fields){try{b.AppendLine(field.Label+": "+Value(field,bytes));}catch(IOException e){b.AppendLine(field.Label+": nerozpoznáno ("+e.Message+")");}}
            b.AppendLine("\r\nSurová odpověď: "+String.Join(" ",response.Select(x=>x.ToString("X2"))));return b.ToString();
        }
    }
    internal interface IParameterVci : IDiscoveryVci { string ReadRecord(ReadRecord record); }
    internal static class ModuleReader
    {
        internal static ReadResult Run(Catalogue catalogue,VehicleDefinition vehicle,DiscoveredEcu unit,IdentityMatch match,ReadRecord record,Action<string> progress)
        {
            var route=CanRoute.Plan(catalogue,vehicle).Single(r=>r.Id==unit.Address);
            if(!route.Candidates.Contains(match.Key))throw new IOException("Definice nepatří ke zvolené jednotce.");
            var definition=catalogue.Definition(match.Key);
            if(record==null && !(definition.ReadDtcServices??new int[0]).Any(service=>service==0x17||service==0x19))throw new IOException("Pro tento model nemáme definované čtení KWP závad.");
            if(record!=null)record=CanonicalRecord(definition,record,match.Variant);
            var result=new ReadResult();var report=new StringBuilder("ČTENÍ JEDNOTKY — "+match.Model+" / "+match.Variant+"\r\n"+DateTimeOffset.Now.ToString("o")+"\r\n");
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");Directory.CreateDirectory(folder);
            result.LogPath=Path.Combine(folder,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-module-"+Guid.NewGuid().ToString("N").Substring(0,6)+".txt");
            using(var log=new StreamWriter(new FileStream(result.LogPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read),Encoding.UTF8))
            {
                log.AutoFlush=true;
                try
                {
                    using(var wire=new SerialVci(s=>log.WriteLine(DateTimeOffset.Now.ToString("o")+" "+s)))
                    {
                        var session=Session(wire,catalogue,vehicle,route,match,record,progress);result.Success=session.Success;report.AppendLine(session.Report);
                    }
                }
                catch(Exception e){result.Success=false;report.AppendLine(e.Message);}
                report.AppendLine("\r\nJednorázový odečet. Převody a popisy podle komunitní databáze; neznamenají určení vadného dílu.");result.Report=report.ToString();log.WriteLine(result.Report);
            }
            File.WriteAllText(result.LogPath+".report.txt",result.Report,Encoding.UTF8);return result;
        }
        internal static ReadResult Session(IParameterVci wire,Catalogue catalogue,VehicleDefinition vehicle,CanRoute route,IdentityMatch match,ReadRecord record,Action<string> progress)
        {
            var result=new ReadResult();var report=new StringBuilder();bool started=false,cabinAttempted=false;
            try
            {
                CanRoute.Validate(route);
                if(!route.Candidates.Contains(match.Key))throw new IOException("Definice nepatří ke zvolené adrese.");
                var definition=catalogue.Definition(match.Key);
                if(record!=null)record=CanonicalRecord(definition,record,match.Variant);
                else if(!(definition.ReadDtcServices??new int[0]).Any(service=>service==0x17||service==0x19))throw new IOException("Čtení závad není pro tuto definici povoleno.");
                progress("Ověřuji krabičku a znovu identifikaci "+match.Model+"…");Discovery.CheckAdapter(wire);started=true;
                VciProtocol.Expect(wire.Exchange(VciCommand.Pins),"*114");VciProtocol.Expect(wire.Exchange(VciCommand.Relays),"*106");
                VciProtocol.Expect(wire.Configure(route),"*256");VciProtocol.Expect(wire.Exchange(VciCommand.Timing),"*0 10");VciProtocol.Expect(wire.Exchange(VciCommand.ReceiveMode),"*256");
                if(route.CabinDiagnosticSession){cabinAttempted=true;var start=Discovery.EcuBytes(wire.CabinSession(route,true));if(start.Length<2||start[0]!=0x50||start[1]!=0xC0)throw new IOException("Jednotka nepotvrdila relaci karoserie.");}
                else if(route.HasKwp&&route.Request<0x740){var start=Discovery.EcuBytes(wire.Exchange(VciCommand.StartAbs));if(start.Length==0||start[0]!=0xC1)throw new IOException("Jednotka nepotvrdila zahájení komunikace.");}
                var matches=new List<IdentityMatch>();
                if(definition.IdentityService==0x21||definition.IdentityService==0x22){
                    var identityRequest=new ReadRecord{Service=definition.IdentityService,Identifier=definition.IdentityIdentifier};
                    var identity=Discovery.EcuBytes(wire.ReadRecord(identityRequest));
                    var current=new UnitSnapshot{Unit=new DiscoveredEcu{Candidates=route.Candidates,Nodes=route.Nodes},Matches=new List<IdentityMatch>(),Reads=new List<DataRead>{BulkReader.Classify(identityRequest.Service,identityRequest.Identifier,"Identifikace",identity)}};
                    BulkReader.ResolveReferences(catalogue,vehicle,current);matches=current.Matches.Where(m=>m.Key==match.Key).ToList();
                }else{
                    var identity=Discovery.EcuBytes(wire.Identify(IdentityQuery.KwpSupplier));
                    matches=IdentityResolver.Resolve(catalogue,vehicle.Code,new DiscoveredEcu{Identity=String.Join(" ",identity.Select(x=>x.ToString("X2"))),Candidates=route.Candidates});
                }
                if(matches.Count!=1||matches[0].Key!=match.Key||matches[0].Variant!=match.Variant)throw new IOException("Identifikace se změnila nebo není jednoznačná; čtení zastaveno.");
                progress(record==null?"Čtu závady…":"Čtu "+record.Name+"…");
                if(record!=null){var layout=IdentityResolver.Layout(record,match.Variant);report.AppendLine(RecordDecoder.Decode(record,layout,Discovery.EcuBytes(wire.ReadRecord(record))));}
                else report.AppendLine(DecodeFaults(definition,Discovery.EcuBytes(wire.Exchange((definition.ReadDtcServices??new int[0]).Contains(0x19)?VciCommand.ReadUdsDtc:VciCommand.ReadAbsDtc))));
                result.Success=true;
            }
            catch(Exception e){report.AppendLine("ČTENÍ NEDOKONČENO: "+e.Message);}
            finally
            {
                if(started)try{Discovery.Close(wire,route,cabinAttempted);report.AppendLine("\r\nUkončení spojení potvrzeno krabičkou. Toto potvrzení neověřuje funkčnost ABS/ESP.");}
                catch(Exception e){result.Success=false;report.AppendLine("UKONČENÍ NEPOTVRZENO: "+e.Message+". Před dalším použitím odpojte krabičku od auta i USB.");}
            }
            result.Report=report.ToString();return result;
        }
        internal static ReadRecord CanonicalRecord(EcuDefinition definition,ReadRecord requested,string variant)
        {
            var records=definition.Records.Where(r=>r.Service==requested.Service&&r.Identifier==requested.Identifier).ToArray();
            if(records.Length!=1)throw new IOException("Neznámý nebo nejednoznačný záznam.");
            SerialVci.EncodeReadRecord(records[0]);IdentityResolver.Layout(records[0],variant);return records[0];
        }
        internal static string DecodeFaults(EcuDefinition definition,int[] b)
        {
            if(b.Length>=1&&b[0]==0x59){
                if(b.Length<3||b[1]!=2||(b.Length-3)%4!=0||b.Any(x=>x<0||x>255))throw new IOException("Neúplný nebo neznámý formát UDS závad; nejde o prázdnou paměť.");
                var uds=new StringBuilder("Počet záznamů závad: "+((b.Length-3)/4)+"\r\nPodporovaná stavová maska: "+b[2].ToString("X2"));
                for(int i=3;i<b.Length;i+=4){string raw=b[i].ToString("X2")+b[i+1].ToString("X2");var known=(definition.Dtcs??new FaultDefinition[0]).Where(d=>d.Raw==raw).ToArray();uds.Append("\r\nDTC "+raw+b[i+2].ToString("X2")+" · stav "+b[i+3].ToString("X2")+(known.Length==1?" · "+known[0].Code+" — "+known[0].Label:" · bez přiřazeného popisu"));}
                return uds.ToString();
            }
            if(b.Length==3&&b[0]==0x7F&&b[1]==0x17)throw new IOException("Jednotka odmítla čtení závad: "+String.Join(" ",b.Select(x=>x.ToString("X2"))));
            if(b.Length<2||b[0]!=0x57||b.Length!=2+3*b[1])throw new IOException("Neznámý formát závad; není vydáván za prázdnou paměť.");
            var report=new StringBuilder("Počet záznamů závad: "+b[1]+"\r\n");
            for(int i=2;i<b.Length;i+=3){string raw=b[i].ToString("X2")+b[i+1].ToString("X2");var d=(definition.Dtcs??new FaultDefinition[0]).Where(x=>String.Equals(x.Raw,raw,StringComparison.OrdinalIgnoreCase)).ToArray();report.AppendLine("\r\n"+(d.Length==1?d[0].Code+" — "+d[0].Label:"Kód "+raw+" — popis neověřen")+"\r\nSurový kód "+raw+", stav 0x"+b[i+2].ToString("X2")+" (bez odhadu významu stavových bitů)");}
            report.AppendLine("\r\nSurová odpověď: "+String.Join(" ",b.Select(x=>x.ToString("X2"))));return report.ToString();
        }
    }
}

