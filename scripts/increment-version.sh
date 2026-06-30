#!/bin/bash

# increment-version.sh - Incrementerar OSBase-versionen
# Använd: ./scripts/increment-version.sh [major|minor|patch]
# Standard är patch-increment

set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PROJECT_DIR="$SCRIPT_DIR/.."

# Läs nuvarande version från OSBase.cs
CURRENT_VERSION=$(grep -oP 'public override string ModuleVersion => "\K[^"]+' "$PROJECT_DIR/src/OSBase.cs")

if [ -z "$CURRENT_VERSION" ]; then
    echo "❌ Kunde inte hitta version i src/OSBase.cs"
    exit 1
fi

echo "📦 Nuvarande version: $CURRENT_VERSION"

# Parse version
IFS='.' read -r major minor patch <<< "$CURRENT_VERSION"

# Bestäm vad som ska incrementeras
BUMP_TYPE="${1:-patch}"

case "$BUMP_TYPE" in
    major)
        major=$((major + 1))
        minor=0
        patch=0
        ;;
    minor)
        minor=$((minor + 1))
        patch=0
        ;;
    patch)
        patch=$((patch + 1))
        ;;
    *)
        echo "❌ Okänd versionstyp: $BUMP_TYPE"
        echo "   Använd: major, minor, eller patch"
        exit 1
        ;;
esac

NEW_VERSION="$major.$minor.$patch"

echo "📝 Uppdaterar version till: $NEW_VERSION"

# Uppdatera OSBase.cs
sed -i 's/public override string ModuleVersion => "[^"]*"/public override string ModuleVersion => "'$NEW_VERSION'"/' "$PROJECT_DIR/src/OSBase.cs"
echo "✅ Uppdaterad src/OSBase.cs"

# Uppdatera .csproj
sed -i 's/<Version>[^<]*<\/Version>/<Version>0.'$minor'.'$patch'<\/Version>/' "$PROJECT_DIR/src/OSBase.csproj"
sed -i 's/<FileVersion>[^<]*<\/FileVersion>/<FileVersion>0.'$minor'.'$patch'<\/FileVersion>/' "$PROJECT_DIR/src/OSBase.csproj"
echo "✅ Uppdaterad src/OSBase.csproj"

echo "✅ Version uppdaterad från $CURRENT_VERSION till $NEW_VERSION"
