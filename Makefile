# Day0Gen — local build/verify helpers.
# Usage:
#   make            # fetch dotnet-sdk via nix (cached now) and compile src/ (Release)
#   make build      # same as above
#   make audit      # mechanical C#5-only syntax audit (no toolchain needed)
#   make clean
#
# The SDK out-link is shared so nix downloads it only once:
#   /tmp/dotnet-sdk-result -> /nix/store/...-dotnet-sdk-wrapped-8.0.424

DOTNET_LINK := /tmp/dotnet-sdk-result
DOTNET := $(shell readlink $(DOTNET_LINK) 2>/dev/null)/bin/dotnet
DOTNET_ENV := DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDTERMINALLOGGER=false

.PHONY: all build audit clean sdk

all: build

# Make sure the nix dotnet-sdk exists (no-op when already fetched).
sdk:
	@if [ ! -x "$(DOTNET)" ]; then \
	    echo ">> fetching dotnet-sdk via nix (first run downloads ~182 MiB) ..."; \
	    nix build nixpkgs#dotnet-sdk --out-link $(DOTNET_LINK); \
	fi
	@test -x "$(DOTNET)" || { echo "ERROR: dotnet not available at $(DOTNET)"; exit 2; }

build: sdk
	$(DOTNET_ENV) $(DOTNET) build src -c Release

audit:
	python3 scripts/audit-cs5.py src/Day0Gen.cs

clean:
	rm -rf src/bin src/obj
