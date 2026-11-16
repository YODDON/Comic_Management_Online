#!/usr/bin/env node
// Hook PreToolUse cho Claude Code: chặn các thao tác vi phạm luật cứng trong AGENTS.md.
// Exit 2 = chặn, nội dung stderr được gửi lại cho agent làm lý do.
// In JSON permissionDecision "ask" = bắt agent hỏi người dùng trước khi làm.
// Chỉ cần Node, chạy được trên Windows, Linux, macOS.

const { execSync } = require("child_process");

// Version đã khóa (major). Đổi ở đây nếu bạn chủ động quyết định nâng.
const LOCKED_MAJOR = {
  "MassTransit": 8,
  "MassTransit.EntityFrameworkCore": 8,
  "MassTransit.RabbitMQ": 8,
  "MediatR": 12,
  "AutoMapper": 12,
  "AutoMapper.Extensions.Microsoft.DependencyInjection": 12,
};

// Vùng nhạy cảm: sửa file ở đây phải hỏi người dùng.
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
  process.stderr.write("BỊ CHẶN bởi .claude/hooks/guard.js: " + reason + "\n");
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
      block(`${m[1]} bị khóa ở major ${locked} (giấy phép). Muốn đổi version phải hỏi người dùng và sửa LOCKED_MAJOR trong guard.js.`);
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
    process.exit(0); // không đọc được thì không chặn
  }
  const tool = input.tool_name || "";
  const ti = input.tool_input || {};
  const cwd = input.cwd || process.env.CLAUDE_PROJECT_DIR || process.cwd();

  if (["Edit", "Write", "MultiEdit"].includes(tool)) {
    const p = String(ti.file_path || "");
    if (/[\\/]Migrations[\\/]/i.test(p)) block("không được sửa file trong thư mục Migrations (AGENTS.md luật 4).");
    if (isEnvFile(p)) block("không được sửa file .env chứa bí mật. Chỉ sửa .env.example với giá trị giả.");

    const text = [ti.content, ti.new_string, ...(Array.isArray(ti.edits) ? ti.edits.map((e) => e.new_string) : [])]
      .filter(Boolean).join("\n");
    if (/\.csproj$/i.test(p)) checkVersions(text);
    if (/<NoWarn>|<TreatWarningsAsErrors>\s*false|#pragma\s+warning\s+disable/i.test(text)) {
      ask("Thay đổi này tắt cảnh báo. AGENTS.md cấm tắt cảnh báo để cho build qua. Xác nhận có chủ ý?");
    }
    if (/[\\/](tests?|.*\.Tests?)[\\/]/i.test(p) && tool === "Edit") {
      ask("Agent đang sửa file test có sẵn. Xác nhận đây không phải sửa test để cho qua.");
    }
    if (SENSITIVE.some((r) => r.test(p))) {
      ask("File thuộc vùng nhạy cảm (Payment, Wallet, saga, SePay, JWT, Auth). AGENTS.md mục 6 yêu cầu hỏi trước khi sửa.");
    }
    process.exit(0);
  }

  if (tool === "Bash") {
    const cmd = String(ti.command || "");
    if (/(^|[\s;&|"'\/\\])\.env(?!\.example)(\.[\w-]+)?(\s|$|["';&|])/i.test(cmd)) {
      block("lệnh đụng tới file .env chứa bí mật.");
    }
    if (/[\\/]Migrations[\\/]/i.test(cmd) && /\b(sed|rm|mv|cp|tee|del|Remove-Item|Set-Content)\b|>/i.test(cmd)) {
      block("không được sửa thư mục Migrations bằng lệnh shell.");
    }
    if (/\bgit\s+push\b[^\n]*(--force|-f\b)/i.test(cmd)) block("không được force push.");
    if (/\bgit\s+(commit|push)\b/i.test(cmd)) {
      const b = currentBranch(cwd);
      if (b === "main" || b === "master") block(`đang ở nhánh ${b}. Tạo nhánh của giai đoạn (ví dụ phase-1-bo-dich) rồi mới commit.`);
      if (/\bgit\s+push\b[^\n]*\b(main|master)\b/i.test(cmd)) block("không được push thẳng lên main.");
    }
    if (/--no-verify/i.test(cmd)) block("không được bỏ qua kiểm tra bằng --no-verify.");
    if (/\bdotnet\s+ef\s+(database\s+drop|migrations\s+remove)\b/i.test(cmd)) block("thao tác có thể mất dữ liệu hoặc sửa migration. Hỏi người dùng.");
    if (/\brm\s+-[a-z]*r[a-z]*f|\bRemove-Item\b[^\n]*-Recurse/i.test(cmd)) {
      ask("Lệnh xóa đệ quy. Xác nhận đúng thư mục cần xóa.");
    }
    if (/\bdotnet\s+add\b[^\n]*\bpackage\b|\bnpm\s+(install|i)\s+\S/i.test(cmd)) {
      ask("Agent đang cài package mới. Kiểm tra package có thật và nằm trong danh sách đã chốt.");
    }
    process.exit(0);
  }

  process.exit(0);
});
