# Tipsport pro Jellyfin

Experimentální, neoficiální serverový plugin pro **Jellyfin 12.4 / .NET 10**. Nabízí sportovní přenosy z vlastního přihlášeného účtu Tipsportu. Kodi, Python ani Selenium nejsou potřeba.

## Co umí

- Knihovna Tipsport: živé zápasy, připravované zápasy a složky podle sportu, navíc **01 · Oblíbené** a **02 · Dnes**. Dnešní den se počítá podle Europe/Prague; probíhající přenosy jsou zahrnuté i bez času začátku.
- V seznamu zápasů řadí oblíbené týmy před ostatní, poté podle známého začátku; neznámé časy jsou poslední. Používá nativní metadata řazení Jellyfinu a při prvním importu je doplní během obnovy metadat. Vlastní řazení vybrané klientem má přednost.
- Přenosy s výslovným příznakem ukončení od poskytovatele skryje; samotný minulý čas začátku nepovažuje za konec zápasu. Položky stažené z aktuálního programu odstraní Jellyfin při obnovení katalogu.
- Rozlišuje plánovaný začátek, výslovně ukončený přenos, chybějící relaci a přenos stažený z nabídky. Odmítnutí služby nemusí znamenat vypršení přihlášení. Zobrazení detailu chyby přehrávání závisí na klientovi Jellyfinu.
- Odkazy na jednotlivé zápasy zůstávají stejné při přechodu do živého vysílání i při aktualizaci náhledu. Existující odkazy z verze 0.2.0 se zachovají; zamčené vlastní názvy a popisy se nepřepisují. Migrační mapa odkazů se ukládá do `Tipsport.private/links.json`.
- Stálé položky až pro 20 oblíbených týmů, například Kometu. Původní položka i její TV identifikátor zůstávají zachované.
- V každé složce přehled **00 · PRÁVĚ SE SLEDUJE** s názvem běžícího zápasu; lze se přes ni připojit ke stejnému vstupu. Jeho název ukazuje obsazený zápas nebo volný vstup. Uvnitř lze otevřít přenos a v detailu zjistit poslední naměřenou kvalitu.
- Vlastní široké náhledy 16:9: jména soupeřů přímo v obrázku, stav ŽIVĚ/PŘIPRAVUJEME, sport a známý čas začátku. Zápasy Komety doplní klubovým logem načteným z webu klubu; při jeho nedostupnosti zůstane čitelný název. Renderování používá SkiaSharp dodávaný Jellyfinem.
- Při konci vstupu, chybě čtení nebo 20 sekundách bez dat nejprve ukončí starý proces a nejvýše třikrát obnoví stejný zápas s nově získanou adresou. Připojení diváci zůstávají zachovaní; konečné přehrání přechodu závisí na toleranci přehrávače.
- Administrace obnovuje přehled po 15 sekundách: aktuální zápas, připojení Jellyfinu, obnovování, kvalita a poslední chyba. Odmítnutí požadavku upozorní na nutnou kontrolu relace; plugin nerozlišuje automaticky vypršení cookie od nedostupnosti služby nebo chybějícího oprávnění.
- Pro oblíbený tým `Kometa` nebo `HC Kometa Brno` načítá oficiální klubové logo (také přes `tvg-logo` v M3U).
- Volitelný výstup M3U pro standardní TV vysílání Jellyfinu.
- Jeden vstup od poskytovatele, který může sdílet více místních přehrávačů Jellyfinu.
- Když už běží jiný zápas, nový nezávislý vstup odmítne. Stejný zápas z položky týmu i ze seznamu používá stejný vstup.
- Převod HLS do MPEG-TS přes FFmpeg dodaný s Jellyfinem, pomocí `-c copy`. Překódování pro konkrétní zařízení dál zajišťuje Jellyfin.
- Ve výchozím stavu vybírá nejlepší dostupnou kvalitu; každý divák může snížit výstupní kvalitu ve svém přehrávači Jellyfinu. Volitelný limit rozlišení zdroje v administraci platí společně pro celý sdílený vstup až při jeho příštím otevření. Z nabízených variant vybere nejlepší pod limitem; pokud taková varianta chybí nebo kvalitu nelze ověřit, požádá o zvýšení limitu místo jeho tichého překročení. U odděleného zvuku vytvoří dočasný lokální master playlist se zvoleným videem a zvukovými stopami. Tento playlist je pouze v paměti, dostupný jen přes loopback a není součástí diagnostiky.
- Tlačítko **Stáhnout diagnostiku** v administraci stáhne JSON s verzí, počty připojení, rozlišeními a časy kontrol. Export používá výslovný seznam údajů: neobsahuje cookies, relaci, názvy týmů, sledované zápasy, adresy serveru, cesty ani podepsané URL. Nic automaticky neodesílá.
- Z HLS master playlistu vybírá nejvyšší dostupné rozlišení a při shodě vyšší datový tok. Při automatické kvalitě u odděleného zvuku nebo nepřístupného playlistu ponechá výběr FFmpegu. Diagnostika pluginu ukazuje nabídnuté varianty, bez podepsaných adres. Nízké rozlišení samotného zdroje plugin nenavyšuje.
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

Výchozí TV playlist obsahuje pouze oblíbené týmy a volitelný testovací tenis. Jednotlivé právě živé zápasy lze přidat přepínačem v nastavení; připravované zápasy zůstávají v knihovně Tipsport, aby nevytvářely stovky neaktivních TV kanálů.

Při migraci nejprve ukončete původní testovací streamy. Tuner původního Kodi adaptéru přepněte na novou adresu, aby vedle sebe nezůstaly dva nezávislé zdroje. Původní instalaci lze ponechat jako vypnutou zálohu.

## Přihlášení a omezení

- Každý provozovatel používá vlastní účet a oprávnění ke sledování. Plugin nevykonává sázky ani jiné finanční operace.
- Plugin neprovádí automatické přihlášení heslem. Po vypršení relace je nutné ručně předat novou cookie.
- Neobchází způsobilost účtu, regionální omezení, ochranu DRM ani bezpečnostní výzvy poskytovatele.
- Používejte sdílený přenos podle podmínek služby a vlastních oprávnění. Plugin nepřidává vysílací licenci.
- Účet, který už sleduje přenos v Jellyfinu, současně nepoužívejte pro nezávislý přenos přes jinou aplikaci.
- Oblíbený tým funguje, jen pokud právě běží odpovídající zápas. Čas začátku se zobrazuje pouze tehdy, když ho lze z odpovědi poskytovatele skutečně přečíst.
- XMLTV zdroj je na `/Plugins/Tipsport/guide.xml` (pouze loopback). Adresa je v nastavení pluginu. V **TV vysílání → Zdroje programu** přidejte XMLTV s touto adresou a obnovte program. Kanály mají stejné `tvg-id` jako M3U; oblíbené týmy obsahují odpovídající zápasy, pokud poskytovatel vrátí skutečný čas začátku. Bez známého začátku se pořad nevytváří. Jellyfin zahazuje pořady bez času konce, proto je konec v programu označený jako orientační: ve výchozím nastavení 180 minut od začátku, nastavitelné na 30–480 minut. Tento odhad neomezuje skutečnou délku přehrávání. Dostupnost programu se proto může lišit podle sportu.
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

Verze **0.2.2.0** (stabilní vydání) přidává správci a bezpečnému diagnostickému exportu dobu získání adresy, čas prvních dat po otevření vstupu, dobu analýzy, stáří posledních dat a velikost omezené paměti pro připojení diváků. Tyto hodnoty neměří celkové zpoždění proti stadionu nebo Tipsportu. Volitelný experimentální start HLS od druhého posledního kompletního segmentu snižuje vstupní rezervu bez změny kvality. Ve výchozím stavu je vypnutý; platí pouze pro rozpoznané adresy playlistů a až při novém spuštění vstupu. Při nestabilní síti ponechte běžné nastavení. Sada 110 testů prošla. Na NUNAK-001 / Windows / Jellyfin 12.4 bylo ověřeno načtení verze i skutečný export měření. Běžný přenos České Budějovice–Karlovy Vary měl získání zdroje 242 ms, první data 430 ms, analýzu 763 ms a orientační start prohlížeče kolem 10 s. V režimu blíž živému okraji hrál Vítkovice–Třinec v 854×480 / 2179 kbit/s, s měřením 223 ms / 418 ms / 690 ms; zdroj zůstal sdílený i při připojení druhého diváka. Uživatel potvrdil přehrávání v aplikaci iOS přes opravené jellyfin.nuni.cz se startem přibližně 17 s. Nejde o kontrolovaný benchmark: příprava bufferu a celkové zpoždění nebyly odděleně změřeny. Režim blíž živému okraji byl po testu uložený zpět na vypnuto, bez přerušení aktivního zdroje. Samsung TV na této verzi ještě nebyl ověřen. Verze 0.2.2.0 byla na výslovný pokyn provozovatele povýšena na stabilní vydání a zařazena do automatického katalogu. Omezení ověření uvedená výše zůstávají platná.

Obnova přenosu po výpadku Tipsportu zůstává omezená třemi pokusy se zachováním čtenářů. Obnova po výpadku Apache závisí i na chování aplikace/přehrávače; serverový plugin sám nemůže přikázat aplikaci, aby znovu otevřela ukončené přehrávání. Tato klientská část zůstává otevřená a není vydávána za opravenou.

Verze **0.2.1.2** předává Jellyfinu datový tok vybrané HLS varianty včetně zvuku. Zkracuje úvodní analýzu společného vstupu FFmpegu na jednu sekundu, průběžně vyprazdňuje výstup a zkouší nejprve sekundovou analýzu metadat; pokud chybí úplné údaje o obrazu nebo zvuku, zopakuje původní třísekundovou analýzu. Zachovává kopírování původního obrazu a zvuku. Čas analýzy zapisuje bez adres a přihlašovacích údajů. Automatická sada obsahuje 103 úspěšných testů včetně záložní analýzy a zrušení požadavku.

Dne 1. 11. 2026 byla verze 0.2.1.2 načtena na Windows / Jellyfin 12.4 a přehrávala Kometu v 854×480 / 2179 kbit/s přes sdílený vstup. Analýza metadat trvala 824 ms; jeden orientační test připojení prohlížeče zobrazil obraz za přibližně 9–10 sekund oproti přibližně 12 sekundám před úpravou. Nejde o kontrolovaný benchmark. Uživatel při následném testu Samsung TV naměřil přibližně 23 sekund; hlavní čekání na TV tedy toto vydání neodstranilo. Zpoždění proti původnímu Tipsportu nebylo změřeno. Uživatel zprovoznil iPhone vypnutím „Force transcoding of remote media sources such as Live TV“; tuto volbu pro kompatibilní zdroj ponechte vypnutou. Plošný limit účtu ani přepis transcoding URL plugin nezavádí. Obnova přehrávače po restartu reverzní proxy není tímto vydáním vyřešená.

Verze **0.2.1.0** přidává složky Oblíbené a Dnes, řazení zápasů, přesnější dostupnost, volitelný společný limit zdroje při zachování automatické nejlepší kvality a export diagnostiky z povoleného seznamu údajů. Prochází 96 automatických testů. Na Windows / Jellyfin 12.4 byly ověřené nové složky, zachovaný odkaz Komety a skutečné přehrávání hokeje BIK Karlskoga–Östersund s limitem 360p: ze zdrojem nabízených 768×432 a 512×288 vybral 512×288 / 510 kbit/s včetně odděleného zvuku. Výchozí nastavení bylo po testu vráceno na nejlepší automatickou kvalitu; po finálním restartu tentýž přenos skutečně hrál v 768×432. Ověřen byl i náhled skutečného diagnostického JSON bez přihlašovacích údajů a Kometa na prvním místě hokejového seznamu.

Verze **0.2.0.1** opravuje přímé odkazy na zápasy: při přechodu z připravovaného do živého vysílání a změně náhledu zůstává identifikátor stejný. Zachovává existující položky z 0.2.0 a aktualizuje jejich název, popis i obrázek přes rozhraní Jellyfinu; respektuje zamčená metadata. Automatická sada obsahuje 80 úspěšných testů včetně migrace odkazu, restartu a změny stavu. Na Windows / Jellyfin 12.4 bylo ověřeno načtení verze, zachování odkazu na Kometu s novým obrázkem a souběžné přehrávání Pardubice–Mladá Boleslav ve dvou prohlížečích přes jediný vstup (854×480 / 2179 kbit/s), bez nového vstupu po odpojení jednoho diváka. Uživatel potvrdil přehrávání také na Samsung TV. Jde o několikaminutový provozní test, nikoli test celého zápasu. Odkazy odstraněné při přechodu z 0.1 se zpětně neobnovují.

Po aktualizaci obnovte nabídku Tipsportu. Stálá položka oblíbeného týmu a její TV identifikátor zůstávají zachované.

Verze **0.2.0.0** rozšiřuje přehled běžícího přenosu, více týmů, široké náhledy, XMLTV a omezenou automatickou obnovu přenosu. Automatické testy (74) pokrývají i zachování čtenářů po výpadku, dokončení starého procesu před obnovou, ukončení po odchodu diváků, limit opakování, stabilní identifikátory týmů a XMLTV se známými začátky a výslovně označeným odhadem konce. Na Windows / Jellyfin 12.4 bylo ověřeno načtení, přehrávání tenisu v prohlížeči (zdroj 704×396), viditelný název obsazeného vstupu, dynamické náhledy soupeřů s logem Komety a zachování konfigurace po restartu. Samostatný integrační test ověřuje čtení XMLTV skutečnou knihovnou Jellyfin.XmlTv. Tato sada testů nenahrazuje dlouhodobý test ani test každého modelu TV.

Verze **0.1.1.0** přidává logo Komety a výběr HLS variant. Dne 1. 11. 2026 bylo na stejné Windows instalaci ověřeno přehrávání a diagnostika skutečného master playlistu: pro aktuální testovací tenis nabízel 512×288 a 320×180; zvoleno bylo 512×288 / 856 kbit/s. Jedná se o omezení této konkrétní nabídky, nikoli důkaz maximální kvality jiných zápasů. Automatická sada obsahuje 56 úspěšných testů, včetně pořadí variant, shodného rozlišení, odděleného zvuku a odmítnutí interních či nešifrovaných adres.

Verze **0.1.0.0** je experimentální první vydání. Dne 1. 11. 2026 bylo ověřeno načtení na Windows / Jellyfin 12.4, převzetí existující relace, skutečný tenisový přenos bez běžícího Kodi, souběh prohlížečů a Samsung Tizen TV a zachování nastavení po restartu. Automatická sada obsahuje 47 úspěšných testů. Kompatibilita se staršími Jellyfin 10.x a provoz na Linuxu nebyly ověřeny. Dlouhodobá stabilita vyžaduje další provozní zkušenosti.

Skript balení připraví také `.artifacts/manifest.candidate.json` s adresou konkrétního vydání a kontrolním součtem. Tento soubor se po úspěšném ověření a zveřejnění odpovídajícího ZIPu použije pro aktualizaci katalogu. Do veřejného repozitáře ani balíčku nepatří místní konfigurace, relace a přihlašovací údaje.

Projekt není spojený s Tipsportem ani oficiálně podporovaný projektem Jellyfin. Příspěvky a hlášení chyb posílejte bez cookies, hesel a podepsaných streamovacích URL.
