-- Synthetic chart of accounts for the load generator: 1,000 customer asset accounts in TRY. Transfers between
-- them never trip the overdraft rule (prevent_overdraft is false), so every well-formed load request is a 201
-- and any 5xx the SLO sees is caused by the fault under test, not by business-rule rejections.
-- With 1,000 accounts and two random accounts per entry, two concurrent entries share a row lock rarely; hot
-- accounts are a separate concern measured in ledger-recon.
INSERT INTO ledger.accounts (code, name, type, currency)
SELECT format('CUST-%s', lpad(g::text, 5, '0')),
       format('Synthetic customer %s', g),
       'asset',
       'TRY'
  FROM generate_series(1, 1000) AS g;
