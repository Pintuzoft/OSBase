#!/bin/bash

# Ensure the script stops on errors
set -e

# Define directories
BUILD_DIR="build"
RELEASE_DIR="release"

# Extract plugin name from C# file
PLUGIN_NAME=$(grep -Po '(?<=public override string ModuleName => ")[^"]*' src/OSBase.cs)
if [ -z "$PLUGIN_NAME" ]; then
    echo "Error: Could not find plugin name in src/OSBase.cs"
    exit 1
fi

# Extract version from C# file
VERSION=$(grep -Po '(?<=public override string ModuleVersion => ")[^"]*' src/OSBase.cs)
if [ -z "$VERSION" ]; then
    echo "Error: Could not find version in src/OSBase.cs"
    exit 1
fi

# Define package name
PACKAGE_NAME="${PLUGIN_NAME}_v${VERSION}.zip"

# Ensure the build directory exists
if [ ! -d "$BUILD_DIR" ]; then
    echo "Build directory not found. Run build.sh first."
    exit 1
fi

# Prepare the release directory
echo "Preparing release directory..."
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR/$PLUGIN_NAME"

# Copy build files to the plugin folder inside release
echo "Copying files to release directory..."
cp -r "$BUILD_DIR/"* "$RELEASE_DIR/$PLUGIN_NAME/"

# Optionally copy additional files like README.md or LICENSE to the plugin folder
cp README.md "$RELEASE_DIR/$PLUGIN_NAME/" 2>/dev/null || true
cp LICENSE "$RELEASE_DIR/$PLUGIN_NAME/" 2>/dev/null || true

# Remove old zip packages
echo "Removing old release packages..."
rm -vf OSBase_v*

# Create a zip package
echo "Creating release package: $PACKAGE_NAME..."
cd "$RELEASE_DIR"
zip -r "../$PACKAGE_NAME" ./*
cd ..

echo "Release package created: $PACKAGE_NAME"
