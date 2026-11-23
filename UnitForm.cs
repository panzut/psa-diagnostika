using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PsaReader
{
    internal sealed class UnitValueRow
    {
        public string Group {get;set;} public string Parameter {get;set;} public string Value {get;set;}
        public string State {get;set;} public string Time {get;set;} public string Owner {get;set;} public string Search {get;set;}
        internal DataRead Source;
    }
    internal static class UnitPresentation
    {
        internal static string Group(DataRead r,bool cabin)
        {
            if(r.Service==0x17||r.Service==0x19)return "Závady";
            if(r.Service==0x35)return "Technické tabulky";
            if(r.Service==0x21&&cabin)switch(r.Identifier){
                case 0xC0:return "Napájení a zapalování";case 0xC1:return "Klimatizace";case 0xC2:return "Pedály, pásy a tempomat";case 0xC5:return "Světla a klakson";case 0xC6:return "Alarm";case 0xC7:return "Klíče a imobilizér";case 0xC8:return "Dveře a zamykání";case 0xC9:return "Teploty, palivo a servis";case 0xCA:return "Vzdálenost a spotřeba";case 0xCB:return "Kontrolky";case 0xCC:return "Okna";case 0xCD:return "Stěrače a ostřikovače";case 0xD2:return "Stavy výstupů";case 0xD3:return "Relé a přívěs";
            }
            if(r.Identifier>=0xB1&&r.Identifier<=0xBF)return "Výbava a nastavení (čtení)";
            if(r.Name=="Identifikace"||r.Identifier==0x80||r.Identifier==0xFE||r.Identifier==0xF190||r.Name=="VIN")return "Identifikace";
            return r.Name??"Další údaje";
        }
        static readonly Dictionary<string,string> labels=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
            {"Battery voltage","Napětí baterie"},{"Protected battery voltage","Napětí chráněného napájení"},{"Alternator excitation voltage","Budicí napětí alternátoru"},{"Brake pedal pressed","Sešlápnutý brzdový pedál"},{"Ignition positive","Zapnuté zapalování"},{"Horn control","Povel klaksonu"},{"Request for horns","Požadavek klaksonu"},{"Windscreen wiping control","Povel předních stěračů"},{"Windscreen washing control","Povel předních ostřikovačů"},{"Request for windscreen washer","Požadavek předních ostřikovačů"},{"High speed wiping request","Požadavek rychlého stírání"},{"Low speed wiping request","Požadavek pomalého stírání"},{"Rear screen wipe command","Povel zadního stěrače"},{"Rear screen washing control","Povel zadního ostřikovače"},{"Automatic wipe mode","Automatické stírání"},{"Front right-hand door","Pravé přední dveře"},{"Front left-hand door","Levé přední dveře"},{"Rear right-hand door","Pravé zadní dveře"},{"Rear left-hand door","Levé zadní dveře"},{"Vehicle locking status","Stav zamknutí auta"},{"Exterior temperature","Venkovní teplota"},{"Engine coolant temperature","Teplota chladicí kapaliny"},{"Engine oil temperature","Teplota motorového oleje"},{"Total distance covered","Celková ujetá vzdálenost"},{"Current consumption","Okamžitá spotřeba"},{"Fuel level displayed","Zobrazovaná hladina paliva"},{"Request to switch on the dipped headlamps","Požadavek potkávacích světel"},{"Request to switch on the side lights","Požadavek obrysových světel"}};
        internal static string Label(string value){string result;return labels.TryGetValue(value??"",out result)?result:value;}
        internal static string Value(string value){switch(value){case "Inactive":return "Neaktivní";case "Active":return "Aktivní";case "NO":return "Ne";case "Yes":return "Ano";case "Open":return "Otevřeno";case "Closed":return "Zavřeno";case "Unlocked":return "Odemčeno";case "Locked":return "Zamčeno";case "None":return "Žádný";case "Immediate switch-off":return "Vypnuto";default:return value;}}
        internal static string Normalize(string text){var b=new StringBuilder();foreach(char ch in (text??"").Normalize(NormalizationForm.FormD))if(CharUnicodeInfo.GetUnicodeCategory(ch)!=UnicodeCategory.NonSpacingMark)b.Append(Char.ToLowerInvariant(ch));return b.ToString();}
        internal static List<UnitValueRow> Rows(IEnumerable<UnitSnapshot> snapshots)
        {
            var rows=new List<UnitValueRow>();
            foreach(var unit in snapshots)foreach(var read in unit.Reads.Where(r=>r.Service==0x21||r.Service==0x22)){
                string group=Group(read,unit.Unit.Nodes.Contains("BSI"));DateTimeOffset stamp;string time=DateTimeOffset.TryParse(read.Time,out stamp)?stamp.ToLocalTime().ToString("dd.MM. HH:mm:ss"):read.Time;
                if(read.Parameters!=null&&read.Parameters.Count>0)foreach(var p in read.Parameters)rows.Add(new UnitValueRow{Group=group,Parameter=Label(p.Label),Value=Value(p.Value),State=p.State,Time=time,Owner=p.Owner,Search=Normalize(group+" "+p.Label+" "+p.Name+" "+Label(p.Label)+" "+p.Value),Source=read});
                else rows.Add(new UnitValueRow{Group=group,Parameter=read.Name,Value=read.State=="Přečteno"?"Odpověď uložena, formát nerozpoznán":"—",State=read.State=="Přečteno"?"Surová data":read.State,Time=time,Owner=String.Join(" / ",unit.Unit.Nodes),Search=Normalize(group+" "+read.Name+" "+read.State),Source=read});
            }
            return rows;
        }
    }
    internal sealed class UnitForm : Form
    {
        readonly Catalogue catalogue;readonly VehicleDefinition vehicle;readonly VehicleNode node;readonly Action<VehicleSnapshot> received;
        List<UnitSnapshot> snapshots;List<UnitValueRow> rows=new List<UnitValueRow>();bool busy;volatile bool cancelled;
        readonly AccessoryPages accessories;
        readonly Label banner=new Label{Dock=DockStyle.Top,Height=56,Padding=new Padding(14,6,14,6),ForeColor=Color.FromArgb(55,75,95)};
        readonly Label status=new Label{Dock=DockStyle.Bottom,Height=44,Padding=new Padding(12)};
        readonly Button read=new Button{Text="Načíst z auta",AutoSize=true};readonly Button stop=new Button{Text="Zastavit čtení",AutoSize=true,Enabled=false};
        readonly Button clearFaults=new Button{Text="Smazat závady této jednotky…",AutoSize=true,Enabled=false};
        readonly CheckBox clearPrepared=new CheckBox{Text="Auto stojí, motor je vypnutý, zapalování zapnuté a Delphi zavřená.",AutoSize=true};
        readonly CheckBox parked=new CheckBox{Text="Auto stojí, zapalování je zapnuté, Delphi je zavřená.",AutoSize=true};
        readonly TextBox filter=new TextBox{Width=290};readonly ComboBox groups=new ComboBox{Width=260,DropDownStyle=ComboBoxStyle.DropDownList};
        readonly CheckBox unavailable=new CheckBox{Text="Nerozpoznané a odmítnuté",AutoSize=true};
        readonly DataGridView values=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoGenerateColumns=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,BackgroundColor=Color.White};
        readonly TextBox overview=ReadOnlyText(),faults=ReadOnlyText(),raw=ReadOnlyText();readonly TabControl tabs=new TabControl{Dock=DockStyle.Fill};
        static TextBox ReadOnlyText(){return new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=true,Font=new Font("Segoe UI",11)};}
        internal UnitForm(Catalogue catalogue,VehicleDefinition vehicle,VehicleNode node,IEnumerable<UnitSnapshot> snapshots,Action<VehicleSnapshot> received)
        {
            this.catalogue=catalogue;this.vehicle=vehicle;this.node=node;this.snapshots=snapshots.ToList();this.received=received;
            base.Text=node.Name+" — PSA Diagnostika";Size=new Size(1180,700);MinimumSize=new Size(900,620);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);
            var title=new Label{Text=node.Name+" ("+node.Code+")",Font=new Font("Segoe UI",18,FontStyle.Bold),Dock=DockStyle.Top,Height=55,Padding=new Padding(14,10,0,0)};
            var bar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(12,4,12,10)};var back=new Button{Text="Zpět na jednotky",AutoSize=true};bar.Controls.Add(back);bar.Controls.Add(read);bar.Controls.Add(stop);bar.Controls.Add(parked);
            var overviewTab=new TabPage("Přehled");overviewTab.Controls.Add(overview);tabs.TabPages.Add(overviewTab);
            var faultTab=new TabPage("Závady");faultTab.Controls.Add(faults);
            var clearBar=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,Padding=new Padding(10)};clearBar.Controls.Add(clearPrepared);clearBar.Controls.Add(clearFaults);faultTab.Controls.Add(clearBar);tabs.TabPages.Add(faultTab);
            clearPrepared.CheckedChanged+=delegate{UpdateClear();};clearFaults.Click+=delegate{ClearFaults();};
            var valueTab=new TabPage("Hodnoty");var filters=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8)};filters.Controls.Add(new Label{Text="Hledat",AutoSize=true,Padding=new Padding(0,5,0,0)});filters.Controls.Add(filter);filters.Controls.Add(groups);filters.Controls.Add(unavailable);
            foreach(var column in new[]{new[]{"Group","Skupina","23"},new[]{"Parameter","Parametr","36"},new[]{"Value","Hodnota","30"},new[]{"State","Stav údaje","22"},new[]{"Time","Čas odečtu","16"},new[]{"Owner","Zdroj","12"}})values.Columns.Add(new DataGridViewTextBoxColumn{DataPropertyName=column[0],HeaderText=column[1],FillWeight=Int32.Parse(column[2])});
            values.DefaultCellStyle.WrapMode=DataGridViewTriState.True;values.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;
            valueTab.Controls.Add(values);valueTab.Controls.Add(filters);tabs.TabPages.Add(valueTab);
            var rawTab=new TabPage("Surová data");raw.Font=new Font("Consolas",10);rawTab.Controls.Add(raw);tabs.TabPages.Add(rawTab);
            accessories=new AccessoryPages(tabs,catalogue,vehicle,node.Code,()=>this.snapshots,Service);
            Controls.Add(tabs);Controls.Add(bar);Controls.Add(banner);Controls.Add(title);Controls.Add(status);
            back.Click+=delegate{Close();};read.Click+=delegate{Read();};stop.Click+=delegate{cancelled=true;stop.Enabled=false;status.Text="Ukončuji čtení a spojení…";};
            filter.TextChanged+=delegate{ApplyFilter();};groups.SelectedIndexChanged+=delegate{ApplyFilter();};unavailable.CheckedChanged+=delegate{ApplyFilter();};
            values.CellDoubleClick+=delegate(object sender,DataGridViewCellEventArgs e){if(e.RowIndex>=0){var row=values.Rows[e.RowIndex].DataBoundItem as UnitValueRow;if(row!=null){raw.Text=row.Source.Values+"\r\n\r\nSurová odpověď: "+row.Source.Raw+"\r\nČas: "+row.Source.Time;tabs.SelectedTab=rawTab;}}};
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){cancelled=true;e.Cancel=true;status.Text="Nejdříve dokončím ukončení spojení. Potom lze detail zavřít.";}};
            Fill();
        }
        void Fill()
        {
            UpdateClear();
            accessories.Refresh(busy);
            rows=UnitPresentation.Rows(snapshots);string previous=groups.SelectedItem as string;groups.Items.Clear();groups.Items.Add("Všechny skupiny");foreach(string group in rows.Select(r=>r.Group).Distinct().OrderBy(x=>x))groups.Items.Add(group);groups.SelectedItem=previous;if(groups.SelectedIndex<0)groups.SelectedIndex=0;
            string times=String.Join(" – ",snapshots.SelectMany(s=>s.Reads).Select(r=>r.Time).Where(t=>!String.IsNullOrEmpty(t)).OrderBy(t=>t).Where((t,i)=>i==0).ToArray());
            banner.Text=snapshots.Count==0?"Zatím nejsou načtená data. Otevření detailu se k autu nepřipojuje.":"Uložený odečet · "+times+"\r\nSpojení nyní není otevřené. Pro nový odečet použij Načíst z auta.";
            var summary=new StringBuilder(node.Name+"\r\n\r\n");
            foreach(var snapshot in snapshots){summary.AppendLine("Komunikace: "+(snapshot.Unit.Responded?"odpověď doložena":"bez potvrzené odpovědi"));summary.AppendLine("Ukončení předchozího spojení: "+(snapshot.Unit.Disconnected?"potvrzeno":"NEPOTVRZENO"));foreach(var match in snapshot.Matches.Where(m=>node.Models.Any(k=>k.Key==m.Key)))summary.AppendLine("Model: "+match.Model+"\r\nVarianta: "+match.Variant+"\r\nReference: "+match.Reference);if(snapshot.Unit.Nodes.Length>1)summary.AppendLine("Společná diagnostická cesta: "+String.Join(" / ",snapshot.Unit.Nodes)+". Sloupec Zdroj rozlišuje původ přeložených hodnot.");if(snapshot.Error!=null)summary.AppendLine(snapshot.Error);summary.AppendLine();}
            summary.AppendLine("Čtení stavů nespouští akční členy. Během diagnostiky nejezděte; ABS/ESP může být omezené.");overview.Text=summary.ToString();
            var faultReads=snapshots.SelectMany(s=>s.Reads).Where(r=>r.Service==0x17||r.Service==0x19).ToArray();
            faults.Text=faultReads.Length==0?"Závady zatím nebyly přečtené. Neznamená to prázdnou paměť závad.":String.Join("\r\n\r\n",faultReads.Select(r=>r.Time+"\r\n"+r.State+"\r\n"+r.Values+"\r\nSurová odpověď: "+r.Raw));
            raw.Text=String.Join("\r\n\r\n",snapshots.SelectMany(s=>s.Reads).Select(r=>r.Time+" · "+r.Service.ToString("X2")+" "+r.Identifier.ToString("X2")+" · "+r.State+"\r\n"+r.Values+"\r\n"+r.Raw));
            read.Enabled=CanRoute.Plan(catalogue,vehicle).Any(r=>r.Nodes.Contains(node.Code));ApplyFilter();
        }
        void ApplyFilter()
        {
            string term=UnitPresentation.Normalize(filter.Text),group=groups.SelectedIndex>0?groups.SelectedItem as string:null;
            var visible=rows.Where(r=>(group==null||r.Group==group)&&(term.Length==0||r.Search.Contains(term))&&(unavailable.Checked||r.Source.Parameters!=null&&r.Source.Parameters.Count>0)).ToList();values.DataSource=visible;
            foreach(DataGridViewRow row in values.Rows){var item=row.DataBoundItem as UnitValueRow;if(item!=null&&item.State!="Přečteno")row.DefaultCellStyle.ForeColor=Color.FromArgb(145,80,30);}
            if(!busy)status.Text=visible.Count+" zobrazených parametrů · "+rows.Count+" dostupných položek. Dvojklik na hodnotu ukáže původní odpověď.";
        }
        void Read()
        {
            if(busy)return;if(!parked.Checked){status.Text="Nejdřív potvrď, že auto stojí a je připravené ke čtení.";return;}
            busy=true;UpdateClear();cancelled=false;read.Enabled=parked.Enabled=false;stop.Enabled=true;banner.Text="Probíhá nový odečet. Zobrazené hodnoty zůstávají z předchozího odečtu až do dokončení.";
            accessories.Refresh(true);
            var worker=new BackgroundWorker{WorkerReportsProgress=true};worker.DoWork+=delegate(object sender,DoWorkEventArgs e){e.Result=BulkReader.Run(catalogue,vehicle,()=>cancelled,s=>worker.ReportProgress(0,s),u=>{},node.Code);};worker.ProgressChanged+=delegate(object sender,ProgressChangedEventArgs e){status.Text=Convert.ToString(e.UserState);};
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e){busy=false;parked.Enabled=true;parked.Checked=false;stop.Enabled=false;if(e.Error!=null){Fill();banner.Text="Nové čtení selhalo: "+e.Error.Message+". Zobrazená data zůstala beze změny.";}else{var result=(VehicleSnapshot)e.Result;if(result.Units.Count>0){snapshots=result.Units;received(result);}Fill();if(!result.Completed)banner.Text="Nový odečet nebyl dokončen: "+result.Error+"\r\nKontroluj čas a stav jednotlivých údajů.";}worker.Dispose();};worker.RunWorkerAsync();
        }
        void Service(BsiAction action,bool engineOff,int intervalKm,Action<BsiServiceResult> done)
        {
            if(busy||!engineOff)return;
            busy=true;UpdateClear();read.Enabled=parked.Enabled=stop.Enabled=false;accessories.Refresh(true);banner.Text="Probíhá servisní operace BSI. Neodpojujte krabičku ani nevypínejte zapalování.";
            var worker=new BackgroundWorker{WorkerReportsProgress=true};
            worker.DoWork+=delegate(object sender,DoWorkEventArgs e){e.Result=BsiServices.Run(catalogue,vehicle,action,engineOff,s=>worker.ReportProgress(0,s),intervalKm);};
            worker.ProgressChanged+=delegate(object sender,ProgressChangedEventArgs e){status.Text=Convert.ToString(e.UserState);};
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e){busy=false;read.Enabled=parked.Enabled=true;parked.Checked=false;accessories.Refresh(false);var result=e.Error==null?(BsiServiceResult)e.Result:new BsiServiceResult{Report=e.Error.Message};banner.Text=result.Success?"Servisní operace dokončena; výsledek ověřený odečtem BSI.":"Servisní operace nebyla potvrzena. Podrobnosti jsou na otevřené kartě.";done(result);UpdateClear();worker.Dispose();};
            worker.RunWorkerAsync();
        }
        void UpdateClear()
        {
            clearPrepared.Enabled=!busy;clearFaults.Enabled=false;
            if(busy||!clearPrepared.Checked)return;
            try{FaultClearPlan.Create(catalogue,vehicle,node.Code,snapshots);clearFaults.Enabled=true;}catch(Exception){clearFaults.Enabled=false;}
        }
        void ClearFaults()
        {
            if(busy||!clearPrepared.Checked)return;FaultClearPlan plan;
            try{plan=FaultClearPlan.Create(catalogue,vehicle,node.Code,snapshots);}catch(Exception e){faults.Text=e.Message;return;}
            if(MessageBox.Show("Smazat paměť závad jednotky "+node.Name+"?\r\n\r\nPůvodní odečet se uloží do logu. Mohou se smazat také související diagnostické údaje; příčina závady tím nezmizí. Po smazání se paměť znovu přečte.","Potvrdit smazání závad",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            busy=true;UpdateClear();read.Enabled=parked.Enabled=stop.Enabled=false;accessories.Refresh(true);banner.Text="Zálohuji paměť závad, potom provedu potvrzené smazání a kontrolní odečet…";
            var worker=new BackgroundWorker{WorkerReportsProgress=true};
            worker.DoWork+=delegate(object sender,DoWorkEventArgs e){e.Result=FaultClear.Run(plan,true,s=>worker.ReportProgress(0,s));};
            worker.ProgressChanged+=delegate(object sender,ProgressChangedEventArgs e){status.Text=Convert.ToString(e.UserState);};
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e){busy=false;parked.Enabled=true;parked.Checked=false;clearPrepared.Checked=false;read.Enabled=CanRoute.Plan(catalogue,vehicle).Any(r=>r.Nodes.Contains(node.Code));accessories.Refresh(false);UpdateClear();if(e.Error!=null){DiagnosticsExport.RecordError(e.Error);faults.Text=e.Error.Message;}else{var result=(BsiServiceResult)e.Result;faults.Text=result.Report+"\r\n\r\nLog: "+result.LogPath;banner.Text=result.Success?"Operace dokončena, kontrolní odečet je na kartě Závady.":"Mazání nebo kontrolní odečet nebyly potvrzeny. Viz výsledek a log.";}worker.Dispose();};
            worker.RunWorkerAsync();
        }
    }
}



