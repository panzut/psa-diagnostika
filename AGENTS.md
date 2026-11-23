# Trvalé bezpečnostní pravidlo projektu

Výslovný požadavek uživatele: nikdy neaktivovat airbagy.

- Zádržný systém SRS (airbagy, předpínače bezpečnostních pásů a další pyrotechnické prvky) smí aplikace pouze číst.
- Nikdy neimplementovat ani neodesílat povel k odpálení, aktivaci či akčnímu testu těchto prvků. Žádné zápisy, kódování, odemykání ani resetování jednotky SRS.
- Toto omezení platí také pro budoucí obecné funkce zapnout/vypnout, surové příkazy a hromadné operace. Nesmějí obejít ochranu zádržného systému.
- Neznámý povel, cíl nebo účinek není povolený akční test. Při nejistotě nic neodesílat.
- Před přidáním jakýchkoli akčních funkcí musí existovat kontrola povolených příkazů a cílových jednotek na společné odesílací vrstvě, s testy odmítnutí SRS a neznámých cílů. Pouhé skrytí tlačítka nestačí.

Delphi ani firmware DS150E neaktualizovat.

Delphi byl podle dalšího výslovného upřesnění uživatele pouze počáteční zdroj. Další vývoj ovládání a testů probíhá v našem programu s vlastními protokolovými profily PSA; nespojovat jeho dostupnost s podporou funkcí v Delphi. Pro neznámé parametry nadále platí zákaz odhadování. Testy řídicí vrstvy se simulovaným transportem nejsou ověřením fyzického akčního testu.

Uživatel požádal také o funkci dočasného vypnutí úsporného režimu BSI. Jde o samostatnou servisní funkci, nikoli změnu denního svícení. Podle zobrazené nápovědy Delphi vyžaduje zapnuté zapalování, vypnutý motor a napětí vyšší než 12,1 V; potlačení trvá 30–60 minut jen u podporovaných verzí. Samotné přidání do nabídky neopravňuje odhadnout neznámé parametry rutiny. Před spuštěním ověřit konkrétní postup i aktuální podmínky vozidla.

Uživatel následně výslovně požádal o implementaci testů světel/stěračů a nastavení denního svícení ON/OFF; rovněž povolil konkrétní vypnutí výchozího denního svícení. Nejde o oprávnění k jiným změnám vozidla. Chybějící parametry příkazu, ukončení testu nebo dokončení zápisu se nesmějí odhadovat. Do ověření kompletního postupu jsou tyto operace v programu nedostupné. Náhled změny není provedený zápis. Zákaz aktivace a zápisů SRS platí beze změny.
