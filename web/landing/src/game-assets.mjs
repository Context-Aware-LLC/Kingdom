// Public, immutable, hashed game bundles. No listing, uploads, or arbitrary bucket paths.
const PATH =
    /^\/game-assets\/(backgrounds\/v1\/(?:Android|iOS|StandaloneWindows64|StandaloneOSX|StandaloneLinux64)\/backgrounds_desktoptablet_[a-z0-9_.-]+_[a-f0-9]{32}\.bundle)$/;
const failure = (status) =>
    new Response(null, { status, headers: { "cache-control": "no-store" } });

export async function gameAsset(request, env, ctx, cache = globalThis.caches?.default) {
    const url = new URL(request.url);
    const match = PATH.exec(url.pathname);
    if (!match) return failure(404);
    if (request.method !== "GET" && request.method !== "HEAD") return failure(405);
    if (!env.GAME_ASSETS) return failure(503);

    const range = request.headers.get("range");
    const cacheKey = new Request(url.origin + url.pathname);
    if (!range && cache) {
        const hit = await cache.match(cacheKey);
        if (hit) return respond(request, hit);
    }

    let object;
    try {
        object =
            request.method === "HEAD"
                ? await env.GAME_ASSETS.head(match[1])
                : await env.GAME_ASSETS.get(
                      match[1],
                      range ? { range: request.headers } : undefined,
                  );
    } catch (error) {
        console.error("Game asset read failed", error.message);
        return failure(503);
    }
    if (!object) return failure(404);
    // R2 also reports a full-object range when no partial response was requested.
    // Only a ranged GET is a 206; full responses must stay cacheable as 200.
    const partial = request.method === "GET" && range && object.range;
    const headers = new Headers({
        "content-type": "application/octet-stream",
        "content-length": String(partial?.length ?? object.size),
        "cache-control": "public, max-age=31536000, immutable",
        etag: object.httpEtag,
        "accept-ranges": "bytes",
        "access-control-allow-origin": "*",
        "x-content-type-options": "nosniff",
    });
    if (partial)
        headers.set(
            "content-range",
            `bytes ${partial.offset}-${partial.offset + partial.length - 1}/${object.size}`,
        );
    const response = new Response(request.method === "HEAD" ? null : object.body, {
        status: partial ? 206 : 200,
        headers,
    });
    if (!range && request.method === "GET" && cache && ctx)
        ctx.waitUntil(cache.put(cacheKey, response.clone()));
    return respond(request, response);
}

function respond(request, response) {
    const validator = request.headers.get("if-none-match");
    if (validator && validator === response.headers.get("etag"))
        return new Response(null, { status: 304, headers: response.headers });
    return request.method === "HEAD" ? new Response(null, response) : response;
}
