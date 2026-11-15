# ElectricityDash

[![CI](https://github.com/driis/dr.ElectricityDash/actions/workflows/ci.yml/badge.svg)](https://github.com/driis/dr.ElectricityDash/actions/workflows/ci.yml)

A personal dashboard for my electricity use. Prices vary a lot by hour, so I want to see what I consume, what it costs, and whether I use power when it is cheap.

- Imports consumption from [Eloverblik](https://eloverblik.dk) and stores it with the spot price for the same hour
- Adds tariffs, taxes and fees on top of spot price
- Calculates the monthly bill and forecasts the current month
- Dashboard (day, week, month) with consumption graphs and a score: the share of kWh used in the 6 cheapest hours of each day

**Status:** early. Spot price ingestion works. Consumption import, billing and the dashboard are not built yet. The subscription fee is not modelled yet, so bill totals will be incomplete.

## Stack

C# / .NET 11, Blazor (frontend and backend in one project), PostgreSQL, Dapper, DbUp for schema migrations. Deployment is undecided, likely a serverless cloud such as Cloud Run.

## Run locally

Dependencies run in Docker. The app runs on your machine.

```
docker compose up -d --wait
dotnet run --project src/ElectricityDash.Web
```

Load spot prices (Danish dates, defaults to today and tomorrow):

```
curl -X POST "http://localhost:5074/api/ingest/spot-prices?from=2025-10-01&to=2025-10-07"
```

Importing consumption (not built yet) will need an Eloverblik refresh token. Keep it in `dotnet user-secrets`, never in the repo.

## Test

```
docker compose up -d --wait
dotnet test
```

Mostly API-level integration tests against a real Postgres, with Eloverblik and Energi Data Service mocked. Each test class gets its own temporary database. CI runs the same on every push to `main` and every PR.

## Data sources

- Consumption and grid tariffs: [Eloverblik Customer API](https://docs.eloverblik.dk/docs/guides/api/customer)
- Spot prices: [Energi Data Service](https://www.energidataservice.dk)
