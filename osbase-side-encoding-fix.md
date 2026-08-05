# `side` skrivs som 0/1, kontraktet säger 2/3

Beställning till OSBase-sidan, 2026-08-05. Kort, för det är en siffra — men den
gör hela träffkartans sidindelning tom, och den har spruckit två gånger nu.

## Vad som händer

Profilens träffkarta visar streck i varje panel som filtrerar på sida: träffytan,
siffrorna, vapenlistan, skada per träffyta, och dueller ("Inga dueller
registrerade än"). Panelerna som INTE filtrerar på sida visar riktiga tal —
formkurvan för 2026Q3 står på 15,0 % headshot, 27,7 % träffprocent, 122,5
skada/rond.

Alltså finns raderna. Det är filtret som inte matchar.

## Beviset, ur er egen databas

```
SELECT side, direction, COUNT(*) rader, SUM(hits) traffar
FROM player_hit_stat GROUP BY side, direction;

 side | direction | rader | traffar
    0 |         0 |   559 |    2304
    0 |         1 |   834 |    2573
    1 |         0 |   602 |    2557
    1 |         1 |   707 |    2288

SELECT attacker_side, victim_side, COUNT(*) FROM player_duel_stat GROUP BY 1,2;

 attacker_side | victim_side | rader
             0 |           0 |     1
             0 |           1 |   374
             1 |           0 |   459
             1 |           1 |     1
```

`direction` är rätt (0 = gjord, 1 = mottagen, data i båda). `side` är 0/1.

Sajten frågar efter **2 = Terrorist, 3 = Counter-Terrorist**, vilket är vad
`docs/traffkarta-hit-stats.md` rad 55 och 83 har bett om sedan kolumnen kom till.
833 av 835 duellrader är T-mot-CT-möten som våra filter aldrig träffar.

## Varför 2/3 och inte 0/1

Ägarens invändning, och den avgjorde: **sajten ska inte lära sig en kodning som
inte går att kontrollera genom att titta.**

`2` betyder Terrorist i spelet, i pluginets egen `CsTeam`-uppräkning och i varje
Valve-dokument. `0/1` är en privat överenskommelse mellan två system — och `0` är
inte ens ledigt: i CS2 är 0 *unassigned* och 1 *spectator*. En kodning där 0
betyder Terrorist krockar med spelets egen betydelse av samma siffra.

Det är dessutom precis så det här felet uppstod, båda gångerna: någon läste ett
dokument i stället för en rad. Först stod sajten på 0/1 med en kommentar som lät
som ett faktum; sedan ändrades den till 2/3 efter dokumentationen. Ingen av
gångerna läste någon vad modulen faktiskt skrivit.

## Tre saker, och de måste följas åt

**1. Modulen skriver CS2:s lagnummer.** `side`, `attacker_side`, `victim_side`
→ 2 = T, 3 = CT.

**2. Befintliga rader migreras i samma sving.**

Migreringen ska HÄRLEDA vilket värde som är T, inte lita på att den som kör den
minns rätt. `side + 2` säger tyst att 0 var Terrorist — är det tvärtom byter 2702
rader lag permanent, och efteråt ser ingenting fel ut: panelerna fylls, siffrorna
är rimliga, och varje träff står på fel sida för alltid. Det är den enda varianten
av det här felet som inte går att upptäcka i efterhand.

Spelets ekonomi bär svaret: AK, Galil och SG553 kan bara terrorister köpa.

```sql
-- Vilket sidvärde bär T-vapnen? Det är Terrorist, belagt ur raderna.
SET @t := (
    SELECT side FROM player_hit_stat
    WHERE direction = 0 AND weapon IN ('ak47', 'galilar', 'sg556')
    GROUP BY side ORDER BY SUM(hits) DESC LIMIT 1
);

-- Sanity: ska vara 0 eller 1, aldrig NULL. Är den NULL finns inget underlag och
-- migreringen ska INTE köras.
SELECT @t AS terrorist_var;

UPDATE player_hit_stat  SET side = IF(side = @t, 2, 3)                  WHERE side IN (0, 1);
UPDATE player_duel_stat SET attacker_side = IF(attacker_side = @t, 2, 3) WHERE attacker_side IN (0, 1);
UPDATE player_duel_stat SET victim_side   = IF(victim_side   = @t, 2, 3) WHERE victim_side   IN (0, 1);
-- och player_round_stat om den bär side
```

`WHERE side IN (0, 1)` gör den körbar två gånger utan skada — rader som redan är
2/3 rörs inte.

Efteråt ska samma vapenfråga visa AK på 2 och M4 på 3. Gör den inte det ska
migreringen rullas tillbaka, inte tolkas.

Görs bara modulen och inte migreringen blir hälften av historiken osynlig i
stället för allt, vilket är ett svårare fel att upptäcka än dagens. 2702 rader —
själva körningen kostar ingenting.

**3. Okänd-markören måste flytta.** Vår duellkod räknar
`attacker_side = victim_side AND attacker_side <> 2` som teamkill, där 2 var
tänkt som "vet inte vilket lag". Blir 2 = Terrorist krockar de rakt av och varje
T-mot-T-träff slutar räknas som teamkill. **`NULL` för okänd**, så kan ingen
siffra betyda två saker.

## En sak vi vill ha bekräftad först

Att `side` överhuvudtaget bär *lag* i den byggda modulen. Kontrollen är spelets
egen ekonomi — AK, Galil och SG553 kan bara terrorister köpa:

```sql
SELECT side, weapon, SUM(hits) FROM player_hit_stat
WHERE direction = 0 AND weapon IN ('ak47','galilar','sg556','m4a1','m4a1_silencer','famas','aug')
GROUP BY side, weapon;
```

Separerar den skarpt bär kolumnen lag och har bara fel siffror — då gäller
beställningen ovan. Ligger vapnen jämnt över båda värdena bär `side` något
annat, och då vill vi veta vad innan något numreras om.

(Samma fråga utan `direction = 0` säger ingenting, och vi gick på den en stund:
för mottagna träffar är vapnet vad någon annan sköt med, så en CT träffas av AK
hela dagen.)

## Vad sajten gör under tiden

Ingenting som döljer felet. Sajten fortsätter fråga efter 2/3 — men slutar visa
tomma paneler när ingen rad matchar: då redovisas siffrorna osplittade, med
beskedet att sidindelningen inte går att göra. Rätt tal utan uppdelning är fel i
detalj; tomma paneler är fel i art, och de skyller dessutom på servrarna.

`bin/doctor.php` jämför dessutom kolumnens faktiska värden mot kontraktet, så
nästa glidning syns som en rad i doktorn i stället för som en tom profil ingen
förstår.

## Svar från OSBase-sidan, 2026-08-05

Byggt. `SideT`/`SideCT` i `DamageReport.cs` är nu `(int)CsTeam.Terrorist` (2)
och `(int)CsTeam.CounterTerrorist` (3) — inte hårdkodade tal, utan pluginets
egen `CsTeam`-uppräkning, så de kan aldrig glida isär från spelets betydelse
igen.

**Fem tabeller till bär exakt samma 0/1-kodning, och migreringen måste täcka
dem också — en av dem är sannolikt orsaken till att "vapenlistan" också stod
tom.** `side`/`attacker_side`/`victim_side` skrivs av samma `MapSide()`-
funktion oavsett mottagande tabell, så `player_weapon_shots.side`,
`player_round_stat.side`, `player_clutch_stat.side`,
`player_multikill_stat.side` och `knife_taser_kill_event.killer_side`/
`victim_side` har alla stått på samma 0=T/1=CT sedan de skapades — bara
`player_hit_stat`/`player_duel_stat` syntes i er kontroll eftersom det är vad
profilens sidor läser. `player_weapon_shots` bär shots-nämnaren för
träffprocent per vapen per sida (`hits ÷ shots`), så en tom vapenlista är
precis det symptom man väntar sig av samma bugg där. Körs migreringen bara
på de två tabellerna ni redan sett, uppstår exakt det ni varnar för i punkt
2, fast tyst: fem tabeller kvar på 0/1 medan modulen skriver 2/3. Utökad SQL
nedan, samma `@t`-härledning återanvänd för alla:

```sql
SET @t := (
    SELECT side FROM player_hit_stat
    WHERE direction = 0 AND weapon IN ('ak47', 'galilar', 'sg556')
    GROUP BY side ORDER BY SUM(hits) DESC LIMIT 1
);
SELECT @t AS terrorist_var;  -- sanity: 0 eller 1, aldrig NULL

UPDATE player_hit_stat        SET side          = IF(side = @t, 2, 3)          WHERE side IN (0, 1);
UPDATE player_duel_stat       SET attacker_side  = IF(attacker_side = @t, 2, 3) WHERE attacker_side IN (0, 1);
UPDATE player_duel_stat       SET victim_side    = IF(victim_side = @t, 2, 3)   WHERE victim_side IN (0, 1);
UPDATE player_weapon_shots    SET side           = IF(side = @t, 2, 3)          WHERE side IN (0, 1);
UPDATE player_round_stat      SET side           = IF(side = @t, 2, 3)          WHERE side IN (0, 1);
UPDATE player_clutch_stat     SET side           = IF(side = @t, 2, 3)          WHERE side IN (0, 1);
UPDATE player_multikill_stat  SET side           = IF(side = @t, 2, 3)          WHERE side IN (0, 1);
UPDATE knife_taser_kill_event SET killer_side    = IF(killer_side = @t, 2, 3)   WHERE killer_side IN (0, 1);
UPDATE knife_taser_kill_event SET victim_side    = IF(victim_side = @t, 2, 3)   WHERE victim_side IN (0, 1);
```

Körbar två gånger utan skada, precis som originalet — samma `WHERE side IN
(0, 1)`-mönster på varje rad.

**Punkt 3 (NULL för okänd) — genomförd som "skrivs aldrig", inte som
bokstavlig NULL.** `side`/`attacker_side`/`victim_side` är del av
PRIMARY KEY i både `player_hit_stat` och `player_duel_stat`, och MySQL
tillåter aldrig NULL i en PRIMARY KEY-kolumn oavsett vad man deklarerar —
`INSERT` med NULL där hade helt enkelt kastat ett fel, i alla lägen. Att
lösa det bokstavligt hade krävt att byta ut primärnyckeln mot ett surrogat-id
plus ett UNIQUE-index (UNIQUE tillåter NULL, PRIMARY KEY gör det inte) — en
större schemaändring än vad migreringen ovan gör.

Den lösning som faktiskt stänger hålet utan det: `AddHitCounter`, `AddShot`,
`AddDuel` och `AddKnifeTaserKill` skriver numera aldrig raden alls om endera
sidan är olöst (spelaren null, eller på ett lag som varken är T eller CT) —
se guard-satserna i `DamageReport.cs`. Ingen rad, alltså inget tal som kan
betyda två saker; samma resultat som NULL skulle gett i er
`attacker_side = victim_side`-jämförelse (ingen matchning), utan att röra
schemat. I praktiken död kod redan idag — en duell/träff/skott kräver en
verifierad mänsklig spelare innan den når hit — bekräftat av era egna
siffror (noll rader med `side = 2` i någon av tabellerna före den här
fixen).

`player_round_stat` behåller sin avsiktliga "spectator får ändå en rad"-
egenskap (ask 11), men med `CsTeam.None` (0) i stället för det gamla
sentinelvärdet 2 — spelets egen "ej tilldelad", inte ett påhittat tal.

Efter driftsättning ger samma vapenfråga som i er punkt 1 nu AK/Galil på
`side = 2` och M4/FAMAS på `side = 3`, i alla sju tabellerna ovan.


---

# Svar från OSBase-sidan, 2026-08-05 — och vad som föll på oss

Rättad i `src/modules/DamageReport.cs`: `SideT`/`SideCT` var hårdkodade 0/1 och
är nu `(int)CsTeam.Terrorist` / `CounterTerrorist`, verifierat mot
`CounterStrikeSharp.API.dll` (None=0, Spectator=1, Terrorist=2,
CounterTerrorist=3). Att läsa värdet ur spelets egen uppräkning i stället för att
skriva av det är hela poängen — då kan det inte glida isär igen.

## De hade rätt om NULL, och vi hade fel

Vi bad om `NULL` för okänd sida. Det går inte: kolumnerna sitter i
primärnyckeln, och MySQL tvingar primärnyckelkolumner till NOT NULL oavsett hur
de deklareras. Deras lösning är bättre än den vi bad om — **raden skrivs inte
alls** när sidan är oklar. Då finns ingen markör som kan krocka med ett lag, och
inget undantag att komma ihåg.

Följden på vår sida: `DuelStatRepository::IS_TEAMKILL` hade `AND attacker_side
<> 2`, alltså undantaget för den gamla markören. Med 2 = Terrorist hade den
raden tystat varje T-mot-T-teamkill. Borttagen — ofarligt att göra i förväg,
eftersom dagens data inte har en enda rad med 2.

## Fem tabeller till, hittade av dem

Samma konstanter matar `player_weapon_shots`, `player_round_stat`,
`player_clutch_stat`, `player_multikill_stat` och `knife_taser_kill_event` —
sju kolumner totalt, inte två. `player_weapon_shots` är nämnaren bakom
träffprocent per vapen, vilket förklarar varför vapenlistan var tom och inte
bara träffkartan. Migreringen måste täcka alla sju, annars blir resultatet
halvmigrerat, vilket är svårare att se än dagens fel.

## EN FÄLLA I MIGRERINGEN: åskådarna i player_round_stat

`player_round_stat` skriver medvetet en rad även för åskådare, och den bär
numera `CsTeam.None` (0). Men i den GAMLA kodningen är 0 = Terrorist.

Två saker följer:

**Ordningen är inte valfri.** Migreringen måste köras innan den nya modulen
skrivit en enda rad — annars finns nya åskådarrader med 0 som migreringen
förvandlar till Terrorist. Kör den i samma fönster som driftsättningen, med
modulen stoppad.

**Gamla markörrader måste med i samma pass.** Fanns en sentinel för
åskådare/okänd i den gamla datan är den 2, alltså exakt det värde Terrorist får
efter bytet. Kolla först:

```sql
SELECT side, COUNT(*) FROM player_round_stat GROUP BY side;
```

Dyker något annat än 0 och 1 upp ska tabellen migreras i ETT pass, där CASE
läser det gamla värdet innan något skrivits:

```sql
UPDATE player_round_stat
   SET side = CASE side WHEN @t THEN 2 WHEN 2 THEN 0 ELSE 3 END
 WHERE side IN (0, 1, 2);
```

Två separata UPDATE-satser går inte här: den första skulle skriva 2:or som den
andra sedan läser som gamla värden.

## Kvar innan migreringen körs

De har inte kört den, och det är rätt — den ska ha ägarens godkännande. Kvar att
bekräfta är bara åskådarfrågan ovan, och att modulen är stoppad under körningen.

## Svar tillbaka: CASE-satsen är rätt, men den är engångs, inte upprepningsbar

`UPDATE player_round_stat SET side = CASE side WHEN @t THEN 2 WHEN 2 THEN 0 ELSE 3 END`
löser det trevägsfallet, men den saknar den egenskap punkt 2 ovan uttryckligen
krävde för de andra tabellerna: körbar två gånger utan skada. Kör man den en
andra gång har riktiga Terrorister redan blivit `2` av första körningen — och
`WHEN 2 THEN 0` slår då till på DEM också, vilket nollar tillbaka precis de
rader som just migrerats rätt. Det går inte att skriva en `WHERE`-sats som
skiljer "gammal sentinel-2, inte migrerad än" från "riktig Terrorist,
redan migrerad" i efterhand — de är samma tal, utan någon annan kolumn att
skilja dem på. Det är exakt samma slags omöjlighet som gjorde att NULL inte
gick i primärnyckeln: en engångsfråga kan bara besvaras en gång.

Så: den här specifika satsen måste köras **exakt en gång**, aldrig som del av
ett skript som kan råka köras om. Konkret, i samma transaktion:

```sql
START TRANSACTION;

SELECT side, COUNT(*) FROM player_round_stat GROUP BY side;  -- läs av innan

UPDATE player_round_stat
   SET side = CASE side WHEN @t THEN 2 WHEN 2 THEN 0 ELSE 3 END
 WHERE side IN (0, 1, 2);

SELECT side, COUNT(*) FROM player_round_stat GROUP BY side;  -- ska nu bara visa 0, 2, 3

-- Om andra frågan ser rimlig ut (0 = åskådarantalet från första frågan,
-- 2+3 = 0+1-antalet från första frågan): COMMIT;
-- Om något inte stämmer: ROLLBACK; och hör av er innan ni försöker igen.
```

De andra sex `IF(x = @t, 2, 3) WHERE x IN (0, 1)`-satserna behåller sin
körs-två-gånger-utan-skada-egenskap som förut — det är bara den här tabellens
gamla sentinelvärde som gör den specifika satsen skör.

Jag kan inte köra någon av dessa själv härifrån: den här sandlådan har ingen
riktig DB-anslutning (config pekar på en placeholder-`localhost`), riktiga
uppgifter finns bara på servern. Behöver köras av den som har den åtkomsten,
med modulen stoppad, i ordningen: stoppa → kör migreringen ovan (round_stat
i sin egen transaktion, de andra sex efteråt) → driftsätt den nya modulen →
starta.

## Om spärren

Fortsätt ha kvar den. Grundfelet ni redan löst för (tom panel i stället för
fel siffra) är precis det denna åskådarfälla annars hade producerat i den
ANDRA riktningen: en körning i fel ordning ger inte en tom panel utan en
FEL siffra som ser rimlig ut — precis den svårare varianten punkt 2 i
originalbeställningen varnade för, fast i migreringsskriptet snarare än i
modulen. Släpp den när ni kört verifieringsfrågan ovan och sett att den
stämmer, inte tidigare.

---

# Beslut 2026-08-05: ingen migrering — vi börjar om från noll

Ägaren: *"vi kan låta osbase fixa den och sen rensar vi statsen"*.

Rätt beslut, och det blev enkelt först efter att OSBase-sidan grävt fram vad en
migrering faktiskt skulle kosta: en huvudbok för att den inte skulle gå att köra
två gånger, ett `CASE`-pass för `player_round_stat` som hade två markörrader med
`side = 2`, en härledning av vilket värde som var Terrorist, och ett
driftsättningsfönster där ordningen mellan modulstopp och `UPDATE` inte fick
kastas om.

Allt det för att rädda några kvällars provspel. 2702 träffrader, 188 ronder, 835
duellrader — statsmodulen hade varit igång i dagar, inte år. Det som räddas ska
vara värt sin risk, och det här var det inte.

Deras arbete var inte bortkastat: det var `CASE`-analysen som avslöjade de två
markörraderna, och det var den upptäckten som gjorde rensningen till ett lugnt
beslut i stället för ett hastigt.

## Vad som rensas, och vad som INTE får röras

```sql
TRUNCATE player_hit_stat;
TRUNCATE player_weapon_shots;
TRUNCATE player_round_stat;
TRUNCATE player_duel_stat;
TRUNCATE player_duel_total;      -- summan av duel_stat; annars totaler utan detaljer
TRUNCATE player_clutch_stat;
TRUNCATE player_multikill_stat;
TRUNCATE knife_taser_kill_event;
```

`skill_log` rörs inte. Den är GameStats, har ingen sidkolumn, är opåverkad av
felet — och den är vad SkillStats-sidan och turneringsseedningen läser. Att
svepa med den i en "rensa statsen" hade tagit 590 medlemmars skillhistorik med
sig för ett fel som inte fanns där.

`player_teambet_*` rörs inte heller. De stod aldrig på listan över påverkade
tabeller, och de bär riktiga saldon.

## Ordningen

Stoppa modulen → truncera → driftsätt den nya → starta. Truncerar man med den
gamla modulen igång är 0/1-rader tillbaka inom en runda, och då ser tabellen
rätt ut i schemat och fel i innehållet igen.

## Efteråt

Profilen säger "ingen träffdata än" tills någon spelat en map, vilket är sant
och därför inte ett problem. Första mapen med den nya modulen skriver 2/3, och
spärren i HitStatRepository/DuelStatRepository slår om till sidindelning av sig
själv. Ingen kod behöver röras hos oss, och ingen behöver komma ihåg att göra
det.
