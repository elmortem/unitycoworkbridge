#!/usr/bin/env bash
set -euo pipefail

version="${AGENTBRIDGE_VERSION:-1.3.0}"
install_dir="${AGENTBRIDGE_INSTALL_DIR:-$HOME/.local/bin}"

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

asset_name="agentbridge-$os-$arch.tar.gz"
release_base="https://github.com/elmortem/unitycoworkbridge/releases/download/agentbridge-v$version"

download_release_asset() {
	local asset_name="$1"
	local destination="$2"
	local asset_url="$release_base/$asset_name"
	if ! curl -fsSL "$asset_url" -o "$destination"; then
		echo "Failed to download AgentBridge release asset '$asset_name'. Release agentbridge-v$version may be incomplete: https://github.com/elmortem/unitycoworkbridge/releases/tag/agentbridge-v$version" >&2
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

if [[ ":$PATH:" != *":$install_dir:"* ]] && [ "${AGENTBRIDGE_NO_PATH_UPDATE:-0}" != "1" ]; then
	case "${SHELL:-}" in
		*/zsh) profile="$HOME/.zprofile" ;;
		*) profile="$HOME/.profile" ;;
	esac
	path_line="export PATH=\"$install_dir:\$PATH\""
	if ! grep -Fqx "$path_line" "$profile" 2>/dev/null; then
		printf '\n%s\n' "$path_line" >> "$profile"
	fi
fi

echo "Installed agentbridge to $install_dir/agentbridge"
echo "Open a new terminal or restart the agent application, then run: agentbridge --version"
