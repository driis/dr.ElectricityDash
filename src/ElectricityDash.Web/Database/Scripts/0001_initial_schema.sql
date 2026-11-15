-- Spot price per delivery hour (Energi Data Service, ex VAT). Billing is hourly (see CLAUDE.md).
CREATE TABLE spot_price (
    price_area  text           NOT NULL,
    hour_start  timestamptz    NOT NULL,
    dkk_per_mwh numeric(14, 4) NOT NULL,
    PRIMARY KEY (price_area, hour_start)
);

-- Metered consumption per hour (Eloverblik).
CREATE TABLE consumption (
    hour_start  timestamptz   PRIMARY KEY,
    kwh         numeric(12, 3) NOT NULL CHECK (kwh >= 0),
    imported_at timestamptz   NOT NULL DEFAULT now()
);
