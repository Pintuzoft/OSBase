# M4A1-S och M4A4 hamnar i samma hink i `player_hit_stat`

Beställning till OSBase-sidan, 2026-08-11. Kort, för det är ett vapennamn — men
det gör att en spelare som uteslutande kör M4A1-S får noll procent på sitt eget
vapen, och att data förloras oåterkalleligt för varje kväll som går.

## Vad som händer

Profilens vapenpanel rankar på träffar ur `player_hit_stat`. Ägaren, som spelar
nästan uteslutande med M4A1-S, ser:

```
M4A4      17 %
FAMAS     14 %
AK-47      4 %
Galil AR   1 %
M4A1-S     –        ← vapnet han faktiskt använder
```

Sajten läser vapennamnet rakt av. Kartan `m4a1` → M4A4 och `m4a1_silencer` →
M4A1-S är densamma som ligger i `docs/osbase-eventweekend-contract.md`, och
frågan grupperar på det som står i kolumnen — ingen normalisering, ingen
prefixmatchning, ingen sammanslagning. Står M4A1-S på `–` finns det alltså inga
rader med det namnet.

## Beviset, ur er egen databas

Kört mot `osbase` på prod 2026-08-11:

```
SELECT weapon, COUNT(*) FROM player_hit_stat     WHERE weapon LIKE 'm4a1%' GROUP BY weapon;
  m4a1            845
  m4a1_silencer     0

SELECT weapon, COUNT(*) FROM player_weapon_shots WHERE weapon LIKE 'm4a1%' GROUP BY weapon;
  m4a1             33
  m4a1_silencer    90
```

Skottabellen håller alltså isär dem, och båda pjäserna används i communityt —
den tystade nästan tre gånger så ofta. Träfftabellen har inte en enda rad med
`m4a1_silencer`.

Det är inte ett omdöpt vapen. Hade M4A4 bara fått ett annat namn skulle den
tystade fortfarande ha egna rader. **Två vapen skrivs till ett namn.**

`m4a1` i sig är rätt: det ÄR M4A4:ans klassnamn (`weapon_m4a1`). Felet är att
den tystade också landar där.

## Varför det bara syns på M4:an

M4A1-S är det enda vapnet i CS vars basklassnamn tillhör ett **annat vapen i
samma slot**:

| Vapen | Klassnamn |
|---|---|
| M4A4 | `weapon_m4a1` |
| M4A1-S | `weapon_m4a1_silencer` |
| P2000 | `weapon_hkp2000` |
| USP-S | `weapon_usp_silencer` |

Tappas `_silencer` för USP-S blir resultatet ett namn som inte finns, vilket
märks. Tappas det för M4A1-S blir resultatet ett giltigt namn på **fel vapen**,
och statistiken ser rimlig ut hela vägen.

## Frågan som avgör var ni ska leta

*Besvarad 2026-08-11, se «Roten» nedan. Frågan står kvar för att svaret ska gå
att läsa mot vad som faktiskt frågades.*

```sql
SELECT weapon, COUNT(*) FROM player_hit_stat
 WHERE weapon IN ('usp_silencer','hkp2000','m4a1','m4a1_silencer') GROUP BY weapon;
```

- **Finns `usp_silencer`** är suffixet inte problemet i allmänhet — då är det
  M4-specifikt, troligen en uppslagning där båda M4:orna pekar på samma post.
- **Saknas `usp_silencer` också** tappas hela suffixfamiljen, och USP-S är
  drabbad likadant fast tystare.

## Roten, och rättningen (OSBase, 2026-08-11)

**Det är spelet som slår ihop dem, inte koden.** CS2:s `player_hurt` rapporterar
`m4a1` i `e.Weapon` även när skottet kom från M4A1-S — till skillnad från
`weapon_fire`, som bär rätt klassnamn. Där ligger hela förklaringen till att de
två tabellerna sa emot varandra: skottabellen matas av `weapon_fire` och var
därför korrekt hela tiden, medan träffabellen matas av `player_hurt`.

Ingen uppslagning på OSBase-sidan var alltså fel. Fältet de läste var det.

**Rättat i `DamageReport.cs`:** för de två tvetydiga basnamnen läses angriparens
faktiskt utrustade vapen av från pawnen (`WeaponServices.ActiveWeapon`) för att
avgöra vilken pjäs det var. Alla andra vapen lämnas orörda — de kommer redan rätt
via `e.Weapon`, och en disambiguering de inte behöver vore bara en väg till nya
fel.

Att avläsningen är säker just här beror på slotten: en spelare kan bära **en**
primär, så den aktiva M4:an *är* den som sköt. Samma sak i pistolslotten för
`hkp2000`/`usp_silencer`. Kulor i CS är dessutom hitscan — skottet och skadan
sker i samma ögonblick, så det finns inget glapp där någon hinner byta vapen
mellan avfyrning och träff.

USP-S/P2000 fixades i samma svep, av samma skäl — ~~och därmed var saken
utagerad.~~ **Det stämde inte:** pistolhalvan var död kod och skrev aldrig en enda
rad. Se «Efterkontrollen» sist i dokumentet.

Läget 2026-08-11: rättningen är byggd och committad på OSBase-sidan, **inte
driftsatt**. Tills den är det gäller allt nedan om vad som förloras per kväll.

## Vad vi behöver ~~(löst)~~

*Det ursprungliga önskemålet, som det formulerades innan roten var känd:*

Att `player_hit_stat.weapon` hämtas ur samma källa som
`player_weapon_shots.weapon`. Skottskrivaren skriver redan rätt namn varje
kväll; det är den enda skillnaden mellan de två.

Lösningen blev bättre än beställningen: i stället för att flytta träffskrivaren
till `weapon_fire` — vilket hade knutit ihop två händelser som beskriver olika
saker — läses det utrustade vapnet av vid skadetillfället, och bara för de
tvetydiga namnen.

## Vad som redan är förlorat, och varför det brådskar

Tabellen lagrar **summor**, inte händelser: en rad per (spelare, vapen, zon,
riktning, sida, säsong). När en spelares M4A4-träffar och M4A1-S-träffar hamnar
i samma räknare finns det inget kvar att dela på i efterhand. Av de 845 raderna:

| | rader | |
|---|---:|---|
| Spelare som **aldrig** avfyrat en M4A4 | 180 | går att rätta — hela räknaren tillhör den tystade |
| Äkta M4A4 | 6 | rätt som de står |
| Spelare som avfyrat **båda** | 221 | **oåterkalleligt hopblandade** |
| Mottagna träffar (`direction = 1`) | 438 | går inte att rätta — skytten står inte i tabellen |

Och den första raden är färskvara: en spelare som i dag ligger i de 180 flyttas
till de 221 i samma stund han avlossar ett enda skott med en upplockad M4A4.
Möjligheten att rätta hans historik försvinner då för gott. Högen med
oåterkalleliga rader växer alltså varje kväll tills skrivaren är lagad.

De 438 mottagna läker bara framåt.

## Vad vi gör på vår sida — och vad vi inte gör

**Utfört 2026-08-11:** 180 rader flyttade på prod. Kvar hos `m4a1` står 6 äkta
M4A4, 221 odelbara och 438 mottagna. Ångerfilen ligger i `storage/`.

`bin/fix-m4a1-silencer.php` flyttar tillbaka de 180 raderna. Regeln är
faktabaserad och inte statistisk: har en spelare skott på `m4a1_silencer` men
inga alls på `m4a1`, kan träffarna under `m4a1` bara ha kommit från den tystade.
Övriga lämnas orörda, och skriptet skriver en ångerfil före första skrivningen.

**Vi översätter INTE namnet vid läsning.** Det vore frestande och det vore fel:
de 6 äkta M4A4-raderna tillhör spelare som verkligen använder det vapnet, och en
översättning hade stulit deras statistik i stället. Felet ska lagas där det
skrivs.

## Kontrollen efteråt

När skrivaren är rättad ska nya rader dyka upp inom en kväll:

```sql
SELECT weapon, COUNT(*), MAX(updated_at) FROM player_hit_stat
 WHERE weapon LIKE 'm4a1%' GROUP BY weapon;
```

`m4a1_silencer` ska ha rader med färskt `updated_at`. Fortsätter den stå på noll
medan `player_weapon_shots` fylls på är det inte lagat, oavsett vad koden säger.

Och samma fråga för det andra paret, som fixades i samma svep men aldrig
mättes före rättningen:

```sql
SELECT weapon, COUNT(*), MAX(updated_at) FROM player_hit_stat
 WHERE weapon IN ('usp_silencer','hkp2000') GROUP BY weapon;
```

Här finns ingen backfyllning att göra på vår sida. USP-S:ens träffar har hamnat
under `hkp2000`, alltså på P2000 — ett vapen som faktiskt används — och samma
regel som räddade de 180 M4-raderna går inte att tillämpa: pistolslotten byts
under en runda på ett sätt primärvapnet inte gör, så «har aldrig avfyrat en
P2000» säger mindre om enskilda ronder. Det paret läker framåt.

## Efterkontrollen, 2026-08-30

Ägaren såg att duellpanelen på hans profil stod tom för M4A4 medan vapenlistan
gav samma vapen 23 %, och drog i tråden. Den ledde till två fel, inte ett.
Panelerna var oense för att de läser olika tabeller: träffarna ur
`player_hit_stat`, som skrivs fel, och duellerna ur `player_duel_stat`, som
alltid burit rätt namn.

Mätningen, kört mot `osbase` på prod:

```
weapon          rader  träffar  senast
hkp2000           964     8824  2026-08-29 23:07:27
m4a1             1903    36090  2026-08-29 23:24:11
m4a1_silencer     180     1432  2026-08-10 21:46:43
usp_silencer        –        –  –
```

**1. Rättningen var aldrig driftsatt.** `m4a1_silencer` står på exakt 180 rader
med en tidsstämpel från kvällen `bin/fix-m4a1-silencer.php` kördes — det är
alltså VÅRA rader, inte spelets. Sedan dess: ingenting. Under samma nitton nätter
växte `m4a1` från 845 rader till 1903, alltså fortsatte varje M4A1-S-träff hamna
under M4A4. Fixen landade 2026-08-11 och ingår i v0.0.545 (släppt 22 augusti),
så servrarna kör en build som är äldre än så.

**2. USP-halvan hade aldrig kunnat fungera.** `ResolveHitWeapon` accepterade det
utrustade vapnets namn bara när det `StartsWith` händelsens namn — ett antagande
om att det riktiga namnet är en förfining av det tvetydiga. Det är sant för
gevären (`m4a1_silencer` börjar med `m4a1`) och falskt för pistolerna:
`usp_silencer` delar inget prefix alls med `hkp2000`. Grenen var död kod från
första dagen, vilket syns på att `usp_silencer` inte har en enda rad — CT:s
standardpistol, i ett halvår. Ingen märkte det, för ett felaktigt namn som
existerar ser ut som data.

Rättat genom att skriva ut paren i stället för att härleda dem:

```csharp
private static readonly Dictionary<string, HashSet<string>> AmbiguousSlotmates = new() {
    ["m4a1"] = new() { "m4a1", "m4a1_silencer" },
    ["hkp2000"] = new() { "hkp2000", "usp_silencer" },
};
```

### Ordningen som gäller

1. Rätta `ResolveHitWeapon` — annars rullas en halv fix ut och USP-S fortsätter
   tyst hamna på P2000.
2. Bygg och driftsätt på servrarna. Kontrollera versionen; allt under 0.0.545
   saknar även M4-halvan.
3. Kvällen efter ska både `m4a1_silencer` och `usp_silencer` ha färskt
   `updated_at`. Gör de inte det är det inte lagat, oavsett vad koden säger.
4. **Först därefter** `bin/fix-m4a1-silencer.php`. Körs det före driftsättningen
   blandar nästa kväll tillbaka det som just rättats, och de rader som då fått
   ett enda M4A4-skott blir omöjliga att rädda i stället för lätta.

USP-S går inte att backfylla. Träffarna ligger under `hkp2000`, ett vapen som
faktiskt används, och regeln som räddade de 180 M4-raderna biter inte i
pistolslotten — den byts under en rond på ett sätt primärvapnet inte gör. Det
paret läker bara framåt.
