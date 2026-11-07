import assert from "node:assert/strict";
import { test } from "node:test";
import { gameAsset } from "../src/game-assets.mjs";

const key =
    "backgrounds/v1/Android/backgrounds_desktoptablet_assets_login_" + "a".repeat(32) + ".bundle";
const url = "https://kingdomofbadideas.com/game-assets/" + key;
const body = new Uint8Array([1, 2, 3, 4]);
const object = () => ({ size: 4, body, httpEtag: '"hash"', range: { offset: 0, length: 4 } });

test("serves bytes with immutable caching and exact bucket key", async () => {
    const env = {
        GAME_ASSETS: {
            get: async (requested) => {
                assert.equal(requested, key);
                return object();
            },
        },
    };
    const response = await gameAsset(new Request(url), env, null, null);
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("content-range"), null);
    assert.match(response.headers.get("cache-control"), /immutable/);
    assert.deepEqual(new Uint8Array(await response.arrayBuffer()), body);
});

test("HEAD reports length without reading the object body", async () => {
    const env = {
        GAME_ASSETS: {
            head: async () => object(),
            get: () => {
                throw Error("unexpected GET");
            },
        },
    };
    const response = await gameAsset(new Request(url, { method: "HEAD" }), env, null, null);
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("content-range"), null);
    assert.equal(response.headers.get("content-length"), "4");
    assert.equal(await response.text(), "");
});

test("missing objects are not cached, and writes/arbitrary paths are refused", async () => {
    const env = { GAME_ASSETS: { get: async () => null } };
    const missing = await gameAsset(new Request(url), env, null, null);
    assert.equal(missing.status, 404);
    assert.equal(missing.headers.get("cache-control"), "no-store");
    assert.equal(
        (await gameAsset(new Request(url, { method: "PUT" }), env, null, null)).status,
        405,
    );
    assert.equal(
        (await gameAsset(new Request(url.replace(".bundle", ".json")), env, null, null)).status,
        404,
    );
    assert.equal(
        (await gameAsset(new Request(url.replace("Android/", "../")), env, null, null)).status,
        404,
    );
});

test("supports partial bytes and conditional revalidation", async () => {
    const env = {
        GAME_ASSETS: {
            get: async (_, options) => {
                assert.equal(options.range.get("range"), "bytes=1-2");
                return { ...object(), range: { offset: 1, length: 2 }, body: body.slice(1, 3) };
            },
        },
    };
    const response = await gameAsset(
        new Request(url, { headers: { range: "bytes=1-2" } }),
        env,
        null,
        null,
    );
    assert.equal(response.status, 206);
    assert.equal(response.headers.get("content-range"), "bytes 1-2/4");
    assert.equal(response.headers.get("content-length"), "2");
    const conditional = await gameAsset(
        new Request(url, { headers: { "if-none-match": '"hash"' } }),
        { GAME_ASSETS: { get: async () => object() } },
        null,
        null,
    );
    assert.equal(conditional.status, 304);
});

test("edge cache avoids R2 reads", async () => {
    const response = await gameAsset(new Request(url), { GAME_ASSETS: {} }, null, {
        match: async () => new Response(body),
    });
    assert.deepEqual(new Uint8Array(await response.arrayBuffer()), body);
});
