using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PsaReader
{
    internal static class MappedDecoder
    {
        internal static Dictionary<int,int[]> Table(IEnumerable<DataRead> reads,int root)
        {
            var pages=reads.Where(r=>r.Service==0x35&&r.Identifier==root).OrderBy(r=>r.Page).ToArray();
            var stream=new List<int>();bool complete=false;int expected=1;
            foreach(var page in pages){
                if(page.State!="Přečteno"||page.Page!=expected++||complete)throw new IOException("Tabulka má chybějící nebo duplicitní stránku.");
                int[] b=page.Raw.Split(' ').Select(x=>Convert.ToInt32(x,16)).ToArray();
                // Observed BSI page envelope: root, status, echoed page, payload length,
                // payload and two trailing bytes kept in Raw (not interpreted as parameters).
                if(b.Length<9||b[0]!=0x75||b[1]!=root||(b[2]!=0x80&&b[2]!=0xFF)||b[3]!=0||b[4]!=0||b[5]!=page.Page||b[6]>17||b.Length!=9+b[6])throw new IOException("Neověřený formát tabulky parametrů.");
                stream.AddRange(b.Skip(7).Take(b[6]));complete=b[2]==0xFF;
            }
            if(!complete)throw new IOException("Chybí potvrzený konec tabulky parametrů.");
            var result=new Dictionary<int,int[]>();var record=new List<int>();
            foreach(int value in stream.Concat(new[]{255})){
                if(value!=255){record.Add(value);continue;}
                if(record.Count==0)continue;
                if(record[0]>15||record.Count<2)throw new IOException("Neplatný záznam v tabulce parametrů.");
                int id=root+record[0];if(result.ContainsKey(id))throw new IOException("Duplicitní datový záznam v tabulce.");
                result.Add(id,record.Skip(1).ToArray());record.Clear();
            }
            return result;
        }
        internal static void Reprocess(Catalogue c,UnitSnapshot unit)
        {
            var matches=unit.Matches.Where(m=>m.Key=="AEE2004.full/BSI/BSI").ToArray();if(matches.Length!=1)return;
            var match=matches[0];var definition=c.Definition(match.Key);
            foreach(int root in new[]{0xB0,0xC0,0xD0}){
                Dictionary<int,int[]> table;
                try{table=Table(unit.Reads,root);}catch(IOException){continue;}
                foreach(var read in unit.Reads.Where(r=>r.Service==0x21&&r.State=="Přečteno"&&table.ContainsKey(r.Identifier))){
                    try{
                        var records=definition.Records.Where(r=>r.Service==read.Service&&r.Identifier==read.Identifier).ToArray();if(records.Length!=1)continue;
                        var layout=IdentityResolver.Layout(records[0],match.Variant);if(layout.Mapped==null||layout.Mapped.Length==0)continue;
                        var bound=Bind(layout,table[read.Identifier]);var bytes=read.Raw.Split(' ').Select(x=>Convert.ToInt32(x,16)).ToArray();
                        read.Values="BSI — rozložení podle tabulky načtené ve stejné relaci\r\n"+RecordDecoder.Decode(records[0],bound,bytes);
                        read.Parameters=RecordDecoder.Fields(records[0],bound,bytes,"BSI");
                        read.Definitions=new[]{records[0].Source};
                    }catch(IOException e){read.Parameters=new List<ParameterValue>();read.Values="Mapa parametrů načtena, ale hodnoty nejsou ověřené: "+e.Message;}
                }
            }
        }
        // Order comes from the ECU's own parameter table, never YAML enumeration order.
        // Unknown indexes or ambiguous byte packing invalidate the entire layout.
        internal static RecordLayout Bind(RecordLayout source,int[] indexes)
        {
            if(indexes==null||indexes.Length==0||indexes.Length>255)throw new IOException("Prázdná nebo příliš dlouhá mapa parametrů.");
            if(indexes.Distinct().Count()!=indexes.Length)throw new IOException("Duplicitní index v mapě parametrů.");
            var fields=new List<FieldDefinition>();int cursor=0;
            foreach(int index in indexes){
                var matches=(source.Mapped??new MappedField[0]).Where(entry=>entry.Index==index).ToArray();
                if(matches.Length!=1)throw new IOException("Index "+index+" nemá jednoznačnou definici; hodnoty nebudou odhadovány.");
                var m=matches[0];if(m.Length<1||m.Length>256)throw new IOException("Neplatná šířka parametru.");
                if(m.Length>=8)cursor=(cursor+7)/8*8;
                int byteIndex=cursor/8,lowBit=cursor%8;
                if(m.Length<8&&lowBit+m.Length>8)throw new IOException("Neověřené balení parametru přes hranici bajtu.");
                if(m.Length>=8&&m.Length%8!=0)throw new IOException("Neověřená necelobajtová šířka parametru.");
                string bits=m.Length<8?(byteIndex+1)+"."+(lowBit+m.Length-1)+"-"+(byteIndex+1)+"."+lowBit:(byteIndex+1)+".7-"+(byteIndex+m.Length/8)+".0";
                var f=m.Field;if(f==null)throw new IOException("Chybí definice parametru.");
                fields.Add(new FieldDefinition{Name=f.Name,Label=f.Label,Bits=bits,Type=f.Type,Factor=f.Factor,Offset=f.Offset,Unit=f.Unit,Values=f.Values,FlagBoundary=true});
                cursor+=m.Length;
            }
            return new RecordLayout{Patterns=source.Patterns,Length=(cursor+7)/8,Fields=fields.ToArray()};
        }
    }
}

