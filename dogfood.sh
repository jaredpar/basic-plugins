#!/usr/bin/env bash
# Usage: dogfood.sh -i   Build and install the plugin locally
#        dogfood.sh -u   Uninstall local build and reinstall from marketplace
set -euo pipefail

repo_root="$(cd "$(dirname "$0")" && pwd)"
plugin_name="basic-triage-mcp"

usage() {
    echo "Usage: $0 -i | -u" >&2
    echo "  -i  Install: build and install the plugin locally" >&2
    echo "  -u  Undo: uninstall local build and reinstall from marketplace" >&2
    exit 1
}

install() {
    undo_any

    # Install from the local directory
    echo "Installing $plugin_name"
    copilot mcp add "$plugin_name" "$repo_root/artifacts/bin/Pipeline.Mcp/debug/Pipeline.Mcp"
    mkdir -p ~/.copilot/skills
    ln -s "$repo_root/plugins/basic-triage-mcp/skills/azdo-helix" ~/.copilot/skills/azdo-helix
    ln -s "$repo_root/plugins/basic-triage-mcp/skills/squirrel" ~/.copilot/skills/squirrel
}

undo_any() {
    # Uninstall any existing version — ignore errors
    copilot plugin uninstall "$plugin_name" 2>/dev/null || true
    copilot plugin uninstall "$plugin_name@basic-plugins" 2>/dev/null || true
    copilot mcp remove "$plugin_name" 2>/dev/null || true
    rm ~/.copilot/skills/azdo-helix 2>/dev/null || true
    rm ~/.copilot/skills/squirrel 2>/dev/null || true
}

undo() {
    undo_any

    # Reinstall from marketplace
    echo "Installing plugin from marketplace: $plugin_name@basic-plugins"
    if ! copilot plugin install "$plugin_name@basic-plugins"; then
        echo "copilot plugin install from marketplace failed" >&2
        exit 1
    fi

    echo "Marketplace plugin installed successfully. Start copilot to use it."
}

[ $# -eq 0 ] && usage

while getopts "iu" opt; do
    case "$opt" in
        i) install ;;
        u) undo ;;
        *) usage ;;
    esac
done
