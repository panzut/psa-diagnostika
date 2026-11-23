using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace PsaReader
{
    public sealed class AddressDefinition { public string Transport { get; set; } public string Protocol { get; set; } public string Bus { get; set; } public int Request { get; set; } public int Response { get; set; } }
    public sealed class EcuReference { public string Name { get; set; } public string Key { get; set; } }
    public sealed class VehicleNode { public string Code { get; set; } public string Name { get; set; } public bool Optional { get; set; } public EcuReference[] Models { get; set; } }
    public sealed class VehicleDefinition { public string Id { get; set; } public string Code { get; set; } public string Title { get; set; } public string Years { get; set; } public string Architecture { get; set; } public string Source { get; set; } public VehicleNode[] Nodes { get; set; } public override string ToString() { return Title; } }
    public sealed class EcuSummary { public string Key { get; set; } public string Model { get; set; } public string Name { get; set; } public string[] Protocols { get; set; } public AddressDefinition[] Addresses { get; set; } public int RecordCount { get; set; } public string Source { get; set; } }
    public sealed class FieldDefinition { public string Name { get; set; } public string Label { get; set; } public string Bits { get; set; } public string Type { get; set; } public double Factor { get; set; } public double Offset { get; set; } public string Unit { get; set; } public Dictionary<string,string> Values { get; set; } public bool FlagBoundary {get;set;} }
    public sealed class MappedField { public int Index {get;set;} public int Length {get;set;} public FieldDefinition Field {get;set;} }
    public sealed class RecordLayout { public string[] Patterns { get; set; } public int? Length { get; set; } public FieldDefinition[] Fields { get; set; } public MappedField[] Mapped {get;set;} }
    public sealed class ReadRecord { public int Service { get; set; } public int Identifier { get; set; } public string Name { get; set; } public string Kind { get; set; } public RecordLayout[] Layouts { get; set; } public string Source { get; set; } }
    public sealed class EcuVariant { public string Name { get; set; } public string[] Parts { get; set; } public string[] Cars { get; set; } }
    public sealed class FaultDefinition { public string Code { get; set; } public string Raw { get; set; } public string Label { get; set; } }
    public sealed class EcuDefinition { public string Key { get; set; } public string Model { get; set; } public EcuVariant[] Variants { get; set; } public ReadRecord[] Records { get; set; } public int[] ReadDtcServices {get;set;} public FaultDefinition[] Dtcs {get;set;} public bool ClearDtcSupported {get;set;} public bool CabinDiagnosticSession {get;set;} public int IdentityService {get;set;} public int IdentityIdentifier {get;set;} public string ReferenceBits {get;set;} }
    public sealed class Catalogue
    {
        public int Schema { get; set; } public string Revision { get; set; } public string Repository { get; set; } public string License { get; set; }
        public VehicleDefinition[] Vehicles { get; set; } public EcuSummary[] Ecus { get; set; }
        public static Catalogue Load()
        {
            var result=Read<Catalogue>(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","catalog.json"));
            if(result.Schema!=1 || result.Vehicles==null || result.Ecus==null) throw new IOException("Neznámý formát katalogu.");
            if(result.Ecus.Select(e=>e.Key).Distinct().Count()!=result.Ecus.Length || result.Vehicles.Select(v=>v.Id).Distinct().Count()!=result.Vehicles.Length) throw new IOException("Duplicitní identifikátory katalogu.");
            return result;
        }
        static T Read<T>(string file)
        {
            if(new FileInfo(file).Length>32*1024*1024) throw new IOException("Definice je příliš velká.");
            return new JavaScriptSerializer { MaxJsonLength=32*1024*1024, RecursionLimit=100 }.Deserialize<T>(File.ReadAllText(file,Encoding.UTF8));
        }
        public EcuSummary Find(string key) { return Ecus.FirstOrDefault(e=>e.Key==key); }
        public EcuDefinition Definition(string key)
        {
            if(Find(key)==null || !System.Text.RegularExpressions.Regex.IsMatch(key,@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new IOException("Neznámá definice.");
            var d=Read<EcuDefinition>(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","definitions",key.Replace('/',Path.DirectorySeparatorChar)+".json"));
            if(d.Key!=key) throw new IOException("Identifikátor definice nesouhlasí.");
            return d;
        }
        public string Describe(VehicleDefinition vehicle, VehicleNode node)
        {
            var b=new StringBuilder(); b.AppendLine(node.Name+" ("+node.Code+")");
            b.AppendLine(node.Optional?"Volitelná výbava. Přítomnost zatím není potvrzená.":"Jednotka uvedená v profilu. Přítomnost zatím není potvrzená.");
            b.AppendLine("\r\nKandidátní definice — nejsou automaticky identifikací osazené ECU:");
            foreach(var model in node.Models??new EcuReference[0])
            {
                var e=Find(model.Key); b.AppendLine("\r\n"+model.Name);
                if(e==null){b.AppendLine("Podrobná definice v databázi chybí.");continue;}
                b.AppendLine("Protokoly: "+String.Join(", ",e.Protocols));
                foreach(var address in e.Addresses) b.AppendLine("  "+address.Transport+" / "+address.Protocol+" / "+address.Request.ToString("X")+" → "+address.Response.ToString("X"));
                b.AppendLine("Záznamy ke čtení v databázi: "+e.RecordCount);
                var d=Definition(e.Key);
                foreach(var record in d.Records) b.AppendLine("  "+record.Service.ToString("X2")+" "+record.Identifier.ToString(record.Service==0x21?"X2":"X4")+" — "+record.Name+" ("+record.Layouts.Length+" variant formátu)");
                b.AppendLine("Zdroj: "+Repository+"/blob/"+Revision+"/"+e.Source);
            }
            b.AppendLine("\r\nProvozní možnosti určuje ovladač a ověřená shoda ECU. Počet definic neznamená počet podporovaných funkcí na tomto autě.");
            return b.ToString();
        }
    }
}



