# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Public repo: no PII, no secrets

This repository is **public**. Never commit:

- Address, name, meter ID (metering point ID), CPR/CVR, or any other personal data. This includes test fixtures, sample API responses, screenshots, docs and commit messages. Use obviously fake values (e.g. meter ID `571313100000000000`-style placeholders, "Test Street 1").
- Secrets of any kind: the Eloverblik refresh token, API keys, real connection strings. Use `dotnet user-secrets` locally and environment variables / a secret manager when deployed.
- Real consumption data exported from the user's account. Fixtures must be synthetic.

The only credentials in the repo are for the throwaway local Postgres in `docker-compose.yml` and CI, which are not secrets. Before every commit, check the diff for PII and secrets.

## Project

ElectricityDash is a personal hobby project (single user, one meter, Denmark). Electricity is expensive and its price varies by hour, so the service:

- Records daily consumption and stores it together with the spot price for the consumption period.
- Knows the additions on top of spot price (grid tariffs, system tariff, electricity tax, supplier margin, VAT) and how they vary over time.
- Calculates the monthly bill and a forecast for the current month.
- Shows a dashboard of consumption and whether we consume in the cheapest hours, with a score. Views: day, week, month.

### Domain decisions

- **Supplier:** Aura (spot price plus margin). **Grid company:** Dinel (tariffs from Eloverblik `getcharges`).
- **Billing resolution: hourly.** Aura's own pages say flex products follow the Nordic spot price "time for time", with no mention of 15-minute settlement. The market moved to 15-minute prices on 2025-10-01, but billing here is hourly. Store and bill per hour. If the spot source is 15-minute, average the four quarters into the hour. **Still to confirm against an actual Aura invoice.**
- **Subscription fee: NOT YET MODELLED.** The user will add it later. Until then the monthly bill and forecast exclude it. Make this visible in the UI/API output, never silently present a total as complete.
- **Score:** share of kWh consumed in off-peak hours, meaning the cheapest hours of the day. Shown for day, week and month. **Off-peak = the 6 cheapest hours of each 24-hour day.** "Day" is the Danish calendar day (`Europe/Copenhagen`), so on DST-change days it has 23 or 25 hours, and the rule is still the 6 cheapest hours of that day. Ties are broken by earlier hour. Score = kWh used in those hours / total kWh used.
- **One meter.** No multi-tenant or multi-meter design needed, but do not hardcode the meter ID. It comes from configuration or from Eloverblik.
- **Auth:** none for now, local use only. Add auth before any public deployment (see Open decisions).

## Architecture

- **Backend + frontend:** one C# / .NET 11 project, `src/ElectricityDash.Web`. Frontend is **Blazor** (Web App, interactive server). The dashboard is mainly graphs and status.
- **Database:** PostgreSQL 17, run in Docker (`docker-compose.yml`).
- **Data access:** **Dapper** over Npgsql (`NpgsqlDataSource` from DI). Raw SQL, no ORM.
- **Schema management:** DbUp. Embedded SQL scripts in `src/ElectricityDash.Web/Database/Scripts/` (`NNNN_description.sql`), applied at startup by `DatabaseMigrator`. Scripts are append-only. Never edit an applied script, add a new one.
- **Ingestion:** a **scheduled job hitting an endpoint** (e.g. Cloud Scheduler calling the app). Each source is an idempotent backfill (upsert by hour), safe to call repeatedly with overlapping ranges. Eloverblik data arrives a day or more late.
  - **Spot prices: done.** `POST /api/ingest/spot-prices?from=YYYY-MM-DD&to=YYYY-MM-DD` (Danish dates, inclusive; defaults to today and tomorrow, max 366 days). Code in `SpotPrices/`: typed `EnergyDataServiceClient` (resilience handler for retry/timeout), `HourlyAggregation` (15-minute to hourly average), `SpotPriceRepository` (Dapper upsert), `SpotPriceIngestor`. Upstream failures return 502 and store nothing. Verified against the live API. Tomorrow's prices are published around 13:00 CET.
  - **Only `DayAheadPrices` (from 2025-10-01) is supported.** Earlier history needs the hourly `Elspotprices` dataset, not built yet. Needed before backfilling consumption older than that.
  - **Consumption (Eloverblik): not built yet.**
  - The ingest endpoint has no authentication yet. Fine for local use, must be fixed before exposing it.
- **Deployment:** undecided, likely a price-aware serverless cloud (e.g. Cloud Run). Keep the app stateless, containerizable, configured by environment variables, fast to start. No in-process background schedulers.
- **Local dev:** dependencies run in Docker, the app runs on the host (`dotnet run` / IDE) so debugging is easy. The app is not containerized for local use.

### Layout

```
src/ElectricityDash.Web/        Blazor app, minimal API endpoints, Dapper data access, DbUp scripts
tests/ElectricityDash.Tests/    xunit; API-level integration tests + unit tests
docker-compose.yml              local dependencies (Postgres)
.github/workflows/ci.yml        build + test on push to main and on PRs
```

### External data sources

| Data | Source | Notes |
|---|---|---|
| Consumption | Eloverblik Customer API | `https://api.eloverblik.dk/customerapi/api` |
| Spot price | Energi Data Service (`api.energidataservice.dk`) | Eloverblik has no spot prices |
| Tariffs / fees | Eloverblik `getcharges` | Verify against real data before trusting. Tax and supplier margin come from config |

**Eloverblik (verified from the swagger spec, https://api.eloverblik.dk/customerapi/swagger/v1/swagger.json):**
- Auth: `GET /token` with the long-lived *refresh token* as Bearer returns a short-lived *data access token* (about 24h). Use that for the other calls. A non-refresh token gives 403.
- Time series: `POST /meterdata/gettimeseries/{dateFrom}/{dateTo}/{aggregation}`. Aggregation is one of `Actual`, `Quarter`, `Hour`, `Day`, `Month`, `Year`. Dates are `YYYY-MM-DD`. Max range 730 days. Data goes back five years plus the current year. Body lists the metering point IDs.
- Other endpoints: `meteringpoints/meteringpoints`, `meteringpoint/getdetails`, `getcharges`, `getchargelinkswithcharges` (404 if the charges feature is off).
- The refresh token is a secret (see top of file).

**Prior art:** `../teslamate-cost` (github.com/driis/teslamate-cost, checked out locally) has `Source/dr.TeslamateCost/EnergyDataServiceClient.cs`, `SpotPriceWithOffset.cs` and `TaxesAndFees.cs`. Do not copy as is. Review and fix while porting. Issues already spotted:
- `priceArea` argument is ignored. The filter is hardcoded to `DK1`.
- Uses the `Elspotprices` dataset (hourly). Energi Data Service now publishes 15-minute `DayAheadPrices` from 2025-10-01 (handled, see Ingestion). Elspotprices holds the older data.
- Tariff cut-offs use a fixed `-2h` offset and `when.Hour` is read in whatever offset the value carries. Do all time-of-day logic in `Europe/Copenhagen` (DST aware), and store timestamps in UTC.
- Retry is recursive and only handles 429. Use `IHttpClientFactory` (typed client) with `Microsoft.Extensions.Http.Resilience` instead of `new HttpClient()` per instance.
- `ApplicationException` is used for errors, and `SpotPriceDKK` can be null. Use specific exceptions and handle missing prices explicitly.
- Tariff constants are hardcoded. Tariffs and taxes should be data with valid-from dates.
- DST fallback hack ("try next hour") in the price lookup. Replace with a correct UTC-keyed lookup.

## Code standards

- Target `net11.0` (always the latest .NET, set in `Directory.Build.props`, so project files must not set their own `TargetFramework`), latest C# language version (set in `Directory.Build.props`, along with nullable and warnings-as-errors). Use modern features where they fit: records, primary constructors, collection expressions, `required` members, pattern matching, `TimeProvider` (never `DateTime.Now`/`UtcNow` directly), `DateOnly`/`TimeOnly` where semantically right.
- Prefer immutable records for DTOs and domain values.
- Money and prices are `decimal`. State the unit in the name (`DkkPerKwh`, `Kwh`). Store UTC, present in `Europe/Copenhagen`.
- Prefer minimal APIs and small focused types over layered ceremony. Hobby project, keep it simple.

## Testing

A comprehensive test suite is a requirement.

- **Favor API-level integration tests** (vertical slices): `WebApplicationFactory<Program>` via `ApiFactory`, real HTTP calls, assert on responses and database state.
- **The database is real.** `ApiFactory` gives each test class its own throwaway database (`electricitydash_test_<guid>`), created by the app's own DbUp migrations on startup and dropped on dispose. No in-memory substitute. The Postgres server comes from `TEST_POSTGRES_CONNECTION`, defaulting to the docker-compose Postgres on localhost. So `docker compose up -d` before running tests locally.
- **External APIs are mocked** at the HTTP level (e.g. WireMock.Net or a stub `HttpMessageHandler`) for Eloverblik and Energi Data Service. Never hit real endpoints from tests.
- **Add unit tests where they add value:** pricing and tariff calculation, score calculation, forecast, DST edge cases (23h and 25h days), period splitting. Do not unit test plumbing.
- Deterministic tests: inject `TimeProvider`, no sleeping.
- xunit is v2 (`2.9.3`). Do not use v3-only APIs such as `TestContext.Current`.
- `Microsoft.AspNetCore.Mvc.Testing` must match the target framework major version (currently 11.x).
- External HTTP APIs are faked in `ApiFactory` through `FakeEnergyDataService` (an `HttpMessageHandler` plugged in as the typed client's primary handler), so the real client, resilience pipeline and JSON parsing run. `FixedTimeProvider` replaces `TimeProvider`. Add a similar fake for Eloverblik.

## Commands

```
docker compose up -d --wait   # local Postgres (dependencies only)
dotnet build
dotnet test                   # needs the Postgres above
dotnet run --project src/ElectricityDash.Web
```

`GET /api/health` returns status and the number of applied migrations.

## CI

`.github/workflows/ci.yml` runs build and tests on every push to `main` and every PR. It starts a Postgres service container and passes it to the tests through `TEST_POSTGRES_CONNECTION`. After pushing to `main`, check that the run is green (`gh run list` / `gh run watch`).

## Git workflow

- **Identity:** all commits use the personal GitHub account: `user.name = driis`, `user.email = dr@driis.dk`, set in the repo-local git config. Verify with `git config user.email` before committing. Never use the work identity.
- **Remote:** `origin` is `git@github-public:driis/dr.ElectricityDash.git`. The `github-public` SSH alias (in `~/.ssh/config`) selects the personal key. Plain `git@github.com` uses the work key and authenticates as the wrong account. The `gh` CLI also has separate work and personal accounts. Reading CI runs works with either (public repo), but changes to the repo itself (rename, settings) need `gh auth switch --user driis`, and you must switch back afterwards.
- **Push straight to `main`.** Hobby project, do not open a PR unless explicitly asked.
- Do not add attribution (`Co-Authored-By`, "Generated with Claude Code", etc.) to commits or PRs. This follows the user's global instructions.
- Small, coherent commits with clear messages.

## Open decisions

Ask the user before locking these in:

- Confirm hourly billing with an actual Aura invoice, and the subscription fee amount (to be added later).
- Authentication for the dashboard if it is ever published.
- Deployment target and how the scheduled ingestion job is triggered and secured.
