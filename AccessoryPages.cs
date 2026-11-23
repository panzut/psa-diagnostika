using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PsaReader
{
    internal sealed class AccessoryPages
    {
        readonly Catalogue catalogue;readonly VehicleDefinition vehicle;readonly string node;readonly Func<List<UnitSnapshot>> snapshots;
        readonly TextBox preview=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
        readonly Label current=new Label{Dock=DockStyle.Top,Height=58,Padding=new Padding(12)};
        readonly ComboBox desired=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=190};
        readonly Button inspect=new Button{Text="Zkontrolovat změnu (bez zápisu)",AutoSize=true};
        readonly Button save=new Button{Text="Zapsat do auta",AutoSize=true,Enabled=false};
        readonly CheckBox prepared=new CheckBox{Text="Auto stojí, motor je vypnutý, zapalování zapnuté a Delphi zavřená.",AutoSize=true};
        readonly Button economyRun=new Button{Text="Dočasně vypnout úsporný režim",AutoSize=true};
        readonly CheckBox economyPrepared=new CheckBox{Text="Auto stojí, motor je vypnutý, zapalování zapnuté a Delphi zavřená.",AutoSize=true};
        readonly Label reason=new Label{Dock=DockStyle.Bottom,Height=52,Padding=new Padding(12),Text="Zápis vždy znovu přečte jednotku, zkontroluje napětí, uloží zálohu a ověří výsledek po novém připojení. Odmítnutý povel se neopakuje."};
        bool reading;
        readonly NumericUpDown serviceKm=new NumericUpDown{Minimum=1000,Maximum=50000,Increment=1000,Value=10000,ThousandsSeparator=true,Width=110};
        readonly CheckBox servicePrepared=new CheckBox{Text="Auto stojí, motor je vypnutý, zapalování zapnuté a Delphi zavřená.",AutoSize=true};
        readonly Button serviceRun=new Button{Text="Nastavit interval a resetovat servis",AutoSize=true,Enabled=false};
        internal AccessoryPages(TabControl tabs,Catalogue catalogue,VehicleDefinition vehicle,string node,Func<List<UnitSnapshot>> snapshots,Action<BsiAction,bool,int,Action<BsiServiceResult>> execute)
        {
            this.catalogue=catalogue;this.vehicle=vehicle;this.node=node;this.snapshots=snapshots;
            if(node!="BSI")return;
            var testsTab=new TabPage("Testy příslušenství");var testList=new ListBox{Dock=DockStyle.Left,Width=320,DisplayMember="Name"};
            var explanation=new TextBox{Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical};
            var testHeader=new Label{Dock=DockStyle.Top,Height=62,Padding=new Padding(12),Text="Dočasné testy výstupů · zatím nedostupné\r\nAirbagy a předpínače: aktivace a zápisy jsou trvale zakázané."};
            var testButtons=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,Padding=new Padding(10)};
            testButtons.Controls.Add(new Button{Text="Spustit test — nedostupné",Enabled=false,AutoSize=true});
            testButtons.Controls.Add(new Label{Text="Žádný test neběží.",AutoSize=true,Padding=new Padding(8)});
            testList.Items.AddRange(Accessories.Tests(node).Cast<object>().ToArray());
            testList.SelectedIndexChanged+=delegate{var selected=testList.SelectedItem as AccessoryTest;if(selected!=null)explanation.Text=selected.Name+"\r\n\r\n"+selected.Availability+"\r\n\r\n"+selected.Missing+"\r\n\r\nPo doplnění ověřeného postupu: krátký časově omezený test, možnost ukončení a potvrzený návrat řízení autu.\r\n\r\nZdroj definice: "+selected.Source;};
            testsTab.Controls.Add(explanation);testsTab.Controls.Add(testList);testsTab.Controls.Add(testButtons);testsTab.Controls.Add(testHeader);tabs.TabPages.Add(testsTab);
            testList.SelectedIndex=-1;testList.SelectedIndex=0;
            var economyTab=new TabPage("Úsporný režim");
            var economyInfo=new TextBox{Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,
                Text="DOČASNĚ VYPNOUT ÚSPORNÝ REŽIM\r\n\r\n"+
                "Podle nápovědy Delphi může tato servisní funkce potlačit úsporný režim BSI na 30–60 minut. Dostupnost závisí na verzi jednotky; pro tuto BSI zatím nebyla ověřena.\r\n\r\n"+
                "Podmínky uvedené v Delphi:\r\n• Zapnuté zapalování, vypnutý motor.\r\n• Napětí akumulátoru vyšší než 12,1 V.\r\n\r\n"+
                "Před provedením program přečte identifikaci, tabulku parametrů a aktuální napětí BSI. Je-li režim již neaktivní, nic nemění.\r\n\r\n"+
                "Zobrazení této stránky nic neposílá do auta. Funkce nemění nastavení denního svícení ani trvale nevypíná ochranu baterie."};
            var economyButtons=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,Padding=new Padding(10)};
            economyButtons.Controls.Add(economyPrepared);economyButtons.Controls.Add(economyRun);
            economyPrepared.CheckedChanged+=delegate{economyRun.Enabled=!reading&&economyPrepared.Checked&&vehicle.Id=="VCAN:all";};
            economyRun.Click+=delegate{execute(BsiAction.EconomyOff,economyPrepared.Checked,10000,r=>{economyPrepared.Checked=false;economyInfo.Text=r.Report+"\r\n\r\nZáznam: "+r.LogPath;});};
            economyTab.Controls.Add(economyInfo);economyTab.Controls.Add(economyButtons);tabs.TabPages.Add(economyTab);
            var settingTab=new TabPage("Denní svícení");var controls=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(12)};
            controls.Controls.Add(new Label{Text="Požadované nastavení",AutoSize=true,Padding=new Padding(0,5,0,0)});
            desired.Items.AddRange(new object[]{"Vypnuto (OFF)","Zapnuto (ON)"});desired.SelectedIndex=0;controls.Controls.Add(desired);controls.Controls.Add(inspect);controls.Controls.Add(save);
            controls.SetFlowBreak(save,true);controls.Controls.Add(prepared);
            prepared.CheckedChanged+=delegate{save.Enabled=!reading&&prepared.Checked&&vehicle.Id=="VCAN:all";};
            save.Click+=delegate{bool on=desired.SelectedIndex==1;execute(on?BsiAction.DaylightOn:BsiAction.DaylightOff,prepared.Checked,10000,r=>{prepared.Checked=false;preview.Text=r.Report+"\r\n\r\nZáznam: "+r.LogPath;current.Text=r.Success?"Poslední kontrolní odečet: denní svícení "+(on?"ZAPNUTO":"VYPNUTO")+" · "+DateTime.Now.ToString("HH:mm:ss"):"Výsledek změny není potvrzený. Podrobnosti jsou níže.";});};
            desired.SelectedIndexChanged+=delegate{preview.Text="Volba změněna pouze v náhledu. Použij Zkontrolovat změnu. Do auta nebylo nic odesláno.";};
            inspect.Click+=delegate{try{preview.Text=Accessories.Preview(catalogue,vehicle,node,snapshots(),desired.SelectedIndex==1).Report();}catch(Exception e){preview.Text="Návrh nelze připravit: "+e.Message+"\r\nDo auta nebylo nic odesláno.";}};
            settingTab.Controls.Add(preview);settingTab.Controls.Add(controls);settingTab.Controls.Add(current);settingTab.Controls.Add(reason);tabs.TabPages.Add(settingTab);
            var serviceTab=new TabPage("Servisní interval");
            var serviceInfo=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Text="Zvol vzdálenost do příštího servisu v km.\r\n\r\nProgram nejprve zapíše a ověří interval, potom resetuje servisní počítadlo a přečte výsledek. Používej po provedené údržbě. Časové nastavení zůstává zachované.\r\n\r\nRozsah 1 000–50 000 km, v násobcích 200 km. Výchozí volba: 10 000 km."};
            var serviceControls=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(12)};
            serviceControls.Controls.Add(new Label{Text="Interval (km)",AutoSize=true,Padding=new Padding(0,5,0,0)});serviceControls.Controls.Add(serviceKm);serviceControls.Controls.Add(serviceRun);
            serviceControls.SetFlowBreak(serviceRun,true);serviceControls.Controls.Add(servicePrepared);
            servicePrepared.CheckedChanged+=delegate{serviceRun.Enabled=!reading&&servicePrepared.Checked&&vehicle.Id=="VCAN:all";};
            serviceRun.Click+=delegate{execute(BsiAction.ServiceReset,servicePrepared.Checked,(int)serviceKm.Value,r=>{servicePrepared.Checked=false;serviceInfo.Text=r.Report+"\r\n\r\nZáznam: "+r.LogPath;});};
            serviceTab.Controls.Add(serviceInfo);serviceTab.Controls.Add(serviceControls);tabs.TabPages.Add(serviceTab);
        }
        internal void Refresh(bool busy)
        {
            if(node!="BSI")return;
            reading=busy;inspect.Enabled=desired.Enabled=prepared.Enabled=economyPrepared.Enabled=!busy;
            serviceKm.Enabled=servicePrepared.Enabled=!busy;serviceRun.Enabled=!busy&&servicePrepared.Checked&&vehicle.Id=="VCAN:all";
            save.Enabled=!busy&&prepared.Checked&&vehicle.Id=="VCAN:all";economyRun.Enabled=!busy&&economyPrepared.Checked&&vehicle.Id=="VCAN:all";
            if(busy){current.Text="Probíhá čtení. Náhled změny bude dostupný po dokončení.";preview.Clear();return;}
            try{var p=Accessories.Preview(catalogue,vehicle,node,snapshots(),desired.SelectedIndex==1);current.Text="Denní svícení v konfiguraci: "+(p.Current?"ZAPNUTO":"VYPNUTO")+" · uložený odečet "+p.ReadTime;preview.Text="Zvol požadované nastavení a potvrď připravené vozidlo. Tlačítko Zapsat do auta provede čerstvý odečet, změnu a ověření.";}
            catch(Exception e){current.Text="Nastavení není ověřené v dostupných datech.";preview.Text=e.Message;inspect.Enabled=false;}
        }
    }
}
