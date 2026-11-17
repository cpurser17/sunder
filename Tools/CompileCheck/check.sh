#!/usr/bin/env bash
# Compile-checks Assets/Scripts outside Unity (needs the .NET 8 SDK).
#   Tools/CompileCheck/check.sh
# Prints errors (with their real Assets/Scripts paths) or "Build succeeded".
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
src="$here/obj/src"

rm -rf "$src" && mkdir -p "$src"
cp -r "$root/Assets/Scripts/." "$src/"

# Unity 6 calls the Unity 2021.3 reference assemblies don't have.
find "$src" -name '*.cs' -exec sed -i -E \
  's/FindObjectsByType<([A-Za-z0-9_]+)>\((FindObjectsInactive\.[A-Za-z]+)\)/FindObjectsByType<\1>(\2, FindObjectsSortMode.None)/g' {} +

dotnet build "$here/CompileCheck.csproj" -nologo --no-incremental 2>&1 \
  | grep -E "error|warning CS|Build succeeded|FAILED" \
  | sed "s#$src/#Assets/Scripts/#g; s# \[$here/CompileCheck.csproj\]##" | sort -u
exit "${PIPESTATUS[0]}"
