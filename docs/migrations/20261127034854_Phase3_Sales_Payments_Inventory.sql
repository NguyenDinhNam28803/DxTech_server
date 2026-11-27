-- Sinh bởi: dotnet tool run dotnet-ef migrations script Identity_RefreshTokenReplacedBySetNull Phase3_Sales_Payments_Inventory --project server/server.csproj
-- Chỉ để review (PR-9, thiết kế docs/design/pr-09-10-phase3-schema-and-cart-quote.md mục A4–A5). Production áp qua container "migrate" (dxtechs_owner).
-- Khối DO $phase3$ cuối file: thu hồi quyền dxtechs_app; roles.sql mục 5b lặp lại mỗi lần deploy.

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'inventory') THEN
        CREATE SCHEMA inventory;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'sales') THEN
        CREATE SCHEMA sales;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'payments') THEN
        CREATE SCHEMA payments;
    END IF;
END $EF$;

CREATE TABLE inventory.import_batches (
    id uuid NOT NULL,
    variant_id uuid NOT NULL,
    source text NOT NULL,
    file_name text NOT NULL,
    total_rows integer NOT NULL,
    inserted_rows integer NOT NULL,
    duplicate_rows integer NOT NULL,
    invalid_rows integer NOT NULL,
    imported_by uuid,
    note text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_import_batches PRIMARY KEY (id),
    CONSTRAINT ck_import_batches_counts CHECK (total_rows >= 0 AND inserted_rows >= 0 AND duplicate_rows >= 0 AND invalid_rows >= 0 AND inserted_rows + duplicate_rows + invalid_rows = total_rows),
    CONSTRAINT ck_import_batches_importer CHECK (source <> 'admin' OR imported_by IS NOT NULL),
    CONSTRAINT ck_import_batches_source CHECK (source IN ('cli', 'admin')),
    CONSTRAINT ck_import_batches_texts CHECK (char_length(file_name) BETWEEN 1 AND 255 AND (note IS NULL OR char_length(note) <= 500)),
    CONSTRAINT fk_import_batches_product_variants_variant_id FOREIGN KEY (variant_id) REFERENCES catalog.product_variants (id) ON DELETE RESTRICT,
    CONSTRAINT fk_import_batches_users_imported_by FOREIGN KEY (imported_by) REFERENCES identity.users (id) ON DELETE RESTRICT
);

CREATE TABLE sales.orders (
    id uuid NOT NULL,
    code text NOT NULL,
    user_id uuid NOT NULL,
    idempotency_key uuid NOT NULL,
    status text NOT NULL,
    customer_email citext NOT NULL,
    customer_name text NOT NULL,
    vat_info jsonb,
    subtotal_vnd bigint NOT NULL,
    discount_vnd bigint NOT NULL,
    total_vnd bigint NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    paid_at timestamp with time zone,
    fulfilled_at timestamp with time zone,
    cancelled_at timestamp with time zone,
    cancel_reason text,
    refunded_at timestamp with time zone,
    note text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_orders PRIMARY KEY (id),
    CONSTRAINT ck_orders_amounts CHECK (subtotal_vnd >= 0 AND discount_vnd >= 0 AND discount_vnd <= subtotal_vnd AND total_vnd = subtotal_vnd - discount_vnd AND total_vnd > 0),
    CONSTRAINT ck_orders_cancel_reason CHECK (cancel_reason IS NULL OR cancel_reason IN ('expired', 'customer', 'admin')),
    CONSTRAINT ck_orders_cancelled CHECK ((status = 'cancelled') = (cancelled_at IS NOT NULL AND cancel_reason IS NOT NULL)),
    CONSTRAINT ck_orders_code CHECK (code ~ '^DX[0-9A-HJKMNP-TV-Z]{8}$'),
    CONSTRAINT ck_orders_customer_email CHECK (char_length(customer_email) BETWEEN 3 AND 254),
    CONSTRAINT ck_orders_customer_name CHECK (char_length(customer_name) BETWEEN 1 AND 100),
    CONSTRAINT ck_orders_expires CHECK (expires_at > created_at),
    CONSTRAINT ck_orders_fulfilled_at CHECK (status <> 'fulfilled' OR fulfilled_at IS NOT NULL),
    CONSTRAINT ck_orders_note CHECK (note IS NULL OR char_length(note) <= 1000),
    CONSTRAINT ck_orders_paid_at CHECK (status NOT IN ('paid', 'fulfilled', 'refunded') OR paid_at IS NOT NULL),
    CONSTRAINT ck_orders_refunded_at CHECK ((status = 'refunded') = (refunded_at IS NOT NULL)),
    CONSTRAINT ck_orders_status CHECK (status IN ('pending_payment', 'paid', 'fulfilled', 'cancelled', 'refunded')),
    CONSTRAINT ck_orders_vat_info CHECK (vat_info IS NULL OR jsonb_typeof(vat_info) = 'object'),
    CONSTRAINT fk_orders_users_user_id FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE RESTRICT
);

CREATE TABLE payments.webhook_events (
    id uuid NOT NULL,
    provider text NOT NULL,
    event_key text NOT NULL,
    signature_valid boolean NOT NULL,
    remote_ip inet,
    payload jsonb NOT NULL,
    headers jsonb,
    received_at timestamp with time zone NOT NULL DEFAULT (now()),
    processed_at timestamp with time zone,
    attempts integer NOT NULL DEFAULT 0,
    last_error text,
    CONSTRAINT pk_webhook_events PRIMARY KEY (id),
    CONSTRAINT ck_webhook_events_attempts CHECK (attempts >= 0),
    CONSTRAINT ck_webhook_events_event_key CHECK (char_length(event_key) BETWEEN 1 AND 200),
    CONSTRAINT ck_webhook_events_headers CHECK (headers IS NULL OR jsonb_typeof(headers) = 'object'),
    CONSTRAINT ck_webhook_events_payload CHECK (jsonb_typeof(payload) IN ('object', 'array')),
    CONSTRAINT ck_webhook_events_processed CHECK (processed_at IS NULL OR signature_valid),
    CONSTRAINT ck_webhook_events_provider CHECK (provider IN ('sepay', 'payos', 'manual'))
);

CREATE TABLE sales.order_items (
    id uuid NOT NULL,
    order_id uuid NOT NULL,
    variant_id uuid NOT NULL,
    product_name_snapshot text NOT NULL,
    variant_name_snapshot text NOT NULL,
    sku_snapshot text NOT NULL,
    delivery_type text NOT NULL,
    unit_price_vnd bigint NOT NULL,
    quantity integer NOT NULL,
    line_total_vnd bigint NOT NULL,
    fulfilled_at timestamp with time zone,
    CONSTRAINT pk_order_items PRIMARY KEY (id),
    CONSTRAINT ck_order_items_delivery_type CHECK (delivery_type IN ('license_key', 'account', 'manual')),
    CONSTRAINT ck_order_items_line_total CHECK (line_total_vnd = unit_price_vnd * quantity),
    CONSTRAINT ck_order_items_price CHECK (unit_price_vnd >= 0),
    CONSTRAINT ck_order_items_quantity CHECK (quantity BETWEEN 1 AND 20),
    CONSTRAINT ck_order_items_snapshots CHECK (char_length(product_name_snapshot) BETWEEN 1 AND 200 AND char_length(variant_name_snapshot) BETWEEN 1 AND 200 AND char_length(sku_snapshot) BETWEEN 1 AND 64),
    CONSTRAINT fk_order_items_orders_order_id FOREIGN KEY (order_id) REFERENCES sales.orders (id) ON DELETE RESTRICT,
    CONSTRAINT fk_order_items_product_variants_variant_id FOREIGN KEY (variant_id) REFERENCES catalog.product_variants (id) ON DELETE RESTRICT
);

CREATE TABLE sales.order_status_history (
    id bigint GENERATED ALWAYS AS IDENTITY,
    order_id uuid NOT NULL,
    from_status text,
    to_status text NOT NULL,
    actor_type text NOT NULL,
    actor_user_id uuid,
    reason text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_order_status_history PRIMARY KEY (id),
    CONSTRAINT ck_order_status_history_actor_type CHECK (actor_type IN ('customer', 'admin', 'system', 'webhook')),
    CONSTRAINT ck_order_status_history_reason CHECK (reason IS NULL OR char_length(reason) <= 200),
    CONSTRAINT ck_order_status_history_statuses CHECK (to_status IN ('pending_payment', 'paid', 'fulfilled', 'cancelled', 'refunded') AND (from_status IS NULL OR from_status IN ('pending_payment', 'paid', 'fulfilled', 'cancelled', 'refunded'))),
    CONSTRAINT ck_order_status_history_transition CHECK (from_status IS DISTINCT FROM to_status),
    CONSTRAINT fk_order_status_history_orders_order_id FOREIGN KEY (order_id) REFERENCES sales.orders (id) ON DELETE RESTRICT
);

CREATE TABLE payments.payments (
    id uuid NOT NULL,
    order_id uuid,
    provider text NOT NULL,
    provider_txn_id text NOT NULL,
    bank_reference text,
    transfer_content text NOT NULL,
    amount_vnd bigint NOT NULL,
    match_status text NOT NULL,
    webhook_event_id uuid,
    paid_at timestamp with time zone NOT NULL,
    resolved_at timestamp with time zone,
    resolved_by uuid,
    resolution_note text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_payments PRIMARY KEY (id),
    CONSTRAINT ck_payments_amount CHECK (amount_vnd > 0),
    CONSTRAINT ck_payments_match_status CHECK (match_status IN ('matched', 'underpaid', 'overpaid', 'no_order_code', 'unknown_order', 'order_not_payable', 'late_no_stock')),
    CONSTRAINT ck_payments_order_link CHECK ((order_id IS NULL) = (match_status IN ('no_order_code', 'unknown_order'))),
    CONSTRAINT ck_payments_provider CHECK (provider IN ('sepay', 'payos', 'manual')),
    CONSTRAINT ck_payments_resolved CHECK ((resolved_at IS NULL) = (resolved_by IS NULL)),
    CONSTRAINT ck_payments_texts CHECK (char_length(provider_txn_id) BETWEEN 1 AND 200 AND char_length(transfer_content) <= 500 AND (bank_reference IS NULL OR char_length(bank_reference) <= 100) AND (resolution_note IS NULL OR char_length(resolution_note) <= 1000)),
    CONSTRAINT fk_payments_orders_order_id FOREIGN KEY (order_id) REFERENCES sales.orders (id) ON DELETE RESTRICT,
    CONSTRAINT fk_payments_users_resolved_by FOREIGN KEY (resolved_by) REFERENCES identity.users (id) ON DELETE RESTRICT,
    CONSTRAINT fk_payments_webhook_events_webhook_event_id FOREIGN KEY (webhook_event_id) REFERENCES payments.webhook_events (id) ON DELETE RESTRICT
);

CREATE TABLE inventory.license_keys (
    id uuid NOT NULL,
    variant_id uuid NOT NULL,
    batch_id uuid NOT NULL,
    key_encrypted bytea NOT NULL,
    key_version smallint NOT NULL,
    key_hash bytea NOT NULL,
    key_hint text NOT NULL,
    status text NOT NULL,
    order_item_id uuid,
    reserved_at timestamp with time zone,
    delivered_at timestamp with time zone,
    revoked_reason text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_license_keys PRIMARY KEY (id),
    CONSTRAINT ck_license_keys_assigned CHECK (status NOT IN ('reserved', 'delivered') OR (order_item_id IS NOT NULL AND reserved_at IS NOT NULL)),
    CONSTRAINT ck_license_keys_available CHECK (status <> 'available' OR (order_item_id IS NULL AND reserved_at IS NULL AND delivered_at IS NULL)),
    CONSTRAINT ck_license_keys_ciphertext CHECK (octet_length(key_encrypted) >= 29),
    CONSTRAINT ck_license_keys_delivered CHECK (status <> 'delivered' OR delivered_at IS NOT NULL),
    CONSTRAINT ck_license_keys_hash_len CHECK (octet_length(key_hash) = 32),
    CONSTRAINT ck_license_keys_hint CHECK (char_length(key_hint) BETWEEN 1 AND 8),
    CONSTRAINT ck_license_keys_revoked CHECK ((status = 'revoked') = (revoked_reason IS NOT NULL)),
    CONSTRAINT ck_license_keys_status CHECK (status IN ('available', 'reserved', 'delivered', 'revoked')),
    CONSTRAINT ck_license_keys_version CHECK (key_version > 0),
    CONSTRAINT fk_license_keys_import_batches_batch_id FOREIGN KEY (batch_id) REFERENCES inventory.import_batches (id) ON DELETE RESTRICT,
    CONSTRAINT fk_license_keys_order_items_order_item_id FOREIGN KEY (order_item_id) REFERENCES sales.order_items (id) ON DELETE RESTRICT,
    CONSTRAINT fk_license_keys_product_variants_variant_id FOREIGN KEY (variant_id) REFERENCES catalog.product_variants (id) ON DELETE RESTRICT
);

CREATE INDEX ix_import_batches_imported_by ON inventory.import_batches (imported_by) WHERE imported_by IS NOT NULL;

CREATE INDEX ix_import_batches_variant_created ON inventory.import_batches (variant_id, created_at DESC);

CREATE INDEX ix_license_keys_available ON inventory.license_keys (variant_id, created_at) WHERE status = 'available';

CREATE INDEX ix_license_keys_batch_id ON inventory.license_keys (batch_id);

CREATE INDEX ix_license_keys_order_item ON inventory.license_keys (order_item_id) WHERE order_item_id IS NOT NULL;

CREATE UNIQUE INDEX ux_license_keys_key_hash ON inventory.license_keys (key_hash);

CREATE INDEX ix_order_items_variant_id ON sales.order_items (variant_id);

CREATE UNIQUE INDEX ux_order_items_order_variant ON sales.order_items (order_id, variant_id);

CREATE INDEX ix_order_status_history_order_id ON sales.order_status_history (order_id, id);

CREATE INDEX ix_orders_pending_expires ON sales.orders (expires_at) WHERE status = 'pending_payment';

CREATE INDEX ix_orders_status_created ON sales.orders (status, created_at DESC, id DESC);

CREATE INDEX ix_orders_user_created ON sales.orders (user_id, created_at DESC);

CREATE UNIQUE INDEX ux_orders_code ON sales.orders (code);

CREATE UNIQUE INDEX ux_orders_user_idempotency ON sales.orders (user_id, idempotency_key);

CREATE INDEX ix_payments_order_id ON payments.payments (order_id) WHERE order_id IS NOT NULL;

CREATE INDEX ix_payments_resolved_by ON payments.payments (resolved_by) WHERE resolved_by IS NOT NULL;

CREATE INDEX ix_payments_unresolved ON payments.payments (created_at) WHERE match_status <> 'matched' AND resolved_at IS NULL;

CREATE UNIQUE INDEX ux_payments_provider_txn ON payments.payments (provider, provider_txn_id);

CREATE UNIQUE INDEX ux_payments_webhook_event_id ON payments.payments (webhook_event_id) WHERE webhook_event_id IS NOT NULL;

CREATE INDEX ix_webhook_events_received_at ON payments.webhook_events (received_at);

CREATE INDEX ix_webhook_events_unprocessed ON payments.webhook_events (received_at) WHERE processed_at IS NULL AND signature_valid;

CREATE UNIQUE INDEX ux_webhook_events_provider_key ON payments.webhook_events (provider, event_key);

DO $phase3$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dxtechs_app') THEN
    REVOKE DELETE, TRUNCATE ON sales.orders, sales.order_items, inventory.license_keys, inventory.import_batches FROM dxtechs_app;
    REVOKE UPDATE, DELETE, TRUNCATE ON sales.order_status_history, payments.payments FROM dxtechs_app;
    GRANT UPDATE (resolved_at, resolved_by, resolution_note) ON payments.payments TO dxtechs_app;
    REVOKE TRUNCATE ON payments.webhook_events FROM dxtechs_app;
  END IF;
END
$phase3$;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261127034854_Phase3_Sales_Payments_Inventory', '8.0.8');

COMMIT;


