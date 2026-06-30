# 🚀 Automatisk Dependency Upgrade

Detta är en GitHub Actions-automation som kollar varje timme om **CounterStrikeSharp.API** har uppdaterats. Vid en ny version körs en automatisk build och release:

## ✨ Vad den gör

1. **Kollar varje timme** (00 minuten på varje hel timme) om CounterStrikeSharp.API har uppdaterats
2. **Om uppdatering finns:**
   - ✅ Uppdaterar `CounterStrikeSharp.API` version i `src/OSBase.csproj`
   - ✅ Incrementerar OSBase patch-version (0.0.499 → 0.0.500)
   - ✅ Committar ändringar
   - ✅ Kör `./clean.sh`, `./build.sh`, `./release.sh`
   - ✅ Skapar **GitHub Release** med OSBase-versionen som namn
   - ✅ Lägger till `.zip`-paketet i release

## 📅 Schedule

- **Stündlig**: Varje timme (00 minuten) UTC — kollar omedelbar om CSS har uppdaterats
- **Manuell trigger**: Du kan också köra den manuellt från GitHub Actions-fliken

## 📊 Versionering

### Automatisk (Workflow)
- Patch-version incrementeras automatiskt vid CSS-uppdatering
- Format: `MAJOR.MINOR.PATCH` (t.ex. `0.0.500`)

### Manuell inkrement
```bash
# Incrementera patch-version (0.0.499 → 0.0.500)
./scripts/increment-version.sh patch

# Incrementera minor-version (0.0.499 → 0.1.0)
./scripts/increment-version.sh minor

# Incrementera major-version (0.0.499 → 1.0.0)
./scripts/increment-version.sh major
```

## 📝 Release-format

Releases skapas med:
- **Tag**: `v0.0.500`
- **Title**: `OSBase v0.0.500`
- **Body**: Visar uppdaterade versioner av beroenden
- **Asset**: `OSBase_v0.0.500.zip`

## 🔧 Konfiguration

### Ändra schemaläggning
Redigera `.github/workflows/upgrade-dependencies.yml`:

```yaml
schedule:
  - cron: '0 * * * *'  # Ändra tidsintervallet här (nuvarande: varje timme)
```

Cron-format: `minute hour day month day-of-week`

**Exempel:**
- `0 * * * *` = **Varje timme** (00 minuten) ← **NUVARANDE**
- `*/30 * * * *` = Var 30:e minut
- `0 */6 * * *` = Var 6:e timme
- `0 2 * * *` = Varje dag kl 02:00
- `0 0 * * 0` = Varje söndag kl 00:00

### Begränsningar

- Kräver att `.git` är konfigurerad (är det redan på CI)
- Kräver `${{ secrets.GITHUB_TOKEN }}` (tillhandahålls automatiskt)
- Behöver execute-behörighet på `.sh`-filer (redan satt)

## ✅ Vad som kontrolleras

- ✔️ CounterStrikeSharp.API version från NuGet
- ✔️ Jämför med `src/OSBase.csproj`
- ✔️ Om ny version → build → release på GitHub

## 🚦 Workflow-status

Se status under GitHub **Actions** → **Auto Upgrade Dependencies**

## 📦 Varför MySqlConnector inte uppdateras automatiskt

MySqlConnector är stabil och uppdateras ofta. Om du vill automatisera den också, kan du ändra workflowet eller skapa ett separate workflow för det.

## 🔐 Säkerhet

- Använder GitHub's officiella `GITHUB_TOKEN` (begränsad åtkomst)
- Git config är satt till `github-actions[bot]`
- Ingen credentials exponeras i logs
