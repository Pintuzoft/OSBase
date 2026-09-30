# Order till OSBase: kvartalsbytet 2026Q4

*Den här filen står på egna ben: hänvisningar till `docs/…` och `src/…` i
OSWebs (privata) repo är bakgrund, inte något ni behöver läsa. Koden ni ska
räkna likadant som står ordagrant i bilagorna längst ned.*

*Från OSWeb, 2026-09-29. Ägarens beslut, samlade på ett ställe. Bakgrunden och
mätningarna står i `docs/osbase-elo-contract.md`. Den här filen säger vad som ska
göras, i vilken ordning och hur vi båda ser att det blev rätt.*

**Säsongen 2026Q4 börjar torsdag 1 oktober.** Allt här borde helst vara ute då.
Går inte det finns en prioritetsordning i avsnitt 9 och en ofarlig mellanlösning
som gör att resten kan landa några dagar senare utan att någon märker det.

---

## 1. Säsongsbytet: både poäng och rating börjar om, och det gamla står kvar

- **Poängen börjar på 1 000**, inte 0 (`START_POINTS` i `points_formula`).
  Döden kostar poäng nu (avsnitt 4), och startpoängen är bufferten.
- **Ratingen nollställs till 1 000 varje kvartal.** Det är nytt. Förut sa vi
  "nollställs aldrig", men då visste ingen att talet skulle glida till ~3 000.
- **Förra kvartalet ska stå kvar**, precis som poängen gör. `elo_points` har
  redan `season`. **`elo_rating` behöver också `season`**, med
  `(steamid64, season)` som nyckel. Nollställningen blir då bara att en ny
  säsong får nya rader. Ingenting skrivs över och ingenting raderas.
- **`matches` börjar också om per säsong**, så att alla får den provisoriska
  K-faktorn igen och fältet hinner sortera sig snabbt.
- Lagbalanseringen på LAN bör läsa förra kvartalets slutrating tills spelarens
  `matches` i den nya säsongen har passerat den provisoriska gränsen.

## 2. Ratingen: gör den till ett nollsummespel

Ratingen har blåsts upp från 1 000 till ~3 000 på ett kvartal. Orsaken syns på
raderna i liggaren:

- Skabbräv dödade LimpaN^ med ak47 **hs**, 3 269 mot 3 296: `32 · 0,539 · 1,2` = **+20,7**
- Skabbräv dödades av LimpaN^ med glock **hs**, 3 290 mot 3 393: **−11,4**, alltså samma som utan hs

Dräparen får headshot-bonusen, men offret betalar den inte. Varje headshot
skapar ~3 rating ur ingenting.

**Ta bort `(headshot ? 1.2 : 1)` ur ratingen.** Headshot har nu sin plats i
poängen (`HS_BONUS`). Ratingen ska mäta vem som vinner duellen, inte hur.

**Fråga tillbaka:** rör bonusraderna i `elo_bonus_event` ratingen? Vi har sett
`suicide_penalty` med `rating_delta` −5,00. Om rondvinster eller andra bonusar
*lägger till* rating är de en andra läcka av samma slag. Svara med vilka
`kind` som har `rating_delta` ≠ 0. Är det bara straff är det lugnt.

K-asymmetrin (50 provisoriskt mot 32) lämnar vi. Den tar i stort sett ut sig
själv, och vi mäter den efteråt (acceptans 5).

## 3. Poängen: placeringen avgör

Ägarens vision: ettan har ett pris på huvudet. En kill är värd mer ju högre
offret står på **poängtopplistan** jämfört med dig. Ratingen justerar lite men
är inte avgörande.

**Referensimplementationen står i Bilaga A** längst ned (OSWebs
`src/Services/PointsFormula.php`, ordagrant). Räkna likadant. Om ni ser en skillnad
mellan den och texten nedan är det koden som gäller, och säg till.

```
place   = placering på säsongens poänglista vid MAPPSTART (1 = flest poäng)
N       = antal spelare med en elo_points-rad i säsongen
g       = clamp((place_dräpare − place_offer) / N, −1, 1)

g > 0:  bas = EVEN + (TOP − EVEN) · g^EXP
g ≤ 0:  bas = EVEN + (EVEN − FLOOR) · g

rmod    = 1 + W · (2 · (1 − expected) − 1)          // 1 − expected ur ratingens uträkning
kill    = bas · rmod · weapon_weight                 // högst MAX_KILL om MAX_KILL > 0
points  = round(kill + (headshot ? HS_BONUS : 0), 2)
```

- **Placering vid mappstart**, inte live. Läs ställningen en gång när mappen
  börjar och håll den under mappen.
- **Delad placering** vid lika poäng, med standardrankning (1, 2, 2, 4). En
  spelare utan rad i säsongen står på `N + 1`.
- **`1 − expected`** är samma tal som ratingen redan räknar fram vid killen.

**Värnplikt:** dräparens första `WARMUP_KILLS` kills i säsongen ger platt `EVEN`,
oavsett offer, vapen och headshot. Räkna antalet kills per spelare och säsong.

## 4. Döden kostar poäng

Förut kunde poängen bara växa. Det är upphävt av ägaren, men avdraget är
spegelvänt mot vinsten så att det inte lönar sig att campa:

```
loss = min(DEATH_SHARE · bas_dräpare, offrets saldo)   // aldrig under 0
```

- Bara **placeringsbasen**. Dräparens ratingterm, vapenvikt och headshot kostar
  inte offret något.
- Är **offret** i sin värnplikt kostar döden **ingenting**.
- Är **dräparen** i sin värnplikt kostar döden platt `DEATH_SHARE · EVEN`.
- Avdraget klipps mot saldot, och **det klippta värdet är det som skrivs**.

## 4b. Bonusarna, i den nya skalan

`elo_bonus_event` delar redan ut assist och rondvinst, och bombhandlingarna är
planerade som nya `kind` (`docs/rank-and-points-design.md`). **Men värdena där
sattes när en kill var 10p.** Nu är en kill mellan grannar 2p (`EVEN`), så
bonusarna läses ur `points_formula` i samma skala:

| name | förval | när |
|---|---|---|
| `BONUS_PLANT` | 2 | den som planterar |
| `BONUS_DEFUSE` | 2 | den som desarmerar |
| `BONUS_BOMB_PICKUP` | 1 | den som plockar upp bomben |
| `BONUS_BOMB_DROP` | 1 | **avdrag** för den som släpper eller kastar bomben med flit |
| `BONUS_ROUND_WIN` | 1 | varje spelare i laget som vann rundan |
| `BONUS_ASSIST` | 1 | den som assisterade en kill |

**Bomben (ägaren, 2026-09-29):** "att slänga bomben så ger du upp ditt uppdrag
och ger det till någon annan."

- **Plocka upp:** `+BONUS_BOMB_PICKUP`. Den som tar på sig uppdraget.
- **Släppa eller kasta med flit:** `−BONUS_BOMB_DROP`, **alltid**, också om man
  spawnade med bomben. Att passa till en lagkamrat blir alltså −1 för den som
  lämnar och +1 för den som tar, och uppdraget byter ägare.
- **Dö med bomben:** kostar inget ("förhoppningsvis har man burit den närmare
  en bombplats"). `bomb_dropped` kommer i båda fallen. Skilj dem på om bäraren
  lever när bomben släpps.
- **Spawna med bomben:** ingen bonus.
- Avdraget skrivs som en egen rad i `elo_bonus_event` med negativt värde, så att
  liggaren fortfarande summerar till `elo_points`.

Bonusarna rör **inte ratingen** (se frågan i avsnitt 2).

## 4c. Kantfallen

Förval, så att ni inte behöver gissa. Säg till om något krockar med hur modulen
fungerar i dag.

- **Bonussorternas namn** i `elo_bonus_event.kind`: `bomb_plant`, `bomb_defuse`,
  `bomb_pickup`, `bomb_drop`, `round_win`, `assist`. Sajten visar `kind` rakt av
  i kontoutdraget, och DamageReport översätter dem till `Bomb planted` osv.
  Befintliga namn som redan betyder samma sak behåller ni hellre än byter.
- **Bonusar under värnplikten:** gäller som vanligt. Värnplikten gäller bara
  kills och dödsfall.
- **Självmord, fall, död av världen:** ingen poängförändring. Ratingstraffet
  (`suicide_penalty`) som i dag, utom när autoassign orsakade det (avsnitt 12).
- **Bottar:** varken poäng eller rating, åt något håll.
- **Poängraden skapas** vid spelarens första händelse i kvartalet, med
  `START_POINTS`. Summakontrollen i avsnitt 10 bygger på det.
- **Teamkills:** *ägarens beslut väntar.* Förval tills vidare: ger dräparen
  ingenting och kostar offret ingenting, varken poäng eller rating.

## 5. Vapenvikterna: tillämpas fortfarande inte

Tre kills från 2026-09-29, alla med vikt över 1, betalade exakt vad vikten 1,00 ger:

| Kill | Vikt | Utan vikt | Med vikt | Liggaren |
|---|---|---|---|---|
| galilar, 3 246 mot 3 371 | 1,10 | 13,45 | 14,80 | **13,44** |
| mp7, 3 272 mot 3 372 | 1,30 | 12,80 | 16,64 | **12,79** |
| usp_silencer, 3 281 mot 3 066 | 1,40 | 4,49 | 6,29 | **4,49** |

Läs `weapon_point_weight` (matchning exact → prefix → suffix, förval 1,00, se
`WeaponWeightRepository::ruleFor()`) och multiplicera in vikten i killens del
enligt avsnitt 3 (matchningen ordagrant i Bilaga B). Den ska inte användas under värnplikten och inte i dödens
avdrag. Vikten ska synas i `attacker_points_delta`, inte bara i ackumulatorn.

## 6. Värdena: `points_formula`, som sajten skriver

Sajten äger värdena och skriver **hela uppsättningen** till er databas vid varje
ändring (`PointsFormulaSync`, samma mönster som `weapon_point_weight`):

```sql
CREATE TABLE IF NOT EXISTS points_formula (
    name  VARCHAR(32) NOT NULL,
    value DECIMAL(12,4) NOT NULL,
    PRIMARY KEY (name)
) ENGINE=InnoDB;
```

| name | förval | betyder |
|---|---|---|
| `START_POINTS` | 1000 | vad alla står på när säsongen börjar |
| `FLOOR` | 1 | ettan fäller sista platsen |
| `EVEN` | 2 | grannar på listan; också värnpliktens pris |
| `TOP` | 25 | sista platsen fäller ettan |
| `EXP` | 1,5 | kurvans branthet uppåt |
| `W` | 0,2 | ratingens vikt, ±20 % |
| `HS_BONUS` | 1 | fast tillägg för headshot |
| `WARMUP_KILLS` | 50 | värnplikt, kills per säsong |
| `DEATH_SHARE` | 0,5 | andel av dräparens bas som offret förlorar |
| `MAX_KILL` | 0 | tak per kill före headshot; 0 = inget tak |
| `BONUS_PLANT` | 2 | se avsnitt 4b |
| `BONUS_DEFUSE` | 2 | |
| `BONUS_BOMB_PICKUP` | 1 | |
| `BONUS_BOMB_DROP` | 1 | avdrag, skrivet som positivt tal |
| `BONUS_ROUND_WIN` | 1 | |
| `BONUS_ASSIST` | 1 | |

**Läs vid rondstart**, som vikterna. Hårdkoda inga förval: tabellen innehåller
alltid alla rader. En HeadAdmin ändrar värdena på `/admin/poangformel`, och
ändringen ska gälla från nästa rond utan omstart.

## 7. Liggaren: nya kolumner

På `elo_kill_event`:

| Kolumn | Typ | Innehåll |
|---|---|---|
| `victim_points_delta` | DECIMAL | avdraget, ≤ 0, efter klippningen |
| `attacker_place` | INT | dräparens placering vid mappstart |
| `victim_place` | INT | offrets placering vid mappstart |
| `board_size` | INT | N vid mappstart |
| `round_no` | INT | rondnumret i mappen |
| `attacker_in_air` | BOOL NULL | `Attackerinair` från `EventPlayerDeath` |
| `attacker_in_water` | BOOL NULL | om dräparen stod i vatten, se nedan |

`round_no` behövs också på **`elo_bonus_event`**, så att sajten kan visa rundan
som den såg ut i spelet.

`attacker_in_water` finns **inte** på händelsen. Den måste läsas från
dräparens pawn i ögonblicket, via vattennivån eller den flagga motorn använder
när spelaren står i vatten. Vi har inte kunnat verifiera vilket fält CS2
exponerar. **Svara med vilket fält, eller att det inte går.** Luft och vatten ger
inriktningen Flygvapnet (se `docs/BACKLOG.md`, "Inriktning"). Amfibiekåren följer numera SMG:erna, så `attacker_in_water` är inte längre nödvändig, men gärna ändå om den är billig.

Saknas ett värde ska kolumnen vara NULL, inte false. "Vet inte" och "stod på
marken" är olika svar.

## 8. DamageReport visar poängen

**Ägarens beslut (2026-09-29): behåll dagens rapport som den är och lägg bara
till poängen. Ingen rating.** Så här ser den ut i dag:

```
===[ Damage Report (hits:damage) ]===
Victims:
- Pintuz (Killed): 1 hits, 1 damage [Body 1:1]
- Rip (Killed): 3 hits, 174 damage [Chest 2:63, Head 1:111]
Attackers:
- Pintuz (Killed by): 1 hits, 1 damage [Body 1:1]
- Rip (Killed by): 10 hits, 109 damage [Stomach 7:85, Chest 1:8, L-Leg 1:8, R-Leg 1:8]
```

Samma runda, kortad och färgkodad (ägarens utkast, 2026-09-29):

```
===[ Damage Report (hits:damage) ]=== +11,4p
Victims:
- Pintuz (Killed): 1:1 [Body 1:1] +2,0p
- Rip (Killed): 3:174 [Chest 2:63, Head 1:111] +13,4p
- Kaffepulver: 1:27 [Chest 1:27]
Attackers:
- Pintuz (Killed by): 1:1 [Body 1:1] -1,0p
- Rip (Killed by): 10:109 [Stomach 7:85, Chest 1:8, L-Leg 1:8, R-Leg 1:8] -6,2p
- Trewe: 2:45 [Arm 1:20, Chest 1:25]
Bonus:
- Bomb planted +2,0p
- Round won +1,0p
```

(Rubrikens summa i exemplet täcker bara raderna som visas.)

**Kortare:** `3 hits, 174 damage` blir `3:174`. Rubriken säger redan
`(hits:damage)`, så formen förklarar sig själv, precis som zonerna gör i dag.

**Färgerna.** Chatten har en fast palett (`ChatColors`) och ljusstyrkan går inte
att reglera fritt. Fyra nivåer, så att ögat vet var det ska börja: det som
hände, hur mycket, detaljerna, ramen.

| Rad | nick (+ `(Killed)`-texten) | `hits:damage` | `[zoner]` | poäng |
|---|---|---|---|---|
| rubriken | `Silver`, hela texten | | | `Green` om plus, `Red` om minus, `Silver` om exakt 0 |
| `Victims:` / `Attackers:` | `Silver` | | | |
| du **dödade** | `Green` | `Default` | `Grey` | `+Np` i `Green` |
| du bara **skadade** | `Olive` | `Grey` | `Grey` | inget |
| du **dödades av** | `Red` | `Default` | `Grey` | `-Np` i `Red` |
| du bara **skadades av** | `LightRed` | `Grey` | `Grey` | inget |
| `Bonus:` | `Silver` | | | |
| en bonusrad | texten i `Grey` | | | `Green` om plus, `Red` om minus |

- Bindestrecket först på raden är `Silver`, som ramen.
- **`Head` i zonlistan är `Gold`** på alla rader, så att headshots syns direkt.
  Det är det enda undantaget.
- `(Killed)` och `(Killed by)` har nickets färg, så att "Rip (Killed)" läses som
  en enhet.
- Rader med bara skada har bara nicket i färg och resten grått, så att de hamnar
  i bakgrunden. Rader med en kill har tydlig färg, ljusare siffror och poäng.
- Inga fler färger än så. Fler nyanser gör rapporten svårare att läsa, inte
  lättare.

**`(Killed)` och `(Killed by)` står kvar som text.** Rödgrön färgblindhet är den
vanligaste sorten, och för den är `Green` och `Olive` nästan samma färg. Texten
gör raden läsbar ändå.

- **Poängen:** `attacker_points_delta` på `(Killed)`-rader och
  `victim_points_delta` på `(Killed by)`-rader. Är avdraget 0 (värnplikt, eller
  saldot redan på golvet) skrivs `-0,0p` hellre än ingenting.
- **Rubrikens summa** är rundans netto, bonusarna inräknade. Den kräver att
  rundans bonusar är räknade **före** utskriften.
- **Bonus-blocket** visar det som gav eller tog poäng utan att vara en kill
  eller död (ägaren, 2026-09-29), en rad per sort med sortens summa för rundan:
  `Bomb planted`, `Bomb defused`, `Bomb picked up`, `Bomb dropped`,
  `Round won`, `Assist`. Engelska, som resten av rapporten. En rad med summan
  0 visas inte. Blocket utelämnas helt om
  rundan inte gav några bonusar.
- **Rader utan kill** åt något håll får inget tal.
- **Ingen rating** i rapporten.
- **Talen är liggarens tal**, inte en egen uträkning. Decimalkomma, en decimal.
- **Skicka gärna en skärmdump** av första versionen i spelet. Hur färgerna
  ser ut i chatten går inte att avgöra ur en textfil.

## 9. Prioritet om allt inte hinner till torsdag

1. **Säsongsbytet** (avsnitt 1) och **nollsummeratingen** (avsnitt 2). Det här
   går inte att göra i efterhand. En rating som börjar glida på dag ett gör
   det resten av kvartalet.
2. **Mellanlösning för poängen, om formeln inte hinner:** betala platt `EVEN`
   per kill från start, med 0 i avdrag vid död. Det är **exakt vad värnplikten
   ändå betalar**, så ingen spelare kan se någon skillnad förrän folk har
   passerat 50 kills. Den fulla formeln (avsnitt 3–5) kan landa några dagar in
   utan att en enda poäng hamnat fel.
3. **Liggarens kolumner** (avsnitt 7). `attacker_place`/`victim_place`/`board_size`
   och `victim_points_delta` ska landa samtidigt som formeln, eftersom formeln
   inte går att kontrollera utan dem. Luft och vatten så snart som möjligt,
   eftersom varje dag utan dem är data som saknas.
4. **DamageReport** (avsnitt 8): poängen på dagens rapport. Kan komma sist.

## 10. Så ser vi att det blev rätt

Kontrollerna körs mot liggaren efter en kväll med riktigt spel.

1. **Värnplikten syns.** Varje spelares första `WARMUP_KILLS` rader i säsongen
   har `attacker_points_delta` = `EVEN`, exakt.
2. **Varje kill därefter räknas om exakt** med `PointsFormula` ur
   `attacker_place`, `victim_place`, `board_size`, ratingarna, vapnet och
   hs-flaggan, på två decimaler. Samma sak för `victim_points_delta` med
   `deathLoss()`.
3. **Summainvarianten:** `START_POINTS + SUM(attacker_points_delta) +
   SUM(victim_points_delta) + bonusrader = elo_points`. Fyra decimaler, alla
   spelare.
4. **Ratingens implicita K** är 32 (50 provisoriskt) för varje kill, med och
   utan headshot.
5. **Snittratingen står still.** Över en vecka ska snittet bland etablerade
   spelare hålla sig runt 1 000, inom brus.
6. **En ändring på `/admin/poangformel`** syns i nästa ronds kills utan
   serveromstart.
7. **Förra kvartalet står kvar:** `elo_rating` har rader för 2026Q3 med
   slutratingen och nya rader för 2026Q4 som börjar på 1 000.

## 11. Demo-backfillen: gamla kvartal ur gamla demos (inte till torsdag)

Ägaren vill fortfarande köra Elo på de arkiverade demona, som planerat i
`docs/osbase-demo-backfill.md`. **Nollställningen per kvartal gör det mycket
enklare.** Dokumentets stora problem var att ratingen skulle överleva varje
nollställning, så att backfillen måste köras innan någon hade ett tal att
förlora. Det gäller inte längre:

- **Varje kvartal är en egen säsong som börjar på 1 000.** Ett gammalt kvartal
  räknas fram ur sina egna demos, i tidsordning, och rör aldrig det pågående.
  Det finns ingen live-rating att stämma av mot.
- **Omkörbar per kvartal:** varje importerad rad bär demons id. En rättelse är
  "radera det importen skrev för kvartalet, kör igen". Ingen `RebuildFromLedger()`
  behövs.
- **Alla kvartal arkivet täcker**, så långt bakåt det går. Ägaren: Elo-rating,
  poäng och hela poängsystemet för varje kvartal. Varje kvartal får sin
  slutrating, sin ställning och sina rader i liggaren, och därmed sin rang.
- **Samma formel som live.** Placering vid mappstart, värnplikt, dödens avdrag
  och vapenvikter räknas fram ur den ställning som replayen själv bygger upp.
  Ett gammalt kvartal ska se ut som om den nya formeln hade gällt då.

### Så körs ett kvartal (ägaren, 2026-09-29)

Varje kvartal spelas upp som om det hände på nytt, med samma kod som live:

1. **Nollställ kvartalet:** alla står på `START_POINTS` (1 000p) och 1 000 i
   rating, `matches` 0, värnplikten på 0 kills. Radera det som en tidigare
   körning skrev för kvartalet (`source = 'demo'`). Inget annat rörs.
2. **Spela upp alla händelser i äkta tidsordning**, över alla servrar och
   demos samtidigt. Spelades två servrar parallellt blandas deras händelser
   efter klockslag. Kör inte demo för demo, för då räknas en kill på server 2
   mot en ställning som inte fanns just då. Tid = demons start (filnamnet,
   Europe/Stockholm) + tick / 64.
3. **Allt tillstånd bärs vidare**, händelse för händelse: rating, poäng,
   `matches`, värnpliktens kills och ställningen. Har en spelare skrapat ihop
   1 400r och 8 000p fram till en demo, är det de talen nästa kill räknas från,
   och placeringen vid mappstart är ställningen i uppspelningen i det
   ögonblicket.
4. **Logga allt som live loggar:** varje kill och död med alla kolumner i
   avsnitt 7, varje bonus (plant, desarmering, bomben, rondvinst, assist) och
   alla stats-tabeller i listan nedan. Samma rader som om kvartalet spelats med
   den nya koden.
5. **Skriv kvartalets slutläge:** `elo_rating (steamid64, season)` och
   `elo_points (steamid64, season)`.

**Graderna följer av sig själva.** Sajten räknar rangen (Malaj … General …
Fältmarskalk) ur kvartalets slutrating och antal kills. Blir de rätt per
kvartal får varje spelare rätt grad för varje kvartal, utan något extra.

**Formelns värden:** uppspelningen använder `points_formula` som den står när
körningen görs. Ändras värdena efteråt körs kvartalet om. Det är skälet till att
körningen ska gå att upprepa.

**Kontroll per kvartal:**

- `START_POINTS + SUM(attacker_points_delta) + SUM(victim_points_delta) +
  bonusrader = elo_points` för varje spelare, fyra decimaler.
- `1000 + SUM(ratingdeltor) = elo_rating` för varje spelare.
- **Två körningar ger exakt samma resultat.** Samma demos, samma värden, samma
  tal. Gör de inte det beror ordningen på något annat än tiden, och då är
  punkt 2 inte uppfylld.

**Det som krävs:** `season` på `elo_rating` (avsnitt 1), och den **`ScoreKill`**
som kontraktet redan beskriver: Elo-räkningen utbruten ur `OnPlayerDeath` till
en funktion över ren data (id, namn, lag, hs, vapen, mapp, tid), som både
spelhändelsen och en demodriver kan anropa. Rör inte `skill_log`: lagbalanseringen
på LAN läser den tills en ersättare finns.

**Hela uppsättningen, inte bara Elo.** Ägaren vill att backfillen fyller
profilen bakåt: kvartalsväljaren på profilen och aktivitetsmatrisen ska ha data
för de gamla kvartalen. De läser tabeller som en replay ändå skriver:

| Yta på sajten | Tabell |
|---|---|
| aktivitetsmatrisen (speltid per dag) | `player_daily_stat.seconds` |
| formkurvan och dagsrutan | `player_daily_stat` (rating, points, kills, rounds, hits, shots, damage) |
| träffkartan | `player_hit_stat`, `player_weapon_shots` |
| clutches, multikills | `player_clutch_stat`, `player_multikill_stat` |
| dueller och nemesis | `player_duel_stat`, `player_duel_total` |
| mappar och ronder | `player_map_result`, `player_round_stat` |
| kontoutdraget | `elo_kill_event`, `elo_bonus_event` |

Alla med sin `season` och sin riktiga dag. **Dagen räknas i en zon, aldrig med en
offset:** demofilnamn står i lokal tid (Europe/Stockholm) och `sa_bans` i UTC.
Det har redan kostat oss en gång. En kväll som korsar midnatt hamnar på två
dagar, precis som live.

**2026Q3 körs över från demos** (ägarens beslut, 2026-09-29). Demona täcker hela
kvartalet från 1 juli, medan liggaren börjar 7 augusti när Elo gick live. Q3
blir därmed ett helt kvartal, räknat med den nya formeln precis som de äldre.
Två villkor:

- **Kontrollera täckningen först.** GOTV har luckor: den stoppas före mappbyte,
  och servrar kraschar. För matcher efter 7 augusti som saknar demo spelas
  liggarens rader in i kronologin i stället, så att inga kills försvinner.
- **Den nuvarande Q3-liggaren raderas inte.** Den markeras som ersatt, och de
  nya raderna får `source = 'demo'`. Den är den enda källan för luckorna och
  beviset på vad som räknades live.

## 12. Bugg: autoassign sätter spelare under mappen

Spelare hamnar under mappen och faller. Ägarens misstanke: autoassign placerar
någon som redan har spawnat.

**Regeln (ägaren, 2026-09-29): autoassign ska aldrig flytta en spelare som
redan har spawnat.** Den ska bara placera den som står i Spectator eller
Unassigned och inte har någon levande pawn. Har spelaren valt lag själv, eller
redan står i spel, lämnas den ifred.

Troligt förlopp: autoassign reagerar en stund efter att spelaren anslutit (på
`player_team`, `player_connect_full` eller med en fördröjning). Hinner spelaren
välja lag och spawna under tiden blir den flyttad en gång till, och en pawn som
byter lag eller respawnas utanför spelets vanliga spawnval hamnar i världens
nollpunkt (0, 0, 0). På de flesta mappar ligger den under golvet.

**Vad vi ber om:**

1. **Kontrollera läget precis innan flytten**, inte när händelsen kom in: lag
   fortfarande Spectator eller Unassigned, och ingen levande pawn. Annars gör
   ingenting.
2. **Tvinga inte fram `Respawn()` mitt i en runda.** Sätt laget och låt spelaren
   spawna vid nästa runda, eller använd spelets eget lagbyte så att spelet
   väljer spawnpunkt.
3. **Om ni ändå respawnar:** kontrollera pawnens position efteråt. Står den
   nära (0, 0, 0), flytta den till en ledig `info_player_terrorist` /
   `info_player_counterterrorist`.
4. **Räkna spawnpunkterna per lag och mapp.** Workshop-mappar har ibland färre
   spawnpunkter än spelare. Autoassign ska inte fylla ett lag utöver det mappen
   har plats för. De två autoassign-fallen i loggarna vi sett förut
   (`docs/ban-highlights-contract.md`, 9b) var på `de_rainfall` och
   `de_foroglio`, båda workshop.

**Det kostar spelare rating i dag.** Den som faller under mappen dör av världen,
och det räknas som självmord: `suicide_penalty`, −5 rating. Den som föll på grund
av pluginet ska inte straffas. Kan ni skilja ett sådant dödsfall från ett riktigt
självmord (dog inom några sekunder efter en autoassign-flytt), skriv inget
straff för det.

**Samma plats som SourceTV-sparken** (`docs/ban-highlights-contract.md`, 9b):
DemoRecorder sparkas i sekunden efter en autoassign-placering. Det kan vara
samma kodväg.

## 13. Lagbalanseraren: byt från SkillStats till Elo

Balanseraren fungerar dåligt i dag. Ägarens diagnos (2026-09-29): den går på
SkillStats och väger den pågående mappen allt tyngre, så att den efter ett par
rundor nästan bara följer mappens lokala tal. Det talet är brus. Balanseraren
flyttar någon, bruset vänder, och nästa stund är det andra laget för starkt: "ena
stunden är CT lite bättre, sen byts nån och T blir starkare än CT".

**Byt grunden till Elo-ratingen**, med den pågående mappen som en justering med
tak:

```
styrka = R + min(rundor / R0, CAP) · (P − R)

R    = spelarens Elo-rating (se "Efter nollställningen" nedan)
P    = prestationsratingen på den pågående mappen: motståndarnas snittrating
       + 400 · log10((kills + 0,5) / (deaths + 0,5)), samma tal som sajtens
       "spelade som" (RatingRepository::mapSessions)
R0   = 20 rundor
CAP  = 0,3   (mappen väger aldrig mer än 30 %)
```

Efter 5 rundor väger mappen alltså runt 8 %, och aldrig mer än 30 %. En dålig
kväll syns, men den skriver inte över ett års spel.

**Lagstyrka och vinstchans:** lagets snitt av `styrka`, och det starkare lagets
vinstchans räknad med Elo-formeln på skillnaden:
`1 / (1 + 10^(−(A − B) / 400))`.

**Hysteres, det som stoppar pendlingen:**

- Balansera bara när det starkare lagets vinstchans är över **60 %**.
- Gör bara en flytt som tar ned chansen **under 55 %**. En flytt som bara byter
  vilket lag som är för starkt görs inte.
- Högst **en flytt per tre rundor**.
- **Samma spelare flyttas aldrig två gånger** på samma mapp.
- Flytta hellre den som är död eller nyss anslöt, vid rundstart. Aldrig någon
  levande mitt i en runda (se avsnitt 12).

**Efter nollställningen:** på torsdag står alla på 1 000, och ratingen vet
ingenting på några veckor. Använd **förra kvartalets slutrating** som `R` tills
spelarens `matches` i den nya säsongen har passerat den provisoriska gränsen.
Spelare utan någon rating alls räknas som fältets median, så att de inte drar
ett lag åt något håll bara för att de är okända.

Talen (60 %, 55 %, R0, CAP, tre rundor) är startvärden. Kan de läsas ur en
cfg eller från `points_formula` blir de enkla att justera efter en vecka.

## Öppna frågor till ägaren

- **Teamkills** (avsnitt 4c): ska den som dödar en lagkamrat förlora poäng?
- **`!rank` i spelet:** ska den visa placering, poäng och grad
  ("#4 · 1 842p · Kapten II")? Graden räknas i dag på sajten
  (`src/Domain/EloRank.php`: percentil i ratingen bland dem med minst 50 kills,
  full stege först när 50 spelare kommit så långt). Pluginet behöver i så fall
  räkna likadant, eller läsa graden från sajten.

## Vad som redan är klart på sajten

- `points_formula` med adminsida (`/admin/poangformel`) och synk till er databas.
- `PointsFormula`, referensimplementationen, med tester.
- Vapenviktssidans förhandsvisning räknar med den nya formeln.
- `/elo`: rang (Värnpliktig → Malaj → … → Fältmarskalk) ur ratingen, kontoutdraget
  dag för dag och mapp för mapp, samt "spelade som" per mapp.

När `season` finns på `elo_rating` och de nya kolumnerna finns i liggaren
följer sajten efter: kontoutdraget visar avdraget och placeringarna, och
kvartalsbläddringen visar kvartalets egen rating.

---

## Bilaga A: poängformeln, ordagrant (OSWeb `src/Services/PointsFormula.php`)

Räkna likadant. Värdena kommer ur `points_formula` (avsnitt 6); `DEFAULTS`
nedan är förvalen, men tabellen innehåller alltid alla rader.

```php
<?php

declare(strict_types=1);

namespace OSWeb\Services;

/**
 * Poängformeln för kvartalets tävling — referensimplementationen.
 *
 * OSBase räknar poängen vid killen; sajten äger värdena (`points_formula`) och
 * den här klassen. Den finns av samma skäl som WeaponWeightRepository::ruleFor():
 * sajten ska kunna VISA vad en inställning ger innan någon spelat en rond på
 * den, och OSBase ska ha en körbar beskrivning att jämföra sin egen mot.
 * Formeln och besluten bakom står i docs/osbase-order-2026Q4.md.
 *
 * I KORTHET (ägarens vision, 2026-09-29):
 *  - En kill är värd mer ju högre offret står på topplistan jämfört med dig.
 *    Grannar ≈ EVEN, sista som fäller ettan ≈ TOP, ettan som fäller sista ≈ FLOOR.
 *  - Ratingen justerar ±W — "den ska inte vara avgörande".
 *  - Headshot är ett fast tillägg. Vapenvikten multiplicerar killens del.
 *  - En död kostar DEATH_SHARE av dräparens placeringsbas, aldrig under 0.
 *  - De första WARMUP_KILLS är värnplikt: platt EVEN, och döden kostar inget.
 */
final class PointsFormula
{
    /**
     * Förvalen — gäller för varje namn som saknar rad i tabellen.
     *
     * @var array<string, float>
     */
    public const DEFAULTS = [
        'START_POINTS' => 1000.0,
        'FLOOR' => 1.0,
        'EVEN' => 2.0,
        'TOP' => 25.0,
        'EXP' => 1.5,
        'W' => 0.2,
        'HS_BONUS' => 1.0,
        'WARMUP_KILLS' => 50.0,
        'DEATH_SHARE' => 0.5,
        // 0 = inget tak. Ägaren har inte bestämt något tak; en decoy från sista
        // plats på ettan får vara en jackpott tills någon säger annat.
        'MAX_KILL' => 0.0,
        // Bonusarna, i samma skala som en kill mellan grannar (EVEN). Värdena i
        // docs/rank-and-points-design.md sattes när en kill var 10p; här är en
        // plant värd en kill, som den var tänkt.
        'BONUS_PLANT' => 2.0,
        'BONUS_DEFUSE' => 2.0,
        'BONUS_BOMB_PICKUP' => 1.0,
        // Ett avdrag, skrivet som positivt tal: vad det kostar att släppa eller
        // kasta bomben med flit. Att dö med den kostar inget.
        'BONUS_BOMB_DROP' => 1.0,
        'BONUS_ROUND_WIN' => 1.0,
        'BONUS_ASSIST' => 1.0,
    ];

    /**
     * Vad varje värde betyder, för adminsidan. Samma ordning som DEFAULTS.
     *
     * @var array<string, array{0: string, 1: string}>
     */
    public const LABELS = [
        'START_POINTS' => ['Startpoäng', 'Vad alla står på när kvartalet börjar.'],
        'FLOOR' => ['Golv', 'Vad ettan får för att fälla sista platsen.'],
        'EVEN' => ['Grannar', 'Vad en kill mellan grannar på listan är värd. Också värnpliktens platta pris.'],
        'TOP' => ['Tak', 'Vad sista platsen får för att fälla ettan.'],
        'EXP' => ['Kurvans branthet', '1 = rak linje. Högre = bara riktiga skrällar betalar stort.'],
        'W' => ['Ratingens vikt', '0,2 = ratingen flyttar en kill högst ±20 %.'],
        'HS_BONUS' => ['Headshot', 'Fast tillägg, läggs på sist.'],
        'WARMUP_KILLS' => ['Värnplikt', 'Kills per kvartal innan formeln tar över.'],
        'DEATH_SHARE' => ['Dödens andel', 'Andel av dräparens placeringsbas som offret förlorar.'],
        'MAX_KILL' => ['Tak per kill', '0 = inget tak.'],
        'BONUS_PLANT' => ['Plantera bomben', 'Bonus till den som planterar.'],
        'BONUS_DEFUSE' => ['Desarmera bomben', 'Bonus till den som desarmerar.'],
        'BONUS_BOMB_PICKUP' => ['Plocka upp bomben', 'Den som tar på sig uppdraget.'],
        'BONUS_BOMB_DROP' => ['Släppa bomben (avdrag)', 'Att släppa eller kasta den med flit är att lämna ifrån sig uppdraget. Att dö med den kostar inget.'],
        'BONUS_ROUND_WIN' => ['Rondvinst', 'Till varje spelare i laget som vann rundan.'],
        'BONUS_ASSIST' => ['Assist', 'Till den som hjälpte till med en kill.'],
    ];

    /** @var array<string, float> */
    private array $v;

    /**
     * @param array<string, float> $values namn => värde; saknade tar förvalet
     */
    public function __construct(array $values = [])
    {
        $this->v = array_intersect_key($values, self::DEFAULTS) + self::DEFAULTS;
    }

    public function value(string $name): float
    {
        return $this->v[$name];
    }

    /** @return array<string, float> */
    public function values(): array
    {
        return $this->v;
    }

    /**
     * Placeringsbasen: vad avståndet på listan är värt, före ratingterm, vapen
     * och headshot.
     *
     * `g` = (dräparens plats − offrets plats) / listans storlek, +1 när sista
     * fäller ettan, −1 när ettan fäller sista. Uppåt en kurva från EVEN mot TOP,
     * nedåt en rak linje från EVEN mot FLOOR.
     */
    public function base(int $attackerPlace, int $victimPlace, int $boardSize): float
    {
        $g = $boardSize > 0 ? ($attackerPlace - $victimPlace) / $boardSize : 0.0;
        $g = max(-1.0, min(1.0, $g));
        $even = $this->v['EVEN'];

        return $g > 0
            ? $even + ($this->v['TOP'] - $even) * $g ** $this->v['EXP']
            : $even + ($even - $this->v['FLOOR']) * $g;
    }

    /**
     * Vad dräparen får.
     *
     * @param float $surprise `1 − expected` ur ratingens egen uträkning: 0,5 vid
     *                        lika rating, mot 1 när offret är mycket bättre
     * @param int $killsBefore dräparens kills i kvartalet FÖRE den här
     */
    public function killPoints(
        int $attackerPlace,
        int $victimPlace,
        int $boardSize,
        float $surprise,
        float $weaponWeight,
        bool $headshot,
        int $killsBefore,
    ): float {
        if ($killsBefore < $this->v['WARMUP_KILLS']) {
            return round($this->v['EVEN'], 2);
        }

        $kill = $this->base($attackerPlace, $victimPlace, $boardSize)
            * (1 + $this->v['W'] * (2 * $surprise - 1))
            * $weaponWeight;

        if ($this->v['MAX_KILL'] > 0) {
            $kill = min($kill, $this->v['MAX_KILL']);
        }

        return round($kill + ($headshot ? $this->v['HS_BONUS'] : 0.0), 2);
    }

    /**
     * Vad offret förlorar, som ett positivt tal (liggaren skriver det negativt).
     *
     * Bara placeringsbasen — dräparens ratingterm, vapen och headshot är hens
     * förtjänst, inte offrets miss (ägaren: "precis bara bas-beloppet").
     *
     * @param int $attackerKillsBefore dräparens kills i kvartalet före killen
     * @param int $victimKillsBefore offrets kills i kvartalet
     * @param float $victimBalance offrets saldo före avdraget
     */
    public function deathLoss(
        int $attackerPlace,
        int $victimPlace,
        int $boardSize,
        int $attackerKillsBefore,
        int $victimKillsBefore,
        float $victimBalance,
    ): float {
        // Offret gör sin värnplikt: döden kostar inget.
        if ($victimKillsBefore < $this->v['WARMUP_KILLS']) {
            return 0.0;
        }

        // Dräparen gör sin värnplikt: platt, så att en nykomling på sista plats
        // inte slår hål i ettans saldo med ett skott.
        $base = $attackerKillsBefore < $this->v['WARMUP_KILLS']
            ? $this->v['EVEN']
            : $this->base($attackerPlace, $victimPlace, $boardSize);

        return round(max(0.0, min($this->v['DEATH_SHARE'] * $base, $victimBalance)), 2);
    }
}
```

Provvärden ur testerna (100 spelare på listan, värnplikten avklarad):

| Anrop | Resultat |
|---|---|
| `base(2, 1, 100)` (tvåan fäller ettan) | ≈ 2,02 |
| `base(51, 1, 100)` | ≈ 10,13 |
| `base(100, 1, 100)` (sista fäller ettan) | ≈ 24,66 |
| `base(1, 101, 100)` (ettan fäller en utan rad) | 1,00 |
| `killPoints(51, 1, 100, 0.5, 1.0, false, 1000)` | 10,13 |
| `killPoints(51, 1, 100, 1.0, 1.0, false, 1000)` | 12,16 |
| `killPoints(51, 1, 100, 0.5, 2.0, true, 1000)` | 21,26 |
| `killPoints(100, 1, 100, 1.0, 5.0, true, 49)` (värnplikt) | 2,00 |
| `deathLoss(100, 1, 100, 1000, 1000, 5000)` | ≈ 12,33 |
| `deathLoss(100, 1, 100, 1000, 10, 5000)` (offret i värnplikt) | 0,00 |
| `deathLoss(100, 1, 100, 10, 1000, 5000)` (dräparen i värnplikt) | 1,00 |

## Bilaga B: vapenviktens matchning, ordagrant (OSWeb `WeaponWeightRepository::ruleFor()`)

`ORDER = ['exact', 'prefix', 'suffix']`, förval `1.00`. Första träff vinner,
i den ordningen — `knife` har en exakt rad och `knife_` en prefixrad.

```php
public static function ruleFor(string $weapon, array $rules): array
    {
        $weapon = strtolower(trim($weapon));

        if ($weapon === '') {
            return ['multiplier' => self::DEFAULT_MULTIPLIER, 'pattern' => null, 'match_type' => null];
        }

        foreach (self::ORDER as $type) {
            foreach ($rules as $rule) {
                if ($rule['match_type'] !== $type) {
                    continue;
                }

                $p = $rule['pattern'];

                $hit = match ($type) {
                    'exact' => $weapon === $p,
                    'prefix' => $p !== '' && str_starts_with($weapon, $p),
                    'suffix' => $p !== '' && str_ends_with($weapon, $p),
                    default => false,
                };

                if ($hit) {
                    return ['multiplier' => $rule['multiplier'], 'pattern' => $p, 'match_type' => $type];
                }
            }
        }

        return ['multiplier' => self::DEFAULT_MULTIPLIER, 'pattern' => null, 'match_type' => null];
    }
```


---

# Svar från OSBase (2026-09-30, byggt i v0.0.558)

Allt i avsnitt 1–8 och 13 är byggt. Avsnitt 11 (demo-backfill) är inte byggt,
men `ScoreKill` är utbruten som beskrivet. Avsnitt 12: se nedan.

## Kolumnnamnen vi använder

Exakt som ordern:

| Tabell | Kolumn | Typ | Notering |
|---|---|---|---|
| `elo_rating` | `season` | `VARCHAR(8)` | PK är nu `(steamid64, season)`. Alla rader som fanns före migreringen taggas `2026Q3` (Elo gick live 2026-08-07, så inget annat kvartal finns i tabellen). |
| `elo_kill_event` | `victim_points_delta` | `DECIMAL(12,2) NOT NULL DEFAULT 0` | ≤ 0, det klippta värdet. Gamla rader står på 0, vilket är sant (inget avdrag fanns). |
| `elo_kill_event` | `attacker_place`, `victim_place`, `board_size` | `INT NULL` | NULL på rader från före v0.0.558. |
| `elo_kill_event` | `round_no` | `INT NULL` | GameStats rondräknare: 0 under warmup, 1 = första skarpa ronden på mappen. |
| `elo_kill_event` | `attacker_in_air` | `TINYINT(1) NULL` | `EventPlayerDeath.attackerinair`, alltid satt på nya rader. |
| `elo_kill_event` | `attacker_in_water` | `TINYINT(1) NULL` | Se nedan. NULL bara när pawnen inte gick att läsa. |
| `elo_bonus_event` | `round_no` | `INT NULL` | |

Nytt index: `idx_elo_kill_event_attacker (attackerid64, stamp)` — värnplikts-
räknaren seedas med `COUNT(*)` per spelare inom säsongens datumintervall
(`elo_kill_event` har ingen `season`-kolumn; `stamp` är DB-serverns `NOW()`).

## Vatten: vilket fält CS2 exponerar

Händelsen har inget fält. Vi läser dräparens pawn i `player_death`:

- `CBaseEntity.m_fFlags & FL_INWATER` (`0x200`, CounterStrikeSharps `PlayerFlags.FL_INWATER`)
- `CBaseEntity.m_flWaterLevel` (`float`, 0 = torr; Source 2 har ersatt Source 1:s
  `WL_`-enum på entiteten med en float; `WaterLevel_t` finns fortfarande i schemat
  men pawnen exponerar floaten)

`attacker_in_water = FL_INWATER || m_flWaterLevel > 0`. **Inte verifierat live.**
Varje in-water-kill skriver en DEBUG-rad med båda råvärdena så att första
kvällen på en mapp med vatten visar vilket av dem som faktiskt slår, och på
vilket djup. Vill ni ha "midjan eller djupare" i stället för "fötterna blöta"
är det en tröskel på floaten, säg till.

## Frågan i avsnitt 2: vilka `kind` rör ratingen

Före v0.0.558: **bara `assist`** (`rating_delta` +5, platt). Det var en andra
läcka av samma slag som headshot-bonusen. Borttagen — assist ger nu bara
`BONUS_ASSIST` poäng.

`suicide_penalty`/`teamkill_penalty` har aldrig rört ratingen i koden sedan
2026-08-04 (`rating_delta` är alltid 0 på dem). Det ni såg som "−5,00" är
`points_delta` — `rating_delta` är `INT` och kan inte visa decimaler. Från
v0.0.558 skrivs inga sådana rader alls som förval (avsnitt 4c: självmord kostar
inget; teamkill väntar på ägaren, förval inget), men `teamkill_points_penalty`/
`suicide_points_penalty` finns kvar i `elorating.cfg` (0 = av) om ägaren vill
slå på ett poängavdrag.

Ratingen är nu ren nollsumma per duell, frånsett K-asymmetrin som ni lämnar.

## Bomben (4b), hur vi skiljer fallen

- `bomb_pickup` betalar bara om ett `bomb_dropped` redan hänt i ronden —
  motorn skickar `bomb_pickup` också när en T spawnar med bomben.
- `bomb_dropped` avgörs **en frame senare**: då kollar vi om bäraren fortfarande
  lever (och inte finns i rondens dödslista). Att avgöra det inuti eventet är
  inte pålitligt — motorn kan skicka `bomb_dropped` innan pawnens livstillstånd
  hunnit slå om vid död. **Behöver en live-koll:** dö med bomben ska ge ingen rad,
  kasta den ska ge `bomb_drop −1`.
- `kind`-namnen: `bomb_plant`, `bomb_defuse`, `bomb_pickup`, `bomb_drop`,
  `round_win`, `assist`. Inga byten av befintliga namn.

## Avsnitt 5, varför vikterna inte tillämpades

Kodvägen (exact → prefix → suffix, förval 1,00) fanns och var rätt. Den
sannolika orsaken är att `weapon_weight_table` är tom i den live
`elorating.cfg` — då är viktningen av (det var det säkra förvalet innan
grant:et var bekräftat). Från v0.0.558 varnar pluginet vid laddning om den är
tom, och samma sak för `points_formula_table`. **Båda måste sättas
schemakvalificerade i `elorating.cfg` på servern** (t.ex.
`oldswedes.weapon_point_weight` / `oldswedes.points_formula`), och OSBase-
användaren behöver SELECT på båda.

Utan `points_formula_table` kör formeln på förvalen i bilaga A och loggar en
varning; adminsidan har då ingen effekt. Med tabellen läses den om vid varje
rondstart.

## Placering och värnplikt

- Ställningen läses en gång vid mappstart och vid plugin-(om)laddning, hålls
  hela mappen. Standardrankning (1, 2, 2, 4), ingen rad = N + 1.
- Första mappen i säsongen: N = 0, alla utan rad → `Base()` ger `EVEN`.
- `elo_points`-raden skapas vid första händelsen med `START_POINTS`, även när
  händelsens delta är 0 (död under värnplikten). Insert är
  `START_POINTS + delta`, on-duplicate `+ delta`, så två servrar mot samma
  tabell kan inte dubbelseeda.

## Avsnitt 12, autoassign

Punkt 1 och 2 fanns redan i v0.0.556 (2026-09-26, live-verifierad 27 sep):
laget kontrolleras i flyttögonblicket (bara Unassigned/Spectator flyttas), och
`Respawn()` körs enbart under warmup. Punkt 3 (teleport till ledig spawn) är
det som v0.0.553 gjorde och som bröt anslutning helt — inte ombyggd dagen före
säsongsstart. Punkt 4 (räkna spawnpunkter) inte byggd. Ratingstraffet vid fall
är inte längre en fråga: självmord kostar varken poäng eller rating, och en
warmup-död räknas inte alls.

## Avsnitt 13, balanseraren

Byggd som ordern beskriver, aktiv när `balancer_skill_source elo` i
`teambalancer.cfg` (kontrollera vad live-cfg:n har — förvalet är fortfarande
`gamestats`). R läses via `TryGetBalancingRating`: innevarande säsong när
spelaren passerat `provisional_matches`, annars förra kvartalets slutrating,
annars fältets median. Nya nycklar i `teambalancer.cfg`: `balance_trigger_pct 60`,
`balance_target_pct 55`, `map_weight_rounds 20`, `map_weight_cap 0.3`,
`min_rounds_between_moves 3`. Bara byten (par), aldrig samma spelare två gånger
per mapp, bara mellan ronder. Varje pass loggar styrkorna och vinstchansen.

## DamageReport (8)

Byggd enligt utkastet: `3:174`, fyra färgnivåer, `Head` i `Gold`, poäng på
`(Killed)`/`(Killed by)`, `-0,0p` när avdraget är 0, bonusblock med engelska
etiketter, nettosumma i rubriken. Kills som inte poängsattes (warmup, för få
spelare) får inget tal. Skärmdump får ägaren ta.

## Öppna frågor, förval som gäller

- Teamkills: ingenting åt något håll (cfg 0).
- `!rank` visar placering/poäng/rating som förut, ingen grad.
