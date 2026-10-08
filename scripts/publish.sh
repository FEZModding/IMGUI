#!/usr/bin/env bash

set -euo pipefail

repository_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
release_dir="$repository_dir/release"
package_dir="$release_dir/IMGUI"
archive="$release_dir/IMGUI.zip"

rm -rf "$package_dir"
rm -f "$archive"
mkdir -p "$package_dir"

dotnet publish "$repository_dir/IMGUI.csproj" --configuration Release -p:ContinuousIntegrationBuild=true -p:ModOutputDir="$package_dir"

(
  cd "$package_dir"
  zip -r "$archive" .
)

rm -rf "$package_dir"
