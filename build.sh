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

echo "Build complete. Artifacts are in the $BUILD_DIR directory."
