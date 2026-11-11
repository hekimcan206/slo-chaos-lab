-- Fix for experiment 1 (docs/postmortems/01-latency.md): the posting transaction as one server-side function.
--
-- The interactive protocol (claim, lock, apply, commit) costs four network round trips and holds the account row
-- locks across three of them, so every millisecond added between the service and the database is paid four times
-- per request. Called in autocommit mode, this function is one round trip: the same statements, the same order,
-- the same invariants (the deferred balance trigger still fires at commit), executed next to the data.
--
-- Business-rule rejections are raised with SQLSTATE LR422 so the whole statement rolls back, including the
-- idempotency claim, exactly like an early return in the interactive version. MESSAGE carries the stable error
-- code, HINT the human-readable text (HINT, unlike DETAIL, is not redacted by Npgsql).
CREATE FUNCTION ledger.post_entry(
    p_id           uuid,
    p_key          text,
    p_hash         bytea,
    p_description  text,
    p_value_date   date,
    p_currency     char(3),
    p_external_ref text,
    p_codes        text[],
    p_directions   text[],
    p_amounts      bigint[])
RETURNS TABLE (entry_id uuid, posted_at timestamptz, replayed boolean)
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_posted     timestamptz;
    v_existing   uuid;
    v_existing_h bytea;
    v_existing_t timestamptz;
    v_bad        text;
BEGIN
    -- 1. Claim the idempotency key. A concurrent call with the same key blocks here until the first commits.
    INSERT INTO ledger.journal_entries AS je (id, idempotency_key, request_hash, description, value_date, currency, external_ref)
    VALUES (p_id, p_key, p_hash, p_description, p_value_date, p_currency, p_external_ref)
    ON CONFLICT (idempotency_key) DO NOTHING
    RETURNING je.posted_at INTO v_posted;

    IF v_posted IS NULL THEN
        SELECT je.id, je.request_hash, je.posted_at INTO v_existing, v_existing_h, v_existing_t
          FROM ledger.journal_entries AS je
         WHERE je.idempotency_key = p_key;
        IF v_existing_h <> p_hash THEN
            RAISE EXCEPTION 'idempotency.key_reused'
                USING ERRCODE = 'LR422', HINT = 'This Idempotency-Key was already used with a different request payload.';
        END IF;
        RETURN QUERY SELECT v_existing, v_existing_t, true;
        RETURN;
    END IF;

    -- 2. Lock the touched accounts in ascending id order (same order as every other writer: no deadlocks).
    PERFORM 1 FROM ledger.accounts AS a WHERE a.code = ANY (p_codes) ORDER BY a.id FOR UPDATE;

    -- 3. Rules that need current state.
    SELECT string_agg(w.code, ', ' ORDER BY w.code) INTO v_bad
      FROM (SELECT DISTINCT unnest(p_codes) AS code) AS w
     WHERE NOT EXISTS (SELECT 1 FROM ledger.accounts AS a WHERE a.code = w.code);
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'entry.unknown_account' USING ERRCODE = 'LR422', HINT = format('Unknown account(s): %s.', v_bad);
    END IF;

    SELECT string_agg(a.code, ', ' ORDER BY a.code) INTO v_bad
      FROM ledger.accounts AS a
     WHERE a.code = ANY (p_codes) AND a.currency <> p_currency;
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'entry.currency_mismatch'
            USING ERRCODE = 'LR422', HINT = format('Entry currency is %s; account(s) %s use another currency.', p_currency, v_bad);
    END IF;

    SELECT a.code INTO v_bad
      FROM ledger.accounts AS a
      JOIN (SELECT l.code, sum(CASE l.dir WHEN 'D' THEN l.amount ELSE -l.amount END) AS delta
              FROM unnest(p_codes, p_directions, p_amounts) AS l(code, dir, amount)
             GROUP BY l.code) AS d ON d.code = a.code
     WHERE a.prevent_overdraft
       AND CASE WHEN a.type IN ('asset', 'expense') THEN a.balance_minor + d.delta ELSE -(a.balance_minor + d.delta) END < 0
     ORDER BY a.code
     LIMIT 1;
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'account.insufficient_funds'
            USING ERRCODE = 'LR422', HINT = format('Account %s would be overdrawn; overdraft is not allowed.', v_bad);
    END IF;

    -- 4. Apply.
    UPDATE ledger.accounts AS a
       SET balance_minor = a.balance_minor + d.delta, version = a.version + 1, updated_at = now()
      FROM (SELECT l.code, sum(CASE l.dir WHEN 'D' THEN l.amount ELSE -l.amount END) AS delta
              FROM unnest(p_codes, p_directions, p_amounts) AS l(code, dir, amount)
             GROUP BY l.code) AS d
     WHERE a.code = d.code AND d.delta <> 0;

    INSERT INTO ledger.postings (entry_id, line_no, account_id, currency, direction, amount_minor)
    SELECT p_id, l.line_no::smallint, a.id, p_currency, l.dir, l.amount
      FROM unnest(p_codes, p_directions, p_amounts) WITH ORDINALITY AS l(code, dir, amount, line_no)
      JOIN ledger.accounts AS a ON a.code = l.code;

    RETURN QUERY SELECT p_id, v_posted, false;
END;
$$;

-- Functions are executable by PUBLIC by default; only the application role may post.
REVOKE ALL ON FUNCTION ledger.post_entry(uuid, text, bytea, text, date, char, text, text[], text[], bigint[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ledger.post_entry(uuid, text, bytea, text, date, char, text, text[], text[], bigint[]) TO ledger_app;
