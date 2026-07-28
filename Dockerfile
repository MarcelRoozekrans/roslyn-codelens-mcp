# syntax=docker/dockerfile:1
#
# Roslyn CodeLens MCP server.
#
# The solution under analysis is mounted at /workspace; with no arguments the
# server scans the working directory for a .sln (see Program.cs). Pass explicit
# solution paths as arguments to load several at once.
#
#   docker build -t roslyn-codelens-mcp .
#   docker run -i --rm -v "$PWD:/workspace" roslyn-codelens-mcp
#
# See docs/docker.md for MCP client configuration and the NuGet cache mount.

# ---------------------------------------------------------------- build -------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Restore against the project file alone so the (slow) restore layer survives
# source-only edits. Directory.Build.props carries the shared analyzer set.
COPY Directory.Build.props ./
COPY src/RoslynCodeLens/RoslynCodeLens.csproj src/RoslynCodeLens/
RUN dotnet restore src/RoslynCodeLens/RoslynCodeLens.csproj

COPY src/ src/
RUN dotnet publish src/RoslynCodeLens/RoslynCodeLens.csproj \
      --configuration Release \
      --no-restore \
      --output /app

# -------------------------------------------------------------- runtime -------
# Must be the SDK image, not dotnet/runtime: MSBuildLocator.RegisterDefaults()
# resolves a real MSBuild installation at startup, and MSBuildWorkspace needs it
# to evaluate the mounted solution's project files.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# The SDK's first-run banner and telemetry notice write to stdout, which would
# corrupt the MCP framing on the stdio transport.
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

COPY --from=build /app /opt/roslyn-codelens

# Solutions are analyzed from here, so an argument-less run finds a mounted .sln.
WORKDIR /workspace

ENTRYPOINT ["dotnet", "/opt/roslyn-codelens/RoslynCodeLens.dll"]
