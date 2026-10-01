// Prints the confirmed, still-subscribed Early Access list as CSV.
//   node scripts/export-csv.mjs > early-access.csv
import { execFileSync } from "node:child_process";

const sql =
  "SELECT email, confirmed_at, source FROM prereg WHERE confirmed_at IS NOT NULL AND unsubscribed_at IS NULL ORDER BY confirmed_at";
const out = execFileSync("npx", ["wrangler", "d1", "execute", "kobi-site", "--remote", "--json", "--command", sql], {
  encoding: "utf8",
  shell: true,
});
const rows = JSON.parse(out.slice(out.indexOf("[")))[0].results;
// Quote every field, and neutralise a leading = + - @ so a spreadsheet never runs an address as a formula.
const cell = (v) => `"${String(v ?? "").replace(/^[=+\-@]/, "'$&").replace(/"/g, '""')}"`;
console.log("email,confirmed_at,source");
for (const r of rows) console.log([r.email, r.confirmed_at, r.source].map(cell).join(","));
