#!/usr/bin/env bash

set -euo pipefail

usage() {
  echo "Usage: $0 <paperq-binary> <linux-x64|linux-arm64> <version> <output-directory>" >&2
  exit 2
}

if [[ $# -ne 4 ]]; then
  usage
fi

binary="$1"
rid="$2"
version="$3"
output_directory="$4"

case "$rid" in
  linux-x64)
    architecture="amd64"
    ;;
  linux-arm64)
    architecture="arm64"
    ;;
  *)
    echo "Unsupported runtime identifier for Debian packaging: $rid" >&2
    exit 2
    ;;
esac

if [[ ! "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "Version must use MAJOR.MINOR.PATCH with no leading zeroes: $version" >&2
  exit 2
fi

if [[ ! -f "$binary" ]]; then
  echo "Published executable not found: $binary" >&2
  exit 1
fi

for command in dpkg-architecture dpkg-deb dpkg-shlibdeps install; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "Required packaging command is not available: $command" >&2
    exit 1
  fi
done

host_architecture="$(dpkg-architecture -qDEB_HOST_ARCH)"
if [[ "$host_architecture" != "$architecture" ]]; then
  echo "The $rid package must be built on a $architecture host, not $host_architecture." >&2
  exit 1
fi

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/.." && pwd)"
build_root="$(mktemp -d)"
package_root="$build_root/debian/paperq"

cleanup() {
  rm -rf -- "$build_root"
}
trap cleanup EXIT

mkdir -p "$package_root/DEBIAN"
chmod 0755 "$package_root"
install -D -m 0755 "$binary" "$package_root/usr/bin/paperq"
install -D -m 0644 "$repository_root/README.md" "$package_root/usr/share/doc/paperq/README.md"
install -D -m 0644 "$repository_root/LICENSE" "$package_root/usr/share/doc/paperq/copyright"

# dpkg-shlibdeps derives the glibc floor from the published ELF binary. A
# minimal source stanza gives the Debian helper the package context it expects.
cat > "$build_root/debian/control" <<EOF
Source: paperq
Section: utils
Priority: optional
Maintainer: Vojtěch Humpl <vojtahumpl@seznam.cz>

Package: paperq
Architecture: any
Description: Repository-local papercut queue for coding agents
EOF

dependency_output="$(
  cd -- "$build_root"
  dpkg-shlibdeps -O 'debian/paperq/usr/bin/paperq'
)"
dependencies="${dependency_output#shlibs:Depends=}"
if [[ -z "$dependencies" || "$dependencies" == "$dependency_output" ]]; then
  echo "Could not derive Debian runtime dependencies from $binary" >&2
  exit 1
fi

installed_size="$(du -sk "$package_root/usr" | cut -f1)"
cat > "$package_root/DEBIAN/control" <<EOF
Package: paperq
Version: $version
Section: utils
Priority: optional
Architecture: $architecture
Maintainer: Vojtěch Humpl <vojtahumpl@seznam.cz>
Installed-Size: $installed_size
Depends: $dependencies
Homepage: https://github.com/Vathivis/paperq
Description: Repository-local papercut queue for coding agents
 paperq records small, non-blocking problems encountered during coding work in
 transparent Markdown files kept inside the current repository.
EOF

mkdir -p "$output_directory"
asset="$output_directory/paperq_${version}_${architecture}.deb"
dpkg-deb --build --root-owner-group "$package_root" "$asset"

if [[ "$(dpkg-deb -f "$asset" Package)" != "paperq" ]]; then
  echo "Unexpected package name in $asset" >&2
  exit 1
fi
if [[ "$(dpkg-deb -f "$asset" Version)" != "$version" ]]; then
  echo "Unexpected package version in $asset" >&2
  exit 1
fi
if [[ "$(dpkg-deb -f "$asset" Architecture)" != "$architecture" ]]; then
  echo "Unexpected package architecture in $asset" >&2
  exit 1
fi
package_contents="$(dpkg-deb --contents "$asset")"
if ! grep -Eq '^-[r-]wxr-xr-x root/root .* \./usr/bin/paperq$' <<< "$package_contents"; then
  echo "The package does not contain executable /usr/bin/paperq with mode 0755." >&2
  exit 1
fi

printf '%s\n' "$asset"
