using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security;

namespace PsaReader
{
    internal static class SnapshotHtml
    {
        static string E(string value){return SecurityElement.Escape(value??"");}
        static string Name(UnitSnapshot unit,DataRead read)
        {
            if(unit.Matches.Any(m=>m.Model=="BSI")&&read.Service==0x21){
                switch(read.Identifier){case 0xB0:return "VIN";case 0xC0:return "Napájení, baterie, zapalování";case 0xC1:return "Klimatizace a ventilátor";case 0xC2:return "Pedály, pásy, převodovka a tempomat";case 0xC5:return "Světla, směrovky a klakson";case 0xC6:return "Alarm";case 0xC7:return "Stavy klíčů a imobilizéru";case 0xC8:return "Dveře, zámky a dálkové ovládání";case 0xC9:return "Palivo, teploty, olej a servis";case 0xCA:return "Vzdálenost a spotřeba";case 0xCB:return "Kontrolky";case 0xCC:return "Okna";case 0xCD:return "Stěrače, ostřikovače a vyhřívání";case 0xD2:return "Stavy výstupů";case 0xD3:return "Relé, brzda a přívěs";}
            }
            return read.Name;
        }
        internal static void Write(VehicleSnapshot data,string path)
        {
            var b=new StringBuilder("<!doctype html><html lang='cs'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>PSA — odečet vozidla</title><style>body{font:16px system-ui;background:#f3f5f7;color:#182636;margin:0}main{max-width:1250px;margin:auto;padding:30px}h1{margin-bottom:4px}header p{color:#4b6070}section{background:white;border-radius:12px;padding:18px;margin:18px 0}input[type=search]{font:inherit;padding:12px;width:min(95%,600px);border:1px solid #9baab7;border-radius:7px}.filters{position:sticky;top:0;background:#f3f5f7;padding:14px 0;border-bottom:1px solid #ccd6df;z-index:1}article{border-top:1px solid #dbe3ea;padding:14px 0}h3{font-size:17px;margin:0 0 6px}.status{color:#27624a;font-size:14px}.bad{color:#8f3d2b}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:14px/1.6 ui-monospace,Consolas,monospace;background:#f5f7f9;padding:12px;border-radius:6px}summary{cursor:pointer;color:#496477}small{color:#617284}label{display:inline-block;padding:8px}footer{padding:20px 0;color:#617284}button{font:inherit;padding:8px;border:1px solid #9baab7;border-radius:5px;background:white}</style><main><header><h1>PSA Diagnostika · uložený odečet</h1><p>");
            b.Append(E(data.Started)).Append(" · hodnoty byly pořízené postupně, nejde o živé zobrazení.</p><p><strong>").Append(data.Units.Count(u=>u.Unit.Responded)).Append(" odpovídajících adres z ").Append(data.Units.Count).Append("</strong> · ").Append(data.Units.Sum(u=>u.Reads.Count(r=>r.State=="Přečteno"))).Append(" přečtených odpovědí · ").Append(data.Units.Sum(u=>u.Reads.Count)).Append(" čtecích dotazů</p></header><div class='filters'><input id='q' type='search' placeholder='Hledat: stěrače, světla, battery, door…' aria-label='Hledat v odečtu'><label><input id='all' type='checkbox'> Zobrazit i odmítnuté dotazy</label> <span id='count'></span></div>");
            if(!data.Completed)b.Append("<p class='bad'>Průchod nedokončen: ").Append(E(data.Error)).Append("</p>");
            foreach(var unit in data.Units){
                b.Append("<section><h2>").Append(E(String.Join(" / ",unit.Unit.Nodes))).Append(" <small>").Append(E(unit.Unit.Address)).Append("</small></h2><p>").Append(E(unit.Unit.State)).Append(" · ukončení ").Append(unit.Unit.Disconnected?"potvrzeno":"NEPOTVRZENO").Append("</p>");
                foreach(var m in unit.Matches)b.Append("<p><small>Shoda reference: ").Append(E(m.Model+" / "+m.Variant+" / "+m.Reference)).Append("</small></p>");
                if(unit.Error!=null)b.Append("<p class='bad'>").Append(E(unit.Error)).Append("</p>");
                foreach(var read in unit.Reads){
                    bool positive=read.State=="Přečteno";
                    b.Append("<article data-ok='").Append(positive?"1":"0").Append("'><h3>").Append(E(Name(unit,read))).Append("</h3><div class='status ").Append(positive?"":"bad").Append("'>").Append(E(read.State)).Append("</div><small>").Append(E(read.Time)).Append(" · ").Append(read.Service.ToString("X2")).Append(" ").Append(read.Identifier.ToString("X2")).Append("</small>");
                    if(!String.IsNullOrEmpty(read.Values))b.Append("<pre>").Append(E(read.Values)).Append("</pre>");
                    b.Append("<details><summary>Surová odpověď a zdroj</summary><pre>").Append(E(read.Raw)).Append("</pre><small>").Append(E(String.Join("; ",read.Definitions??new string[0]))).Append("</small></details></article>");
                }b.Append("</section>");
            }
            b.Append("<footer>Neznámé formáty zůstávají surovými daty. Odmítnuté čtení není nulová hodnota. Sdílená adresa nepotvrzuje všechny uvedené moduly. Popisy a převody vycházejí z komunitní databáze; stav povelu není měření fyzického sepnutí relé.<br>Nepokryté položky: ").Append(E(String.Join(", ",data.UnplannedNodes??new string[0]))).Append("<br>Bez mazání, kódování, akčních testů a aktualizací.</footer></main><script>const q=document.getElementById('q'),all=document.getElementById('all');function filter(){let n=0;const term=q.value.toLocaleLowerCase();document.querySelectorAll('article').forEach(a=>{const show=(all.checked||a.dataset.ok==='1')&&(!term||a.textContent.toLocaleLowerCase().includes(term)||a.parentElement.querySelector('h2').textContent.toLocaleLowerCase().includes(term));a.hidden=!show;if(show)n++;});document.getElementById('count').textContent=n+' záznamů';}q.addEventListener('input',filter);all.addEventListener('change',filter);filter();</script></html>");
            File.WriteAllText(path,b.ToString(),Encoding.UTF8);
        }
    }
}
