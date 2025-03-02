#!/bin/bash

# Ensure the script stops on errors
set -e

# Define project directory and build output directory
PROJECT_DIR="src"
BUILD_DIR="build"

# Clean the build directory
echo "Cleaning build directory..."
rm -rf "$BUILD_DIR"

# Build the project
echo "Building the project..."
dotnet build "$PROJECT_DIR/OSBase.csproj" -c Release -o "$BUILD_DIR"

# Extract the version number from OSBase.csproj
MYSQL_VERSION=$(grep 'PackageReference Include="MySqlConnector"' src/OSBase.csproj | sed -E 's/.*Version="([^"]+)".*/\1/')
echo "Detected MySqlConnector version: $MYSQL_VERSION"

MYSQL_DLL_PATH="$HOME/.nuget/packages/mysqlconnector/$MYSQL_VERSION/lib/net8.0/MySqlConnector.dll"

echo "Copying MySqlConnector.dll from $MYSQL_DLL_PATH to $BUILD_DIR"
cp "$MYSQL_DLL_PATH" "$BUILD_DIR/"

echo "Build complete. Artifacts are in the $BUILD_DIR directory."
