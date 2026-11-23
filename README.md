# PSA Diagnostika

Experimentální diagnostika PSA pro Windows a USB DS150E/CDP+.

## Stažení a spuštění

Hotový ZIP je v sekci Releases. Rozbalte celý archiv a spusťte PsaReader.exe. Potřebujete .NET Framework 4.5 nebo novější a USB ovladač krabičky. Delphi musí být zavřená.

## Stav verze 0.3.0-test

- Databáze: 117 variant vozidel, 486 definic ECU.
- Čtení lze naplánovat pro 57 profilů CAN AEE2004/AEE2010. Přítomnost v databázi není zárukou komunikace.
- Čtení identifikace, dostupných hodnot a závad KWP/UDS; export logů do ZIP.
- Mazání běžných závad rozpoznané jednotky se zálohou a kontrolním čtením; zatím ověřeno simulací.
- SRS, airbagy a předpínače pouze pro čtení. Žádné aktivace, zápisy ani resetování.
- K-line/VAN a DoIP nejsou podporované. Akční testy příslušenství nejsou dostupné.
- Firmware ani Delphi se neaktualizují.

Praktické ověřování začalo na Lancia Phedra; další auta vyžadují testování. Diagnostikujte pouze stojící vozidlo. Podrobnosti jsou v [CTI-ME.txt](CTI-ME.txt).

## Sestavení

Na Windows spusťte `powershell -ExecutionPolicy Bypass -File build.ps1`. Skript sestaví aplikaci a spustí simulované testy bez komunikace s autem.

## Logy a soukromí

Veřejný balíček neobsahuje osobní odečty. Nové logy mohou obsahovat VIN; neposílejte je do veřejných issues bez kontroly. Při hlášení výsledku uveďte model, rok, motor a verzi aplikace.

## Licence a podklady

Kód obsahuje adaptaci algoritmu pod GPL-3.0-or-later; viz LICENSE a BsiConfigurationKey.cs. Databáze PSA-RE má samostatnou licenci Apache-2.0; zdroj, revize a úpravy jsou uvedené v data/THIRD-PARTY.txt.
