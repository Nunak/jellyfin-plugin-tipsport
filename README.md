# Tipsport pro Jellyfin

Experimentální, neoficiální serverový plugin pro **Jellyfin 12.4 / .NET 10**. Nabízí sportovní přenosy z vlastního přihlášeného účtu Tipsportu. Kodi, Python ani Selenium nejsou potřeba.

Verze **0.3.0.0** přidává přihlášení bez ručního hledání cookie: v samostatném okně na ploše serveru nebo zadáním údajů v HTTPS administraci i ze vzdáleného mobilu. Heslo lze volitelně šifrovaně uložit pro automatickou obnovu. Na Windows / Jellyfin 12.4 bylo ověřeno skutečné přihlášení z mobilního formuláře, obnova uložených údajů po restartu a živé přehrávání. Pokud si Tipsport vyžádá CAPTCHA nebo další ověření, vzdálený formulář je zatím neumí dokončit.

Verze **0.3.0.1** opravuje předčasné odpojování kvalitnějších přenosů při nárazovém přísunu HLS dat. Zachovává původní kvalitu a sdílení jednoho vstupu.

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
3. Bez předání hesla pluginu použijte **Přihlásit k Tipsportu**. Na ploše serveru se otevře samostatné okno Chrome nebo Edge. Přihlaste se v něm přímo k Tipsportu; plugin platnou relaci ověří, zašifrovaně uloží a okno zavře. Okno se otevírá na serveru, ne na vzdáleném telefonu správce; na Windows proto Jellyfin musí běžet v přihlášené uživatelské relaci.
4. Pro správu z mobilu otevřete nastavení pluginu přes **HTTPS**, vyplňte údaje Tipsportu a použijte **Přihlásit z tohoto zařízení**. Přihlášení provede server ve vlastním prohlížeči; na Windows používá běžné okno na přihlášené ploše serveru i při automatickém přihlášení. V mobilu se žádné vyskakovací okno neotevírá. **Šifrovaně uložit pro automatické přihlášení** je výslovná, ve výchozím stavu vypnutá volba. Údaje se uloží až po ověřeném úspěchu. Pokud je neuložíte, pro další přihlášení je budete muset zadat znovu. CAPTCHA, další ověření nebo změna webu mohou vyžadovat ruční dokončení na serveru; neobcházejí se.
5. Převzetí staré relace z Kodi a ruční import zůstávají pod **Pokročilá správa relace** pro migraci. Přihlášení vyžaduje nainstalovaný Chrome/Edge a systémový curl; plugin další software nestahuje ani neinstaluje. Desktopový přihlašovací postup zatím kontejner bez plochy nepodporuje.
6. Po aktualizaci internetových kanálů se zobrazí knihovna Tipsport. Uživatel musí mít povolený přístup k TV i k této knihovně.

Pro použití přes **TV vysílání** přidejte M3U tuner s adresou z nastavení pluginu. Nastavte limit vstupů **1**, povolte **Allow stream sharing** a obnovte program. Adresa funguje jen uvnitř serveru; není určená do externího přehrávače.

Výchozí TV playlist obsahuje pouze oblíbené týmy a volitelný testovací tenis. Jednotlivé právě živé zápasy lze přidat přepínačem v nastavení; připravované zápasy zůstávají v knihovně Tipsport, aby nevytvářely stovky neaktivních TV kanálů.

Při migraci nejprve ukončete původní testovací streamy. Tuner původního Kodi adaptéru přepněte na novou adresu, aby vedle sebe nezůstaly dva nezávislé zdroje. Původní instalaci lze ponechat jako vypnutou zálohu.

## Přihlášení a omezení

- Každý provozovatel používá vlastní účet a oprávnění ke sledování. Plugin nevykonává sázky ani jiné finanční operace.
- Po HTTP 401 plugin zkusí obnovu přes vlastní profil prohlížeče, případně s výslovně uloženými údaji. Obnovování sdílí mezi souběžnými požadavky a po neúspěchu má pětiminutovou prodlevu. Bez hesla trvá nejvýše 35 sekund, s heslem 75 sekund plus ukončení prohlížeče. Původní požadavek opakuje nejvýše jednou. Heslo odešle ve formuláři nejvýše jednou; po neúspěšném automatickém pokusu jeho další použití pozastaví i přes restart serveru. Správce zkontroluje údaje a přihlásí se znovu. CAPTCHA, další ověření a souhlasy musí dokončit člověk přímo u Tipsportu. Obnova není slib neomezené platnosti přihlášení.
- Tlačítko **Obnovit uložené přihlášení** spouští stejnou omezenou obnovu ručně, také ze vzdáleného mobilu. Nepotřebuje nové zadání hesla; používá pouze dříve ověřený profil nebo údaje spojené s aktuální relací. Respektuje probíhající přihlášení i prodlevu mezi pokusy. Je-li uložené heslo pozastavené, výslovné stisknutí dovolí jeden nový pokus; úspěch znovu aktivuje automatickou obnovu. Na pozadí se pozastavené heslo neopakuje.
- Volitelně zapamatované heslo je odděleně šifrované v `credentials.protected`, svázané s ověřenou relací a zemí účtu. Změna účtu ručním importem jej neaktivuje pro jinou relaci. Stavové API heslo ani jméno nevrací; nepoužívají se v argumentech procesu, diagnostice ani běžné XML konfiguraci. Tlačítko **Odebrat uložené heslo** zachová aktuálně funkční relaci. Správce serveru a proces běžící pod účtem Jellyfinu mohou chráněné údaje používat; šifrování není ochrana před kompromitovaným serverem.
- Neobchází způsobilost účtu, regionální omezení, ochranu DRM ani bezpečnostní výzvy poskytovatele.
- Používejte sdílený přenos podle podmínek služby a vlastních oprávnění. Plugin nepřidává vysílací licenci.
- Účet, který už sleduje přenos v Jellyfinu, současně nepoužívejte pro nezávislý přenos přes jinou aplikaci.
- Oblíbený tým funguje, jen pokud právě běží odpovídající zápas. Čas začátku se zobrazuje pouze tehdy, když ho lze z odpovědi poskytovatele skutečně přečíst.
- XMLTV zdroj je na `/Plugins/Tipsport/guide.xml` (pouze loopback). Adresa je v nastavení pluginu. V **TV vysílání → Zdroje programu** přidejte XMLTV s touto adresou a obnovte program. Kanály mají stejné `tvg-id` jako M3U; oblíbené týmy obsahují odpovídající zápasy, pokud poskytovatel vrátí skutečný čas začátku. Bez známého začátku se pořad nevytváří. Jellyfin zahazuje pořady bez času konce, proto je konec v programu označený jako orientační: ve výchozím nastavení 180 minut od začátku, nastavitelné na 30–480 minut. Tento odhad neomezuje skutečnou délku přehrávání. Dostupnost programu se proto může lišit podle sportu.
- Rozhraní poskytovatele není veřejně garantované a jeho změny mohou vyžadovat opravu pluginu.

## Bezpečnost a soukromí

Přihlašovací okno používá pouze vlastní profil `Tipsport.private/browser-profile`, nikdy běžný profil prohlížeče správce. Na Windows je nový profil přístupný pouze účtu serveru, SYSTEM a místním správcům; na Unixu má režim 0700. Komunikace s tímto oknem používá náhodný port na loopbacku; endpoint nelze zadat zvenku a jeho adresa, cookies ani obsah profilu se nevracejí přes API nebo diagnostiku. Postup vychází z [doporučené izolace profilů Chrome](https://developer.chrome.com/blog/remote-debugging-port). Přihlášení lze spustit pouze s administrátorským oprávněním Jellyfinu. Žádná oprávnění existujících souborů relace se automaticky nerozšiřují.

Převzetí relace vyžaduje současně viditelné přihlášené ovládání účtu na webu Tipsportu a úspěšné načtení programu. Samotná funkční nabídka nestačí: program může být dostupný i s anonymní cookie. Chybová stránka poskytovatele přihlášení zastaví; heslo se na ní neodesílá. Průběh a chyba se zobrazují přímo pod mobilním přihlašovacím tlačítkem.

Při neúspěšném uložení zůstává předchozí relace zachovaná a administrace zobrazí konkrétní zprávu o oprávnění úložiště. Účet Jellyfinu musí mít možnost měnit vlastní soubor relace. Soubory předané z instalace spuštěné jako správce mohou vyžadovat jednorázovou opravu vlastnictví; plugin tuto ochranu neobchází.

Relace není součástí běžné konfigurace pluginu ani instalačního ZIPu. Ukládá se přes ASP.NET Data Protection do `Tipsport.private` vedle konfigurací pluginů. Na Windows jsou klíče navíc chráněné DPAPI uživatele serveru; na ostatních systémech musí zůstat soukromý adresář a jeho klíče dostupné jen účtu Jellyfinu. Přenos zálohy na jiný účet nebo počítač může vyžadovat nové přihlášení.

Správa relace a diagnostika vyžadují administrátora Jellyfinu. Interní playlist a datový stream odmítají síťová připojení mimo loopback. Běžný divák používá standardní ověřené přehrávání Jellyfinu. Cookie se posílá jen na `www.tipsport.cz` nebo `www.tipsport.sk`; sekundární poskytovatel ji nedostane. Plugin nezapisuje cookie, podepsané URL ani FFmpeg argumenty do logu.

Samotný Jellyfin může v diagnostice zapisovat přehrávací URL. Chraňte proto také jeho logy, zálohy a konfigurace. Pro vzdálené sledování použijte HTTPS nebo VPN a správně nastavte důvěryhodnou reverzní proxy.

## Sestavení a testy

Potřebujete .NET SDK 10; pro kontrolu přihlašovacích skriptů také Node.js 22 nebo novější.

```sh
dotnet test tests/Tipsport.Tests/Tipsport.Tests.csproj -c Release
dotnet build src/Jellyfin.Plugin.Tipsport/Jellyfin.Plugin.Tipsport.csproj -c Release
node tools/test-login-form.mjs
python3 tools/package.py
```

Výstup vznikne v `.artifacts`. Sestavení je nezávislé na konkrétní instalaci FFmpegu; plugin použije cestu nakonfigurovanou v serveru.

Testy pokrývají parsování programu bez skóre, výběr správného živého zápasu, více současných čtenářů, odmítnutí druhého zápasu, dokončení starého vstupu před spuštěním nového, uvolnění po posledním divákovi, šifrované uložení relace, omezení interních rozhraní a oprávnění správce. Síťové testy s reálným účtem a zařízeními jsou samostatný krok; jednotkové testy je nenahrazují.

## Stav vydání

Verze **0.3.0.1** zvětšuje rezervu pro nárazově stahované HLS segmenty. Původní fronta odpojovala čtenáře po 128 blocích; velikost bloků z pipe se může lišit a tento limit nestačil pro kvalitnější přenosy čtené Jellyfinem v reálném čase. Nová fronta má dva pevné limity: 16 MiB a 4096 bloků na čtenáře. Čtení rozpočet průběžně uvolňuje. Trvale zaseknutý čtenář se stále odpojí, neblokuje ostatní a nezpůsobuje další vstup od poskytovatele. Do diagnostiky přibyl počet těchto odpojení a nejvyšší pozorované zaplnění; varování v logu neobsahuje adresy ani identifikátory zápasů. Kvalita, kodeky, volby iOS i přihlášení se nemění. Regresní test s dávkou 700 bloků po 8 KiB původní kód odpojil; opravený kód přijme celou dávku. Všech 199 testů a kontrola přihlašovacích skriptů prošly. Dne 2. 11. 2026 byla tato DLL načtena na Windows / Jellyfin 12.4 a stejný dříve selhávající florbal Vary Bohemians–Vítkovice hrál v prohlížeči déle než čtyři minuty; druhý přehrávač současně déle než tři minuty. Oba hrály v 1920×1080 / 6275 kbit/s s kopírováním obrazu i zvuku, přes jediný vstup, bez obnovení zdroje a s nulovým počtem odpojení kvůli frontě. Nejvyšší pozorované zaplnění bylo 4,0 MiB, tedy více než i maximální velikost původní fronty 128 × 24064 bajtů. Jde o několikaminutový provozní test, nikoli ověření celého zápasu nebo všech televizorů.

Verze **0.3.0.0**: všech 195 testů .NET a samostatná kontrola skutečných přihlašovacích skriptů v syntetickém DOM prošly. Kontroly zahrnují odmítnutí anonymní relace, ochranu před přepsáním novějšího přihlášení, vazbu uloženého hesla na účet, HTTPS a administrátorská oprávnění, pozastavení po neúspěchu, sdílení obnovy i skryté ověřovací prvky a počáteční načítání stránky. Dne 2. 11. 2026 bylo na Windows / Jellyfin 12.4 ověřeno přihlášení z formuláře na iPhonu, šifrované uložení opt-in údajů, nové přihlášení pomocí uloženého hesla po restartu, následné spuštění stejné cesty obnovy jako při HTTP 401 a skutečné přehrávání A. Molčan–Droguet v prohlížeči (512×288). Nejde o dlouhodobý test vypršení všech relací ani o ověření každého účtu, CAPTCHA, Linuxu či kontejnerů. Nastavení kompatibility přehrávání iOS zůstává zachované.

Verze **0.2.3.1** sdílí analýzu metadat mezi položkami stejného běžícího vstupu. Výsledky se kopírují, aby úpravy dat pro jednoho klienta neovlivnily ostatní; cache se nepoužije po změně vstupu, při obnově nebo po dvou minutách. V diagnostice přibyly počty využití cache a nových analýz.

Volba „Kompatibilní živé přehrávání v iOS“ pomáhá při potížích se startem živého HLS. Pouze pro klienty rozpoznané jako iPhone/iPad a interní zdroj H.264/AAC nastaví segmentový kontejner MPEG-TS místo fMP4. Nemění datový tok, rozlišení, délku segmentů ani povolení kopírovat obraz a zvuk. Filmy, jiné zdroje, kodeky a klienti se nemění. Výchozí nastavení je vypnuté; při potížích s iOS jej správce zapne v nastavení pluginu. iPad používající desktopový user-agent není rozpoznán.

Ověření 0.2.3.1: všech 140 testů prošlo. Po instalaci na Windows / Jellyfin 12.4 uživatel potvrdil přehrávání živého přenosu v aplikaci Jellyfin iOS 1.8 přes mobilní data po dobu nejméně dvou minut a následně potvrdil, že aplikace dále hraje. Předchozí pokusy s fMP4 se na mobilních datech nerozběhly v aplikaci ani Safari, zatímco filmy a domácí Wi-Fi fungovaly. Ve fungujícím pokusu byl příkaz FFmpeg ověřen jako `-hls_segment_type mpegts`, `-codec:v:0 copy`, `-codec:a:0 copy`, s původním zdrojem 854×480 / přibližně 2179 kbit/s a šestisekundovými segmenty. Nebyl použit dočasný limit datového toku. Nejde o kontrolovaný test stejného zápasu ve všech režimech ani o potvrzení všech poskytovaných kodeků; Samsung TV nebyla v tomto vydání znovu testována.

Při obnově katalogu se staré karty výslovně skončených zápasů označí „SKONČENO“. Zápasy chybějící v aktuálním úplném programu dostanou „MIMO NABÍDKU“, bez domýšlení konce podle času. Zůstanou zachované odkazy, zamčená metadata i právě běžící přenos. Pokud se zápas vrátí do programu, běžná metadata se obnoví.

Volba „Kratší HLS segmenty pro rychlejší start“ nastavuje požadovanou délku segmentů na tři sekundy pouze u autentizovaného přehrávání knihovny Tipsport a přesně rozpoznaného interního zdroje. Zachovává kratší volbu klienta, minimální počet segmentů, parametry přihlášení, kodeky, kontejner i datový tok. Samostatného M3U tuneru ani filmů se nedotýká. Výchozí nastavení je vypnuté; provozní přínos musí potvrdit konkrétní klient. Dlouhý interval klíčových snímků může skutečné segmenty prodloužit i nad požadovanou délku. Při provozním testu byly hlášené výpadky a volba byla vypnuta; příčina výpadků nebyla jednoznačně přiřazena délce segmentů. Ověřený mobilní pokus výše ji nepoužíval.

Obnova po tvrdém restartu Apache tímto vydáním není vyřešená. Serverový plugin nemůže znovu otevřít přehrávač, který po síťové chybě přehrávání ukončil. Pro běžné změny konfigurace Apache použijte graceful reload, který nechává probíhající požadavky doběhnout: https://httpd.apache.org/docs/2.4/stopping.html. Obnova skutečného výpadku vyžaduje podporu klienta.

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
