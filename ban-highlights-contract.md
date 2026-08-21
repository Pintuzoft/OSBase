# Ban-klipp automatiskt: sajtens sida av kontraktet

Svar på §36 i `private/ASK_ Automated CS2 Ban Evidence - Highlight System.md`
("First task for you"): vad hemsidan har i dag, vad den saknar, och exakt vilket
schema och vilka endpoints jag föreslår att den får.

Bakgrunden i en mening: admins orkar inte längre spela in med ShadowPlay, och en
bann utan klipp är en rad som säger att någon fuskat utan att visa det.

**Reviderat två gånger 2026-08-09**, efter ägarens förtydliganden. Fyra beslut
styr resten av dokumentet:

1. **Sajten är enda skrivaren.** Indexeraren och renderworkern skriver aldrig
   direkt i databasen — allt går genom ett internt API. Det gäller även när
   indexeraren kör på samma maskin: det är en processgräns, inte en maskingräns.
2. **Batchat, inte per händelse.** En färdiganalyserad demo skickas som ETT
   analysresultat. Tiotusen kills får aldrig bli tiotusen anrop.
3. **Tre kandidater, och alla tre renderas när de finns.** ChatGPT föreslog att
   bara #1 skulle renderas för att spara GPU-tid. Ägaren avgjorde tvärtom, och
   argumentet bär: *"kör vi 3 så är det högre chans vi får ett klipp där fusket
   syns."* Ett klipp som missar fusket är värt noll, och att rendera #2 i
   efterhand kostar en runda genom en människa plus en väntan till. GPU:n är vår
   egen; adminens tid är det som är ont om. Talet är ändå config, för
   Windowsburken kan behöva andas.
4. **Ingenting om tid hårdkodas.** Varken lookback, väntfönster eller
   hållbarhet — allt är config, för ingen av dem är känd.

---

## 1. Hur bans sparas i dag

**De bor inte hos oss.** `sa_bans` ligger i CS2-SimpleAdmins egen databas, som
sajten når genom `SimpleAdminDatabase` — en andra PDO-anslutning, konfigurerad
för sig. Vi läser fritt och skriver bara det som är vårt att skriva.

`SimpleAdminBan` (`src/Domain/SimpleAdminBan.php`) bär raden:

```
id  playerName  playerSteamId  playerIp  adminName  adminSteamId
reason  durationMinutes  createdAt  endsAt  serverId  status
```

Två fällor som redan är hanterade i modellen och som ett API måste bevara:
`duration` är **minuter** med 0 = permanent, och för en permanent bann skrivs
`ends = created` — läser man `ends` bokstavligt ser varje permanent bann ut som
sedan länge utgången.

Vad sajten skriver till `sa_bans` i dag (`SimpleAdminBanRepository`):

| metod | skriver |
|---|---|
| `updateDetails()` | `player_name`, `player_ip`, `reason`, och adminparet `admin_name` + `admin_steamid` |
| `claimRecentBan()` | fyller nick och admin på en färsk RCON-bann som ännu är omärkt |
| `unban()` / `makePermanent()` | status respektive längd |

**Adminnamnet går bara att sätta som par med SteamID.** `canBeLiftedBy()` avgör
rätten att häva utifrån `admin_steamid`, så ett namn utan matchande id vore en
uppgift som ser sann ut och styr ingenting. Roboten ska aldrig röra adminfälten.

---

## 2. Klippen finns redan, och gör mer än man tror

`ban_clip` (`database/migrations/0254_ban_clip.sql`) är vår tabell, nycklad på
`sa_bans.id` — ingen främmande nyckel, eftersom bannen bor i en annan databas.

Kedjan som redan är byggd och körs i produktion:

```
POST /admin/bans/{id}/klipp/start      → rad i ban_clip, status "uploading"
POST /admin/bans/{id}/klipp/{clip}/bit → 4 MB rå kropp, läggs på slutet
POST /admin/bans/{id}/klipp/{clip}/klar→ status "queued"
bin/ban-clip.php (systemd)             → ffmpeg → "ready"
GET  /admin/bans/{id}/klipp/status     → läget, för sidan som väntar
```

Statusen bor i databasen just för att arbetet tar minuter:
`uploading → queued → transcoding → ready → failed`, med `error` på svenska.

**Bitvis av en anledning som gäller renderworkern lika mycket som webbläsaren:**
en bit på 4 MB ryms under PHP:s förvalda `post_max_size` (8 MB), så en 3 GB-fil
går igenom utan att någon serverinställning ändras. Biten skickas som **rå
kropp** och inte som filuppladdning, eftersom `upload_max_filesize` bara gäller
det senare. PowerShell kan använda exakt samma protokoll som webbläsaren.

`ClipStorage` håller två kataloger: halvfärdiga filer **utanför** webbroten,
färdiga klipp **i** webbroten under `/banklipp` med slumpat filnamn — en video
som strömmas genom PHP kan inte besvara byteintervall, och utan dem går det inte
att spola.

---

## 2b. Demoerna finns redan på webbservern, och sajten kan dem

Det här visste jag inte när jag skrev första utkastet, och det tar bort en hel
öppen fråga: delningen ligger monterad över NFS och är symlänkad in i docroten,
så webbservern läser filerna **lokalt**.

```
/var/www/oldswedes.se/public/demofiles/     DEMOS_PATH, config/app.php
https://oldswedes.se/demofiles/…            samma filer, serverade av Apache
```

Indexeraren behöver alltså ingen SMB-montering. Renderworkern på Windows når
samma filer antingen över SMB eller på URL:en ovan — den senare är redan öppen,
eftersom `/demofiles` serveras av Apache direkt (en demo på tiotals megabyte har
inget i en PHP-process att göra).

`DemoLibrary` (`src/Services/DemoLibrary.php`) driver `/demos`-sidan i dag och
bär tre saker den nya indexeraren ska ärva i stället för att lära sig om:

- **`.dem.gz` är normalfallet, inte undantaget.** Inspelaren skriver `.dem` och
  något gzippar den efteråt. En regel som bara känner `.dem` hittar ingenting i
  mappar som är fulla.
- **Datumet ligger i filnamnet, och det finns två konventioner:**
  `auto-20240329-045030-…` från den gamla inspelaren och `20260727-201827-…`
  från den som kör nu. Bara datumdelen är de överens om. Det gör "hämta
  kvällens demos" till en namnjämförelse.
- **`stat()` är dyrt över NFS.** Sidan sorterar med flit på namnet och inte på
  mtime, för mappen med LAN-demos innehåller sjuttontusen filer. En indexerare
  som statar allt varje varv gör webbservern långsam varje kväll.

Vad som INTE finns: någon tabell över demos. `/demos` läser katalogen när någon
öppnar sidan, en nivå i taget, och recursar aldrig. Indexet i §5.2 är alltså nytt
och kolliderar inte med något.

---

## 2c. Vad monthly_highlights faktiskt gör — läst, inte antaget

Ägaren skickade den dokumenterade koden 2026-08-09
(`private/monthly_highlights_documented.zip`). Två saker i asken visade sig vara
fel om verkligheten, och båda är värda att skriva ned innan någon bygger på dem.

### Rättelse 1: `record.jsonc` används inte längre

Asken §24 vill återanvända `record.jsonc`. Den nuvarande renderaren gör inte det
— den anropar CS Demo Managers CLI direkt:

```
csdm analyze <stage-katalog> --source valve --force
csdm video <demo> <start_tick> <end_tick> --focus-player <SteamID64>
           --recording-system HLAE --encoder-software FFmpeg …
```

Handoff-dokumentet är uttryckligt: *"Do not replace this with an older
record.jsonc implementation unless there is an explicit reason."* Bann-kedjan ska
alltså gå samma väg.

Och `--focus-player <SteamID64>` **är** POV-kravet i §10.4. Det är inte en
inställning som behöver byggas — den är redan vägen renderaren fungerar på.

### Rättelse 2: beteendeanalysen finns inte

Asken beskriver aimbot-, spinbot-, triggerbot- och wallhack-analys i detalj.
Ingen av dem är implementerad. README:t säger rakt ut att analysen är
**händelsebaserad** och att tick-för-tick-analys av siktvinklar vore ett nytt
lager.

Det som finns är kandidatfamiljer ur spelets egna händelser:

```
fast_multi_kill        special_weapon_single   special_weapon_combo
noscope                team_collapse           projectile_impact_kill
teamkill_funny         last_alive_suicide      insta_defuse_clutch
```

lästa ur `player_death`, `player_hurt`, `weapon_fire`, `bomb_planted`,
`bomb_defused`, `round_end`, `hegrenade_detonate`.

**Och det räcker längre än man tror för vårt ändamål.** Spelaren är redan bannad
av en människa — systemet ska inte bevisa fusk, det ska hitta en situation värd
att titta på. Ägarens egen beskrivning av vad en aimbot-bann behöver var *"typ
nåra snabba kills"*, och `fast_multi_kill` är precis det, redan byggt och redan
kört i månadsvis.

Skillnaden mot månadskörningen är inte analysen utan **frågan**: i stället för
"månadens bästa ur alla spelare" är det "den här SteamID:n, det här
tidsfönstret, rangordnat". Samma skördare, annat filter.

Det betyder att steg 4 i §9 mest är ett urval — och att beteendeanalysen kan
skjutas till den dag de enkla situationerna visar sig otillräckliga. `teamkill_funny`
och `last_alive_suicide` täcker dessutom griefing redan i dag.

## 2d. PARSERPROVET, kört 2026-08-10 — och tre saker jag hade fel om

Ägaren la en riktig demo i `private/`
(`20260807-203518-de_seabase_d_prefab.dem.gz`, 108 MB packad / 219 MB uppackad,
från en av era egna kvällar). demoparser2 0.42 installerades i en venv och
kördes mot den. Fem fynd, och tre av dem ändrar slutsatser som står ovanför.

**RÄTTELSE 1: parsningen kostar sekunder, inte minuter.**

```
parse_header                     omedelbart
parse_event("player_death", …)   2,7 s   201 rader
parse_ticks([… pitch, yaw …])    3,4 s   1 491 525 rader
```

Hela resonemanget om att tick-data är "ett nytt kostnadsslag" bygger på att det
skulle vara dyrt. Det är det inte. En och en halv miljon rader med siktvinklar
tar tre sekunder — mindre än att kopiera filen från delningen.

**RÄTTELSE 2: beteendeanalys är alltså inom räckhåll.** Slutsatsen ovan att
aim- och spinbotanalys hör till en annan kostnadsklass gäller inte. `pitch` och
`yaw` finns per tick per spelare och är billiga att läsa. Det som återstår är
att skriva analysen — inte att ha råd med den.

Med en varning från samma prov: mitt första försök mätte STÖRSTA yaw-ändring per
tick och gav 167–178°/tick för samtliga tio spelare. Ett enskilt snärt ser
likadant ut som en snurra när man bara tittar på maxvärdet. En riktig spinbot
känns igen på UTHÅLLIGHET — många tickar i följd med hög hastighet — och den som
bygger den analysen ska veta att den naiva versionen inte skiljer någon från
någon.

**RÄTTELSE 3: chatten finns i demon, med tickar.**

```
player_chat: 18 rader — teamonly, text, tick, user_name, user_steamid
```

Avsnittet om spam bygger på att chatten bara finns i loggströmmen och därför
måste fångas i ett fönster runt bannen medan den rullande bufferten ännu har
den. Det stämmer för loggen, men demon bär samma rader med exakta tickar — och
det är en bättre källa på alla sätt: den är kvar när bufferten rullat vidare, och
tickarna är precis vad ett hopsytt spamklipp behöver.

`ban_chat`-tabellen behövs alltså inte som fångstmekanism. Vill man ändå visa
raderna på bannen kan de komma ur demon, tillsammans med klippet.

### Och skörden hittade bannen utan att veta om den

Provet kördes hela vägen: demon analyserades, kandidaterna skickades genom
`POST /api/demos/analys`, och sajten frågades om den bannade spelaren. Skördaren
var minsta möjliga — multikills grupperade på fem sekunder, plus specialvapen —
och visste ingenting om vem som var bannad.

Toppkandidaten i hela demon, med god marginal:

```
sabrina carpenter fan   fast_multi_kill   6 kills   166 poäng   12,1 s
                        {"weapons":["ssg08"],"headshots":6}
Mammascan               fast_multi_kill   3 kills   117 poäng    3,1 s
TonkeN                  fast_multi_kill   3 kills   111 poäng    6,1 s
```

Sex kills på tolv sekunder, **alla headshots, alla med scout** — och spelaren är
den som bannades för `Multihack` samma kväll. Ingen beteendeanalys, inga
siktvinklar, ingen tröskel: bara "gruppera kills och räkna". Ronden som förklarar
bannen låg överst av sig själv.

Det är beviset som saknades för att kedjan är värd att bygga. Det säger inte att
poängmodellen är rätt — den är tio rader lång och ska ersättas av
`select_candidates.py` — men det säger att det ENKLA fallet fungerar, och att
`fast_multi_kill` med sina `action_seconds` bär precis det en aimbot-bann vill
visa.

**En fälla i mitt eget prov, värd att ärva.** Andra kandidaten för samma spelare
blev `teamkill_funny` med vapnet `world`. En fallskada har ingen angripare, och
min naiva regel läste "samma lagnummer" som en lagkamratsdöd. Den som skriver
skörden på riktigt måste skilja på en död UTAN angripare och en död av en
lagkamrat — annars får varje spelare som ramlar ned från en kant en griefing-rad
på sin bann.

### Två fynd till, som inte ändrar en slutsats men sparar någon en kväll

**`round_end` FINNS INTE.** Demons 57 eventtyper innehåller
`round_officially_ended`, `round_freeze_end`, `round_poststart` och
`round_prestart` — men ingen `round_end`. Och `harvest_candidates.py` ber om
just `round_end` (README:t räknar upp den bland de lästa). Den begäran ger
ingenting, tyst. Värt att kontrollera i monthly-pipelinen innan någon bygger
vidare på rondgränser därifrån.

**OKÄNDA PROPS FÖRSVINNER UTAN ETT LJUD.** `parse_ticks(["is_spotted"])` gav
tillbaka en tabell med bara `tick`, `steamid` och `name` — inget fel, ingen
varning, bara en kolumn som saknas. Wallhack-approximationen som föreslås längre
ned bygger på ett spotted-tillstånd, och det fältet heter alltså något annat
eller finns inte alls. Askens egen regel gäller med full kraft: gissa aldrig ett
parser-fältnamn, läs ut det.

### Vad demon berättar om sig själv

`parse_header()` ger mer än väntat, och det tar bort behovet av att gissa ur
filnamnet:

```
map_name      de_seabase_d_prefab
server_name   OLDSWEDES.SE - Workshop Maps
addons        3070961307              (workshop-id, alltså vilken mapp)
patch_version 14174                   (CS2-bygget — se §4 b om renderbarhet)
```

`patch_version` är särskilt intressant: den säger vilket CS2-bygge demon
spelades in med. Frågan "går den här fortfarande att rendera" har ingen säker
signal (§4 b), men en demo inspelad på ett äldre bygge än det installerade är
åtminstone en misstanke som går att mäta i stället för att gissa.

---

### Går aim och wallhack att identifiera? Ett ärligt svar per fusk

Ägaren misstänkte att svaret är nej, och han har mestadels rätt. Men skälet är
inte att algoritmerna är svåra — det är att **den nuvarande parsningen aldrig
läser en enda siktvinkel.**

`harvest_candidates.py` anropar bara `parse_event`, och begär spelarfälten
`steamid, name, team_name, team_num, health, is_alive, X, Y, Z`. Ingen
`parse_ticks`, alltså ingen `pitch`, ingen `yaw`, ingen blickriktning över tid.

Det är en kostnadsskillnad och inte en kodrad: händelser är tusentals rader per
demo, medan tick-data är tio spelare gånger varje tick — en fyrtiominutersdemo
blir miljontals rader. Att lägga till beteendeanalys är alltså att införa ett
nytt kostnadsslag i indexeraren, inte att skriva en funktion till.

Med det sagt, per fusk:

**Spinbot — ja, och det är det enda som är både billigt och entydigt.** En
oavbruten extrem yaw-hastighet har ingen laglig förklaring. Den behöver dessutom
inte full tick-upplösning: en spinbot syns i var åttonde tick, för den slutar
aldrig snurra. Om något ska byggas efter teamkills är det den här.

**Aim — delvis, och aldrig som bevis.** Ett snap sker på ett par tickar, och en
GOTV-demo spelar in långsammare än servern räknar. Det som överlever
samplingen är statistik snarare än rörelse: headshot-andel, fördelningen av tid
från första skott till kill, hur jämnt målbyten sker. Det är signaler som pekar,
inte som visar — och en bra spelare pekar likadant en kväll när det lossnar.

**Wallhack — nej, inte utan mappens geometri.** Frågan "kunde hen se offret" är
en synlinjeberäkning mot väggarna, och den datan finns inte i demon.

Det finns EN användbar approximation, och det är en signal och inte en dom: CS
håller reda på om en spelare är **spotted**. Dödar någon upprepade gånger
fiender som aldrig blivit spottade av hens lag, är det värt att titta på. Ljud
förklarar mycket av det — men inte allt, och inte varje gång. Fältets riktiga
namn måste läsas ur våra demos innan någon bygger på det; askens egen regel
gäller: gissa inte parser-fält.

**Och den viktigaste slutsatsen är att frågan mest är fel ställd.** Vi behöver
inte identifiera fusket — en admin har redan gjort det, och en människa kommer
titta på klippet. Det systemet behöver är att **välja rätt ögonblick**.

Där duger svaga signaler utmärkt, för de bara rangordnar. Att lyfta fram en kill
på en ospottad fiende i en wallhack-bann är att välja en bra scen; att skriva
"wallhack, 0,72" bredvid samma kill är ett påstående vi inte kan bära. Samma
skillnad som skiljer vikter från filter i tabellen nedan — svaga signaler får
rangordna, aldrig anklaga.

**Och skicklighet är aldrig en signal.** Ägaren: *"är det duktiga spelare inne så
är dem välkomna"*, och smurf finns inte som bannanledning på OldSwedes. Det är
inte bara en policy utan en regel för koden: hög headshot-andel, snabba kills och
en brant skillkurva är precis vad en bra kväll ser ut som, och de får aldrig i
sig själva få något att flaggas, sorteras upp som misstänkt eller hamna i en
lista någon tittar igenom. Samma tanke som ägaren uttryckte om admins:
*"man ska ju inte banna bara för att man har personliga skäl — han måste fuska
med den statsen."* Systemet ska inte göra om det felet i kod.

Det påverkar rangordningen konkret: `fast_multi_kill` lyfts när det finns en
BANN att illustrera, aldrig som en upptäckt i sig. Ingen kandidat existerar utan
en bann att höra till.

### Anledningen styr vilka situationer som duger

Ägaren: *"försök hitta situationer som matchar banreason typ."* Det är inte
samma rangordning som månadskörningen använder — den letar efter det roligaste,
vi letar efter det som visar det admin påstod.

**Men anledningarna är för generella för ett filter.** Ägaren: *"visst vi har
wallhack och aimbot osv, men vi har också other och hacking och unknown."* Och
`multihack` betyder i första hand aim-assist och wallhack samtidigt — men kan
lika gärna vara spinbot plus aim.

Det gör mappningen till **vikter, inte filter**. Ett filter på "Other" ger noll
träffar per definition, och den vanligaste anledningen skulle bli den som aldrig
får ett klipp. En vikt bara omordnar, och en okänd anledning faller tillbaka på
den vanliga poängen i stället för på ingenting.

```
poäng = kandidatens egen poäng + anledningsbonus
```

Tre hinkar räcker att börja med:

| Hink | Anledningar (delsträngsmatchning, gemener) | Bonus |
|---|---|---|
| **Sikte** | aimbot, aim, triggerbot, spinbot, multihack, hacking, cheat | `fast_multi_kill` med låg `action_seconds` ↑↑, `noscope` ↑ |
| **Sikt genom väggar** | wallhack, wh, multihack | rök/vägg-flaggor ↑↑ när de finns, annars ingen bonus alls |
| **Lag** | grief, teamkill, tk, troll | `teamkill_funny`, `last_alive_suicide` ↑↑, allt som hyllar spelaren ↓↓ |
| *(ingen hink)* | other, unknown, tomt, allt annat | ingen bonus — högsta råpoäng vinner |

**`multihack` ligger i två hinkar med flit.** Den betyder ju båda sakerna, och
en anledning som betyder två saker ska få bonus för båda snarare än tvingas välja
— vilket som helst av dem visar det admin såg.

Fyra regler runt tabellen:

1. **Vikter, aldrig uteslutning.** Enda undantaget är griefing: en bann för
   teamkills ska inte illustreras med spelarens snyggaste ace. Där drar
   bonusen nedåt i stället för uppåt, och det är fortfarande en vikt.
2. **Mappningen är config, inte kod.** Anledningarna är fritext som admins
   skriver för hand och kommer stavas på tio sätt — `Hacking`, `hax`, `Multi
   hack`. En tabell går att rätta när nästa stavning dyker upp; en `switch` i en
   fil ändras aldrig.
3. **Wallhack-hinken ger ingen bonus när flaggan saknas** — den lyfter alltså
   ingenting i dag, eftersom skördaren inte ser genom väggar (§2c). Det är rätt:
   hellre att den vanliga poängen får bestämma än att systemet låtsas ha vägt in
   något det inte kan mäta.
4. **Ingen anledning diskvalificerar en kandidat.** Att griefing-hinken är tom
   betyder inte att ingen grievade — se §4c.

**Och den praktiska slutsatsen är att första versionen knappt behöver det här.**
Med `other`, `hacking` och `unknown` bland de vanligaste anledningarna landar de
flesta bannar i "ingen hink", alltså rakt på kandidaternas egen poäng. Bygg den
vägen först och lägg vikterna ovanpå när det finns riktiga fall att kalibrera
mot.

Och det som redan finns är mer träffsäkert än det låter för det vanligaste
fallet: `fast_multi_kill` bär `action_seconds` och `max_gap_seconds`, alltså
*hur snabbt* kills kom efter varandra. Tre kills på 1,2 sekunder är precis det
en aimbot-bann vill visa, och talet finns redan i kandidaten.

### Renderkön har redan ett schema, och sajten ska tala det

`prepare_render_queue.py` skriver `render_queue.json`, och `monthly_render.ps1`
läser den, kopierar demon in i CS2:s katalog, kör `csdm analyze`, sätter
`demo_name` till den stagade sökvägen och `player_id` från `player_steamid`.

Postens fält:

```
order  source_order  tier  reserve_reason  candidate_id  type  subtype  score
player  player_steamid  team_name  team_num  demo  map  round  tick
start_tick  end_tick  action_start_tick  action_end_tick  original_end_tick
safety_trimmed  kills  action_seconds  max_gap_seconds  weapons  flags
details  category
```

**Sajtens `/api/render/claim` ska svara med exakt den här formen.** Då blir
bann-renderaren en variant av `monthly_render.ps1` — hämta jobbet i stället för
att läsa en fil, resten oförändrat — i stället för ett andra renderingssystem som
ska hållas i synk med det som redan fungerar.

`original_end_tick` är inte prydnad: wrappern återställer till det och lägger på
1,5 sekunders eftersläpp, så en bann-kandidat måste bära samma fält eller tappa
sin andning i slutet.

---

## 3. Vad sajten har för inkommande maskin-API i dag

Ett enda, och det är värt att kopiera formen från:

```
POST /serverlog/{host}/{port}?token=...      ServerLogController
```

CS2-servern kan inte logga in som en webbläsare, så endpointen är
CSRF-undantagen (prefixlista i `public/index.php`, `CsrfMiddleware`) och vaktas
av en delad hemlighet plus en kontroll att servern är en vi känner till.

Det finns alltså **ingen** inloggning för ett skript i dag. Det är den enda
riktiga luckan — allt annat ovan går att återanvända.

---

## 4. Tre olika klockor, och ingen av dem är känd

Det här avsnittet finns för att jag hade fel i första versionen: jag läste
"demos slutar funka" som en retentionregel och byggde kön på den. Det är tre
skilda klockor, och de ska inte blandas ihop.

**a) Demon finns inte ÄNNU.** Bannen sätts mitt i kvällen; demon är inte
avslutad, komprimerad, kopierad till lagringen eller indexerad. Det här är det
vanligaste utfallet direkt efter en bann, och det är därför
`waiting_for_demo` måste finnas oavsett hur retention ser ut:

```
bann sätts → ingen indexerad demo → waiting_for_demo → försök igen → demon dyker upp → vidare
```

`no_demo_found` får sättas **först när väntfönstret gått ut**. Sätts det direkt
ljuger sidan om något som bara inte hänt än.

**b) Demon går inte längre att RENDERA.** Efter en större CS2-uppdatering eller
när workshop-mappen uppdaterats kan CS2 inte spela upp filen — även om den
ligger kvar på disk och även om vi redan indexerat den.

**Och den klockan går inte att läsa av i förväg.** Ägaren: *"vi vet inte, det är
när cs2 får en större uppdatering eller mappen uppdateras."* Ingen tabell, ingen
tidsgräns och ingen heuristik kan säga när det hände — **bara den som försöker
rendera får veta**, och den får veta det genom att CS2 vägrar ladda filen.

Två följder, och de är de viktigaste designbesluten i dokumentet:

1. **`demo_expired` sätts av renderworkern, aldrig av en klocka.** Ett jobb får
   inte förfalla i tysthet för att någon gissat fel om hur länge en demo håller.
   `expires_at` finns kvar som en frivillig "sluta försöka"-spärr och ska
   normalt stå långt bort eller vara tom.
2. **Fart är hela motmedlet.** Ägaren igen: *"jag måste typ köra den när ny ban
   lagts."* Renderjobbet ska alltså skapas när bannen sätts, inte i en nattlig
   körning — varje timme som går är en chans att Valve släpper en uppdatering.
   Kön är händelsestyrd, inte schemalagd.

**Parsningen överlever däremot.** Ägaren tror att parsern funkar oavsett, och
det stämmer med hur formaten skiljer sig: demoparser2 läser en fil, CS2 måste
ladda den *och* mappen. Därför är indexet värt att bygga även om videon
misslyckas — *"de_inferno, rond 12, ~tick 147900"* är fortfarande svaret på var
en admin ska titta, och det svaret slutar aldrig gälla.

**c) Demon raderas från lagringen.** Ingen fastställd retention finns.
Monthly-highlights arbetar med ungefär 3,5 veckors demos, men det är inte samma
sak som att äldre demos försvinner.

Följden: **inget tidsvärde hårdkodas någonstans.**

```
BAN_LOOKBACK_DAYS=3       # hur många dygn bakåt sökningen får vandra (tak, inte mål)
DEMO_WAIT_HOURS=12        # hur länge en bann får stå i waiting_for_demo
RENDER_TTL_HOURS=          # tomt = försök hur länge som helst, se b) nedan
CANDIDATE_COUNT=3         # hur många situationer som sparas
AUTO_RENDER_COUNT=3       # hur många som renderas utan att någon ber om det
```

`AUTO_RENDER_COUNT` är lika med `CANDIDATE_COUNT` med flit: finns tre kandidater
renderas tre, finns bara en renderas en. Sänk talet om renderburken inte hinner
med — men gör det för att den faktiskt inte hinner, inte för att spara ström.

### Sökningen vandrar bakåt ett dygn i taget

Ägaren: *"den tar ban-datum men om spelaren inte hittas i det datumets demos så
ska den fortsätta bakåt i tiden, typ dagen före, kolla alla demos, sen dagen
före."*

```
bannens datum → alla demos den dagen → hittas spelaren?
     nej ↓                                    ja ↓
dagen före → alla demos                   ta situationerna, sluta leta
     nej ↓
dagen före … tills BAN_LOOKBACK_DAYS
```

Tre saker gör det här billigt, och alla tre kommer av vad sajten redan vet:

**Ett dygn är en namnjämförelse, inte en katalogvandring.** Datumet ligger i
filnamnet (§2b), så "den 7 augusti demos" är ett prefixfilter över en
kataloglistning. Ingen `stat()`, ingen parsning bara för att veta vilken dag en
fil hör till.

**"Vilka demos innehåller SteamID X" är en fråga till indexet, inte till
parsern.** `demo_player` svarar direkt för allt som redan är indexerat, och
indexeraren betar av de senaste dygnen kontinuerligt. Vandringen bakåt blir
alltså normalt en serie SQL-frågor; parsning behövs bara för dagar som ännu inte
hunnit indexeras — och då i **datumordning, nyast först**, vilket är exakt samma
ordning som vandringen ändå går i.

**På bannens egen dag väger demos före banntidpunkten tyngst.** En bann sätts när
någon sett något; det som är värt att titta på ligger före klockslaget, inte
efter. Demos som spelats in efter bannen sorteras sist snarare än bort — en
serverklocka som går fel ska inte kunna gömma undan hela kvällen.

**Där jag går utanför bokstaven:** ägaren sa "om spelaren inte hittas". Jag
föreslår att vandringen fortsätter även när spelaren hittas men dagen inte gav
någon situation värd namnet. Målet är ett klipp, inte en närvarolista, och en
kväll där någon spelat fem ronder utan att göra något syns är lika tom som en
kväll utan demos. Vandringen stannar när `CANDIDATE_COUNT` är fyllt eller när
dygnen tar slut.

**Och taket är lågt med flit.** Ägaren: *"om de inte hittas ska den inte vandra
flera år bakåt i tiden och leta, använd bara nåra dagar."* Tre dygn är förvalet.
En bann handlar om något någon nyss såg; hittar vi det inte i den närmaste
kvällen eller två är svaret inte att leta längre bort, utan att svara att vi inte
hittade det. Se §4c.

### 4c. En bann bör gå att visa

Det här är ägarens skarpaste krav, och det är ett omdöme om hur admins arbetar
snarare än en teknisk detalj: *"om vi inte kan hitta det så borde kanske bannen
ifrågasättas och admin borde ladda upp egen video, man ska ju inte banna bara
för att man har personliga skäl — 'han måste fuska med den statsen'."*

Regeln bakom är rätt och värd att bygga för: **en bann ska kunna visas.** Sitter
den på en känsla ska den känslan behöva ta formen av ett klipp.

**En invändning som måste stå kvar i koden, inte bara här.** Att roboten inte
hittade en situation är INTE ett belägg för att bannen är fel, och sidan får
aldrig säga att den är det. Tre helt oskyldiga skäl ger samma tomma resultat:

- Skördaren är händelsebaserad (§2c). En wallhackare som spelar lugnt och vinner
  dueller hen inte borde vinna genererar inga händelser alls — och det är just
  den sortens fusk som är svårast att se och lättast att banna på magkänsla.
- Demon kan saknas, vara trasig eller ligga på en server som inte spelar in.
- Fusket kan ha synts i något ingen kan mäta: någon som väntar i exakt rätt
  hörn, tre gånger i rad.

Därför formulerar sajten det som en **begäran om bevis**, aldrig som en dom över
kollegan:

> Roboten hittade inget att visa för den här bannen inom 3 dygn. Har du sett
> något själv — ladda upp ditt klipp. En bann bör kunna visas.

Ingenting om misstanke, ingen räknare över admins utan klipp, ingen lista. Den
skillnaden är hela skillnaden mellan ett verktyg som höjer kvaliteten och ett
som får folk att sluta banna för att slippa bli granskade.

**Eskaleringen är ägarens, och den är tidsstyrd.** Står en bann kvar utan klipp —
varken robotens eller adminens — efter `EVIDENCE_GRACE_DAYS`, öppnar sajten ett
Watson-ärende till ägaren. Inte en anklagelse: ett ärende som säger att en bann
saknar underlag och att någon bör titta. Det är exakt vad ägaren bad om med
*"eller att sidan eskalerar till mig, så kan jag kolla"*, och det är rätt person:
den som äger communityt får väga en bann utan bevis, inte en automat.

```
EVIDENCE_GRACE_DAYS=7     # hur länge en bann får stå utan klipp innan Pintuz får ett ärende
```

### Var gränsen går

Sajten håller redan två gränser: den lägger inte till kolumner i OSBase schema,
och inte i SimpleAdmins. Det gäller fortfarande.

Nytt i sajtens databas: demoindexet, situationerna, renderkön. De är våra egna
tabeller — **och sajten är enda processen som skriver i dem.** Indexeraren talar
HTTP även om den kör på samma maskin. En andra skrivare i samma tabeller är en
låsning ingen kommer minnas orsaken till, och rätten att ändra ett schema hör
ihop med att äga koden som skriver det.

---

## 5. Föreslaget schema

### 5.1 `api_client` — nycklar för maskiner

```sql
CREATE TABLE api_client (
    id          INT PRIMARY KEY AUTO_INCREMENT,
    name        VARCHAR(64) NOT NULL,        -- "demo-indexer", "WIN-RENDER-01"
    token_hash  CHAR(64) NOT NULL,           -- sha256; klartexten visas en gång
    scopes      VARCHAR(255) NOT NULL DEFAULT '',
    created_at  INT NOT NULL,
    created_by  INT NOT NULL,
    last_used_at INT NULL,
    last_ip     VARBINARY(16) NULL,
    revoked_at  INT NULL,
    UNIQUE KEY api_client_token (token_hash)
);
```

Skälet att inte återanvända loggtokenmönstret: den hemligheten går inte att
återkalla utan en deploy, och alla anrop ser likadana ut i loggen. Med en nyckel
per maskin kan bannlistan säga *"klipp uppladdat av WIN-RENDER-01"*, och en
nyckel som läckt stängs av från admin-sidan på tio sekunder.

Scopes håller isär de två klienterna: indexeraren får skriva demoindex men aldrig
röra en bann, renderworkern tvärtom.

### 5.2 Demoindexet

```sql
CREATE TABLE demo (
    id            INT PRIMARY KEY AUTO_INCREMENT,
    path          VARCHAR(255) NOT NULL,     -- som indexeraren såg den
    filename      VARCHAR(190) NOT NULL,
    map           VARCHAR(64) NOT NULL DEFAULT '',
    server_id     INT NULL,                  -- sa_servers.id när den går att knyta
    started_at    INT NULL,
    ended_at      INT NULL,
    bytes         BIGINT NOT NULL DEFAULT 0,
    checksum      CHAR(40) NULL,
    parser_version   VARCHAR(16) NOT NULL DEFAULT '',
    analysis_version VARCHAR(16) NOT NULL DEFAULT '',
    analyzed_at   INT NULL,
    error         VARCHAR(255) NULL,
    UNIQUE KEY demo_file (path, filename),
    KEY demo_time (started_at)
);

CREATE TABLE demo_player (
    demo_id    INT NOT NULL,
    steamid64  VARCHAR(32) NOT NULL,
    nickname   VARCHAR(64) NOT NULL DEFAULT '',
    first_tick INT NULL,
    last_tick  INT NULL,
    kills      INT NOT NULL DEFAULT 0,
    deaths     INT NOT NULL DEFAULT 0,
    PRIMARY KEY (demo_id, steamid64),
    KEY demo_player_steam (steamid64)   -- "hitta alla demos med SteamID X"
);

CREATE TABLE demo_situation (
    id          INT PRIMARY KEY AUTO_INCREMENT,
    demo_id     INT NOT NULL,
    steamid64   VARCHAR(32) NOT NULL,
    round       INT NULL,
    start_tick  INT NOT NULL,
    end_tick    INT NOT NULL,
    type        VARCHAR(32) NOT NULL,   -- "ace", "fast_3k", "knife", "teamkill"…
    kill_count  TINYINT NOT NULL DEFAULT 0,
    score       DECIMAL(6,2) NOT NULL DEFAULT 0,
    meta_json   TEXT NULL,
    KEY demo_situation_player (steamid64, score)
);
```

**Nickhistoriken behöver ingen egen tabell.** `demo_player` bär nicket per demo
och `demo` bär tiden — "vilket nick hade hen närmast bannen" är en sortering,
inte en tabell till som kan hamna ur synk.

**`meta_json` och inte tjugo kolumner.** Vad en situation bär skiljer sig per
typ, och analysen ska kunna förbättras utan att gamla demos parsas om.
Kolumnerna som ligger utanför är de man **söker** på.

**Råa kills och ticks ligger INTE här.** Asken vill spara dem för att kunna
förbättra algoritmen utan omparsning, och det är rätt — men de hör hemma hos
indexeraren, inte i sajtens databas. Sajten behöver situationerna för att välja
kandidat och visa admins var de ska titta; den ställer aldrig en fråga om en
enskild tick, och tiotusen rader per demo genom ett HTTP-API är fel form för
båda sidor.

### 5.3 `render_job` — det Windows får

```sql
CREATE TABLE render_job (
    id           INT PRIMARY KEY AUTO_INCREMENT,
    ban_id       INT NOT NULL,
    demo_id      INT NOT NULL,
    situation_id INT NULL,
    steamid64    VARCHAR(32) NOT NULL,
    start_tick   INT NOT NULL,
    end_tick     INT NOT NULL,
    rank         TINYINT NOT NULL DEFAULT 1,   -- 1..3
    score        DECIMAL(6,2) NOT NULL DEFAULT 0,
    state        VARCHAR(16) NOT NULL DEFAULT 'candidate',
    worker       VARCHAR(64) NULL,
    claimed_at   INT NULL,
    heartbeat_at INT NULL,
    attempts     TINYINT NOT NULL DEFAULT 0,
    -- Frivillig "sluta försöka"-spärr, normalt tom. Att en demo blivit
    -- orenderbar går inte att veta i förväg — se §4 b). demo_expired sätts av
    -- workern som faktiskt fick CS2 att vägra, inte av den här kolumnen.
    expires_at   INT NULL,
    error        VARCHAR(255) NULL,
    created_at   INT NOT NULL,
    UNIQUE KEY render_job_candidate (ban_id, rank),
    KEY render_job_queue (state, created_at)
);
```

`state`: `candidate → queued → claimed → rendering → uploading → done`, plus
`failed` och `demo_expired`.

**Alla kandidater som finns köas** (`AUTO_RENDER_COUNT`, förvalt lika med
`CANDIDATE_COUNT`). Ägarens skäl: chansen att minst ett klipp faktiskt visar
fusket är högre med tre försök än med ett, och ett klipp som missar är värt noll.
Finns bara två situationer renderas två; finns en, en.

`candidate`-tillståndet finns kvar ändå, för det är där ett jobb hamnar när
talet sänks — och för att en admin ska kunna be om en situation som aldrig
köades.

`UNIQUE (ban_id, rank)` är hela idempotensen asken kräver (§ "Retry safety"): en
omstartad worker kan inte skapa fem identiska jobb, för platsen är upptagen.

**Leasen är en heartbeat och inte en timeout hos klienten.** En Windowsmaskin som
kraschar mitt i en rendering säger ingenting; kön måste själv kunna se att
hjärtat slutat slå och lägga tillbaka jobbet.

### 5.4 `ban_clip` — flera klipp per bann, ett utvalt

Normalfallet är **upp till tre** klipp per bann — ett per kandidat som gick att
rendera — plus eventuellt ett en admin laddat upp för hand. Det är därför
`chosen` behövs: den som satte bannen tittar igenom dem och pekar ut det som
bäst visar fusket.

```sql
ALTER TABLE ban_clip
    ADD COLUMN chosen     TINYINT(1) NOT NULL DEFAULT 0,
    ADD COLUMN source     VARCHAR(16) NOT NULL DEFAULT 'admin',  -- 'admin' | 'robot'
    ADD COLUMN label      VARCHAR(64) NOT NULL DEFAULT '',       -- "Ace, rond 12"
    ADD COLUMN job_id     INT NULL,
    ADD COLUMN api_client INT NULL;
```

- **Ett klipp är utvalt per bann.** Är inget utpekat visas det med högst
  kandidatpoäng — en bann med tre klipp och ingen vald ska inte se tom ut, och
  robotens egen rangordning är en bättre gissning än ingen.
- **Publikt visas bara det utvalda.** Bannlistan är offentlig; tre varianter av
  samma kväll är en adminfråga.
- **Roboten väljer aldrig.** Den renderar och laddar upp; en människa pekar ut.
  Skillnaden mellan "högst poäng" och "vald" är skillnaden mellan en gissning
  och ett omdöme, och bara det ena får kallas bevis.

### 5.5 `ban_highlight` — vad roboten gjorde, i klartext

```sql
CREATE TABLE ban_highlight (
    ban_id        INT PRIMARY KEY,
    state         VARCHAR(24) NOT NULL,
    detected_type VARCHAR(32) NULL,
    confidence    DECIMAL(3,2) NULL,
    note          VARCHAR(255) NULL,   -- på svenska, visas för admin
    candidates    TINYINT NOT NULL DEFAULT 0,
    waiting_since INT NULL,            -- när väntfönstret började ticka
    escalated_at  INT NULL,
    updated_at    INT NOT NULL
);
```

`state`: `pending`, `waiting_for_demo`, `analyzing`, `rendering`, `uploaded`,
`no_demo_found`, `no_situation_found`, `demo_expired`, `rejected`, `failed`.

**`no_demo_found` och `no_situation_found` är olika svar och ska stå i olika ord
på sidan.** Det första betyder att spelaren inte dök upp i någon demo inom
`BAN_LOOKBACK_DAYS` — hen kanske spelade på en server som inte spelar in, eller
under ett annat konto. Det andra betyder att hen fanns där men att inget hände
som gick att klippa, och då är demon fortfarande värd att öppna för hand.
`note` bär vilka dygn som vandrades igenom, för det är den frågan en admin
ställer härnäst.

Tabellen finns av ett north star-skäl: **en admin ska kunna se skillnad på
"roboten har inte kommit hit än", "demon finns inte ännu" och "roboten hittade
ingen demo".** Utan den ser alla tre ut som en tom ruta, och då ringer folk till
dig i stället för att läsa.

`detected_type` visas **bara** för admins och ändrar aldrig `reason` — asken är
uttrycklig (§ "Admin reason får aldrig förstöras"), och en heuristik som skriver
om en admins beslut är en heuristik som fått sista ordet.

---

## 6. När roboten inte räcker till

Ägarens krav: *"om vi inte hittar nån demo eller om klippet inte faktiskt visar
fusket så måste vi uppmana dem om att kolla igenom demot själv, eller att sidan
eskalerar till mig."*

**Väntar** (`waiting_for_demo`)
> Kvällens demo har inte kommit till lagringen än. Roboten försöker igen.

**Ingen demo** (`no_demo_found` efter `DEMO_WAIT_HOURS`, eller `demo_expired`)
> Spelaren finns inte i någon demo de senaste 7 dygnen. Ladda upp ett klipp för
> hand om du har ett.

**Spelaren fanns, men inget att klippa** (`no_situation_found`)
> Spelaren finns i kvällens demos men roboten hittade inget att klippa. Demon:
> `de_inferno`, 2026-08-07 — öppna den och titta själv. Har du sett något —
> ladda upp ditt klipp. En bann bör kunna visas.

Notera ordvalet: *"inget att klippa"*, inte *"inget som matchar anledningen"*.
Anledningen är en vikt och inte ett filter (§2c), så en tom hand betyder att
ingenting stack ut — inte att just den sortens fusk uteblev.

Skillnaden är hela poängen med två tillstånd: i det första leder ingen väg
vidare, i det andra ligger materialet kvar och väntar på ett par ögon. Båda
slutar i samma uppmaning, och ingen av dem i en anklagelse (§4c).

**Inget av klippen visar fusket.** En knapp per klipp: *"visar inte fusket"*.
Med tre renderade klipp är det normalt ett val mellan dem snarare än en väntan.
Då:
1. finns en kandidat kvar som aldrig renderades (talet var sänkt, eller en
   rendering föll) köas den
2. är alla underkända går bannen till `rejected`, och sidan säger:
   > Roboten hittade inget som visar det. Demon: `de_inferno`, 2026-08-07, rond
   > 12, ~tick 147900. Öppna den i CS Demo Manager och klipp själv, eller lämna
   > över till Pintuz.

**Och sidan pekar ut var man ska titta.** Även när automatiken misslyckas vet den
vilken demo och ungefär vilken tick spelaren var intressant. Att skriva ut det
sparar timmen som annars går åt att leta — det är halva vinsten även när videon
uteblir, och det är den enda delen av kedjan som ger värde redan innan en enda
MP4 renderats.

**Eskaleringen går genom Watson.** `TronWatson` har redan `report()`,
`handOver()` och notifieringar, och ett ärende bär historik och deltagare. En
knapp *"lämna över till Pintuz"* öppnar ett ärende med bannen, demon och
tickarna i kroppen. En egen mejlväg vid sidan om vore ett andra ställe att glömma
bort.

---

## 5.6 Storleken, och vad tre klipp per bann faktiskt kostar

Ägaren: *"vi vill ändå hålla ner klippen så de inte tar upp så stor plats, vi gör
ju om dem så de blir typ 30-50mb styck."*

Omkodningen finns redan och är inställd (`ClipTranscoder`): **720p som tak**,
x264 med CRF 21 där kodaren finns, annars VP9 med CRF 28 — och aldrig
uppskalning, för ett fusk syns i vad någon gör och inte i hur många pixlar det
gjordes i.

Räkningen som följer av tre kandidater:

| | längd | ungefär |
|---|---|---|
| en vanlig situation (`fast_multi_kill`) | 10–20 s | 5–15 MB |
| ett hopsytt spamklipp, 8 fönster à 4 s | ~30 s | 15–25 MB |
| `ban_moment`, oklippt | 2 min | 60–100 MB |
| **tre kandidater per bann** | | **runt 100 MB** |

Tre beslut som följer, och två av dem är ägarens ord:

**1. Ihopsyning bara för spam.** Ägaren: *"om vi börjar klippa ihop andra typer
vet jag dock inte."* Han har rätt, och renderarens egen kommentar säger samma
sak: snabba 2k/3k ska förbli sammanhängande så man inte tappar tidskänslan. Ett
hopp mitt i en duell gör klippet obegripligt; ett hopp mellan två spamskurar är
själva innehållet. `StitchTypes` får alltså spam och inget annat.

**2. `ban_moment` måste kortas.** Två minuter är den överlägset dyraste
kandidaten och den minst informativa per megabyte. Sextio till nittio sekunder
före banntidpunkten räcker för att se vad admin såg, och halverar filen.

```
BAN_MOMENT_SECONDS=75
```

**3. Ihopsyningen SPARAR plats, den kostar inte.** Åtta fönster à fyra sekunder
är trettio sekunder video i stället för de två minuter en oklippt spamperiod
hade blivit. Att välja segment är att välja bort tystnad.

### Markera flera klipp och sy ihop dem

Ägaren: *"man väljer vilket klipp som bäst representerar bannen, men man kan
markera alla 3, och om fler än 1 markeras så klipper den ihop dem kanske?"*

Ja — och det här är en annan sorts ihopsyning än den i §6b2, som sker under
renderingen inne i CS2. Den här sker **efteråt, på färdiga MP4-filer**, och den
är nästan gratis: sajten har redan ffmpeg och en kö att göra jobbet i.

```
admin markerar klipp 1 och 3
        ↓
kö-jobb "stitch"  →  ffmpeg concat  →  ett nytt klipp, source='stitch', chosen=1
        ↓
bannen visar EN video publikt, som förut
```

**BARA ROBOTENS KLIPP.** Ägaren: *"egen uppladdning går före — det är när typ
2-3 klipp genererats från skriptet som de ska kunna kopplas ihop."* Det är rätt
gräns och den gör funktionen mycket enklare, se punkt 1.

Tre tekniska detaljer:

**1. Vi VET att bitarna passar ihop — vi gjorde dem själva.** Ägaren satte fingret
på det: *"och då vet vi att dem är lika stora."* Robotens klipp kommer ur samma
renderare och genom samma `ClipTranscoder`, med samma upplösning, kodare och
bildfrekvens. Alltså kan ffmpeg foga ihop dem med `-c copy` — ingen omkodning,
sekunder i stället för minuter, ingen kvalitetsförlust.

Det är skillnaden mellan "brukar fungera" och "kan inte gå fel": egenskapen är
garanterad därför att sajten producerat båda filerna, inte hoppats fram. Och
eftersom egna uppladdningar aldrig ingår behövs ingen fallback för avvikande
format — en hel gren kod som kan utebli, tack vare gränsen ägaren drog.

**2. Ordningen är kronologisk, inte efter poäng.** Klippen berättar en kväll, och
en kväll läses framåt. Två situationer ur samma demo sorteras på tick; ur olika
demos på tid.

**3. Delarna sparas i raden.** `parts` håller vilka klipp hopfogningen kom ur, så
en admin kan ångra sig och en framtida läsare förstår vad hen ser. Ett hopfogat
klipp utan ursprung är en video ingen kan granska.

### Egen uppladdning går före

En admin som laddar upp sitt eget klipp har sett något och visat det. Den filen
är bevis; robotens är förslag.

- **Ett eget klipp blir `chosen` direkt** om inget annat redan är utpekat, och
  ligger överst i galleriet. Ingen behöver kryssa i något för att det ska gälla.
- **Det uppfyller §4c.** En bann med adminens eget klipp saknar inte underlag,
  och varken uppmaningen eller eskaleringen ska utlösas.
- **Ligger renderjobb kvar i kö avbryts de.** Har admin redan visat vad hen såg
  finns ingen anledning att bränna GPU-tid på tre gissningar. Jobb som redan
  RENDERAR får gå klart — att avbryta halvvägs sparar ingenting och lämnar skräp
  efter sig. Kandidaterna finns kvar i `demo_situation` och går att rendera på
  begäran om någon ändå vill se dem.

```sql
ALTER TABLE ban_clip
    ADD COLUMN parts VARCHAR(64) NULL;   -- kommaseparerade ban_clip.id
```

**Och det här löser lagringsfrågan nedan snyggare än en städregel gör.** Tre
kandidater på ~100 MB blir ett hopfogat klipp på 20–40 MB som faktiskt visar
allt admin ville visa — och då är de tre ursprungen inte längre något man ens
vill behålla.

### Och en fråga till ägaren: får de bortvalda klippen städas?

När en admin pekat ut vilket klipp som visar fusket (§6c) ligger två renderade
videor kvar som ingen kommer titta på igen. Att radera dem efter en frist skulle
ta kostnaden från ~100 MB till ~40 MB per bann — en tredjedel.

Jag föreslår det INTE på egen hand, eftersom sajtens hållning är att innehåll
aldrig raderas. Men de här filerna är robotens utkast och inte någons innehåll:
ingen har skrivit dem, ingen kommer sakna dem, och situationen de visar finns
kvar i `demo_situation` — samma klipp går att rendera igen så länge demon lever.

Ett rimligt förval vore därför:

```
CANDIDATE_KEEP_DAYS=30    # bortvalda klipp städas efter detta, valt klipp aldrig
```

Men det är ägarens beslut, inte dokumentets.

---

## 6b. Att föreslå en bättre anledning

Ägaren: *"har vi möjlighet att identifiera fusket bättre än en admin gjort så
kan vi absolut föreslå ny banreason."* Det stämmer med askens regel, som säger
att `reason` aldrig får skrivas om automatiskt men att ett förslag är en annan
sak.

**Två sorters förslag, och bara den ena går att göra i dag.**

**Ett iakttaget faktum.** Fyra teamkills på tolv ronder är inte en tolkning — det
står i `player_death` med lagfärgerna på båda. Sitter bannen på `Other` och
demon visar teamkills, då är "Griefing" ett bättre ord för samma kväll, och
sajten kan säga det utan att gissa. Samma sak för självmord som sista överlevande.

**En slutsats om avsikt.** "Det där såg ut som aimbot" kräver den
beteendeanalys som inte finns (§2c). En snabb trippel är ingen aimbot; den är en
snabb trippel. Tills analysen finns ska sajten inte föreslå aimbot, wallhack
eller spinbot över huvud taget — och den dagen den gör det ska förslaget bära
sin `confidence` och sina flaggor öppet.

Alltså: **bygg mekanismen nu, mata den bara med fakta.** Griefing är hela
förstaversionen, och det är ändå det vanligaste fallet där ordet `Other` döljer
något som har ett namn.

```sql
ALTER TABLE ban_highlight
    ADD COLUMN suggested_reason  VARCHAR(64) NULL,
    ADD COLUMN suggested_because VARCHAR(255) NULL,   -- på svenska, iakttagelsen
    ADD COLUMN suggestion_state  VARCHAR(16) NULL;    -- open | accepted | dismissed
```

På ändra-sidan, intill anledningsfältet:

> **Förslag:** *Griefing* — 4 teamkills och ett självmord som sista överlevande
> på de_inferno, 2026-08-07.  [Använd] [Nej tack]

Fyra regler:

1. **Aldrig automatiskt.** Anledningen är adminens beslut; förslaget är en knapp.
   Accepteras det går det genom samma `updateDetails()` som formuläret och
   hamnar i granskningsloggen som adminens ändring, inte robotens.
2. **Alltid VARFÖR, aldrig bara VAD.** "Griefing (0.91)" säger ingenting man kan
   ha en åsikt om. Iakttagelsen ska stå utskriven, för det är den admin ska
   bedöma — samma regel som gäller Tron: motivera, sammanfatta inte.
3. **Ett nej är slutgiltigt.** `dismissed` visas aldrig igen. En sajt som frågar
   en andra gång är en sajt man slutar läsa.
4. **Bara när det är en förbättring.** Ingen föreslår `Griefing` på en bann som
   redan heter `Griefing`, och ingen föreslår `Other`.

---

## 6b2. Spam: chatten finns redan i loggen, rösten gör det inte

Ägaren: *"spamma är ju bara chatten och voicen, jag vet inte om voice går att
detektera på ett bra vis?"*

**Chatten behöver inte demon alls.** CS2:s loggström bär `say`- och
`say_team`-raderna, och sajten tar redan emot den strömmen
(`ServerLogController` → `ServerLogStore`). Text, tidsstämplad, med SteamID på
raden — oändligt mycket lättare att arbeta med än en videoruta.

Men den **sparas inte**. Lagret är en rullande fil på 1 MB per server som kapas
till en halv när den blir full: minuter på en kväll som lever. Chatten finns
alltså i förbifarten, inte i en historik.

**Två vägar, och jag föreslår den smala.**

*Ett chattarkiv* vore enkelt att bygga och skulle svara på allt — men det är att
börja spara medlemmars konversationer på obestämd tid, och det är ett beslut om
personuppgifter snarare än om bannar. Det kräver en retentionregel, en rad i
integritetstexten och ägarens uttryckliga ja.

*Ett fönster runt bannen* räcker för ändamålet. Kön är händelsestyrd (§4b), så
bann-arbetaren är igång inom minuter — och då ligger raderna fortfarande kvar i
den rullande bufferten. Ta det som finns kring banntidpunkten, spara **de**
raderna på bannen, och rör inget annat.

```sql
CREATE TABLE ban_chat (
    ban_id     INT NOT NULL,
    stamp      INT NOT NULL,
    steamid64  VARCHAR(32) NOT NULL,
    nickname   VARCHAR(64) NOT NULL DEFAULT '',
    channel    VARCHAR(8) NOT NULL DEFAULT 'say',   -- say | say_team
    text       VARCHAR(255) NOT NULL,
    KEY ban_chat_ban (ban_id)
);
```

Det är samma hållning som klippet självt: bevis knutet till ett beslut, inte ett
lager över allt som sagts. Och för en spam-bann är det ett bättre bevis än en
video — femton identiska rader i följd säger sig självt, och de går att läsa på
en telefon.

**Rösten går inte att detektera, och det ska vi inte försöka.** Röstdata ligger
i demon som Opus-paket; parsern läser den inte, och även om den gjorde det vore
"spam" en fråga om hur länge någon höll ned knappen snarare än om vad som sades.
Att bygga en mätare på det är mycket arbete för ett svar en människa ger på fem
sekunder.

**Klippet är svaret i stället.** Renderaren spelar in med ljud (`audio`-flaggan
finns redan i renderargumenten), så för en röst-bann räcker det att klippa
minuterna kring banntidpunkten och låta en människa lyssna. Vilket leder till
nästa sak, som är nyttig långt utanför spam:

### Klipp ihop spammet till en video — renderaren kan det redan

Ägaren: *"då kan den ju typ klippa ihop en massa voice/chatt till en video?"*
Ja, och det kostar nästan ingenting: **flersegmentsklipp finns redan i
renderaren.**

`render_highlights_csdm_direct_v26_monthly_order.ps1` läser ett `segments`-fält
på ett highlight och renderar varje bit för sig innan de sys ihop:

```
segments: [ {start_tick, end_tick}, {start_tick, end_tick}, … ]

SegmentMergeGapSeconds = 1.0    ligger bitarna närmare än så blir de ETT klipp
StitchTransitionSeconds = 0.25  toning mellan bitar som fortfarande är åtskilda
StitchTypes = "ace"             bara listade typer får sys ihop
```

Saknas `segments` syntetiseras ett enda segment ur `start_tick`/`end_tick` — så
allt annat i kedjan fungerar precis som förut.

För en chattspam-bann blir det alltså:

```
say-raderna ur loggen  →  tidsstämplar  →  tickar i den demo som var igång
        →  ett segment på ~4 s runt varje  →  segments[]  →  ETT klipp
```

Tre saker som redan är lösta åt oss, och en som måste göras:

- **Skurar slås ihop automatiskt.** Femton rader på tjugo sekunder blir inte
  femton hopp — `SegmentMergeGapSeconds` gör dem till ett sammanhängande klipp.
  Samma tanke som skördarens gruppering av kills, redan inställbar.
- **Toningen finns.** Inga hårda hopp mellan bitarna om man inte vill ha dem.
- **Ljudet är med**, vilket är hela poängen här.
- **`StitchTypes` måste utökas** med vår spam-typ, **och bara den**. Den står i
  dag på `"ace"` ensam, med en kommentar om varför: snabba 2k/3k ska förbli
  sammanhängande så man inte tappar tidskänslan. För spam gäller motsatsen —
  hoppen ÄR budskapet. Ägaren drog samma gräns själv (§5.6).

**Och det löser rösten på köpet, delvis.** Vi kan inte tidsstämpla röstspam, men
den som spammar i voice spammar oftast i chatten också — och även när hen inte
gör det pratar hen under samma minuter. Ett hopsytt klipp byggt på
chattidsstämplarna fångar därför rösten i förbifarten, eftersom ljudet ändå
spelas in. Ingen detektion, bara rätt minuter valda av en annan anledning.

Finns ingen chatt alls att bygga fönster av står `ban_moment` kvar som svar.

### `ban_moment`: en kandidat som alltid finns

När ingen händelsebaserad situation matchar är **den sista minuten före bannen**
alltid en giltig kandidat (`BAN_MOMENT_SECONDS`, se §5.6 för varför den inte är
två). Ingen analys behövs — banntidpunkten mappas till en tick i den demo som var
igång.

Den täcker precis det skördaren är blind för: röstspam, chattspam, någon som
blockerar i en dörr, någon som står still och vägrar spela. Och den är gratis:
tidsstämpeln finns på bannraden, tick-numret är en division.

Regler:

- Läggs sist bland kandidaterna, inte först. En riktig situation är alltid
  bättre än ett tidsfönster.
- Ersätter inte `no_situation_found`. Sidan ska fortfarande säga att roboten inte
  hittade något att peka på — klippet är en utgångspunkt för admin, inte ett
  fynd.
- Bara när en demo faktiskt täcker banntidpunkten. Annars är det ingen kandidat
  utan en gissning om vilken kväll det var.

---

## 6c. Ska "Other" och "Unknown" tas bort?

Ägarens fråga: tvinga fram en snabb analys genom att stryka de vaga
anledningarna. Målet är rätt — `Other` är där information går för att dö — men
jag tror medlet är fel, av tre skäl.

**1. Bannen sätts i spelet, inte här.** `css_addban` körs från konsolen eller
SimpleAdmins meny; sajten ser raden efteråt. Att stryka ett alternativ är alltså
en ändring i pluginets konfiguration, inte i den här kodbasen — och konsolvägen
tar fritext hur som helst.

**2. Ett tvingat val i stridens hetta blir en gissning.** En admin ser något
konstigt, bannar för att stoppa det, och tittar på demon EFTERÅT. Tvingas hen
välja "Aimbot" i det ögonblicket har sajten fått en etikett som ser ut som en
slutsats men är en magkänsla — exakt samma fel som §2c förbjuder sajten själv att
göra. En ärlig `Other` är mer sann än en gissad `Aimbot`, och den är dessutom
lättare att rätta.

**3. Det kostar oss nästan ingenting i den här kedjan.** Anledningen är en vikt
och inte ett filter (§2c). En bann med `Other` får samma kandidater som alla
andra; det enda som uteblir är bonusen. Det `Other` faktiskt skadar är den
PUBLIKA bannlistan och överklaganden — en medlem som läser "Other" får veta
ingenting.

### I stället: tvinget flyttas till valet av klipp

Ägarens andra förslag, och det är det som löser hela knuten: *"borde vi bara
hitta situationer, och sen om det är other eller unknown så blir dem tvingade att
byta samtidigt som de pekar ut den bästa videon?"*

Ja. Kravet står kvar, men det hamnar **där kunskapen finns**. Mitt i ronden är
en anledning en gissning; framför tre renderade klipp är den en iakttagelse.

```
bann sätts i spelet  → reason "Other", helt i sin ordning
        ↓
roboten renderar upp till tre klipp
        ↓
admin öppnar bannen för att peka ut det bästa
        ↓
ÄR ANLEDNINGEN VAG blir den ett obligatoriskt fält i samma formulär
        ↓
ett spara: valt klipp + riktig anledning
```

Fyra saker som gör skillnaden mellan ett tvång som fungerar och ett som
kringgås:

1. **Ett formulär, en knapp.** Anledningen är inte en andra uppgift som poppar
   upp efteråt — den står i samma ruta som klippvalet, med förslaget (§6b)
   ifyllt när vi har ett faktum att grunda det på. Att peka ut klippet och att
   säga vad det visar är ju samma tanke. Markeras flera klipp syr sajten ihop
   dem till ett (§5.6), så knappen heter samma sak oavsett hur många kryss som
   sitter i.
2. **Listan måste rymma allt man bannar för**, inte bara fusktyper. Griefing,
   spam, rasism, ban evasion. Saknas ordet man behöver kommer någon välja
   närmaste fel ord, och då har tvånget gjort datan sämre i stället för bättre.
   Det är exakt vad `Other` finns för att skydda mot, så ersättningen måste
   faktiskt vara komplett. **Ägaren skriver listan**, inte det här dokumentet —
   den ska spegla vad ni faktiskt bannar för.

   **Och `smurf` står inte på den.** Ägaren, 2026-08-10: *"smurf är inte nåt vi
   brukar banna för, är det duktiga spelare inne så är dem välkomna."* Skickligt
   spel är ingen förseelse på OldSwedes, och ordet skulle bara ge folk ett fack
   att stoppa en magkänsla i.
3. **Tvånget gäller bara valet, aldrig visningen.** Klippen går att se utan att
   någon fyller i något; det är att UTSE ett som kräver ordet. En admin som bara
   vill titta ska aldrig mötas av ett formulär.
4. **Ingen anledning krävs för att avfärda.** *"Visar inte fusket"* (§6) får inte
   kosta en etikett — den vägen leder till att inget klipp visar något och att
   admin ändå tvingats gissa.

Skrivningen går genom samma `updateDetails()` som formuläret och loggas som
adminens ändring, inte robotens — samma regel som §6b.

**Och den publika listan är den som vinner mest.** Ett klipp och ordet
"Multihack" säger en medlem vad som hände. "Other" och en tom ruta säger att
någon blev av med sin plats utan att någon vill berätta varför.

Kvar som andra nudge: **en rad i adminvyn som räknar bannar med vag anledning**,
länkad in i ändra-sidan. Den fångar de gamla bannarna som aldrig kommer få ett
klipp, där tvånget ovan aldrig utlöses.

Skulle ägaren ändå vilja strama åt i spelet är den bästa varianten att korta
menyn till de ord ni faktiskt använder och låta `Other` vara kvar som sista
utväg — men det är en plugin-fråga, och efter det här behövs den knappast.

---

## 7. Föreslagna endpoints

Alla under `/api/` med `Authorization: Bearer <token>`, CSRF-undantagna på samma
sätt som `/serverlog/`. Fel: `401` okänd/återkallad nyckel, `403` fel scope,
`404` okänd bann, `409` tillståndskrock.

### 7.1 Indexeraren skriver — en demo i taget, inte en händelse i taget

```http
POST /api/demos/analys
```

```json
{
  "path": "//colanas2/demos/demos/workshop",
  "filename": "2026-08-07_de_inferno.dem",
  "map": "de_inferno",
  "started_at": "2026-08-07T20:12:00+02:00",
  "ended_at":   "2026-08-07T21:04:00+02:00",
  "bytes": 184320000,
  "checksum": "…",
  "parser_version": "demoparser2 0.x",
  "analysis_version": "os_demo 1",
  "players": [
    { "steamid64": "765…", "nickname": "HAXX0R", "first_tick": 1200,
      "last_tick": 189000, "kills": 31, "deaths": 12 }
  ],
  "situations": [
    { "steamid64": "765…", "round": 12, "start_tick": 147900, "end_tick": 148700,
      "type": "fast_3k", "kill_count": 3, "score": 94.0,
      "meta": { "flags": ["aim_snap"], "headshots": 3 } }
  ]
}
```

**Hela demon i ett anrop.** Svaret är `{"demo_id": 123, "situations": 7}`.
Upprepas anropet för samma `path` + `filename` med samma `analysis_version`
händer ingenting — samma demo får inte indexeras två gånger, och idempotensen
ligger i unikhetsnyckeln snarare än i att indexeraren minns rätt.

Blir kroppen för stor för `post_max_size` får situationerna skickas i en andra
begäran mot `/api/demos/{id}/situationer`. Fortfarande batchat: ett anrop per
demo, inte per situation.

```http
POST /api/demos/{id}/misslyckades   { "error": "korrupt fil" }
GET  /api/demos/oindexerade?sedan=…  -- valfri; indexeraren kan ha egen bokföring
```

### 7.2 Renderkön — det Windows faktiskt behöver

> **BYGGT 2026-08-10, i en mindre form än förslaget nedan.** Vägarna heter
> `/api/klipp/*` och inte `/api/render/*`, och jobbet bär bara det renderaren
> behöver för att filma: demo, nedladdningslänk, SteamID64, två tickar, karta,
> rond, situationstyp.
>
> ```http
> GET  /api/klipp/bannar          bannarna vi har underlag på
> GET  /api/klipp/bannar/{id}     kandidaterna på en bann, utan lås
> POST /api/klipp/nasta           ta nästa jobb och lås det
> POST /api/klipp/{id}/klar       { "file": "…", "bytes": 41943040 }
> POST /api/klipp/{id}/misslyckades { "error": "csdm dog" }
> ```
>
> Skillnaderna mot förslaget, och varför:
>
> - **Inget nick och ingen anledning i jobbet.** Renderaren behöver dem inte för
>   att filma, och det som aldrig skickas kan inte hamna i en logg på en
>   Windowsburk. `admin_reason` och `player` ströks därför.
> - **Ingen `demo_smb`.** En väg räcker tills två behövs, och `/demofiles` går
>   redan förbi PHP.
> - **`orenderbar` finns inte än.** Skillnaden mellan otur och en demo CS2 aldrig
>   kommer kunna ladda är verklig och beskrivs rätt ovan — men den kan bara
>   observeras när renderaren körts skarpt, och det har den inte. Tills dess
>   räknar `misslyckades` försök och slutar efter tre.
> - **Tom kö svarar `{"job": null}` och inte 204.** Renderaren frågar var femte
>   minut i evighet; ett svar som ser ut som ett problem i loggen är fel svar på
>   ett normalläge.
> - **Låset släpps efter en timme** i stället för att kräva `puls`. Färre rörliga
>   delar, samma skydd: en burk som stängs av mitt i tar inte jobbet med sig.
>
> Kandidaterna ligger i `ban_highlight_candidate` (migration 0278), en rad per
> kandidat. Förut fanns bara antalet i `ban_highlight` och tickarna låg i en
> sessionsflash — borta vid nästa sidladdning, och omöjliga att nå för en maskin
> som kör någon annanstans, ibland dagar senare.

```http
POST /api/render/claim      { "worker": "WIN-RENDER-01" }
```

Svaret är **en post i `render_queue.json`:s form** (§2c) med bann-fälten
tillagda, så att `monthly_render.ps1` kan återanvändas nästan orörd:

```json
{
  "job_id": 812,
  "ban_id": 4711,
  "admin_reason": "Hacking",
  "expires_at": null,

  "order": 1,
  "candidate_id": "…",
  "type": "fast_multi_kill",
  "subtype": "3k",
  "score": 94.0,
  "category": "multi",

  "player": "HAXX0R",
  "player_steamid": "76561198000000000",
  "team_name": "TERRORIST",
  "team_num": 2,

  "demo": "20260807-201827-de_inferno.dem",
  "demo_url": "https://oldswedes.se/demofiles/workshop/20260807-201827-de_inferno.dem.gz",
  "demo_smb": "\\\\colanas2\\demos\\demos\\workshop\\20260807-201827-de_inferno.dem.gz",
  "map": "de_inferno",
  "round": 12,
  "tick": 147900,

  "start_tick": 147900,
  "end_tick": 148700,
  "action_start_tick": 148020,
  "action_end_tick": 148560,
  "original_end_tick": 148760,
  "safety_trimmed": false,

  "kills": 3,
  "action_seconds": 4.2,
  "weapons": ["ak47"],
  "flags": [],
  "details": {}
}
```

`player_steamid` blir `--focus-player` och därmed POV:en; `original_end_tick`
finns med för att wrappern återställer till den och lägger på 1,5 sekunder.

**Ett hopsytt klipp bär dessutom `segments`:**

```json
"segments": [
  { "start_tick": 141200, "end_tick": 141460 },
  { "start_tick": 143020, "end_tick": 143280 }
]
```

Saknas fältet syntetiserar renderaren ett segment ur `start_tick`/`end_tick`, så
vanliga kandidater påverkas inte. Se §6b2 för när det används.
`demo_url` och `demo_smb` är sajtens tillägg — månadskörningen läser en katalog,
bann-workern får veta var filen finns.

`204` när kön är tom. Claimet är atomiskt i en transaktion — två workers får
aldrig samma jobb.

```http
POST /api/render/{job}/puls          { "state": "rendering" }
POST /api/render/{job}/misslyckades  { "error": "CS2 kraschade", "retry": true }
POST /api/render/{job}/orenderbar    { "error": "CS2 kunde inte ladda demon" }
```

**`orenderbar` är en egen endpoint med flit.** En krasch är otur och ska
försökas igen; en demo CS2 inte kan ladda kommer aldrig gå att ladda, och varje
nytt försök är bortkastad GPU-tid på alla tre kandidaterna. Workern är den enda
i kedjan som kan observera skillnaden (§4 b), så den måste kunna säga vilken av
dem det var. Anropet sätter jobbet till `demo_expired` och bannen till samma —
inte till `failed`, för det var ingen som gjorde något fel.

`retry: false` (eller för många försök) markerar jobbet `failed`. Är alla
kandidater slut går bannen till `rejected` och §6 tar vid; finns en okörd
kandidat kvar köas den.

**Två vägar till filen, ingen av dem genom PHP.** Windowsmaskinen når samma
lagring över SMB, och samma fil ligger öppen på `/demofiles` där Apache serverar
den direkt. Workern väljer själv: SMB när den är monterad, URL när den inte är.
En demo på tiotals megabyte ska inte passera en PHP-process — samma regel som
`DemoLibrary` redan följer för `/demos`-sidan.

Asken §23 vill ändå att Windows renderar från **lokal disk**. Kopiera alltså ned
filen först, packa upp `.gz`, och rendera därifrån.

### 7.3 Klippet upp

Samma bitvisa protokoll som webben, med nyckel i stället för session:

```http
POST /api/render/{job}/klipp/start   { "filename": "…", "bytes": 41234567 }
POST /api/render/{job}/klipp/{clip}/bit     (rå kropp, ≤ 4 MB)
POST /api/render/{job}/klipp/{clip}/klar
```

Klippet får `source = 'robot'`, `chosen = 0`, `label` ur situationen ("Ace, rond
12") och `job_id`. Jobbet går till `done` och `ban_highlight.state` till
`uploaded` när minst ett klipp är färdigt.

### 7.4 Nicket och adressen

```http
POST /api/bans/{id}/uppgifter   { "nickname": "HAXX0R", "ip": "83.25.77.2" }
```

Två regler i koden, inte i skriptet:

- **Skriv aldrig över något en människa satt.** Fältet fylls bara om det är
  tomt. En admin som rättat ett nick ska inte se det bytas nästa natt.
- **`reason` och adminparet är utanför.** Det första är admins beslut, det andra
  styr vem som får häva.

Adressen är värd varningen som redan står på ändra-sidan: *"en adress som delas
— samma NAT, samma hushåll — stänger ute fler än den bannade."* Den hämtas ur
serverloggen; demon bär ingen IP.

### 7.5 Kön av bannar

```http
GET /api/bans/utan-klipp?timmar=24&limit=50
```

Sajtens egen worker hittar dem själv, men endpointen är den enklaste vägen att se
vad automatiken tycker att den har att göra — och den kostar ingenting när
resten ändå finns.

---

## 8. Vad sajten behöver installerat

**Python + demoparser2.** PHP kan inte läsa CS2-demos. Indexeraren blir ett
Python-skript som talar mot API:et ovan — det är dess enda beröring med sajten,
och därför spelar det ingen roll om det körs av systemd på webbservern eller från
en annan maskin.

```bash
python3 -m venv /opt/os-demo/venv
/opt/os-demo/venv/bin/pip install demoparser2
```

**Licensen kontrolleras innan det installeras.** Ägarens regel gäller allt som
läggs till: fritt/öppet, och licensen ska följa med. `demoparser2` ser ut att
vara MIT, men det ska verifieras och skrivas ned, inte antas.

**Räkna med CPU.** En demo tar minuter att parsa och de kommer varje kväll. Kör
indexeraren `nice`ad från en systemd-enhet, precis som `bin/ban-clip.php` redan
gör — webbservern ska svara på sidor medan den arbetar.

**Kopiera hem, parsa lokalt, städa.** Ägaren: *"den har ju nfs, men den kan ju
absolut ladda hem X antal demos och analysera dem."* Det är rätt ordning, och av
samma skäl som `DemoLibrary` slipper `stat()`: en parser läser filen hoppvis, och
hoppvis läsning över NFS är långsam för oss och tung för delningen. En sekventiell
kopia följd av lokal parsning är snabbare och snällare.

```
DEMO_WORK_DIR=/var/lib/os-demo/tmp   # utanför delningen, utanför webbroten
DEMO_BATCH=10                        # hur många som hämtas per varv
```

Uppackningen av `.dem.gz` sker i samma katalog, och den töms efter varje demo —
inte efter varje varv. En halv delning i en temp-katalog är hur en disk tar slut
en söndagsmorgon.

**Ingen SMB-montering behövs** — se §2b. `DEMOS_PATH` pekar redan rätt.

`ffmpeg` och `deploy/ban-clip.service` krävs redan för dagens klippkedja.
Migrationerna självapplicerar (`schema_migrations` + `Migrator::autoMigrate`), så
en deploy är `git pull`.

---

## 9. Vad som byggs, i ordning

1. **Bevisa parsern på en riktig demo.** SteamID, nick, rond, tick, view angles,
   `weapon_fire`, `player_death`. Gissa inte fältnamn — asken §35 Phase 1.
2. `api_client` + `Bearer` + CSRF-undantag. Allt annat hänger på den.
3. `demo` + `demo_player` + `POST /api/demos/analys`. Bevis: *"hitta alla demos
   med SteamID X"*.
4. `demo_situation` + situationsuttaget. **`harvest_candidates.py` gör redan
   jobbet** (§2c) — det som behövs är ett annat filter: en SteamID och ett
   tidsfönster i stället för en månad och alla spelare. Ingen beteendeanalys
   krävs för att komma igång.
5. `ban_highlight` + raden på ändra-sidan. **Här blir det synligt för admins
   utan en enda video**: sidan kan säga vilken demo och vilken rond man ska
   titta på, och det är redan värt tiden det sparar.
6. `render_job` + `/api/render/claim`, `puls` och `orenderbar`, med **kön
   händelsestyrd från att bannen sätts** — inte en nattlig körning. Varje timme
   som går är en chans att Valve släpper uppdateringen som gör demon obrukbar.
7. Uppladdningen + galleriet med upp till tre klipp + "visar inte fusket" +
   knappen som pekar ut det som ska synas publikt. **Här sitter tvånget** (§6c):
   är anledningen vag krävs en riktig i samma formulär som klippvalet. Kräver
   också att anledningslistan skrivs klar först — den måste rymma allt ni bannar
   för, inte bara fusktyper.
8. Anledningsförslaget (§6b), matat med teamkill-fakta och inget annat. Det
   hänger på steg 4 och kan byggas när som helst därefter — men inte före, för
   ett förslag utan iakttagelse att visa upp är precis det §6b förbjuder.
9. Eskaleringen till Watson: en bann utan klipp efter `EVIDENCE_GRACE_DAYS` blir
   ett ärende till ägaren (§4c). Sist i ordningen med flit — den delen bör byggas
   när resten fungerar och man vet hur ofta den skulle utlösas, annars är det
   första den gör att skicka trettio ärenden om gamla bannar.

Steg 1–5 kräver ingen Windowsmaskin och ingen GPU.

---

## 9b. Ask: SourceTV sparkas ut mitt i matchen (2026-08-21)

**Mätt, inte misstänkt. Tolv gånger på sex dagar, alla under speltid.**

Varje gång en nyansluten spelare placeras i ett lag sparkas SourceTV-inspelaren i
samma sekund, och resten av mappen spelas aldrig in. `OSBase[autoassign]` loggar
placeringen strax före — men se rutan nedan om vad vi faktiskt vet och vad vi
sluter oss till.

### Vad loggen visar

Två fall, sex dagar isär, med exakt samma tre rader i exakt samma ordning:

```
19:02:49  [DEBUG] OSBase[autoassign] autoassign steamid=… current_team=1 ct=0 t=0 -> Terrorist
19:02:50  "L0S<5><[U:1:52179867]>" switched from team <Spectator> to <TERRORIST>
          DemoRecorder kicked by Console (NETWORK_DISCONNECT_KICKED)
          Completed SourceTV demo "20260818-185317-de_rainfall.dem":
                  Recording time 572.8, Size 11579087
```

```
15:10:38  [DEBUG] OSBase[autoassign] autoassign steamid=… current_team=1 ct=1 t=0 -> Terrorist
          "Strogg<4><[U:1:29809014]>" switched from team <Spectator> to <TERRORIST>
          DemoRecorder kicked by Console (NETWORK_DISCONNECT_KICKED)
          Delrow kicked by Console (NETWORK_DISCONNECT_KICKED)
          Completed SourceTV demo "20260821-150958-de_foroglio.dem":
                  Recording time 40.0, Size 1065023
```

Att den riktiga boten `Delrow` åker med i det andra fallet är beviset för att det
är en BOT-RENSNING och inte något riktat mot inspelaren: `DemoRecorder` sitter i
en bot-slot och räknas med.

**VAD VI VET OCH VAD VI SLUTER OSS TILL, hållet isär.** Vi har läst er logg, inte
er kod. Det vi VET är att sparken kommer i sekunden efter att `autoassign` loggat
sin placering, i båda fallen, och att `kicked by Console` betyder ett kommando
från servern snarare än en spelare. Vi har uteslutit sajten: varje rcon-kommando
därifrån granskningsloggas, och det enda som står den kvarten är ett mappbyte
(`host_workshop_map`, 15:08:55).

Det vi INTE vet är var kommandot utfärdas. Rimliga kandidater, i den ordning vi
skulle titta:

1. **`autoassign` självt**, om den rensar bottar för att göra plats
2. **En annan OSBase-modul** som lyssnar på samma lagbyte
3. **`bot_quota` i en cfg**, alltså motorn — då är det ingen kodfråga alls utan
   en inställning, och `IsHLTV`-fixen nedan hjälper inte

Är det den tredje ber vi om fel sak, och då är svaret vi vill ha tillbaka just
det. En sökning efter `bot_kick`, `BotKick` eller `IsBot` i er kodbas avgör det
på en minut, och ni har den kunskapen som vi saknar.

Så här ser ett normalt slut ut, för jämförelse — samma server, samma kväll:

```
[DEBUG] OSBase[demos] Match has ended.
[INFO]  OSBase[demos]: Stopped recording demo (match_end).
        Completed SourceTV demo "20260821-145951-de_palais.dem"
        "DemoRecorder<3><BOT><Unassigned>" disconnected (reason "NETWORK_DISCONNECT_HLTVSTOP")
```

`HLTVSTOP` = modulen bad om det. `KICKED` = någon annan gjorde det. **Skillnaden
finns redan i loggen och används inte.**

### Alla tolv, med klockslag

```
08/14  16:09, 16:30, 18:21, 22:59
08/15  18:33
08/17  18:38
08/18  19:02, 19:26, 19:50     ← tre mappar i rad
08/19  17:56, 18:20
08/21  15:10
```

Ingen på natten. Alla mellan 15:10 och 22:59 — alltså när folk spelar, vilket
är precis när bottarna blir överflödiga och när bannar sätts.

### Hur det ser ut från sajtens sida

Kedjan gör rätt hela vägen och hittar ändå ingenting, vilket är det obehagliga:
felet ser ut som ett fel i indexeringen.

Foroglio-demon kom in i indexet som **giltig** — `error IS NULL`, en spelare, noll
situationer. Det såg ut som en tom server. Den enda kill som hann ske låg på
`first_tick = last_tick = 2097`, och **2097 / 64 tickar ≈ 33 sekunder**. Sedan
tvärstopp. Serverloggen visar full match med sex spelare elva minuter senare.

Det var tickräkningen som avslöjade det, inte något i indexet. En avhuggen demo
går inte att skilja från en tom kväll utan att räkna om tickar till sekunder —
och det är därför det tog en kväll att hitta.

### Vad vi ber om

**1. Undanta SourceTV när bottar sparkas.** `IsBot` ensamt räcker inte —
inspelaren är både bot och HLTV. CounterStrikeSharp har flaggan:

```csharp
if (player.IsBot && !player.IsHLTV)   // först då är den en bot att sparka
```

**2. Låt `OSBase[demos]` starta om inspelningen när den försvinner oombedd.**
Det här är det som faktiskt gör den robust, för det kräver inte att någon räknat
upp allt som kan sparka den i framtiden. Skillnaden finns redan: `HLTVSTOP` efter
`match_end` är normalt, `KICKED` mitt i en match är det inte.

Samma ändring lagar en andra sak på köpet. Modulen skriver i dag:

```
[DEBUG] OSBase[demos] demo already started for this map, skipping (match_start).
```

Det är ett **minne**, inte en kontroll. Frågar den i stället om inspelningen
faktiskt går, täcker matchstarten också fallet där något dödat den under warmup.

### Vad det kostar att inte göra det

Priset är inte en trasig fil utan en **saknad**, och saknader syns inte.

Fem av elva bannar har klipp. Resten fick `no_situation_found` eller inget alls —
och minst en av dem, `20260818-185317-de_rainfall`, hade sina nio och en halv
minuter men saknar resten av mappen. Vi vet inte vad som fanns där, och det är
hela poängen: en avhuggen demo svarar "inget stack ut" med samma tonfall som en
lugn kväll.

### Acceptanskriterium, mätbart

Kör efter en kväll:

```
grep -c "DemoRecorder kicked by Console" cs2server-console.log
```

Ska vara **noll**. Varje träff är en mapp där resten inte finns.

Och i sajtens index ska inspelningstiden stämma med mappens längd — en demo som
slutar efter fyrtio sekunder på en mapp som pågick i tolv minuter ska inte längre
vara möjlig.

### En sak vi INTE ber om

**Warmup ska fortsätta vara oinspelat.** Det är ett medvetet val (ägaren,
2026-08-21) och vi rör det inte. Följden är däremot värd att skriva ned, för den
förvånade oss: en bann satt under warmup kan aldrig få ett klipp. Det gäller
särskilt reklam- och spambannar, som ofta sätts innan matchen börjat — det var en
sådan som startade hela den här utredningen. Sajten ska säga det rakt ut i
stället för att svara "spelaren finns inte i någon indexerad demo", vilket låter
som ett besked om personen. Det är sajtens jobb, inte OSBase.

### OSBase svarar (2026-08-21)

**Var kicken utfärdas: inte i OSBase-kod.** Genomsökt hela `src/` efter
`BotKick`, `bot_kick`, `bot_quota` och varje `Server.ExecuteCommand`/
`kick`-anrop — ingenting. `AutoAssign.cs`, modulen som loggar raden precis
före sparken, rör aldrig bottar: dess enda skrivande handling är
`ChangeTeam()` på den nyanslutna människan, och `IsKnownHuman`/
`IsEligiblePlayer`/`CountTeams`/`FindHumanBySteamId` filtrerar bort bottar
på alla fyra ställen den använder dem. Ingen annan modul (`TeamBalancer.cs`,
`Teams.cs`) har någon bot-borttagningslogik heller. **Fix 1 (`IsBot &&
!IsHLTV`) ber om fel sak — det finns ingen kick-kod här att sätta den
guarden i.** `bot_quota 2` är satt hos er, i cfg, precis som ni skrev; om
det är `bot_quota_mode`/`sv_maxplayers` som gör att just detta
lagbyte-mönster kickar en bot är en motor-/cvar-fråga vi inte kan avgöra
från källkoden, öppen i väntan på de två övriga cvar-värdena.

**Fix 2 byggd, oavsett rotorsak.** `Demos.cs` spårar nu sin egen avsikt
(`mapEndHandled`, satt precis innan `tv_stoprecord` anropas) i stället för
att gissa på anslutningsorsaken — varje `EventPlayerDisconnect` där
`Userid.IsHLTV` är sant och vi INTE själva bad om stoppet startar om
inspelningen efter en kort fördröjning, oavsett vad som kickade den.
Skillnaden mellan `HLTVSTOP` och `KICKED` (som ni pekade på) behöver alltså
aldrig avkodas numeriskt — egen avsikt räcker för att skilja dem åt. Samma
ändring gjorde också guarden `recordingStartedForMap` (tidigare ett minne)
till en riktig kontroll: `RunWarmupEnd` frågar nu om SourceTV faktiskt är
anslutet innan den hoppar över en omstart. Warmup förblir oinspelat, orört.
Byggt, 0 varningar — släpps i nästa version.

---

## 10. Öppna frågor

1. ~~Når webbservern demoerna?~~ **Besvarad:** ja, lokalt under
   `/var/www/oldswedes.se/public/demofiles/` (NFS-montering symlänkad i
   docroten). Indexeraren kör på webbservern, renderaren på Windows når samma
   filer över SMB eller URL. Se §2b.
2. ~~Hur märker vi att en demo blivit orenderbar?~~ **Besvarad, och svaret är
   "det går inte i förväg".** Bara renderworkern får veta, genom att CS2 vägrar
   ladda filen. Därför sätts `demo_expired` av workern och inte av en klocka, och
   därför är kön händelsestyrd: jobbet skapas när bannen sätts. Se §4 b).
3. ~~monthly_highlights ska göras läsbar.~~ **Levererad och läst** 2026-08-09,
   dokumenterad av ChatGPT. Fynden står i §2c: ingen `record.jsonc`, ingen
   beteendeanalys, och en renderkö vars schema sajten ska tala. Kvar att svara
   på: **var ska bann-skördaren köra?** Månadskedjan kör Python på Windows med
   `py`-launchern; vår indexerare ska köra på webbservern. Samma kod, annan
   miljö — troligen bara `py` → `python3` och sökvägar, men det ska provas och
   inte antas.
4. ~~POV eller freecam?~~ **Besvarad: POV, alltid.** Ägaren: *"POV är absolut
   nödvändigt så man kan följa siktet."* Det är rätt och det är dessutom en
   princip snarare än en inställning — siktet ÄR beviset. En aimbot syns i hur
   hårkorset far, en triggerbot i när skottet går, en wallhack i vad hårkorset
   följer genom väggen. En freecam visar vad som hände i ronden; POV visar vad
   spelaren gjorde, och det är den frågan en bann handlar om.

   Följden för renderjobbet: `steamid64` är inte metadata utan en av de fyra
   uppgifter Windows behöver (demo, SteamID, starttick, sluttick).
