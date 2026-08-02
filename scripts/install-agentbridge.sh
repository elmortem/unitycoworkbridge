#!/usr/bin/env bash
set -euo pipefail

version="${AGENTBRIDGE_VERSION:-}"
install_dir="${AGENTBRIDGE_INSTALL_DIR:-$HOME/.local/bin}"
rid="${AGENTBRIDGE_RID:-}"

case "$(uname -s)" in
	Darwin) os="osx" ;;
	Linux) os="linux" ;;
	*) echo "unsupported operating system: $(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
	x86_64|amd64) arch="x64" ;;
	arm64|aarch64) arch="arm64" ;;
	*) echo "unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

native_rid="$os-$arch"
if [ -z "$rid" ]; then
	rid="$native_rid"
fi

case "$rid" in
	linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
	*) echo "unsupported runtime identifier: $rid" >&2; exit 1 ;;
esac

asset_name="agentbridge-$rid.tar.gz"

if [ -z "$version" ]; then
	release_base="https://github.com/elmortem/unitycoworkbridge/releases/latest/download"
	release_page="https://github.com/elmortem/unitycoworkbridge/releases/latest"
	release_name="the latest release"
else
	release_base="https://github.com/elmortem/unitycoworkbridge/releases/download/agentbridge-v$version"
	release_page="https://github.com/elmortem/unitycoworkbridge/releases/tag/agentbridge-v$version"
	release_name="release agentbridge-v$version"
fi

download_release_asset() {
	local asset_name="$1"
	local destination="$2"
	local asset_url="$release_base/$asset_name"
	if ! curl -fsSL "$asset_url" -o "$destination"; then
		echo "Failed to download AgentBridge release asset '$asset_name'. $release_name may be incomplete: $release_page" >&2
		return 1
	fi
}

temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT

download_release_asset "$asset_name" "$temporary_directory/$asset_name"
download_release_asset "$asset_name.sha256" "$temporary_directory/$asset_name.sha256"

(
	cd "$temporary_directory"
	if command -v sha256sum >/dev/null 2>&1; then
		sha256sum -c "$asset_name.sha256"
	else
		shasum -a 256 -c "$asset_name.sha256"
	fi
	tar -xzf "$asset_name"
)

mkdir -p "$install_dir"
install -m 0755 "$temporary_directory/agentbridge" "$install_dir/agentbridge"

if [ "$rid" = "$native_rid" ] && [[ ":$PATH:" != *":$install_dir:"* ]] && [ "${AGENTBRIDGE_NO_PATH_UPDATE:-0}" != "1" ]; then
	case "${SHELL:-}" in
		*/zsh) profile="$HOME/.zprofile" ;;
		*) profile="$HOME/.profile" ;;
	esac
	path_line="export PATH=\"$install_dir:\$PATH\""
	if ! grep -Fqx "$path_line" "$profile" 2>/dev/null; then
		printf '\n%s\n' "$path_line" >> "$profile"
	fi
fi

echo "Installed agentbridge ($rid) to $install_dir/agentbridge"
if [ "$rid" = "$native_rid" ]; then
	echo "Open a new terminal or restart the agent application, then run: agentbridge --version"
fi
