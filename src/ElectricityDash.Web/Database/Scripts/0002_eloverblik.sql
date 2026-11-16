-- Eloverblik ingestion: consumption per meter and the charges (tariffs, subscriptions, fees) on top of spot price.
--
-- Spot price, consumption and charges are ingested independently and arrive at different times
-- (spot prices a day ahead, consumption a day or more late, charges whenever the grid company changes them).
-- So there are deliberately NO foreign keys between them: they are joined on the hour at query time and a
-- missing side (e.g. consumption without a price yet) is a normal state, not a constraint violation.

-- Nothing has ever written to the 0001 consumption table (no ingestion existed), so it is recreated keyed by meter.
DROP TABLE consumption;

-- Metered consumption per delivery hour. Eloverblik may revise recent values, so ingest is an upsert.
CREATE TABLE consumption (
    metering_point_id text           NOT NULL,
    hour_start        timestamptz    NOT NULL,
    kwh               numeric(12, 3) NOT NULL CHECK (kwh >= 0),
    -- Eloverblik quality code: A01 adjusted, A02 missing, A03 estimated, A04 as read from the meter.
    quality           text           NOT NULL,
    imported_at       timestamptz    NOT NULL DEFAULT now(),
    PRIMARY KEY (metering_point_id, hour_start)
);

-- One version of a charge. Eloverblik only returns the versions valid right now, so history accumulates as
-- new versions (new valid_from) or changes (valid_to set, prices corrected) are seen on later ingests.
-- Prices are in DKK, as reported by Eloverblik (VAT treatment still to be verified against an invoice).
CREATE TABLE charge (
    price_id      text        NOT NULL,                  -- Eloverblik priceId (the owner's charge identifier)
    valid_from    timestamptz NOT NULL,
    valid_to      timestamptz,                           -- null = open ended
    kind          text        NOT NULL CHECK (kind IN ('tariff', 'subscription', 'fee')),
    name          text        NOT NULL,
    description   text,
    owner         text,
    -- ISO 8601 duration, how the price positions are laid out or billed:
    --   tariff:  P1D = one flat price per kWh, PT1H = 24 prices by local (Europe/Copenhagen) hour of day
    --   subscription / fee: P1M = DKK per month, P1D = DKK per day, etc.
    period_type   text        NOT NULL,
    quantity      numeric(14, 4),                        -- subscriptions and fees only
    first_seen_at timestamptz NOT NULL DEFAULT now(),    -- when we first learned about this version
    last_seen_at  timestamptz NOT NULL DEFAULT now(),    -- last ingest that still returned it
    PRIMARY KEY (price_id, valid_from)
);

-- Prices of a charge version. position is 1-based (Eloverblik's own numbering): always 1 for flat tariffs,
-- subscriptions and fees, and 1..24 (local hour of day, position 1 = 00:00-01:00) for hourly tariffs.
-- DKK per kWh for tariffs; DKK per period_type for subscriptions and fees.
CREATE TABLE charge_price (
    price_id   text           NOT NULL,
    valid_from timestamptz    NOT NULL,
    position   integer        NOT NULL CHECK (position >= 1),
    dkk        numeric(14, 6) NOT NULL,
    PRIMARY KEY (price_id, valid_from, position),
    FOREIGN KEY (price_id, valid_from) REFERENCES charge (price_id, valid_from) ON DELETE CASCADE
);
