#!/usr/bin/env sh
# Builds the release artifacts of ScriptArt Code into artifacts/:
#   artifacts/ScriptArtCode-<runtime>/        the IDE, self-contained (no .NET install needed to start it), and a .tar.gz of it
#   artifacts/installer-<runtime>/            a Velopack installer and update packages, for runtimes of this OS when vpk is installed
#   artifacts/packages/ScriptArt.Cli.*.nupkg  the vside command as a .NET tool: dotnet tool install -g --add-source artifacts/packages ScriptArt.Cli
#   artifacts/packages/ScriptArt.NodeSdk.*    the node library SDK (with ScriptArt.GraphModel) for building node libraries
#
# Usage: eng/package.sh [runtime...]   for example: eng/package.sh win-x64 linux-x64 osx-arm64
# Without arguments, packages the IDE for the runtime of this machine.
#
# The IDE is published as a folder, not a single file: it verifies generated code with Roslyn against the framework assemblies on
# disk, which a single-file bundle does not have. Building and running projects from the IDE still needs the .NET 10 SDK.
set -eu
cd "$(dirname "$0")/.."
out=artifacts
rm -rf "$out"

if [ "$#" -eq 0 ]; then
  set -- "$(dotnet --info | sed -n 's/^ *RID: *//p' | head -n 1)"
fi

dotnet test tests/ScriptArt.Tests -c Release

for runtime in "$@"; do
  target="$out/ScriptArtCode-$runtime"
  dotnet publish src/ScriptArt.App -c Release -r "$runtime" --self-contained true -p:DebugType=none -o "$target"
  tar -czf "$target.tar.gz" -C "$out" "ScriptArtCode-$runtime"
done

# Velopack packs installers only for the OS it runs on (eng/package.ps1 does Windows).
if command -v vpk >/dev/null 2>&1; then
  version="$(dotnet msbuild src/ScriptArt.App -getProperty:Version)"
  case "$(uname -s)" in Linux) os=linux ;; Darwin) os=osx ;; *) os=none ;; esac
  for runtime in "$@"; do
    case "$runtime" in "$os"-*)
      vpk pack --packId ScriptArt.Code --packVersion "$version" --packTitle "ScriptArt Code" --packAuthors Diginaat         --packDir "$out/ScriptArtCode-$runtime" --mainExe ScriptArtCode --runtime "$runtime" --outputDir "$out/installer-$runtime" ;;
    esac
  done
fi

dotnet pack src/ScriptArt.Cli -c Release -o "$out/packages"
dotnet pack src/ScriptArt.NodeSdk -c Release -o "$out/packages"
dotnet pack src/ScriptArt.GraphModel -c Release -o "$out/packages"
echo "Artifacts are in $out/."
