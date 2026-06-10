#!/bin/bash

set -euo pipefail

MODNAME=$(basename "$PWD")
MODDIR=~/Timberborn/Mods/"$MODNAME"

mkdir -p "$MODDIR"

if find . -type f -name "*.asmdef" -print -quit | grep -q .; then
    mkdir -p "$MODDIR/Scripts"
    dotnet build ./../../../"$MODNAME".csproj \
        -c Release \
        -o "$MODDIR/Scripts" \
        --nologo
    find ~/Timberborn/Mods/$MODNAME/Scripts -type f ! -name "*.dll" -delete
fi

cp ./manifest.json "$MODDIR/manifest.json"

if [ -d ./Root ]; then cp -r ./Root/. ~/Timberborn/Mods/$MODNAME/; fi
if [ -d ./Data ]; then cp -r ./Data/. ~/Timberborn/Mods/$MODNAME/; fi

find ~/Timberborn/Mods/$MODNAME/ -type f -name '*.meta' -delete