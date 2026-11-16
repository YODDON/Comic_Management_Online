#!/usr/bin/env node
// Claude Code PreToolUse hook: blocks actions that break the hard rules in AGENTS.md.
// Exit 2 = block; stderr is sent back to the agent as the reason.
// Printing JSON permissionDecision "ask" = the user must approve first.
// Needs only Node; works on Windows, Linux and macOS.

const { execSync } = require("child_process");

// Locked major versions. Change here only if you deliberately decide to upgrade.
const LOCKED_MAJOR = {
  "MassTransit": 8,
  "MassTransit.EntityFrameworkCore": 8,
  "MassTransit.RabbitMQ": 8,
  "MediatR": 12,
  "AutoMapper": 12,
  "AutoMapper.Extensions.Microsoft.DependencyInjection": 12,
};

// Sensitive areas: editing files here requires the user's approval.
const SENSITIVE = [
  /PaymentAPI[\\/]/i,
  /WalletAPI[\\/]/i,
  /[\\/]Sagas[\\/]/i,
  /PurchaseStateMachine/i,
  /SePay/i,
  /Jwt/i,
  /[\\/]Auth[\\/]/i,
];

function block(reason) {
  process.stderr.write("BLOCKED by .claude/hooks/guard.js: " + reason + "\n");
  process.exit(2);
}

function ask(reason) {
  process.stdout.write(JSON.stringify({
    hookSpecificOutput: {
      hookEventName: "PreToolUse",
      permissionDecision: "ask",
      permissionDecisionReason: reason,
    },
  }));
  process.exit(0);
}

function isEnvFile(p) {
  const name = p.split(/[\\/]/).pop() || "";
  return /^\.env(\..+)?$/i.test(name) && !/\.example$/i.test(name);
}

function checkVersions(text) {
  const re = /PackageReference\s+Include="([^"]+)"\s+Version="(\d+)[^"]*"/g;
  let m;
  while ((m = re.exec(text)) !== null) {
    const locked = LOCKED_MAJOR[m[1]];
    if (locked !== undefined && Number(m[2]) !== locked) {
      block(`${m[1]} is locked to major ${locked} (license). Ask the user; to change it, edit LOCKED_MAJOR in guard.js.`);
    }
  }
}

function currentBranch(cwd) {
  try {
    return execSync("git branch --show-current", { cwd, stdio: ["ignore", "pipe", "ignore"] })
      .toString().trim();
  } catch {
    return "";
  }
}

let raw = "";
process.stdin.on("data", (c) => (raw += c));
process.stdin.on("end", () => {
  let input;
  try {
    input = JSON.parse(raw);
  } catch {
    process.exit(0); // unreadable input: do not block
  }
  const tool = input.tool_name || "";
  const ti = input.tool_input || {};
  const cwd = input.cwd || process.env.CLAUDE_PROJECT_DIR || process.cwd();

  if (["Edit", "Write", "MultiEdit"].includes(tool)) {
    const p = String(ti.file_path || "");
    if (/[\\/]Migrations[\\/]/i.test(p)) block("files in Migrations folders must not be modified (AGENTS.md rule 4).");
    if (isEnvFile(p)) block(".env files contain secrets and must not be edited. Edit .env.example with fake values instead.");

    const text = [ti.content, ti.new_string, ...(Array.isArray(ti.edits) ? ti.edits.map((e) => e.new_string) : [])]
      .filter(Boolean).join("\n");
    if (/\.csproj$/i.test(p)) checkVersions(text);
    if (/<NoWarn>|<TreatWarningsAsErrors>\s*false|#pragma\s+warning\s+disable/i.test(text)) {
      ask("This change suppresses warnings, which AGENTS.md forbids for getting a build to pass. Is it intentional?");
    }
    if (/[\\/](tests?|.*\.Tests?)[\\/]/i.test(p) && tool === "Edit") {
      ask("The agent is editing an existing test file. Confirm this is not changing a test just to make it pass.");
    }
    if (SENSITIVE.some((r) => r.test(p))) {
      ask("This file is in a sensitive area (Payment, Wallet, saga, SePay, JWT, Auth). AGENTS.md requires approval before editing.");
    }
    process.exit(0);
  }

  if (tool === "Bash") {
    const cmd = String(ti.command || "");
    if (/(^|[\s;&|"'\/\\])\.env(?!\.example)(\.[\w-]+)?(\s|$|["';&|])/i.test(cmd)) {
      block("this command touches a .env file containing secrets.");
    }
    if (/[\\/]Migrations[\\/]/i.test(cmd) && /\b(sed|rm|mv|cp|tee|del|Remove-Item|Set-Content)\b|>/i.test(cmd)) {
      block("Migrations folders must not be modified with shell commands.");
    }
    if (/\bgit\s+push\b[^\n]*(--force|-f\b)/i.test(cmd)) block("force push is not allowed.");
    if (/\bgit\s+(commit|push)\b/i.test(cmd)) {
      const b = currentBranch(cwd);
      if (b === "main" || b === "master") block(`you are on ${b}. Create the phase branch from PLAN.md (e.g. phase-1-remove-translation) before committing.`);
      if (/\bgit\s+push\b[^\n]*\b(main|master)\b/i.test(cmd)) block("pushing directly to main is not allowed.");
    }
    const msg = cmd.match(/\bgit\s+commit\b[^\n]*?\s-[a-zA-Z]*m\s+(?:"([^"]*)"|'([^']*)')/);
    if (msg) {
      const first = (msg[1] || msg[2] || "").split(/\r?\n/)[0];
      if (!/^(feat|fix|refactor|test|build|chore|docs)\([a-z0-9-]+\): T\d{2} \S.{0,70}$/.test(first)) {
        block(`commit message "${first}" does not follow the format "<type>(<scope>): <ID> <summary>", e.g. "refactor(comic): T04 remove translation files" (see Git workflow in AGENTS.md).`);
      }
    }
    if (/\bgh\s+pr\s+merge\b[^\n]*--admin/i.test(cmd)) block("bypassing branch protection with --admin is not allowed.");
    if (/\bgit\s+merge\b/i.test(cmd)) block("do not merge locally. Merge through a PR with gh pr merge (see Git workflow in AGENTS.md).");
    if (/--no-verify/i.test(cmd)) block("skipping checks with --no-verify is not allowed.");
    if (/\bdotnet\s+ef\s+(database\s+drop|migrations\s+remove)\b/i.test(cmd)) block("this can lose data or alter migrations. Ask the user.");
    if (/\brm\s+-[a-z]*r[a-z]*f|\bRemove-Item\b[^\n]*-Recurse/i.test(cmd)) {
      ask("Recursive delete. Confirm this is the right folder.");
    }
    if (/\bdotnet\s+add\b[^\n]*\bpackage\b|\bnpm\s+(install|i)\s+\S/i.test(cmd)) {
      ask("The agent is installing a new package. Check it exists and is on the approved list.");
    }
    process.exit(0);
  }

  process.exit(0);
});
