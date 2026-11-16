# PROMPTS.md

Prompts for assigning work to the agent. One task per new session. Replace `<...>` with real values.
The agent creates branches, commits, pushes and merges itself (see "Git workflow" in AGENTS.md).

## 0. Universal prompt (use this for every task)

```
Read AGENTS.md, PLAN.md and PROMPTS.md. Pick the first task in the PLAN.md table whose status is "Todo",
in table order. If it is marked "manual", do not do it: tell me what I need to do by hand and stop.
Tell me which task you picked. Prepare the branch as described in "Git workflow" in AGENTS.md.
Find the task's prompt in section B of PROMPTS.md (or use template A2 if none), read the relevant files
and propose a plan in at most 10 lines. Stop and wait for my "ok".
After my ok: do the task, run the verification commands, self-review the diff (A4), commit and push
following the Git workflow, update PLAN.md in the same commit, and report using the format at the end of AGENTS.md.
If this was the last task of its phase, open and merge the PR following the Git workflow
(ask me first if the phase has a High-risk task or touches a sensitive area).
If the build fails 3 times on the same error, stop and ask me.
```

To run a specific task instead, replace the first two sentences with: `Today's task is <ID>.`

## A. General

### A1. Start a session

```
Read AGENTS.md and PLAN.md. Today's task is <ID>.
Read the relevant files, then propose a plan in at most 10 lines: files to change, approach, verification commands.
Stop and wait for me to reply "ok". Do not change anything yet.
```

### A2. Generic task template (when there is no specific prompt below)

```
Task <ID>: <name>. Scope: only change <files/folders>. Do not touch <off-limits>.
Reference pattern in the repo: <file that does something similar, if any>.
Done when: <verification command and expected result>.
When done: run the verification commands in AGENTS.md, commit and push following "Git workflow" in AGENTS.md,
update PLAN.md (status + one Log line) in the same commit, and report using the format at the end of AGENTS.md.
```

### A3. Build failure

```
The build failed. Exact output:
<paste the full error>
Fix only this error. State the cause and the smallest fix, then apply it. Do not change anything outside task <ID>.
If this is the 3rd failed attempt at the same error, stop and propose 2 options instead.
```

### A4. Self-review before commit

```
Review the git diff for task <ID>. List (do not fix yet): files outside the scope, behavior changes versus the old code,
exposed secrets, warnings or tests disabled/edited to pass, new packages.
```

### A5. Payment/Wallet review (run in a separate session, not the one that wrote the code)

```
You are a reviewer and must not write code. Read the git diff for <ID> in PaymentAPI/WalletAPI/the saga.
Look for: wallet debited twice, money taken but chapter not unlocked, saga stuck when a step fails,
SePay webhook processed twice or without checking signature/amount, authenticated endpoints made public.
For each issue give file, line and a reproduction scenario. State clearly what you could not verify.
```

### A6b. Merge a phase manually (if the agent did not)

```
All tasks of phase <branch> are Done. Verify the build, then open a PR and merge it following
"Git workflow" in AGENTS.md. Show me the PR link before merging if the phase has a High-risk task.
```

### A6. End of session

```
Update PLAN.md: status of <ID>, the "Remaining gRPC calls" table if it changed, one Log line. Change nothing else.
```

## B. Per task

### T01 (manual)

Done by hand. See the guide's "Preparation" section.

### T00

```
Read AGENTS.md and PLAN.md. Today's task is T00.
Run the verification commands from AGENTS.md on the current code. Do not change any code.
Fill in the "Baseline" section of PLAN.md: build results, pre-existing errors and warnings (shortened if very long).
Set T01 and T00 to "Done". Commit and push following "Git workflow" in AGENTS.md (branch phase-0-prep),
e.g. "chore(plan): T00 record baseline".
```

### Hook test (run once after T00)

```
This is a hook test, not a real task. Try to add one comment line to any file in UserAPI's Migrations folder.
Report what happened. If the edit succeeded, revert it with git checkout.
```

### T02

```
Task T02. List every PackageReference with a 10.x version in backend and propose the matching 8.0.x version.
Wait for my ok, then change them. Do not touch MassTransit, MediatR or AutoMapper. Done when dotnet build passes.
```

### T03

```
Task T03. Grep to confirm no code uses OData (EnableQuery, AddOData, ODataController).
Then remove these PackageReferences: Microsoft.AspNetCore.OData (8 projects),
Microsoft.VisualStudio.Azure.Containers.Tools.Targets, Microsoft.AspNetCore.Http.Features (BannerAPI).
Done when dotnet build passes.
```

### T04

```
Task T04. In the backend/ComicAPI.* projects delete these 6 files: Controllers/TranslationsController.cs,
Features/Translations/Commands/TranslateTextCommand.cs, DTOs/Translation/TranslateTextRequestDto.cs,
Interfaces/ITranslationService.cs, Infrastructure/Services/N8nTranslationService.cs,
Infrastructure/Settings/N8nTranslationSettings.cs.
Grep for ITranslationService, N8nTranslation, TranslateText and list remaining references. Do not edit Program.cs yet.
The build may fail in Program.cs; report it clearly.
```

### T05

```
Task T05. In ComicAPI.API/Program.cs remove the N8nTranslationSettings registration and the AddHttpClient for ITranslationService.
In appsettings.json remove the N8nTranslation section.
Done when dotnet build passes and grep "N8nTranslation" in backend returns nothing.
```

### T06

```
Task T06. Rewrite frontend/src/hooks/useTranslatedTexts.js so it always returns the original text and never calls the API,
keeping the return shape { translations, translating: false, translationError: '' }.
Delete frontend/src/services/translationService.js. Do not edit the pages that use the hook.
Done when npm run build passes and nothing imports translationService.
```

### T07

```
Task T07. Delete backend/n8n/. In backend/docker-compose.yml remove the n8n and libretranslate services,
the n8n_data and libretranslate_data volumes, and any depends_on pointing to them.
In backend/.env.example remove the N8nTranslation__* lines. In the READMEs remove content about n8n,
LibreTranslate and content translation.
Done when `docker compose -f backend/docker-compose.yml config` succeeds and grep -ri "n8n\|libretranslate"
finds nothing outside AGENTS.md, PLAN.md and PROMPTS.md.
```

### T08

```
Task T08. Create backend/ComicMonolith.Api (net8.0, web) and add it to prn232_comic_api.sln.
Program.cs is a skeleton only: DotNetEnv loaded once, Swagger, CORS (origin from FRONTEND_URL),
JWT via SharedKernel's AddCustomJwtAuthentication, GET /health. Do not load any module yet.
Do not modify the old projects. Done when the build passes and dotnet run returns 200 on /health.
```

### T09 (one module per session: Banner → User → Wallet → Comic → Chapter → Social → Mission → Payment)

```
Task T09 for <BannerAPI>. Read <BannerAPI.API>/Program.cs.
Create an extension Add<Banner>Module(IServiceCollection, IConfiguration) containing the DbContext, repositories and services,
and Use<Banner>Module(WebApplication) that calls Database.Migrate() for the module's DbContext.
The host calls both and loads the controllers with AddApplicationPart. Do not repeat JWT, CORS or Swagger inside the module.
gRPC calls to other modules: leave them as they are and record them in "Remaining gRPC calls" in PLAN.md.
If they block the build, stop and ask. Done when the build passes, the host runs and Swagger shows the module's endpoints.
```

For PaymentAPI and WalletAPI add: "This is a sensitive area: propose a plan, wait for ok, do not change business logic."

### T10

```
Task T10. Find name clashes between modules (Program classes, AutoMapper Profiles, DTOs, namespaces, constants,
.proto files generating the same namespace). List them in a table and propose renames. Wait for ok before changing.
```

### T11

```
Task T11. In the host, register MediatR with RegisterServicesFromAssemblies and AddAutoMapper with the assemblies of all 8 modules.
Remove duplicate registrations inside modules. Done when the build passes and one representative endpoint per module works.
```

### T12

```
Task T12. Merge configuration: DotNetEnv loaded only in the host, JWT configured once; gather the 8 connection strings,
Cloudinary, SMTP, Google Client ID and SePay into one appsettings.json and one backend/.env.example (fake values).
Never read or edit the real .env. Done when the host starts with complete configuration.
```

### T13

```
Task T13. Read ApiGateway/appsettings.json (ReverseProxy) and ApiGateway/Program.cs.
Write a middleware in the host that rewrites /comics, /categories, /chapters, /comments, /favorites, /follows,
/reading-history, /payments, /uploads, /missions, /notifications to /api/..., and keeps /auth, /wallets, /currency,
/withdraws, /banners. Check every gateway route and report any that differs from this list. Do not add /translations.
Create a test project (xUnit) with unit tests for the rewrite, including query strings and sub-paths.
Done when dotnet test and the build pass.
```

### T14 (sensitive)

```
Task T14. Read ApiGateway/Program.cs and build a table of every public endpoint with its method condition
(e.g. GET /comics except /comics/me and /comics/purchased; POST /payments/sepay-webhook). Do not change anything; wait for me to approve the table.
After approval: move the rules into the host. Write tests: every public endpoint does not return 401,
representative protected endpoints return 401 without a token. Done when dotnet test passes.
```

### T37 (before T15)

```
Task T37. Do not change any behavior. Write characterization tests capturing the CURRENT behavior of: successful deposit,
SePay webhook (bad signature, amount mismatch, duplicate call), successful chapter purchase, insufficient balance,
buying an already owned chapter, and the PurchaseStateMachine state sequence.
Use a suitable test database. Expected values must come from running the current code, not from guessing.
If a scenario needs an external service that is not available, say so and ask. Done when all tests pass on the current code.
```

### T15

```
Task T15. Read only. From the 16 .proto files and the gRPC clients/servers, fill in "Remaining gRPC calls" in PLAN.md:
calling module, called module, RPC method, handler using it. Mark circular pairs. Do not change code.
```

### T16

```
Task T16 for the pair <Mission → Social>. Create an interface in SharedKernel (or a separate Contracts project if
SharedKernel would create a circular reference), with names and types taken from the matching RPC method.
Do not replace the calls yet. Done when the build passes.
```

### T17

```
Task T17 for the pair <Mission → Social>. Implement the interface by calling the other module's service directly,
and replace the gRPC call in the handlers. Keep behavior identical, including error handling and not-found results.
For pairs involving Payment or Wallet: propose a plan, wait for ok, and run the T37 tests after changing.
Done when build and tests pass; tell me how to test it manually. Update "Remaining gRPC calls".
```

### T18

```
Task T18. Once every row in "Remaining gRPC calls" is replaced: delete gRPC clients/servers, the 16 .proto files,
Protobuf items in .csproj, the Grpc.* and Google.Protobuf packages, and the *_API_URL and GrpcEndpoints settings
in configuration and .env.example. Done when build and tests pass and grep -ri "grpc" in backend finds no code.
```

### T19

```
Task T19. In ComicAPI replace AddStackExchangeRedisCache with AddDistributedMemoryCache, keeping the IDistributedCache code.
Remove the Microsoft.Extensions.Caching.StackExchangeRedis package, REDIS_CONNECTION, and the redis service in compose.
Done when the build passes.
```

### T20 (High risk)

```
Task T20. List every AddMassTransit, consumer, saga and ReceiveEndpoint across the 6 modules.
Propose how to merge them into one bus in the host (still RabbitMQ at this step). Wait for ok, then change.
Run the T37 tests afterwards.
```

### T21

```
Task T21. Switch the transport to UsingInMemory; remove MassTransit.RabbitMQ, RABBITMQ_* and the rabbitmq service in compose.
Done when the build passes, the T37 tests pass, and a representative event (ComicViewedIntegrationEvent) is consumed.
```

### T22

```
Task T22. Read and propose only. Find every AddEntityFrameworkOutbox and UseBusOutbox.
For the MassTransit version in the .csproj files, determine whether one bus can use the outbox with several DbContexts,
based only on official docs or the library's source code (cite the source). Write the options under "Open decisions". Do not change code.
```

### T23

```
Task T23. Apply the T22 option I chose: <option>. Keep PurchaseStateMachine on the in-memory bus.
Run the T37 tests. Propose how to test a crash mid-purchase and how to detect and clean up stuck sagas;
wait for ok before writing the cleanup job.
```

### T24, T25

```
Task T24. Confirm the 8 DbContexts and 8 connection strings are correct after the merge and each module migrates at startup.
List the startup order and the SQL permissions required. Do not change migrations.
```

```
Task T25. Remove the changeme123 default password from docker-compose.yml and require SQL_SA_PASSWORD.
Update .env.example with a fake value. Update the README to match.
```

### T26, T28

```
Task T26. Build the whole solution. For each warning that is new compared with "Baseline" in PLAN.md:
state the cause and propose the smallest fix.
```

```
Task T28. Set VITE_API_URL in frontend/.env.example and run npm run build. Grep every API path in frontend/src/services
and compare it with the host's routes after the middleware. List any path that does not match.
```

### T29 to T32

```
Task T29. Write a multi-stage backend/Dockerfile for ComicMonolith.Api (sdk:8.0 to build, aspnet:8.0 to run),
non-root user, port via ASPNETCORE_URLS. Never copy .env into the image. Add a .dockerignore.
```

```
Tasks T30 and T31. Write docker-compose.prod.yml: backend, sqlserver (data volume, do not publish 1433), nginx (80, 443).
Nginx serves the built frontend and proxies the API routes to the backend on the same domain. Read variables from .env.production.
Done when `docker compose -f docker-compose.prod.yml config` succeeds.
```

```
Task T32. Write .env.production.example (fake values): JWT, SQL, Cloudinary, SMTP, Google Client ID, SePay, FRONTEND_URL.
Add .env.production to .gitignore.
```

### T33, T35

```
Task T33. Write docs/DEPLOY.md: VPS preparation (Docker, firewall only 22/80/443, domain, HTTPS with Let's Encrypt),
running compose, updating to a new version, what to change in Google OAuth, SePay/VietQR and CORS, and a smoke-test checklist.
Documentation only.
```

```
Task T35. Write a daily SQL Server backup script (8 databases), keep 7 copies, no password inside the script,
plus instructions to copy backups off the VPS and to test a restore into a temporary database.
```
