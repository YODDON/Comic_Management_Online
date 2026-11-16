# AGENTS.md

Rules for AI agents working in `Comic_Management_Online`. Read this file and `PLAN.md` before changing anything.

## Goal

Migrate the .NET 8 backend from 9 separately running projects (8 APIs + ApiGateway) into **one ASP.NET Core 8 application (modular monolith)** named `ComicMonolith.Api`, where each old service becomes a module. Deploy with Docker Compose on a VPS. The React/Vite frontend stays as is; only `VITE_API_URL` is set at build time.

The code was copied from https://github.com/YODDON/Comic_Management (reference only, never modify that repo).

## Settled decisions (do not change on your own)

- **Remove:** ApiGateway/YARP (replace with a path-rewrite middleware), gRPC and the 16 `.proto` files (replace with direct in-process interfaces), RabbitMQ (MassTransit runs in-memory), Redis (`AddDistributedMemoryCache()`), OData, `Azure.Containers.Tools.Targets`, `Microsoft.AspNetCore.Http.Features 5.0.17`, n8n + LibreTranslate and the whole translation feature.
- **Keep:** MassTransit core + the `PurchaseStateMachine` saga, MediatR, AutoMapper 12, EF Core 8 + SQL Server with 8 DbContexts / 8 databases (each module runs its own `Database.Migrate()`), JWT, BCrypt, Google.Apis.Auth, MailKit, Cloudinary, DotNetEnv.
- **Undecided, ask first:** MassTransit EF Outbox (T22), merging the 8 databases into one.
- **Version lock:** do not change the major version of MassTransit (8), MediatR (12), AutoMapper (12). Newer versions changed license. A hook enforces this.

## Verification commands

```
cd backend && dotnet build prn232_comic_api.sln
cd frontend && npm install && npm run build
```

If a test project exists, also run `dotnet test`. Only report "done" when these pass, and paste the result. A clean build does not prove the logic is correct.

## Rules

1. One session = **one task** from `PLAN.md`. Do not combine tasks or start later tasks early.
2. Read the relevant files first. For High-risk or multi-file tasks, propose a plan (files to change, approach) and **wait for the user's "ok"**.
3. Stay inside the task's scope. No refactoring, renaming or reformatting unrelated files.
4. Never modify `Migrations` folders.
5. Keep the API paths the frontend calls. The old gateway rewrote `/comics`, `/categories`, `/chapters`, `/comments`, `/favorites`, `/follows`, `/reading-history`, `/payments`, `/uploads`, `/missions`, `/notifications` to `/api/...`, and kept `/auth`, `/wallets`, `/currency`, `/withdraws`, `/banners` unchanged. There is no `/translations` route anymore.
6. Keep the public (anonymous) endpoint list from the old gateway (`ApiGateway/Program.cs`).
7. Never read, edit or commit `.env` files or secrets. Only edit `.env.example` with fake values.
8. Never commit or push to `main` directly. Follow the Git workflow below.
9. Do not upgrade packages unless the task requires it. Ask before adding any new package.
10. Never delete or edit tests to make them pass; never suppress warnings or errors.
11. After 3 failed attempts at the same error: stop, paste the exact error, propose 1–2 options and ask.

## Git workflow (the agent does this itself)

1. **Branch:** before starting a task, run `git checkout main && git pull`. If the task's phase branch (column "Branch" in `PLAN.md`) exists, switch to it; otherwise create it from `main`. Never work on `main`.
2. **Commit:** one commit per task, Conventional Commits format with the task ID:
   `<type>(<scope>): <ID> <summary>`, e.g. `refactor(comic): T04 remove translation files`.
   Types: `feat`, `fix`, `refactor`, `test`, `build` (packages, Docker, compose), `chore` (cleanup), `docs`.
   Scope: module or area in lowercase (`host`, `comic`, `payment`, `frontend`, `deps`, `deploy`, `plan`).
   Summary in English, imperative, max 72 characters. Include the `PLAN.md` update in the same commit.
3. **Push:** `git push -u origin <branch>` after each commit.
4. **Merge at the end of a phase:** when every task of the phase is `Done` and verification passes, open a PR with
   `gh pr create --base main --title "<phase>: <summary>" --body "<tasks done, verification results, unverified items>"`,
   then `gh pr merge --merge --delete-branch` (merge commit, so each task commit stays in history).
   Never use `--admin` or bypass branch protection.
5. **Ask before merging** if the phase contains any High-risk task or touches a sensitive area. Show the PR link and wait for "merge".
6. After merging, run `git checkout main && git pull`.

## Sensitive areas (stop and ask)

PaymentAPI, WalletAPI, the `PurchaseStateMachine` saga, chapter purchase, deposits/withdrawals, the SePay/VietQR webhook, JWT, login, Google OAuth, the public endpoint list, and anything that can lose data. Explain what you will change, the risk, and wait for approval. After changing, say how the user should test manually. Do not change Payment/Wallet logic before characterization tests exist (T37).

## Merging modules (T09)

gRPC calls between modules (Mission↔Social, Chapter↔Mission, Comic↔Chapter, Payment→Wallet/Chapter): **do not redesign them yet**. Record them in the "Remaining gRPC calls" table in `PLAN.md`; if they block the build, stop and ask.

## End-of-task report

- **Changed:** list of files.
- **Verification:** commands run and their results.
- **Unverified / user should check:** what the build cannot prove.

Then update the task status in `PLAN.md` and add one line to the Log.
