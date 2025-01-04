#!/bin/bash

# Ensure the script stops on errors
set -e

# Define directories and release details
BUILD_DIR="build"
RELEASE_DIR="release"
VERSION="v0.0.1"
PACKAGE_NAME="OSBase_$VERSION.zip"

# Ensure the build directory exists
if [ ! -d "$BUILD_DIR" ]; then
  echo "Build directory not found. Run build.sh first."
  exit 1
fi

# Create a clean release directory
echo "Preparing release directory..."
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"

# Copy necessary files to the release directory
echo "Copying files to release directory..."
cp -r "$BUILD_DIR/"* "$RELEASE_DIR/"

# Add additional files like README or config if needed
cp README.md "$RELEASE_DIR/" 2>/dev/null || true
cp LICENSE "$RELEASE_DIR/" 2>/dev/null || true

# Create a zip package
echo "Creating release package: $PACKAGE_NAME..."
cd "$RELEASE_DIR"
zip -r "../$PACKAGE_NAME" ./*
cd ..

echo "Release package created: $PACKAGE_NAME"
