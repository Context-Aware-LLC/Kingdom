// kingdomofbadideas.com Worker. Static assets are served by the ASSETS binding; this script
// owns /api/* and the friend link, /f/*. Early Access pre-registration is double opt-in: the form stores the
// address unconfirmed and mails a confirm link; only a confirmed row counts as a sign-up.

const CONSENT_VERSION = "2026-09-30";
const RESEND_COOLDOWN_MS = 10 * 60 * 1000;
const EMAIL_RE = /^[^\s@<>"',;]+@[^\s@<>"',;]+\.[^\s@<>"',;]+$/;

const json = (body, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", "cache-control": "no-store" },
  });

const redirect = (path) =>
  new Response(null, { status: 303, headers: { location: path, "cache-control": "no-store" } });

const token = () => {
  const b = crypto.getRandomValues(new Uint8Array(24));
  return btoa(String.fromCharCode(...b)).replace(/[+/=]/g, (c) => ({ "+": "-", "/": "_", "=": "" })[c]);
};

async function turnstileOk(env, response, ip) {
  if (!response) return false;
  const form = new FormData();
  form.append("secret", env.TURNSTILE_SECRET);
  form.append("response", response);
  if (ip) form.append("remoteip", ip);
  const r = await fetch("https://challenges.cloudflare.com/turnstile/v0/siteverify", { method: "POST", body: form });
  return r.ok && (await r.json()).success === true;
}

// Table layout and inline styles only: email clients ignore stylesheets and web fonts.
function invitationHtml(origin, confirmUrl, unsubUrl) {
  const serif = "Georgia,'Times New Roman',serif";
  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light"></head>
<body style="margin:0;padding:0;background:#0b0f14;">
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#0b0f14;"><tr><td align="center" style="padding:32px 12px;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;background:#f3e7cc;border:1px solid #c9ad76;border-radius:6px;">
    <tr><td align="center" style="padding:40px 36px 8px;font-family:${serif};color:#2a1d10;">
      <div style="font-size:12px;letter-spacing:4px;text-transform:uppercase;color:#8a3b12;">By order of the Crown</div>
      <h1 style="margin:18px 0 0;font-size:30px;line-height:1.15;font-weight:bold;color:#2a1d10;">You are cordially invited</h1>
    </td></tr>
    <tr><td align="center" style="padding:20px 40px 8px;font-family:${serif};font-size:17px;line-height:1.65;color:#2a1d10;">
      His Royal Highness and Her Majesty the Queen cordially invite you to take your place among the first citizens of the Kingdom of Bad Ideas, when Early Access opens its gates.
    </td></tr>
    <tr><td align="center" style="padding:20px 40px 4px;font-family:${serif};font-size:15px;line-height:1.6;color:#5b4630;">
      To accept, kindly affix your seal below. It is the only formality, which is remarkable for this kingdom.
    </td></tr>
    <tr><td align="center" style="padding:24px 40px 8px;">
      <a href="${confirmUrl}" style="display:inline-block;padding:14px 30px;background:#8e1f12;color:#f3e7cc;font-family:${serif};font-size:17px;font-weight:bold;text-decoration:none;border-radius:4px;">Accept the Invitation</a>
    </td></tr>
    <tr><td align="center" style="padding:24px 0 4px;"><img src="${origin}/assets/seal.png" width="96" height="96" alt="The royal wax seal" style="display:block;border:0;"></td></tr>
    <tr><td align="center" style="padding:8px 40px 36px;font-family:${serif};font-size:13px;line-height:1.6;color:#7a6548;">
      Should you have received this in error, kindly disregard it. The Crown will trouble you no further.<br>
      <a href="${unsubUrl}" style="color:#7a6548;">Withdraw from the register</a>
    </td></tr>
  </table>
</td></tr></table>
</body></html>`;
}

async function sendConfirmation(env, origin, email, confirmToken, unsubToken) {
  if (!env.RESEND_API_KEY) {
    console.warn("RESEND_API_KEY not set: confirmation email not sent");
    return false;
  }
  const confirmUrl = `${origin}/api/confirm?t=${confirmToken}`;
  const unsubUrl = `${origin}/api/unsubscribe?t=${unsubToken}`;
  const r = await fetch("https://api.resend.com/emails", {
    method: "POST",
    headers: { authorization: `Bearer ${env.RESEND_API_KEY}`, "content-type": "application/json" },
    body: JSON.stringify({
      from: env.MAIL_FROM,
      to: [email],
      subject: "You are cordially invited to the Kingdom of Bad Ideas",
      html: invitationHtml(origin, confirmUrl, unsubUrl),
      text:
        "BY ORDER OF THE CROWN\n\n" +
        "His Royal Highness and Her Majesty the Queen cordially invite you to take your place among the first citizens of the Kingdom of Bad Ideas, when Early Access opens its gates.\n\n" +
        `To accept, affix your seal here: ${confirmUrl}\n\n` +
        "Should you have received this in error, kindly disregard it. The Crown will trouble you no further.\n\n" +
        `Withdraw from the register at any time: ${unsubUrl}\n`,
      headers: { "List-Unsubscribe": `<${unsubUrl}>` },
    }),
  });
  if (!r.ok) console.error("resend failed", r.status);
  return r.ok;
}

async function preregister(request, env) {
  const origin = new URL(request.url).origin;
  let form;
  try {
    form = await request.formData();
  } catch {
    return json({ ok: false, error: "bad_request" }, 400);
  }
  const email = String(form.get("email") ?? "").trim().toLowerCase();
  if (email.length > 254 || !EMAIL_RE.test(email)) return json({ ok: false, error: "invalid_email" }, 400);
  if (form.get("consent") !== "on") return json({ ok: false, error: "consent_required" }, 400);

  const ip = request.headers.get("cf-connecting-ip");
  if (!(await turnstileOk(env, String(form.get("cf-turnstile-response") ?? ""), ip)))
    return json({ ok: false, error: "challenge_failed" }, 400);

  const now = new Date().toISOString();
  const row = await env.DB.prepare("SELECT confirm_token, unsub_token, confirm_sent_at, confirmed_at FROM prereg WHERE email = ?")
    .bind(email)
    .first();

  // Same answer for new, pending and already-confirmed addresses: the form must not reveal who is on the list.
  if (!row) {
    const c = token();
    const u = token();
    await env.DB.prepare(
      "INSERT INTO prereg (email, created_at, confirm_token, confirm_sent_at, unsub_token, consent_version, source) VALUES (?, ?, ?, ?, ?, ?, ?)",
    )
      .bind(email, now, c, now, u, CONSENT_VERSION, String(form.get("source") ?? "landing").slice(0, 40))
      .run();
    await sendConfirmation(env, origin, email, c, u);
  } else if (!row.confirmed_at && Date.now() - Date.parse(row.confirm_sent_at) > RESEND_COOLDOWN_MS) {
    await env.DB.prepare("UPDATE prereg SET confirm_sent_at = ?, unsubscribed_at = NULL WHERE email = ?").bind(now, email).run();
    await sendConfirmation(env, origin, email, row.confirm_token, row.unsub_token);
  }
  return json({ ok: true });
}

async function confirm(url, env) {
  const t = url.searchParams.get("t") ?? "";
  const res = t
    ? await env.DB.prepare("UPDATE prereg SET confirmed_at = COALESCE(confirmed_at, ?), unsubscribed_at = NULL WHERE confirm_token = ?")
        .bind(new Date().toISOString(), t)
        .run()
    : null;
  return redirect(res?.meta.changes ? "/registered" : "/link-expired");
}

async function unsubscribe(url, env) {
  const t = url.searchParams.get("t") ?? "";
  const res = t
    ? await env.DB.prepare("UPDATE prereg SET unsubscribed_at = COALESCE(unsubscribed_at, ?) WHERE unsub_token = ?")
        .bind(new Date().toISOString(), t)
        .run()
    : null;
  return redirect(res?.meta.changes ? "/unsubscribed" : "/link-expired");
}

// A shared friend link, /f/XXXXX-XXXXX (#424). The code is checked exactly as the game checks it
// (FriendCode.TryNormalize in src/Kingdom.Net.Protocol/FriendCode.cs): case, hyphens and spaces
// forgiven, then the CRC-5 check symbol. The page never names the player behind the code: a route
// that did would let anyone test codes for owners.
const FRIEND_ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
const ANDROID_PACKAGE = "com.contextaware.kobi";

function friendCrcStep(crc, symbol) {
  for (let bit = 4; bit >= 0; bit--) {
    const feedback = ((crc >> 4) ^ (symbol >> bit)) & 1;
    crc = (crc << 1) & 31;
    if (feedback) crc ^= 0x05;
  }
  return crc;
}

function normalizeFriendCode(raw) {
  let code = "";
  let crc = 0;
  for (const typed of raw) {
    if (typed === "-" || typed === " ") continue;
    const c = typed >= "a" && typed <= "z" ? typed.toUpperCase() : typed;
    const symbol = FRIEND_ALPHABET.indexOf(c);
    if (symbol < 0 || code.length === 10) return null;
    if (code.length < 9) crc = friendCrcStep(crc, symbol);
    else if (symbol !== crc) return null;
    code += c;
  }
  return code.length === 10 ? code : null;
}

async function notFound(url, env) {
  const page = await env.ASSETS.fetch(new Request(new URL("/404", url)));
  return new Response(page.body, { status: 404, headers: page.headers });
}

// friend.html is the template; its own address answers 404 so it is only ever seen filled in.
// "Open in the game" is an Android intent (it falls through to the Play Store when the game is not
// installed), the emberfall:// scheme on iOS, and absent on a desktop, which has no game to open.
async function friendInvite(request, url, env) {
  let raw = "";
  try {
    raw = decodeURIComponent(url.pathname.slice("/f/".length)).replace(/\/+$/, "");
  } catch {
    return notFound(url, env);
  }
  const code = normalizeFriendCode(raw);
  if (!code) return notFound(url, env);

  const ua = request.headers.get("user-agent") ?? "";
  const open = /Android/i.test(ua)
    ? `intent://friend/${code}#Intent;scheme=emberfall;package=${ANDROID_PACKAGE};end`
    : /iPhone|iPad|iPod/i.test(ua)
      ? `emberfall://friend/${code}`
      : null;

  const template = await env.ASSETS.fetch(new Request(new URL("/friend", url)));
  const page = new HTMLRewriter()
    .on("#friend-code", { element: (el) => el.setInnerContent(`${code.slice(0, 5)}-${code.slice(5)}`) })
    .on("#open-game", { element: (el) => (open ? el.setAttribute("href", open) : el.remove()) })
    .transform(template);
  const response = new Response(page.body, page);
  // Per code and per platform: nothing may cache it, and the template's validator does not describe it.
  response.headers.set("cache-control", "no-store");
  response.headers.set("vary", "User-Agent");
  response.headers.delete("etag");
  return response;
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname.startsWith("/f/") && request.method === "GET") return friendInvite(request, url, env);
    if (url.pathname === "/friend" || url.pathname === "/friend.html") return notFound(url, env);
    if (url.pathname === "/api/preregister" && request.method === "POST") return preregister(request, env);
    if (url.pathname === "/api/confirm" && request.method === "GET") return confirm(url, env);
    if (url.pathname === "/api/unsubscribe" && request.method === "GET") return unsubscribe(url, env);
    if (url.pathname.startsWith("/api/")) return json({ ok: false, error: "not_found" }, 404);
    return env.ASSETS.fetch(request);
  },
};
