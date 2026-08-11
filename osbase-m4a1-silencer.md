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

```sql
SELECT weapon, COUNT(*) FROM player_hit_stat
 WHERE weapon IN ('usp_silencer','hkp2000','m4a1','m4a1_silencer') GROUP BY weapon;
```

- **Finns `usp_silencer`** är suffixet inte problemet i allmänhet — då är det
  M4-specifikt, troligen en uppslagning där båda M4:orna pekar på samma post.
- **Saknas `usp_silencer` också** tappas hela suffixfamiljen, och USP-S är
  drabbad likadant fast tystare.

## Vad vi behöver

Att `player_hit_stat.weapon` hämtas ur samma källa som
`player_weapon_shots.weapon`. Skottskrivaren skriver redan rätt namn varje
kväll; det är den enda skillnaden mellan de två.

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

