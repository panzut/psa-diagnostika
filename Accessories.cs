using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PsaReader
{
    internal sealed class AccessoryTest
    {
        public string Name {get;set;} public string Group {get;set;}
        public string Availability {get {return "Nedostupné — neověřený příkaz";}}
        public string Source {get;set;}
        public string Missing {get {return "Chybí ověřené hodnoty CONTROL_PARAMETER a NUMBER_RELATIVE, jednotka délky testu a příkaz pro ukončení / návrat řízení autu. Pořadí položek v databázi není číslem výstupu.";}}
    }
    internal static class Accessories
    {
        internal const string Revision="67f7581e7622410efdbb29c7eda3b86982b9b450";
        internal const string BsiKey="AEE2004.full/BSI/BSI";
        internal const string DrlField="LIGHTING_DAY_RUNNING_LAMPS_DEFAULT";
        internal static List<AccessoryTest> Tests(string node)
        {
            var result=new List<AccessoryTest>();
            if(node!="BSI")return result;
            foreach(var item in new[]{
                new[]{"Potkávací světla","Světla","C5"},new[]{"Obrysová světla vpředu a vzadu","Světla","C5"},
                new[]{"Přední stropní svítilna","Světla","C5"},new[]{"Přední stěrače — pomalu","Stěrače","CD"},
                new[]{"Přední stěrače — rychle","Stěrače","CD"},new[]{"Přední ostřikovače","Ostřikovače","CD"}})
                result.Add(new AccessoryTest{Name=item[0],Group=item[1],Source="diag/"+BsiKey+"/ioctls/"+item[2]+"_LID.yml"});
            return result;
        }
        // This returns a local proposal only. It has no VCI dependency and creates no write packet.
        internal static DaylightProposal Preview(Catalogue catalogue,VehicleDefinition vehicle,string node,IEnumerable<UnitSnapshot> snapshots,bool enabled)
        {
            if(node!="BSI"||vehicle==null||vehicle.Id!="VCAN:all"||catalogue.Revision!=Revision)throw new IOException("Pro tuto jednotku nebo verzi databáze není kontrola změny připravená.");
            var candidates=snapshots.Where(s=>s.Unit!=null&&s.Unit.Address=="752:652"&&s.Unit.Wiring=="H03L08"&&s.Unit.Nodes!=null&&s.Unit.Nodes.Contains("BSI")).ToArray();
            if(candidates.Length!=1)throw new IOException("Chybí jednoznačný uložený odečet BSI.");
            var snapshot=candidates[0];
            if(!snapshot.Completed||!snapshot.Unit.Responded||!snapshot.Unit.Disconnected||!String.IsNullOrEmpty(snapshot.Error))throw new IOException("Odečet BSI není úplný nebo ukončení spojení nebylo potvrzené.");
            var matches=snapshot.Matches.Where(m=>m.Key==BsiKey).ToArray();
            if(matches.Length!=1||matches[0].Reference!="9664058780"||matches[0].Variant!="VCAN_V1_1")throw new IOException("Náhled je připraven pouze pro zjištěnou BSI 9664058780 / VCAN_V1_1.");
            var readings=snapshot.Reads.Where(r=>r.Service==0x21&&r.Identifier==0xB9).ToArray();
            if(readings.Length!=1||readings[0].State!="Přečteno")throw new IOException("Chybí jednoznačný kladný odečet nastavení denního svícení.");
            var reading=readings[0];var table=MappedDecoder.Table(snapshot.Reads,0xB0);
            if(!table.ContainsKey(0xB9))throw new IOException("Mapa BSI neobsahuje nastavení B9.");
            var records=catalogue.Definition(BsiKey).Records.Where(r=>r.Service==0x21&&r.Identifier==0xB9).ToArray();
            if(records.Length!=1)throw new IOException("Nejednoznačná definice nastavení.");
            var layout=MappedDecoder.Bind(IdentityResolver.Layout(records[0],matches[0].Variant),table[0xB9]);
            var fields=layout.Fields.Where(f=>f.Name==DrlField).ToArray();
            if(fields.Length!=1||fields[0].Type!="bool")throw new IOException("Mapa neobsahuje jednoznačné nastavení výchozího denního svícení.");
            int[] original;
            try{original=reading.Raw.Split(' ').Select(s=>Convert.ToInt32(s,16)).ToArray();}catch(Exception e){throw new IOException("Neplatný uložený odečet.",e);}
            if(original.Any(b=>b<0||b>255))throw new IOException("Neplatné bajty odečtu.");
            RecordDecoder.Decode(records[0],layout,original);
            var bits=Regex.Match(fields[0].Bits,@"^(\d+)\.([0-7])-\1\.\2$");
            if(!bits.Success)throw new IOException("Parametr nemá ověřený jednobitový formát.");
            int offset=Int32.Parse(bits.Groups[1].Value)+1,mask=1<<Int32.Parse(bits.Groups[2].Value);
            if(offset<2||offset>=original.Length)throw new IOException("Parametr leží mimo odečet.");
            var typeFields=layout.Fields.Where(f=>f.Name=="TYPE_DAYTIME_RUNNING_LAMPS").ToArray();
            if(typeFields.Length!=1||typeFields[0].Type!="enum"||!typeFields[0].Values.ContainsKey("1")||typeFields[0].Values["1"]!="No daytime running lamps")throw new IOException("Chybí ověřená hodnota typu bez denního svícení.");
            var typeBits=Regex.Match(typeFields[0].Bits,@"^(\d+)\.7-\1\.0$");if(!typeBits.Success)throw new IOException("Neověřený formát typu denního svícení.");
            int typeOffset=Int32.Parse(typeBits.Groups[1].Value)+1;
            if(typeOffset<2||typeOffset>=original.Length||typeOffset==offset||(original[typeOffset]!=1&&original[typeOffset]!=2))throw new IOException("Neověřený typ denního svícení pro toto auto.");
            int[] proposed=(int[])original.Clone();bool current=original[typeOffset]!=1;
            proposed[typeOffset]=enabled?2:1;
            proposed[offset]=enabled?proposed[offset]|mask:proposed[offset]&~mask;
            // Decode the candidate again to catch layout/size problems. Never serialize a 3B command.
            RecordDecoder.Decode(records[0],layout,proposed);
            return new DaylightProposal{Current=current,Requested=enabled,ReadTime=reading.Time,Reference=matches[0].Reference,
                Original=Hex(original),Proposed=Hex(proposed),OriginalHash=Hash(original),ProposedHash=Hash(proposed),DataByte=offset-1,Mask=mask,TypeDataByte=typeOffset-1};
        }
        static string Hex(int[] bytes){return String.Join(" ",bytes.Select(b=>b.ToString("X2")));}
        static string Hash(int[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes.Select(b=>(byte)b).ToArray())).Replace("-","");}
    }
    internal sealed class DaylightProposal
    {
        public bool Current {get;set;} public bool Requested {get;set;}
        public string ReadTime {get;set;} public string Reference {get;set;}
        public string Original {get;set;} public string Proposed {get;set;}
        public string OriginalHash {get;set;} public string ProposedHash {get;set;}
        public int DataByte {get;set;} public int Mask {get;set;}
        public int TypeDataByte {get;set;}
        public string Report()
        {
            return "NÁHLED ZMĚNY — NIC NEBYLO ODESLÁNO DO AUTA\r\n\r\nDenní svícení: "+(Current?"Přítomné":"Vypnuté")+" → "+(Requested?"Původní typ + výchozí zapnutí":"Bez denního svícení + výchozí zapnutí vypnuto")+
                "\r\n"+(Original==Proposed?"Požadované hodnoty už odpovídají uloženému odečtu.":"Náhled upravuje pouze typ a výchozí zapnutí denního svícení; ostatní nastavení zachovává.")+
                "\r\nZdroj: uložený odečet "+ReadTime+"\r\nBSI: "+Reference+
                "\r\n\r\nAutomatický režim podle světelného snímače se tímto návrhem nemění. Náhled nepotvrzuje výsledné chování světel na autě."+
                "\r\n\r\nToto je pouze náhled. Tlačítko Zapsat do auta provede nový odečet, zálohu a kontrolu po novém připojení.";
        }
    }
}
