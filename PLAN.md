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
| T03 | phase-0-prep | Remove OData, `Azure.Containers.Tools.Targets`, `Http.Features 5.0.17` (BannerAPI) | Low | 0.5 | Done |
| T04 | phase-1-remove-translation | Delete the 6 translation files in ComicAPI | Low | 0.5 | Done |
| T05 | phase-1-remove-translation | Update ComicAPI `Program.cs` and `appsettings.json` | Low | 0.5 | Done |
| T06 | phase-1-remove-translation | Frontend: rewrite `useTranslatedTexts`, delete `translationService.js` | Low | 0.5 | Done |
| T07 | phase-1-remove-translation | Clean up `backend/n8n/`, `docker-compose.yml`, `.env.example`, READMEs | Low | 0.5 | Done |
| T08 | phase-2-host | Create `ComicMonolith.Api` (net8.0), add to `.sln`, `/health` endpoint | Med | 3 | Done |
| T09 | phase-2-host | Turn the 8 APIs into `AddXxxModule` (one module per session: Banner, User, Wallet, then the rest) | Med | 8 | In progress (Banner, User, Wallet done; Comic, Chapter, Social, Mission, Payment todo) |
| T10 | phase-2-host | Resolve name clashes (`Program` classes, AutoMapper profiles, DTOs, namespaces) | Med | 3 | Done (analysis only, no code change; re-check when all modules are in the host) |
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
- **T10, name-clash table (found by reading `backend`, 2026-11-17):**

  | Item | Where | Verdict | Action |
  | --- | --- | --- | --- |
  | `CloudinaryService`, `ICloudinaryService` | Banner, Chapter, Comic, Mission (each in its own namespace) | No clash: different namespaces, so different DI keys | None |
  | `CloudinarySettings` | Chapter, Comic, Mission (own namespaces; all bind the `Cloudinary` config section) | No clash | None (config merge is T12) |
  | `ComicValidator`, `IComicValidator` | Chapter, Social (own namespaces) | No clash | None |
  | `MissionProgressNotifier`, `IMissionProgressNotifier` | Chapter, Social (own namespaces) | No clash | None |
  | `UserPurchase` | Chapter, Payment entities (own namespaces) | No clash | None |
  | `Program` classes | 8 APIs, top-level statements (internal, global namespace) | No clash: internal to each assembly | None |
  | Controller routes | 16 controllers | No duplicate route | None |
  | AutoMapper profiles | Chapter 1, Comic 2, no duplicate maps | No clash. `ChapterAPI/Program.cs:41` scans `AppDomain.CurrentDomain.GetAssemblies()`, which would register every module's profiles | T11 (use explicit assemblies) |
  | gRPC generated types | `ChapterAPI.Protos` in 4 assemblies, `MissionAPI.Protos` in 3, `WalletAPI.Protos` in 3, `SocialAPI.Protos` in 2; `chapter.proto`/`comic.proto` reuse the package and namespace with different contents | CS0433 only if one project references two of those assemblies and uses the type; the host does not use them today | Deferred to T17/T18 (protos are deleted) |
  | `Class1` placeholders | 6 files (Chapter and Comic Application/Domain/Infrastructure), global namespace | Unused | None, not touched |

  Not verifiable yet: Wallet, Comic, Chapter, Social, Mission and Payment are not loaded in the host (T09), so a combined build and run is still pending. Class names were found with a regex over `*.cs` (Migrations excluded), so nested or generic type names could be missed.
- **T22:** not yet verified whether one bus can use the outbox with several DbContexts. Options: (a) drop the outbox, go in-memory, add a job that cleans up stuck sagas; (b) keep RabbitMQ; (c) merge the Payment/Wallet/Chapter databases so a purchase is one transaction.
- **T27, manual test flows:** register, login (password and Google), read a comic, comment, follow, favorite, missions, SePay deposit, chapter purchase, withdrawal, banners, image upload.
- **T37:** expected values come from running the current system (with RabbitMQ), never from the agent's guesses.

## Remaining gRPC calls (filled in by T09, T15)

| From module | To module | RPC method | Used by handler | Replaced? |
| --- | --- | --- | --- | --- |
| ComicAPI | UserAPI | `UserService.GetUsersByIds` | `ComicHelper.EnrichComicsWithAuthorNamesAsync`, used by GetComics, GetHotComics, GetLastCompletedComics, GetOutstandingComicsList/Paged, GetMyComics, GetPurchasedComics, AddOutstandingComic; plus `GetComicBySlugQuery` (direct call) | No |

| PaymentAPI | WalletAPI | `WalletService.AddCoin` | `CreateDepositCommand`, `ProcessSePayWebhookCommand`, `PurchaseChapterCommand`, `GetBalanceQuery`, `GetTransactionsQuery`, `GetTransactionByIdQuery` (6 handlers; all inject the client, all 6 call `AddCoinAsync`) | No |

`WalletService.DebitCoin` is exposed by `WalletGrpcService` but no caller was found (grep over `backend`, found by T09-Wallet; debits go through the `DebitWalletCommand` MassTransit message). MissionAPI has a copy of `wallet.proto` but nothing uses it. The host does not map `WalletGrpcService`. Sensitive: replacing these calls needs the T37 tests first (T17).

`UserService` also exposes `GetUserById` and `ValidateToken` (UserAPI `UserGrpcService`); no other module calls them (grep over `backend`, found by T09-User). The host does not map `UserGrpcService` yet.

## Open decisions

- [ ] T22: keep or drop EF Outbox.
- [ ] Merge the 8 databases into one or not.
- [ ] What replaces MassTransit once v8 security support ends (official date still to be confirmed).
- [ ] Before deploy: review the NU1903 vulnerable-package warnings and bump patch versions within the locked majors.
- [ ] `frontend/scripts/dev.mjs` starts UserAPI from a path that does not exist (`backend/UserAPI/UserAPI.csproj`; the real one is `backend/UserAPI/UserAPI.API/UserAPI.API.csproj`). Checked: the same is true for the 7 other APIs (Banner, Comic, Chapter, Mission, Payment, Wallet, Social); only `ApiGateway/ApiGateway.csproj` is correct, so `npm run dev` cannot start the backend. Not fixed yet; the script is obsolete once the monolith host exists (T08).
- [ ] Leftover `/translations` references not removed by T07: `backend/ApiGateway/appsettings.json` (`translations-route`), `backend/ApiGateway/Program.cs:99` (public rule for `POST /translations`), `frontend/vite.config.js` proxy entry. Planned for T13/T14 (gateway replacement) and the frontend config.

## Log

| Date | Task | Error hit / lesson learned |
| --- | --- | --- |
| 2026-11-16 | T00 | Baseline builds are clean (0 errors). Warnings counted by `dotnet build` appear twice in the raw log (project + solution line), so grep counts must be halved. Build used SDK 10, not 8: re-check when an SDK 8 is available. |
| 2026-11-16 | T02 | Found locked majors already violated: MediatR 14.2.0 in 6 projects (Banner API+Application, Comic API+Application, User API+Application), MassTransit and MassTransit.RabbitMQ 9.2.0 in ComicAPI.API. MediatR 14 needs Logging.Abstractions >= 10, so T02 (8.0.3) fails with NU1605. T02 edits discarded; new task T38 runs first. |
| 2026-11-16 | T38 | MediatR 14.2.0 -> 12.2.0 (6 projects), MassTransit and MassTransit.RabbitMQ 9.2.0 -> 8.3.0 (ComicAPI.API). Build: 0 errors, 93 warnings (same as baseline). No LicenseKey or 13+/9-only API used. ComicViewed consumer not run against RabbitMQ. |
| 2026-11-17 | T02 | Logging.Abstractions 10.0.11 -> 8.0.3 (12 projects), Caching.StackExchangeRedis 10.0.11 -> 8.0.34 (ComicAPI.API). Only worked after T38 (MediatR 14 required Logging.Abstractions >= 10). Build: 0 errors, 93 warnings (baseline). |
| 2026-11-17 | T03 | Removed 16 PackageReferences: OData 8.2.5 (8 API projects), Containers.Tools.Targets 1.22.1 (7), Http.Features 5.0.17 (BannerAPI.Application). No code used OData (grep: csproj hits only). Build: 0 errors, 93 warnings (baseline). |
| 2026-11-17 | T04 | Deleted the 6 ComicAPI translation files (empty `Translations/` and `Translation/` folders vanish with them). Build now fails with 4x CS0234 in `ComicAPI.API/Program.cs` lines 59-61 (N8nTranslationSettings, N8nTranslationService, ITranslationService), expected until T05. Left for T05/T07: `Program.cs`, `appsettings.json` line 31, `backend/.env.example` lines 60-62. Frontend build passes. |
| 2026-11-17 | T05 | Removed the N8nTranslationSettings `Configure` and the `AddHttpClient<ITranslationService, ...>` from ComicAPI `Program.cs`, and the `N8nTranslation` section from `appsettings.json`. The 4x CS0234 from T04 are gone: build 0 errors, 93 warnings (baseline); frontend build passes; grep for ITranslationService/N8nTranslation/TranslateText in `*.cs`/`*.json` is empty. Still left for T07: `backend/.env.example` lines 59-62, compose, `backend/n8n/`, READMEs. |
| 2026-11-17 | T06 | `useTranslatedTexts` no longer calls the API: it returns `{ translations: {key: original}, translating: false, translationError: '' }` (same shape as the old `vi` branch, so the 19 pages need no change). Deleted `translationService.js`. Frontend build passes (95 modules), backend build 0 errors / 93 warnings (baseline), grep for `translationService` and `/translations` in `frontend/src` is empty. Left as dead code: the `translationError`/`translating` banners in the pages (never render now). Not tested in a browser. |
| 2026-11-17 | T07 | Deleted `backend/n8n/` (2 files), the `libretranslate` and `n8n` services and their 2 volumes from `backend/docker-compose.yml`, the `N8nTranslation__*` lines from `backend/.env.example`, and n8n/LibreTranslate/translation content from the 3 READMEs (backend section 7 removed entirely). `docker compose config` OK (services: sqlserver, rabbitmq, redis); grep n8n/libretranslate outside AGENTS/PLAN/PROMPTS is empty; backend build 0 errors / 93 warnings (baseline); frontend build passes. Left on purpose: `/translations` in ApiGateway and `vite.config.js` (see Open decisions). Compose `version` key is obsolete (warning only, not touched). |
| 2026-11-17 | T08 | Created `backend/ComicMonolith.Api` (net8.0, Web SDK; DotNetEnv 3.1.1, JwtBearer 8.0.12, Swashbuckle 6.6.2, ref SharedKernel only) and added it to the `.sln`. `Program.cs` skeleton: DotNetEnv once, `AddCustomJwtAuthentication`, CORS "Frontend" (from `FRONTEND_URL`), Swagger (Development), controllers (none loaded), anonymous `GET /health`. Dev port 5100 (gateway uses 5028). Build: 0 errors, 93 warnings (baseline, none from the new project). `dotnet run` -> `/health` 200 `{"status":"ok"}`, swagger.json 200; frontend build passes. Not done on purpose: gateway's 401/404 JSON wrapper and public-endpoint list (T13/T14). The host has no `[Authorize]` fallback yet, so no endpoint is protected until modules are added. |
| 2026-11-17 | T09 (Banner) | Added `BannerAPI.API/BannerModule.cs` (`AddBannerModule`: controllers via `AddApplicationPart`, repository, Cloudinary service, MediatR, `BannerDbContext`; `UseBannerModule`: `Database.Migrate()`). `BannerAPI.API/Program.cs` now just calls them, so the old standalone API still works. Host references `BannerAPI.API` and calls both. Build: 0 errors, 93 warnings (baseline, no CS0436); frontend build passes. Ran the host from outside the repo against a throwaway LocalDB database (dropped afterwards): `/health` 200, Swagger lists `/banners`, `/banners/admin`, `/banners/upload`, `/banners/{id}`, `GET /banners` 200 (empty list), `GET /banners/admin` without token 401, Migrate applied. Without any `BANNER_DB_CONNECTION`/connection string the host fails at startup (DbContext not registered, same rule as the old API) until T12 merges configuration. BannerAPI has no gRPC calls (unused `Grpc.AspNetCore` package and `GrpcEndpoints` config left for T18), so "Remaining gRPC calls" is unchanged. Not tested: image upload/Cloudinary, authenticated admin calls, `/banners` path through the old gateway. Each module also calls `AddControllers()`/`AddMediatR` itself for now; T11 consolidates. |
| 2026-11-17 | T09 (User) | Added `UserAPI.API/UserModule.cs` (`AddUserModule`: controllers via `AddApplicationPart`, `UserDbContext` from `AUTH_DB_CONNECTION` / `AuthConnection`, memory cache, 5 scoped services, MediatR; `UseUserModule`: `Database.Migrate()`). `UserAPI.API/Program.cs` calls them and keeps its own JWT, Swagger and gRPC, so the standalone API still works. Host references `UserAPI.API` and calls both. First build failed with NU1605 (host DotNetEnv 3.1.1 < UserAPI 3.2.0); fixed by raising the host's DotNetEnv to 3.2.0 (existing version in repo). Build: 0 errors, 93 warnings (baseline); frontend build passes. Ran the host from outside the repo against throwaway LocalDB databases (dropped afterwards): `/health` 200, Swagger lists all `/auth/*` and `/auth/admin/users*`, register 200, login 403 until email verified (expected), after setting `IsEmailVerified` in the throwaway DB login 200 with JWT, `GET /auth/me` 200 with token and 401 without, `/auth/admin/users` 403 for a non-admin and 401 without token. Lesson: `register`/`login` return 500 unless `Cloudinary:*` is configured, because `AvatarStorageService` builds the Cloudinary client in its constructor (old behavior, not changed); also `pkill` does not stop Windows processes, so a first host run kept serving on the port until killed by PID. Host does not map `UserGrpcService`; ComicAPI's call to `GetUsersByIds` recorded in "Remaining gRPC calls". Not tested: Google login, real SMTP email, avatar upload, refresh/logout/reset-password flows, admin role calls, `/auth` through the old gateway. |
| 2026-11-17 | T10 | Analysis only, no code changed (see "T10, name-clash table" in Task notes). Same-named types (Cloudinary*, ComicValidator, MissionProgressNotifier, UserPurchase) live in distinct namespaces, `Program` classes are internal, no duplicate routes or AutoMapper maps. Real risks: gRPC generated types duplicated across assemblies (deferred to T18) and `AddAutoMapper(AppDomain.CurrentDomain.GetAssemblies())` in ChapterAPI (T11). Build 0 errors / 93 warnings (baseline); frontend build passes. Only Banner and User are in the host, so the check must be repeated after T09 finishes. Lesson: T10 sits after T09 in the table but needs all modules loaded to be fully verified. |
| 2026-11-17 | T09 (Wallet) | Added `WalletAPI.API/WalletModule.cs` (`AddWalletModule`: controllers `/currency` and `/withdraws` via `AddApplicationPart`, 3 repositories, MediatR, `WalletDbContext` from `WALLET_DB_CONNECTION` / `WalletConnection` / `DefaultConnection`; `UseWalletModule`: `Database.Migrate()`). No business logic changed. `WalletAPI.API/Program.cs` calls them and keeps gRPC, MassTransit (consumers, EF Outbox, RabbitMQ), JWT and Swagger, so the standalone API still works; it also keeps the old hardcoded localhost connection fallback, which the module does not have. Host references `WalletAPI.API` and calls both. Build: 0 errors, 93 warnings (baseline); frontend build passes. Ran the host from outside the repo against throwaway LocalDB databases (dropped afterwards): Swagger lists `/currency`, `/currency/history`, `/withdraws`, `/withdraws/me|withdrawable|admin|{id}`; Migrate applied 10 migrations and created Wallets, CurrencyEntries, Withdraws plus the MassTransit Inbox/Outbox tables; without a token all 6 probed endpoints return 401; with a hand-made HS256 JWT (default dev secret) Reader gets 200 on history/me/withdrawable, 403 on `/withdraws/admin` and `POST /currency`, Admin gets 200 on `/withdraws/admin`. **Not in the host on purpose:** the 3 consumers (`MissionRewardGranted`, `DebitWallet`, `RefundWallet`) and the EF Outbox, because the host has no bus until T20; so in the host nothing can debit or refund a wallet through the saga yet (Payment and Mission are not loaded either). **Heads-up for T11/T12:** `JsonStringEnumConverter` (only Wallet had it) is now registered through the module's `AddJsonOptions`, which is global in the host, so enums of every module serialise as strings; check Comic/Chapter/Mission/Payment responses when they are added. Payment to Wallet `AddCoin` calls recorded in "Remaining gRPC calls". Not tested: deposit/withdraw writes (`POST /withdraws`, `POST /currency` as Admin), the standalone WalletAPI run (needs RabbitMQ), real JWTs from UserAPI. |
