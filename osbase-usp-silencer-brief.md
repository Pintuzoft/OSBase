# USP-S loggas inte alls, och M4-rättningen är inte driftsatt

Överlämning till OSBase-sidan, 2026-08-30. Två fel som ser ut som ett, och som
kräver olika saker av er: det ena är en kodbugg att granska, det andra är en
build som aldrig rullats ut.

Uppdagat bakifrån: en spelares duellpanel på profilen stod tom för M4A4 medan
vapenlistan gav samma vapen 23 %. Panelerna läser olika tabeller —
`player_hit_stat` för träffar, `player_duel_stat` för dueller — och bara den
första skrivs fel.

## Mätningen

Kört mot `osbase` på prod, 2026-08-30:

```
weapon          rader  träffar  senast
hkp2000           964     8824  2026-08-29 23:07:27
m4a1             1903    36090  2026-08-29 23:24:11
m4a1_silencer     180     1432  2026-08-10 21:46:43
usp_silencer        –        –  –
```

## Fel 1 — USP-S-grenen är död kod

`DamageReport.ResolveHitWeapon` (src/modules/DamageReport.cs) avslutas:

```csharp
string actual = NormalizeWeapon(active.DesignerName);
return actual.StartsWith(normalizedWeapon, StringComparison.Ordinal) ? actual : normalizedWeapon;
```

Vakten antar att det riktiga namnet är en **förfining** av det tvetydiga. Det
håller för gevären — `m4a1_silencer` börjar med `m4a1` — och är falskt för
pistolerna: USP-S heter `usp_silencer` och delar inget prefix alls med
`hkp2000`. `"usp_silencer".StartsWith("hkp2000")` är falskt, så grenen faller
alltid tillbaka på händelsens namn.

Följden syns i tabellen: `usp_silencer` har inte en enda rad någonsin, medan
`hkp2000` är färsk. Varje USP-S-träff i communityt ligger under P2000 — CT:s
standardpistol, sedan modulen byggdes. Ingen märkte det, för ett felaktigt namn
som existerar ser ut som data.

### Rättningen

Skriv ut paren i stället för att härleda dem:

```csharp
private static readonly Dictionary<string, HashSet<string>> AmbiguousSlotmates = new() {
    ["m4a1"] = new() { "m4a1", "m4a1_silencer" },
    ["hkp2000"] = new() { "hkp2000", "usp_silencer" },
};

private static string ResolveHitWeapon(string normalizedWeapon, CCSPlayerController? attacker) {
    if (!AmbiguousSlotmates.TryGetValue(normalizedWeapon, out var slotmates)) {
        return normalizedWeapon;
    }

    // pawn / WeaponServices / ActiveWeapon-kontrollerna oförändrade

    string actual = NormalizeWeapon(active.DesignerName);
    return slotmates.Contains(actual) ? actual : normalizedWeapon;
}
```

Det gör vakten striktare i den riktning som betyder något: bär angriparen något
utanför paret är det inget svar på frågan "vilken av de två var det", och
händelsens eget namn behålls.

Bygger rent (`dotnet build OSBase.sln`, 0 varningar, 0 fel). Färdig patch mot
nuvarande HEAD finns om ni vill ha den i stället för att skriva av.

## Fel 2 — M4-halvan fungerar, men körs inte

`m4a1`-grenen passerar vakten och skulle ha skrivit rader. Att den inte gjort
det beror inte på koden:

- `m4a1_silencer` står på **exakt 180 rader**, alla med tidsstämpel från
  2026-08-10 21:46 — det är en manuell migrering på sajtsidan, inte spelet.
- Under samma nitton nätter växte `m4a1` från 845 rader till 1903.
- Fixen landade 2026-08-11 (`9a02439`) och ingår i **v0.0.545**, släppt 22
  augusti.

Servrarna kör alltså en build äldre än 0.0.545. Kontrollera versionen
(`css_plugins list`) — under 0.0.545 saknas även M4-halvan.

## Ordningen

1. Rätta `ResolveHitWeapon`. Rullas fixen ut som den är blir bara halva jobbet
   gjort, och USP-S fortsätter tyst hamna på P2000.
2. Bygg och driftsätt på servrarna.
3. Kvällen efter ska **både** `m4a1_silencer` och `usp_silencer` ha färskt
   `updated_at`:

```sql
SELECT weapon, COUNT(*), SUM(hits), MAX(updated_at) FROM player_hit_stat
 WHERE weapon IN ('m4a1','m4a1_silencer','hkp2000','usp_silencer') GROUP BY weapon;
```

4. Först därefter kör sajtsidan sitt reparationsskript. Körs det före
   driftsättningen blandar nästa kväll tillbaka det som just rättats.

## Vad som inte går att rädda

`player_hit_stat` lagrar summor, inte händelser, så en hopblandad räknare går
inte att dela i efterhand.

- **M4-paret:** rader från spelare som aldrig avfyrat en `m4a1` kan flyttas —
  har de skott på `m4a1_silencer` och inga på `m4a1` kan träffarna bara vara
  den tystades. Den möjligheten försvinner i samma stund de plockar upp en M4A4
  en enda gång, så högen krymper för varje kväll utan driftsättning.
- **USP-paret:** går inte att backfylla alls. Träffarna ligger under `hkp2000`,
  ett vapen som faktiskt används, och regeln ovan biter inte i pistolslotten —
  den byts under en rond på ett sätt primärvapnet inte gör. Det paret läker
  bara framåt.
