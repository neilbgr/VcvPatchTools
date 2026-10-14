CLI     := src/VcvPatchTools.Cli
WEB     := src/VcvPatchTools.Web
CONFIG  := Release
PLUGINS ?= ../Cardinal/plugins

.PHONY: version build test format catalog cardinal-catalog web publish-linux publish-win publish-osx publish-web publish-all clean

# The version every binary is built with (Directory.Build.props); a release is the tag v<version>.
version:
	@dotnet msbuild src/VcvPatchTools.Core -getProperty:Version

build:
	dotnet build

test:
	dotnet test

format:
	dotnet format

# Regenerates catalog/ports.json from the Cardinal plugin sources (rebuild afterwards: it is embedded in Core).
catalog:
	dotnet run --project $(CLI) -- catalog build --src $(PLUGINS) --out catalog/ports.json --report catalog/scan-report.txt
	dotnet build

# Regenerates catalog/cardinal.json: modules official Cardinal (DISTRHO upstream/main) loads, and those only the
# neilbgr edition (the Cardinal working copy) adds. Needs the Cardinal clone's "upstream" remote.
CARDINAL ?= $(PLUGINS)/..
cardinal-catalog:
	scripts/export-cardinal-upstream.sh $(CARDINAL) out/cardinal-upstream
	dotnet run --project $(CLI) -- catalog cardinal --upstream out/cardinal-upstream --fork $(CARDINAL) --out catalog/cardinal.json
	dotnet build

# Local dev server for the Blazor app: http://localhost:5016 (add ?sample=AmbientJam to load the sample).
web:
	dotnet run --project $(WEB) --launch-profile http

publish-linux:
	dotnet publish $(CLI) -c $(CONFIG) -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64

publish-win:
	dotnet publish $(CLI) -c $(CONFIG) -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64

publish-osx:
	dotnet publish $(CLI) -c $(CONFIG) -r osx-x64 --self-contained -p:PublishSingleFile=true -o publish/osx-x64

# Static site in publish/web/wwwroot: can be served by any static file server.
publish-web:
	dotnet publish $(WEB) -c $(CONFIG) -o publish/web

publish-all: publish-linux publish-win publish-web

clean:
	rm -rf publish out
	dotnet clean
