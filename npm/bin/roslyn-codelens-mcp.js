#!/usr/bin/env node

// Launcher for the RoslynCodeLens.Mcp .NET global tool.
//
// This package ships no server code. It exists so the server can be started with
// `npx -y roslyn-codelens-mcp` by clients and directories that only know how to run
// npm-hosted MCP servers. Everything it does is: verify the .NET SDK is present,
// make sure the global tool matches this package's version, then exec it.
//
// stdout is the JSON-RPC channel, so every diagnostic and every byte of dotnet's
// install output goes to stderr.

const { execFileSync, spawn } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const PACKAGE_ID = "RoslynCodeLens.Mcp";
const COMMAND = "roslyn-codelens-mcp";
const { version } = require("../package.json");

function fail(message) {
  process.stderr.write(`roslyn-codelens-mcp: ${message}\n`);
  process.exit(1);
}

function hasDotnet() {
  try {
    execFileSync("dotnet", ["--version"], { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}

// The installed version of the global tool, or null when it is not installed.
// `dotnet tool list --global` lowercases the package id and prints it as
// `roslyncodelens.mcp   2.14.0   roslyn-codelens-mcp`. Local lookup — no network.
function installedVersion() {
  try {
    const output = execFileSync("dotnet", ["tool", "list", "--global"], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    });
    for (const line of output.split("\n")) {
      const match = line.match(/^roslyncodelens\.mcp\s+(\S+)/i);
      if (match) {
        return match[1];
      }
    }
  } catch {
    // dotnet missing or the command failed — treat the tool as not installed.
  }
  return null;
}

// `dotnet tool install` fails when a different version is already present, so the
// verb depends on what is there. Output is routed to stderr (fd 2) rather than
// captured so a slow first-run restore still shows progress.
function install(alreadyInstalled) {
  const verb = alreadyInstalled ? "update" : "install";
  process.stderr.write(
    `roslyn-codelens-mcp: ${verb === "install" ? "installing" : "updating"} ${PACKAGE_ID} ${version}...\n`
  );
  execFileSync(
    "dotnet",
    ["tool", verb, "--global", PACKAGE_ID, "--version", version],
    { stdio: ["ignore", 2, 2] }
  );
}

// Global tools land in ~/.dotnet/tools, which is not necessarily on PATH — notably
// in the same process that just installed them. Resolve the executable directly and
// prepend the directory for the child so any tool-to-tool lookup also works.
function toolsDir() {
  return (
    process.env.DOTNET_TOOLS_PATH ||
    path.join(process.env.DOTNET_CLI_HOME || os.homedir(), ".dotnet", "tools")
  );
}

function resolveExecutable() {
  const dir = toolsDir();
  const name = process.platform === "win32" ? `${COMMAND}.exe` : COMMAND;
  const candidate = path.join(dir, name);
  return fs.existsSync(candidate) ? candidate : COMMAND;
}

function launch() {
  const dir = toolsDir();
  const child = spawn(resolveExecutable(), process.argv.slice(2), {
    stdio: "inherit",
    env: {
      ...process.env,
      PATH: `${dir}${path.delimiter}${process.env.PATH || ""}`,
    },
  });

  for (const signal of ["SIGINT", "SIGTERM"]) {
    process.on(signal, () => child.kill(signal));
  }

  child.on("error", (err) => fail(`failed to start ${COMMAND}: ${err.message}`));
  child.on("exit", (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
      return;
    }
    process.exit(code ?? 0);
  });
}

if (!hasDotnet()) {
  fail(
    "the .NET SDK was not found on PATH. RoslynCodeLens requires the .NET 10 SDK — https://dotnet.microsoft.com/download"
  );
}

const current = installedVersion();

// Already on the matching version — skip the install round-trip entirely so startup
// stays fast and works offline.
if (current !== version) {
  try {
    install(current !== null);
  } catch (err) {
    if (current === null) {
      fail(`failed to install ${PACKAGE_ID} ${version}: ${err.message}`);
    }
    // A version mismatch is not fatal when a working tool is already present:
    // an offline or rate-limited NuGet should not take the server down.
    process.stderr.write(
      `roslyn-codelens-mcp: could not update to ${version}, continuing with installed ${current}\n`
    );
  }
}

launch();
