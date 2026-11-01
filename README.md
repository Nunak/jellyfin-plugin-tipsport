# Tipsport pro Jellyfin

Experimentální, neoficiální serverový plugin pro **Jellyfin 12.4 / .NET 10**. Nabízí sportovní přenosy z vlastního přihlášeného účtu Tipsportu. Kodi, Python ani Selenium nejsou potřeba.

## Co umí

- Knihovna Tipsport: živé zápasy, připravované zápasy a složky podle sportu.
- Stálá položka pro oblíbený tým, například Kometu.
- Pro oblíbený tým `Kometa` nebo `HC Kometa Brno` načítá oficiální klubové logo (také přes `tvg-logo` v M3U).
- Volitelný výstup M3U pro standardní TV vysílání Jellyfinu.
- Jeden vstup od poskytovatele, který může sdílet více místních přehrávačů Jellyfinu.
- Když už běží jiný zápas, nový nezávislý vstup odmítne. Stejný zápas z položky týmu i ze seznamu používá stejný vstup.
- Převod HLS do MPEG-TS přes FFmpeg dodaný s Jellyfinem, pomocí `-c copy`. Překódování pro konkrétní zařízení dál zajišťuje Jellyfin.
- Z HLS master playlistu vybírá nejvyšší dostupné rozlišení a při shodě vyšší datový tok. U odděleného zvuku nebo nepřístupného playlistu ponechá výběr FFmpegu. Diagnostika pluginu ukazuje nabídnuté varianty, bez podepsaných adres. Nízké rozlišení samotného zdroje plugin nenavyšuje.
- Pro požadavky na API používá systémový `curl`, pokud je dostupný (na Windows `%SystemRoot%\System32\curl.exe`, na Linuxu `/usr/bin/curl` nebo `/bin/curl`). Umožňuje požadavky GET bez těla s hlavičkou Content-Type. Bez něj použije klienta .NET; kompatibilitu s webovou ochranou poskytovatele je nutné ověřit. Cookie předává procesu přes standardní vstup, nikdy v argumentech, a zachovává kontrolu HTTPS certifikátů.

## První nastavení

Vlastní katalog tohoto projektu nevyžaduje zařazení do oficiálního katalogu Jellyfinu. V **Pluginy → Spravovat repozitáře** přidejte název `Nunak – Tipsport` a adresu:

```text
https://raw.githubusercontent.com/Nunak/jellyfin-plugin-tipsport/main/manifest.json
```

Katalog nabízí pouze zveřejněná vydání. Plugin lze instalovat z katalogu a aktualizovat standardní úlohou Jellyfinu pro aktualizace pluginů. Aktualizace vyžaduje restart serveru. Pokud katalog neobsahuje verzi kompatibilní s vaším serverem, neinstalujte balíček pro jinou verzi Jellyfinu.

Pro ruční testovací instalaci:

1. Zastavte Jellyfin, vytvořte v jeho adresáři pluginů složku odpovídající verzi ZIPu, rozbalte do ní jeho obsah a server znovu spusťte. Před nasazením zálohujte konfiguraci. Na Windows s tray instalací jde obvykle o `%ProgramData%\Jellyfin\Server\plugins`. Při ruční aktualizaci odstraňte starou složku pluginu, aby nezůstaly dvě kopie DLL.
2. V administraci otevřete **Pluginy → Tipsport** a zapněte plugin. Vyberte český nebo slovenský účet.
3. V běžném prohlížeči se přihlaste k Tipsportu. Do nastavení pluginu vložte **hodnotu cookie JSESSIONID**, nikoli heslo k účtu. Uložte relaci a ověřte načtení nabídky.
4. Při přechodu z původního Kodi doplňku lze jednorázově převzít jeho soubor relace tlačítkem v nastavení. Hledá se výhradně standardní profil Kodi stejného uživatele Windows, pod kterým běží Jellyfin. Kodi při tom nemusí běžet.
5. Po aktualizaci internetových kanálů se zobrazí knihovna Tipsport. Uživatel musí mít povolený přístup k TV i k této knihovně.

Pro použití přes **TV vysílání** přidejte M3U tuner s adresou z nastavení pluginu. Nastavte limit vstupů **1**, povolte **Allow stream sharing** a obnovte program. Adresa funguje jen uvnitř serveru; není určená do externího přehrávače.

Výchozí TV playlist obsahuje pouze oblíbený tým a volitelný testovací tenis. Jednotlivé právě živé zápasy lze přidat přepínačem v nastavení; připravované zápasy zůstávají v knihovně Tipsport, aby nevytvářely stovky neaktivních TV kanálů.

Při migraci nejprve ukončete původní testovací streamy. Tuner původního Kodi adaptéru přepněte na novou adresu, aby vedle sebe nezůstaly dva nezávislé zdroje. Původní instalaci lze ponechat jako vypnutou zálohu.

## Přihlášení a omezení

- Každý provozovatel používá vlastní účet a oprávnění ke sledování. Plugin nevykonává sázky ani jiné finanční operace.
- Plugin neprovádí automatické přihlášení heslem. Po vypršení relace je nutné ručně předat novou cookie.
- Neobchází způsobilost účtu, regionální omezení, ochranu DRM ani bezpečnostní výzvy poskytovatele.
- Používejte sdílený přenos podle podmínek služby a vlastních oprávnění. Plugin nepřidává vysílací licenci.
- Účet, který už sleduje přenos v Jellyfinu, současně nepoužívejte pro nezávislý přenos přes jinou aplikaci.
- Oblíbený tým funguje, jen pokud právě běží odpovídající zápas. Čas začátku se zobrazuje pouze tehdy, když ho lze z odpovědi poskytovatele skutečně přečíst.
- Výstup M3U zatím neobsahuje plnohodnotné XMLTV EPG. Nabídka a informace o zápasech jsou dostupné v knihovně Tipsport.
- Rozhraní poskytovatele není veřejně garantované a jeho změny mohou vyžadovat opravu pluginu.

## Bezpečnost a soukromí

Relace není součástí běžné konfigurace pluginu ani instalačního ZIPu. Ukládá se přes ASP.NET Data Protection do `Tipsport.private` vedle konfigurací pluginů. Na Windows jsou klíče navíc chráněné DPAPI uživatele serveru; na ostatních systémech musí zůstat soukromý adresář a jeho klíče dostupné jen účtu Jellyfinu. Přenos zálohy na jiný účet nebo počítač může vyžadovat nové přihlášení.

Správa relace a diagnostika vyžadují administrátora Jellyfinu. Interní playlist a datový stream odmítají síťová připojení mimo loopback. Běžný divák používá standardní ověřené přehrávání Jellyfinu. Cookie se posílá jen na `www.tipsport.cz` nebo `www.tipsport.sk`; sekundární poskytovatel ji nedostane. Plugin nezapisuje cookie, podepsané URL ani FFmpeg argumenty do logu.

Samotný Jellyfin může v diagnostice zapisovat přehrávací URL. Chraňte proto také jeho logy, zálohy a konfigurace. Pro vzdálené sledování použijte HTTPS nebo VPN a správně nastavte důvěryhodnou reverzní proxy.

## Sestavení a testy

Potřebujete .NET SDK 10.

```sh
dotnet test tests/Tipsport.Tests/Tipsport.Tests.csproj -c Release
dotnet build src/Jellyfin.Plugin.Tipsport/Jellyfin.Plugin.Tipsport.csproj -c Release
python3 tools/package.py
```

Výstup vznikne v `.artifacts`. Sestavení je nezávislé na konkrétní instalaci FFmpegu; plugin použije cestu nakonfigurovanou v serveru.

Testy pokrývají parsování programu bez skóre, výběr správného živého zápasu, více současných čtenářů, odmítnutí druhého zápasu, dokončení starého vstupu před spuštěním nového, uvolnění po posledním divákovi, šifrované uložení relace, omezení interních rozhraní a oprávnění správce. Síťové testy s reálným účtem a zařízeními jsou samostatný krok; jednotkové testy je nenahrazují.

## Stav vydání

Verze **0.1.1.0** přidává logo Komety a výběr HLS variant. Dne 1. 11. 2026 bylo na stejné Windows instalaci ověřeno přehrávání a diagnostika skutečného master playlistu: pro aktuální testovací tenis nabízel 512×288 a 320×180; zvoleno bylo 512×288 / 856 kbit/s. Jedná se o omezení této konkrétní nabídky, nikoli důkaz maximální kvality jiných zápasů. Automatická sada obsahuje 56 úspěšných testů, včetně pořadí variant, shodného rozlišení, odděleného zvuku a odmítnutí interních či nešifrovaných adres.

Verze **0.1.0.0** je experimentální první vydání. Dne 1. 11. 2026 bylo ověřeno načtení na Windows / Jellyfin 12.4, převzetí existující relace, skutečný tenisový přenos bez běžícího Kodi, souběh prohlížečů a Samsung Tizen TV a zachování nastavení po restartu. Automatická sada obsahuje 47 úspěšných testů. Kompatibilita se staršími Jellyfin 10.x a provoz na Linuxu nebyly ověřeny. Dlouhodobá stabilita vyžaduje další provozní zkušenosti.

Skript balení připraví také `.artifacts/manifest.candidate.json` s adresou konkrétního vydání a kontrolním součtem. Tento soubor se po úspěšném ověření a zveřejnění odpovídajícího ZIPu použije pro aktualizaci katalogu. Do veřejného repozitáře ani balíčku nepatří místní konfigurace, relace a přihlašovací údaje.

Projekt není spojený s Tipsportem ani oficiálně podporovaný projektem Jellyfin. Příspěvky a hlášení chyb posílejte bez cookies, hesel a podepsaných streamovacích URL.
