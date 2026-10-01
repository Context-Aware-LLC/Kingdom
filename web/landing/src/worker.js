// kingdomofbadideas.com Worker. Static assets are served by the ASSETS binding; this script
// only owns /api/*. Early Access pre-registration is double opt-in: the form stores the
// address unconfirmed and mails a confirm link; only a confirmed row counts as a sign-up.

const CONSENT_VERSION = "2026-10-01";
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
      subject: "Confirm your place in the Kingdom of Bad Ideas",
      text:
        "Someone (hopefully you) asked to hear about Early Access for Kingdom of Bad Ideas.\n\n" +
        `Confirm here: ${confirmUrl}\n\n` +
        "If that wasn't you, ignore this email and we will never write again.\n\n" +
        `Unsubscribe any time: ${unsubUrl}\n`,
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

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/api/preregister" && request.method === "POST") return preregister(request, env);
    if (url.pathname === "/api/confirm" && request.method === "GET") return confirm(url, env);
    if (url.pathname === "/api/unsubscribe" && request.method === "GET") return unsubscribe(url, env);
    if (url.pathname.startsWith("/api/")) return json({ ok: false, error: "not_found" }, 404);
    return env.ASSETS.fetch(request);
  },
};
