---
title: Docker
sidebar_position: 5
---

# Running in Docker

The repository ships a `Dockerfile`, so you can run the server without a .NET SDK
on the host. The solution under analysis is bind-mounted into the container.

## Build the image

```bash
docker build -t roslyn-codelens-mcp .
```

## Run it

The server communicates over stdio, so the container needs `-i` and must not
allocate a TTY:

```bash
docker run -i --rm -v "$PWD:/workspace" roslyn-codelens-mcp
```

With no arguments the server scans its working directory (`/workspace`) for a
`.sln`/`.slnx`. To load specific solutions, or several at once, pass them as
arguments — as container paths, not host paths:

```bash
docker run -i --rm -v "$PWD:/workspace" roslyn-codelens-mcp \
  /workspace/First.slnx /workspace/Second.slnx
```

## MCP client configuration

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "command": "docker",
      "args": [
        "run", "-i", "--rm",
        "-v", "/absolute/path/to/your/repo:/workspace",
        "-v", "roslyn-codelens-nuget:/root/.nuget/packages",
        "roslyn-codelens-mcp"
      ]
    }
  }
}
```

## Restore the solution first

`MSBuildWorkspace` needs the analyzed solution's NuGet packages on disk to
resolve project references. An unrestored solution loads with missing references
and tools then return incomplete results.

Either restore on the host before starting the container, or restore inside it:

```bash
docker run --rm -v "$PWD:/workspace" \
  -v roslyn-codelens-nuget:/root/.nuget/packages \
  --entrypoint dotnet roslyn-codelens-mcp restore
```

Mounting a named volume at `/root/.nuget/packages` (as in the config above) keeps
that cache between runs — without it every container start re-downloads packages.

:::warning
Restoring inside the container rewrites `obj/project.assets.json` in the **mounted
host tree**, pointing it at `/root/.nuget/packages/`. Your next host build has to
re-restore before it works. If you build on the host as well, restore there
instead and let the container read the result.
:::

## Why the image is SDK-based

The runtime stage uses `mcr.microsoft.com/dotnet/sdk:10.0` rather than the
smaller `dotnet/runtime` image. `MSBuildLocator.RegisterDefaults()` resolves a
real MSBuild installation at startup, and `MSBuildWorkspace` needs it to
evaluate the mounted solution's project files. The runtime-only image has
neither, and the server cannot load a solution without them.

The cost is size: the image is ~1.3 GB, almost all of it the SDK base layer.

## Windows hosts

Under Git Bash / MSYS, path arguments are rewritten before Docker sees them —
`/workspace/App.slnx` becomes `C:/Program Files/Git/workspace/App.slnx` and the
solution is not found. Prefix the command with `MSYS_NO_PATHCONV=1`, or use
PowerShell:

```powershell
docker run -i --rm -v "${PWD}:/workspace" roslyn-codelens-mcp
```

## Limitations

- **Absolute paths are container paths.** Tools that report or accept file paths
  see `/workspace/...`, not your host layout.
- **Trust decisions do not persist.** The analyzer trust store lives in the
  container filesystem and is lost on `--rm`. Solutions passed as arguments are
  session-trusted automatically; mount a volume over the trust store path if you
  need decisions to survive restarts.
- **The container needs network access** on first run for any restore.
