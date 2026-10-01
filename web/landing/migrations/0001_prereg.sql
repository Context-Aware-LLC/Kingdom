CREATE TABLE IF NOT EXISTS prereg (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  email TEXT NOT NULL UNIQUE,
  created_at TEXT NOT NULL,
  confirm_token TEXT NOT NULL,
  confirm_sent_at TEXT NOT NULL,
  confirmed_at TEXT,
  unsub_token TEXT NOT NULL,
  unsubscribed_at TEXT,
  consent_version TEXT NOT NULL,
  source TEXT
);
CREATE INDEX IF NOT EXISTS prereg_confirm ON prereg(confirm_token);
CREATE INDEX IF NOT EXISTS prereg_unsub ON prereg(unsub_token);
