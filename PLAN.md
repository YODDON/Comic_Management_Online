# PLAN.md

Plan for migrating `Comic_Management` (microservices) into `Comic_Management_Online` (modular monolith).
Status: `Todo` / `In progress` / `Done` / `Blocked`. Hours are rough estimates. Risk: Low / Med / High.
"manual" = done by the user, not the agent.

## Baseline (filled in by T00)

- `dotnet build` of the microservices solution: succeeded, 0 errors, 93 warnings (`cd backend && dotnet build prn232_comic_api.sln`, ~27 s). Built with SDK 10.0.404 (only 9.0.321 and 10.0.404 are installed, no SDK 8, no `global.json`); projects target `net8.0`.
- `npm run build` of the frontend: succeeded, no warnings (`npm install && npm run build`, vite 8.1.4, 96 modules, Node v24.15.0). `npm install` reports 13 vulnerabilities (5 moderate, 8 high), not touched.
- Pre-existing errors and warnings (not regressions):
  - 0 build errors.
  - 46x CS8618 (non-nullable property not initialised): mostly `*.Application` projects of UserAPI, PaymentAPI, ChapterAPI, MissionAPI, WalletAPI, SocialAPI (command/request classes).
  - 42x NU1903: `AutoMapper 12.0.1` has known high-severity advisories (GHSA-6vhm-j9h7-f9gh, GHSA-gpwp-5797-mjff, GHSA-rvv3-g6hj-g44x). Major version is pinned to 12 by decision, so this stays.
  - PaymentAPI.Application (sensitive area, do not touch before T37): 2x CS8600 and 2x CS0219 in `PurchaseChapterCommand.cs` (lines 120, 121, 148, 160; unused `walletDebited`, `remainingBalance`), 1x CS8629 in `PurchaseCompletedConsumer.cs` (line 36).
  - Full logs were kept in the scratchpad only, not committed.

## Tasks

| ID | Branch | Task | Risk | Hours | Status |
| --- | --- | --- | --- | --- | --- |
| T01 | phase-0-prep (manual) | Copy code from the old repo (without `.git`), check `.gitignore`, add the harness files | Low | 1 | Done |
| T00 | phase-0-prep | Baseline: build the current code, record existing errors/warnings under "Baseline" | Low | 1 | Done |
| T38 | phase-0-prep | Align MediatR to 12.2.0 and MassTransit(.RabbitMQ) to 8.3.0 in every project (repo already mixes 14.2.0 / 9.2.0) | Med | 2 | Done |
| T02 | phase-0-prep | Align 10.x packages to 8.0.x (`Caching.StackExchangeRedis`, `Logging.Abstractions`) | Low | 1 | Done |
| T03 | phase-0-prep | Remove OData, `Azure.Containers.Tools.Targets`, `Http.Features 5.0.17` (BannerAPI) | Low | 0.5 | Todo |
| T04 | phase-1-remove-translation | Delete the 6 translation files in ComicAPI | Low | 0.5 | Todo |
| T05 | phase-1-remove-translation | Update ComicAPI `Program.cs` and `appsettings.json` | Low | 0.5 | Todo |
| T06 | phase-1-remove-translation | Frontend: rewrite `useTranslatedTexts`, delete `translationService.js` | Low | 0.5 | Todo |
| T07 | phase-1-remove-translation | Clean up `backend/n8n/`, `docker-compose.yml`, `.env.example`, READMEs | Low | 0.5 | Todo |
| T08 | phase-2-host | Create `ComicMonolith.Api` (net8.0), add to `.sln`, `/health` endpoint | Med | 3 | Todo |
| T09 | phase-2-host | Turn the 8 APIs into `AddXxxModule` (one module per session: Banner, User, Wallet, then the rest) | Med | 8 | Todo |
| T10 | phase-2-host | Resolve name clashes (`Program` classes, AutoMapper profiles, DTOs, namespaces) | Med | 3 | Todo |
| T11 | phase-2-host | Register MediatR and AutoMapper for all modules in the host | Low | 2 | Todo |
| T12 | phase-2-host | Merge configuration: one `.env`, one `appsettings`, JWT configured once | Low | 2 | Todo |
| T13 | phase-2-host | Path-rewrite middleware replacing YARP, with unit tests | Med | 3 | Todo |
| T14 | phase-2-host | Move the gateway's public endpoint list into the host, with 401 tests | High | 2 | Todo |
| T37 | phase-3-tests | **Before T15:** characterization tests for deposit, chapter purchase, SePay webhook, saga | High | 6 | Todo |
| T15 | phase-3-grpc | List every gRPC call, fill in "Remaining gRPC calls" | Med | 2 | Todo |
| T16 | phase-3-grpc | Create an interface for each call direction (per module pair) | Med | 4 | Todo |
| T17 | phase-3-grpc | Implement the interfaces with direct calls (Payment has 6 handlers, must ask) | High | 10 | Todo |
| T18 | phase-3-grpc | Remove gRPC clients/servers, `.proto`, `Grpc.*`, `*_API_URL` variables | Low | 2 | Todo |
| T19 | phase-3-grpc | Replace Redis with `AddDistributedMemoryCache()` (2 ComicAPI queries) | Low | 1 | Todo |
| T20 | phase-4-masstransit | Merge the 6 `AddMassTransit` calls into one bus in the host | High | 4 | Todo |
| T21 | phase-4-masstransit | Switch transport to in-memory, remove `MassTransit.RabbitMQ`, `RABBITMQ_*` | Low | 1 | Todo |
| T22 | phase-4-masstransit | Proposal only: keep or drop EF Outbox (5 DbContexts). **User decides** | High | 3 | Todo |
| T23 | phase-4-masstransit | Keep the saga in-memory, run T37 tests, test a crash mid-purchase | High | 4 | Todo |
| T24 | phase-5-db | Confirm 8 DbContexts, 8 connection strings, migration order at startup | Low | 2 | Todo |
| T25 | phase-5-db | Move the SQL password to `.env`, remove the `changeme123` default from compose | Low | 0.5 | Todo |
| T26 | phase-6-verify | Clean `dotnet build` of the whole solution | Med | 2 | Todo |
| T27 | manual | Manual test of the main flows (list below) | High | 6 | Todo |
| T28 | phase-6-verify | Frontend: `VITE_API_URL`, `npm run build`, check every API path | Low | 1 | Todo |
| T29 | phase-7-deploy | Multi-stage Dockerfile for `ComicMonolith.Api` | Low | 1.5 | Todo |
| T30 | phase-7-deploy | `docker-compose.prod.yml`: backend, SQL Server, Nginx | Med | 2 | Todo |
| T31 | phase-7-deploy | Nginx serves the frontend and proxies the API on the same domain | Med | 2 | Todo |
| T32 | phase-7-deploy | `.env.production.example`, add `.env.production` to `.gitignore` | Med | 1 | Todo |
| T33 | phase-7-deploy | `docs/DEPLOY.md`: VPS, Docker, firewall 22/80/443, domain, HTTPS | Med | 3 | Todo |
| T34 | manual | Update Google OAuth, SePay/VietQR webhook, CORS for the real domain | High | 1.5 | Todo |
| T35 | phase-7-deploy | Daily SQL Server backup script, keep 7 copies, restore guide | Med | 2 | Todo |
| T36 | manual | Smoke test in production | Med | 2 | Todo |

Total estimate: about 92 hours. Work in the order of the rows above (T01 before T00, T37 before T15).

## Task notes

- **T04, the 6 files (ComicAPI.* projects):** `Controllers/TranslationsController.cs`, `Features/Translations/Commands/TranslateTextCommand.cs`, `DTOs/Translation/TranslateTextRequestDto.cs`, `Interfaces/ITranslationService.cs`, `Infrastructure/Services/N8nTranslationService.cs`, `Infrastructure/Settings/N8nTranslationSettings.cs`. No database impact.
- **T06:** the hook keeps returning `{ translations, translating, translationError }` so the ~20 pages using it need no changes.
- **T09:** BannerAPI is the simplest; do it first to tune the prompt.
- **T22:** not yet verified whether one bus can use the outbox with several DbContexts. Options: (a) drop the outbox, go in-memory, add a job that cleans up stuck sagas; (b) keep RabbitMQ; (c) merge the Payment/Wallet/Chapter databases so a purchase is one transaction.
- **T27, manual test flows:** register, login (password and Google), read a comic, comment, follow, favorite, missions, SePay deposit, chapter purchase, withdrawal, banners, image upload.
- **T37:** expected values come from running the current system (with RabbitMQ), never from the agent's guesses.

## Remaining gRPC calls (filled in by T09, T15)

| From module | To module | RPC method | Used by handler | Replaced? |
| --- | --- | --- | --- | --- |
| | | | | |

## Open decisions

- [ ] T22: keep or drop EF Outbox.
- [ ] Merge the 8 databases into one or not.
- [ ] What replaces MassTransit once v8 security support ends (official date still to be confirmed).
- [ ] Before deploy: review the NU1903 vulnerable-package warnings and bump patch versions within the locked majors.

## Log

| Date | Task | Error hit / lesson learned |
| --- | --- | --- |
| 2026-11-16 | T00 | Baseline builds are clean (0 errors). Warnings counted by `dotnet build` appear twice in the raw log (project + solution line), so grep counts must be halved. Build used SDK 10, not 8: re-check when an SDK 8 is available. |
| 2026-11-16 | T02 | Found locked majors already violated: MediatR 14.2.0 in 6 projects (Banner API+Application, Comic API+Application, User API+Application), MassTransit and MassTransit.RabbitMQ 9.2.0 in ComicAPI.API. MediatR 14 needs Logging.Abstractions >= 10, so T02 (8.0.3) fails with NU1605. T02 edits discarded; new task T38 runs first. |
| 2026-11-16 | T38 | MediatR 14.2.0 -> 12.2.0 (6 projects), MassTransit and MassTransit.RabbitMQ 9.2.0 -> 8.3.0 (ComicAPI.API). Build: 0 errors, 93 warnings (same as baseline). No LicenseKey or 13+/9-only API used. ComicViewed consumer not run against RabbitMQ. |
| 2026-11-17 | T02 | Logging.Abstractions 10.0.11 -> 8.0.3 (12 projects), Caching.StackExchangeRedis 10.0.11 -> 8.0.34 (ComicAPI.API). Only worked after T38 (MediatR 14 required Logging.Abstractions >= 10). Build: 0 errors, 93 warnings (baseline). |
