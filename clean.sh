#!/bin/bash

# Ensure the script stops on errors
set -e

# Define directories to clean
CLEAN_DIRS=("src/obj" "src/bin" "build" "release")

echo "Cleaning up build artifacts..."

# Loop through and remove directories
for DIR in "${CLEAN_DIRS[@]}"; do
    if [ -d "$DIR" ]; then
        echo "Removing $DIR..."
        rm -rf "$DIR"
    fi
done

echo "Cleanup complete!"
